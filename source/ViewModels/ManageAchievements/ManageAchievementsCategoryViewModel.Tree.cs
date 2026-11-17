using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    // Category rows, ordering, rename/merge, art, and the single metadata writer.
    public sealed partial class ManageAchievementsCategoryViewModel : ObservableObject
    {
        /// <summary>
        /// True when any row sits under a different parent than its provider gave it - the order
        /// reset restores structure too, so it has work even when no custom order is stored.
        /// A user-created category never deviates (its provider identity tracks its own label).
        /// </summary>
        public bool HasCustomCategoryNesting =>
            CategoryRows.Any(row => row != null &&
                !row.IsDefaultCategory &&
                !string.IsNullOrWhiteSpace(row.CategoryLabel) &&
                !CategoryPathHelper.IsSame(
                    CategoryPathHelper.GetParentPath(row.CategoryLabel) ?? string.Empty,
                    CategoryPathHelper.GetParentPath(row.ProviderCategoryLabel) ?? string.Empty));

        public bool ResetCategoryOrder()
        {
            // Structure first: every row returns to its provider parent, keeping its current
            // leaf name (leaf renames belong to the name reset), and the custom order clears in
            // the same write.
            var structureMoves = CategoryNestPlanner.PlanStructureResetMoves(
                CategoryRows
                    .Where(row => row != null &&
                        !string.IsNullOrWhiteSpace(row.CategoryLabel) &&
                        !string.IsNullOrWhiteSpace(row.ProviderCategoryLabel))
                    .Select(row => new KeyValuePair<string, string>(row.CategoryLabel, row.ProviderCategoryLabel))
                    .ToList());
            if (structureMoves.Count > 0)
            {
                return ApplyCategoryMoves(structureMoves, orderOverride: Array.Empty<string>());
            }

            var order = GameCustomDataLookup.GetAchievementCategoryOrder(_gameId, _settings?.Persisted);
            if (order == null || order.Count == 0)
            {
                return false;
            }

            var images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted);
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            // Order only: the selection and its art are written back exactly as they stand.
            _achievementOverridesService.SetAchievementCategoryMetadata(
                _gameId,
                Array.Empty<string>(),
                images,
                summaryCategory,
                affectsSummaryData: MarkLibraryRefreshDeferred(false));
            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            return true;
        }

        public bool ResetCategoryNames()
        {
            var renames = CategoryRows
                .Where(row => row != null &&
                              !string.IsNullOrWhiteSpace(row.CategoryLabel) &&
                              !string.IsNullOrWhiteSpace(row.ProviderCategoryLabel) &&
                              !string.Equals(row.CategoryLabel, row.ProviderCategoryLabel, CategoryPathHelper.LabelComparison))
                .Select(row => new { Source = row.CategoryLabel, Target = row.ProviderCategoryLabel })
                .ToList();

            // One batch, not one rename at a time. Each ApplyCategoryMoves is a store write plus
            // a row rebuild, and the rebuild re-probes every category's art on disk -- the same
            // cost that made a multi-row indent slow before it was batched. The moves are still
            // planned and chained in order inside ApplyCategoryMoves, so a rename whose target is
            // a later rename's source resolves exactly as it did sequentially.
            var moves = new List<KeyValuePair<string, string>>();
            foreach (var rename in renames)
            {
                if (CategoryEditor.TryPlanCategoryRename(rename.Source, rename.Target, out var move))
                {
                    moves.Add(move);
                }
            }

            return moves.Count > 0 && ApplyCategoryMoves(moves);
        }

        public bool ResetCategoryArt()
        {
            var images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted);
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            if ((images == null || images.Count == 0) && summaryCategory == null)
            {
                return false;
            }

            var order = GameCustomDataLookup.GetAchievementCategoryOrder(_gameId, _settings?.Persisted);
            _achievementOverridesService.SetAchievementCategoryMetadata(
                _gameId,
                order,
                new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer),
                gameSummaryCategory: null);
            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            return true;
        }

        public async Task ApplyCategoryLocalFileOverrideAsync(
            ManageAchievementsCategoryMetadataItem row,
            string localFilePath)
        {
            if (row == null)
            {
                return;
            }

            var normalizedPath = NormalizeText(localFilePath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !File.Exists(normalizedPath))
            {
                SetCategoryImageStatus(
                    L("LOCPlayAch_ManageAchievements_CustomIcons_LocalFileMissing"),
                    isError: true);
                return;
            }

            try
            {
                var managedPath = await _managedCustomIconService
                    .MaterializeCategoryImageAsync(
                        normalizedPath,
                        _gameIdText,
                        row.FileStem,
                        CancellationToken.None,
                        overwriteExistingTarget: true)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(managedPath) || !File.Exists(managedPath))
                {
                    throw new InvalidOperationException("The image file could not be copied into plugin data.");
                }

                row.SetOverrideValue(managedPath);
                SetCategoryImageStatus(null, isError: false);
                RefreshCategoryMetadataState();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed copying category art for gameId={_gameId}, category={row.CategoryLabel}.");
                SetCategoryImageStatus(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
                RefreshCategoryMetadataState();
            }
        }

        public bool MoveCategoryRowsByLabel(
            IReadOnlyList<string> draggedLabels,
            string targetLabel,
            bool insertAfterTarget)
        {
            if (draggedLabels == null || draggedLabels.Count == 0 || string.IsNullOrWhiteSpace(targetLabel))
            {
                return false;
            }

            var source = CategoryRows.ToList();
            var selectedIndexes = ResolveSelectedCategoryIndexes(source, draggedLabels);
            var normalizedTarget = AchievementCategoryHelper.NormalizeCategoryOrDefault(targetLabel);
            var targetIndex = source.FindIndex(item =>
                string.Equals(item?.CategoryLabel, normalizedTarget, CategoryPathHelper.LabelComparison));
            return TryMoveCategoryRows(source, selectedIndexes, targetIndex, insertAfterTarget);
        }

        public bool MoveCategoryRowsToEndByLabel(IReadOnlyList<string> draggedLabels)
        {
            if (draggedLabels == null || draggedLabels.Count == 0 || CategoryRows.Count == 0)
            {
                return false;
            }

            var source = CategoryRows.ToList();
            var selectedIndexes = ResolveSelectedCategoryIndexes(source, draggedLabels);
            return TryMoveCategoryRows(source, selectedIndexes, source.Count - 1, insertAfterTarget: true);
        }

        private bool TryMoveCategoryRows(
            List<ManageAchievementsCategoryMetadataItem> source,
            IReadOnlyList<int> selectedIndexes,
            int targetIndex,
            bool insertAfterTarget)
        {
            if (source == null ||
                source.Count == 0 ||
                selectedIndexes == null ||
                selectedIndexes.Count == 0 ||
                targetIndex < 0)
            {
                return false;
            }

            if (!AchievementOrderHelper.TryReorder(
                source,
                selectedIndexes,
                targetIndex,
                insertAfterTarget,
                out var reordered))
            {
                return false;
            }

            // Snap the flat move back to tree order before it renders: a dragged parent carries
            // its subtree immediately and a drop cannot split one. The persisted order was always
            // rendered tidy on the next rebuild; the grid just showed the stale interleaving with
            // the old connectors until then.
            var reorderedLabels = reordered
                .Select(row => row?.CategoryLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
            var rowsByLabel = reordered
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                .ToDictionary(
                    row => CategoryPathHelper.NormalizePath(row.CategoryLabel),
                    row => row,
                    CategoryPathHelper.LabelComparer);
            var treeOrdered = AchievementCategoryFilterOrderHelper
                .BuildOrderedCategoryTree(reorderedLabels, reorderedLabels)
                .Where(rowsByLabel.ContainsKey)
                .Select(label => rowsByLabel[label])
                .ToList();
            if (treeOrdered.Count != reordered.Count)
            {
                treeOrdered = reordered;
            }

            CollectionHelper.SynchronizeCollection(CategoryRows, treeOrdered);
            StampIndentAffordances(treeOrdered);
            StampCategoryTreeShapes(treeOrdered);
            PersistCurrentCategoryMetadata();
            return true;
        }

        private static List<int> ResolveSelectedCategoryIndexes(
            IReadOnlyList<ManageAchievementsCategoryMetadataItem> source,
            IReadOnlyList<string> draggedLabels)
        {
            var selected = new HashSet<string>(
                (draggedLabels ?? Array.Empty<string>())
                    .Select(AchievementCategoryHelper.NormalizeCategoryOrDefault)
                    .Where(label => !string.IsNullOrWhiteSpace(label)),
                CategoryPathHelper.LabelComparer);
            if (selected.Count == 0)
            {
                return new List<int>();
            }

            var indexes = new List<int>();
            for (var i = 0; i < source.Count; i++)
            {
                var label = source[i]?.CategoryLabel;
                if (!string.IsNullOrWhiteSpace(label) && selected.Contains(label))
                {
                    indexes.Add(i);
                }
            }

            return indexes;
        }

        private void OpenCategoryImagesFolder()
        {
            try
            {
                var pluginDataPath = PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath();
                if (string.IsNullOrWhiteSpace(pluginDataPath))
                {
                    SetCategoryImageStatus(
                        string.Format(
                            L("LOCPlayAch_Status_Failed"),
                            L("LOCPlayAch_ManageAchievements_CustomIcons_OpenFolderUnavailable")),
                        isError: true);
                    return;
                }

                var imagesFolderPath = Path.Combine(IconCacheLocation.CurrentRootOrDefault(pluginDataPath), _gameIdText);
                Directory.CreateDirectory(imagesFolderPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = imagesFolderPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed opening category image cache folder for gameId={_gameId}.");
                SetCategoryImageStatus(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
            }
        }

        /// <summary>
        /// The single funnel for moving a category, whether the user renamed it, indented it, or
        /// dragged it onto another. Descendants follow, so one cycle guard covers every gesture.
        /// </summary>
        public bool RenameCategoryLabel(string sourceCategoryLabel, string targetCategoryLabel)
        {
            return Finish(_editor.RenameCategoryLabel(BuildSnapshot(), sourceCategoryLabel, targetCategoryLabel));
        }

        /// <summary>
        /// Applies a run of source-to-target label moves as one unit: one store write followed by
        /// one row rebuild. Doing this per move is what made indenting slow: each store write fans
        /// out a synchronous whole-library recompute, and each row rebuild re-probes every
        /// category's art on disk.
        /// </summary>
        private bool ApplyCategoryMoves(
            IReadOnlyList<KeyValuePair<string, string>> moves,
            IReadOnlyList<string> orderOverride = null,
            IReadOnlyList<string> movedSelectionLabels = null)
        {
            return Finish(_editor.ApplyCategoryMoves(BuildSnapshot(), moves, orderOverride, movedSelectionLabels));
        }


        /// <summary>
        /// Row-scoped entry point for the indent and outdent buttons. The keyboard path in the tab
        /// passes the whole grid selection instead.
        /// </summary>
        private void IndentOrOutdentFromCommand(object parameter, bool indent)
        {
            if (!(parameter is ManageAchievementsCategoryMetadataItem row))
            {
                return;
            }

            var labels = new List<string> { row.CategoryLabel };
            if (indent)
            {
                IndentCategoryRows(labels);
            }
            else
            {
                OutdentCategoryRows(labels);
            }
        }

        /// <summary>
        /// Nests each row under the nearest category above it that can be its parent - the sibling
        /// immediately preceding it, the way an outliner indents. Rows already as deep as they can
        /// go are left alone.
        /// </summary>
        public bool IndentCategoryRows(IReadOnlyList<string> labels)
        {
            return MoveCategoryRowsByDepth(labels, indent: true);
        }

        /// <summary>Promotes each row to sit beside its current parent.</summary>
        public bool OutdentCategoryRows(IReadOnlyList<string> labels)
        {
            return MoveCategoryRowsByDepth(labels, indent: false);
        }

        private bool MoveCategoryRowsByDepth(IReadOnlyList<string> labels, bool indent)
        {
            if (labels == null || labels.Count == 0)
            {
                return false;
            }

            // Deepest first, and a dragged descendant is dropped because its ancestor carries it.
            var targets = labels
                .Select(CategoryPathHelper.NormalizePath)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(CategoryPathHelper.LabelComparer)
                .ToList();

            targets = targets
                .Where(label => !targets.Any(other => CategoryPathHelper.IsDescendantOf(label, other)))
                .OrderByDescending(CategoryPathHelper.GetDepth)
                .ToList();

            // Every destination is resolved against the rows as they stand now, before anything
            // moves. Resolving as we went read a list that earlier moves had already rewritten, so
            // a later row could land under a row that had just been reparented itself - producing
            // nesting the user never asked for.
            var snapshot = SnapshotCategoryLabels();
            var moves = new List<KeyValuePair<string, string>>();
            foreach (var label in targets)
            {
                var parent = indent
                    ? ResolveIndentParent(snapshot, label)
                    : CategoryPathHelper.GetParentPath(CategoryPathHelper.GetParentPath(label) ?? label);

                if (indent && parent == null)
                {
                    continue;
                }

                if (!indent && CategoryPathHelper.GetDepth(label) <= 1)
                {
                    continue;
                }

                moves.Add(new KeyValuePair<string, string>(label, CategoryPathHelper.Reparent(label, parent)));
            }

            return ApplyCategoryMoves(moves);
        }

        /// <summary>
        /// Makes each row a subcategory of <paramref name="targetParentLabel"/> (null = top level)
        /// as one batch: one snapshot, one store write, one row rebuild, one selection restore.
        /// </summary>
        public bool NestCategoryRowsUnder(IReadOnlyList<string> labels, string targetParentLabel)
        {
            return Finish(_editor.NestCategoryRowsUnder(BuildSnapshot(), labels, targetParentLabel));
        }

        /// <summary>
        /// Drops rows into the gap above <paramref name="gapBeforeLabel"/> (null = end of list),
        /// adopting the gap's level: dropped between two nested siblings, a category becomes
        /// their sibling right there; dropped at the end it becomes top level. Falls back to a
        /// flat reorder when no reparent applies (already at the gap's level, or the reparent is
        /// invalid), so a drop always does something predictable.
        /// </summary>
        public bool NestCategoryRowsIntoGap(IReadOnlyList<string> labels, string gapBeforeLabel)
        {
            var parentOfGap = string.IsNullOrWhiteSpace(gapBeforeLabel)
                ? null
                : CategoryPathHelper.GetParentPath(CategoryPathHelper.NormalizePath(gapBeforeLabel));
            var moves = CategoryNestPlanner.PlanNestMoves(SnapshotCategoryLabels(), labels, parentOfGap);
            if (moves.Count == 0)
            {
                return string.IsNullOrWhiteSpace(gapBeforeLabel)
                    ? MoveCategoryRowsToEndByLabel(labels)
                    : MoveCategoryRowsByLabel(labels, gapBeforeLabel, insertAfterTarget: false);
            }

            var rendered = CategoryRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                .Select(row => row.CategoryLabel)
                .ToList();
            var gapPlan = CategoryNestPlanner.PlanGapOrder(rendered, labels, moves, gapBeforeLabel);
            return ApplyCategoryMoves(moves, gapPlan.Order, gapPlan.SelectionRoots);
        }

        /// <summary>Whether <see cref="NestCategoryRowsUnder"/> would move anything.</summary>
        public bool CanNestCategoryRowsUnder(IReadOnlyList<string> labels, string targetParentLabel)
        {
            return CategoryEditor.CanNestCategoryRowsUnder(BuildSnapshot(), labels, targetParentLabel);
        }

        /// <summary>Rendered category labels in render order, normalized, Default excluded.</summary>
        private List<string> SnapshotCategoryLabels()
        {
            return CategoryRows
                .Where(row => row != null && !row.IsDefaultCategory)
                .Select(row => CategoryPathHelper.NormalizePath(row.CategoryLabel))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
        }

        /// <summary>
        /// The label directly above <paramref name="label"/> that shares its parent. Null when the
        /// row is already first among its siblings, since there is nothing to nest under - which is
        /// also what disables the indent button for that row.
        /// </summary>
        private static string ResolveIndentParent(IReadOnlyList<string> orderedLabels, string label)
        {
            var parent = CategoryPathHelper.GetParentPath(label);
            string previousSibling = null;

            foreach (var candidate in orderedLabels)
            {
                if (CategoryPathHelper.IsSame(candidate, label))
                {
                    break;
                }

                if (string.Equals(
                        CategoryPathHelper.GetParentPath(candidate) ?? string.Empty,
                        parent ?? string.Empty,
                        CategoryPathHelper.LabelComparison))
                {
                    previousSibling = candidate;
                }
            }

            return previousSibling;
        }

        /// <summary>
        /// Folds every achievement in <paramref name="sourceCategoryLabel"/> into
        /// <paramref name="targetCategoryLabel"/>: re-labels them to the target and replaces their
        /// group-based type tags (Base/DLC/Update/Subset) with the target category's, preserving all
        /// other type tags. If the source category was the game-summary-art source, that selection is
        /// reset; the target category's own state is left untouched.
        /// </summary>
        public bool MergeCategoryInto(string sourceCategoryLabel, string targetCategoryLabel)
        {
            return Finish(_editor.MergeCategoryInto(BuildSnapshot(), sourceCategoryLabel, targetCategoryLabel));
        }

        public bool ApplyCategoryRenameOverride(ManageAchievementsCategoryMetadataItem row)
        {
            if (row == null)
            {
                return false;
            }

            var sourceCategory = CategoryPathHelper.NormalizePath(row.CategoryLabel);
            var typed = row.GetNormalizedRenameOverrideValue();

            // Typed labels are a single segment: nesting is expressed by indenting a row, not by
            // spelling out a path, so the two gestures cannot disagree about where a row belongs.
            if (typed != null && CategoryPathHelper.ContainsSeparator(typed))
            {
                SetCategoryImageStatus(L("LOCPlayAch_ManageAchievements_Category_PathSeparatorNotAllowed"), isError: true);
                row.ResetRenameTextFromCurrentCategory();
                return false;
            }

            // The row keeps its place in the tree; only its own name changes.
            var targetLeaf = typed ?? CategoryPathHelper.GetLeafName(row.ProviderCategoryLabel);
            var targetCategory = CategoryPathHelper.Join(
                CategoryPathHelper.GetParentPath(sourceCategory),
                targetLeaf);

            if (string.IsNullOrWhiteSpace(sourceCategory) ||
                string.IsNullOrWhiteSpace(targetCategory) ||
                CategoryPathHelper.IsSame(sourceCategory, targetCategory))
            {
                row.ResetRenameTextFromCurrentCategory();
                return false;
            }

            var renamed = RenameCategoryLabel(sourceCategory, targetCategory);
            if (!renamed)
            {
                row.ResetRenameTextFromCurrentCategory();
            }

            return renamed;
        }

        /// <summary>
        /// Creates an empty top-level category under a unique placeholder name and persists it in
        /// one write. Returns the created label so the view can focus its rename box, or null when
        /// nothing was created (no rows rendered, so the label could never render either).
        /// </summary>
        public string AddNewCategory()
        {
            var outcome = _editor.AddNewCategory(
                BuildSnapshot(),
                L("LOCPlayAch_ManageAchievements_Category_NewCategoryName"));
            Finish(outcome);
            return outcome.CreatedLabel;
        }

        /// <summary>
        /// Creates an empty top-level category under a name the user gave. A name that already names
        /// a top-level category adopts it rather than creating a second one alongside it, so naming
        /// something twice is not a way to end up with two of it.
        /// </summary>
        public string CreateCategory(string leafName)
        {
            var outcome = _editor.CreateCategory(BuildSnapshot(), leafName);
            Finish(outcome);
            return outcome.CreatedLabel;
        }

        /// <summary>
        /// Creates an empty sibling copy of each node: same parent, art override copied, unique
        /// leaf name ("DLC (2)"), placed right after the source's subtree. Achievements, the
        /// subtree, and the summary selection (single-choice) are not copied. One write for the
        /// whole batch. Returns the created labels in creation order.
        /// </summary>
        public List<string> DuplicateCategories(IReadOnlyList<string> labels)
        {
            var outcome = _editor.DuplicateCategories(BuildSnapshot(), labels);
            Finish(outcome);
            return outcome.CreatedLabels.ToList();
        }

        /// <summary>
        /// How many achievements are filed under any of the labels or their descendants - what a
        /// delete would send back to the Default bucket, for the confirmation prompt.
        /// </summary>
        public int CountAchievementsInCategories(IReadOnlyList<string> labels)
        {
            return CategoryEditor.CountAchievementsInCategories(BuildSnapshot(), labels);
        }

        /// <summary>
        /// Deletes each category and its subtree: every achievement filed inside goes back to the
        /// Default bucket, and the labels leave the order, the art overrides, and the summary
        /// selection. Membership and metadata land in one write. The Default bucket itself is
        /// refused. Returns true when anything was removed.
        /// </summary>
        public bool DeleteCategories(IReadOnlyList<string> labels)
        {
            return Finish(_editor.DeleteCategories(BuildSnapshot(), labels));
        }

        private void ReplaceCategoryRows(IEnumerable<ManageAchievementsCategoryMetadataItem> rows)
        {
            foreach (var row in CategoryRows)
            {
                row.PropertyChanged -= CategoryMetadataRow_PropertyChanged;
            }

            var nextRows = (rows ?? Enumerable.Empty<ManageAchievementsCategoryMetadataItem>()).ToList();
            foreach (var row in nextRows)
            {
                row.PropertyChanged += CategoryMetadataRow_PropertyChanged;
            }

            // One Reset rather than a Clear plus an Add per row: the grid rebuilds a container and
            // lays it out on every notification, and each of these rows carries category art.
            CollectionHelper.Replace(CategoryRows, nextRows);

            RefreshCategoryMetadataState();
            OnPropertyChanged(nameof(CanMergeCategories));
            RefreshFilterOptions();
        }

        private void CategoryMetadataRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            // Stamping the filter scopes touches every row, and the trailing
            // RefreshCategoryMetadataState is itself a pass over all of them, so letting the
            // notifications through would make a stamp quadratic. Nothing is lost by skipping it:
            // that state tracks the art values, which a filter scope does not move.
            if (_isStampingFilterScopes)
            {
                return;
            }

            if (e.PropertyName == nameof(ManageAchievementsCategoryMetadataItem.ArtOverrideValue) &&
                !_isPersistingCategoryMetadata)
            {
                SetCategoryImageStatus(null, isError: false);
                // Art values arrive on complete input (focus loss, Enter, picker, drop,
                // clear). Valid values persist immediately; an invalid value stays pending
                // in the row with its inline error and the store keeps the last good value.
                if (sender is ManageAchievementsCategoryMetadataItem artRow &&
                    !artRow.HasArtOverrideValidationError)
                {
                    PersistCurrentCategoryMetadata();
                }
            }

            if (e.PropertyName == nameof(ManageAchievementsCategoryMetadataItem.IsSummarySelected) &&
                !_isEnforcingSummarySelection &&
                !_isPersistingCategoryMetadata &&
                sender is ManageAchievementsCategoryMetadataItem selectedRow)
            {
                if (selectedRow.IsSummarySelected)
                {
                    _isEnforcingSummarySelection = true;
                    try
                    {
                        foreach (var row in CategoryRows.Where(row => row != null && !ReferenceEquals(row, selectedRow)))
                        {
                            row.IsSummarySelected = false;
                        }
                    }
                    finally
                    {
                        _isEnforcingSummarySelection = false;
                    }
                }

                PersistCurrentCategoryMetadata();
            }

            RefreshCategoryMetadataState();
        }

        private void RefreshCategoryRows()
        {
            var groups = _allRows
                .Where(row => row != null)
                .GroupBy(row => AchievementCategoryHelper.NormalizeCategoryOrDefault(row.Category), CategoryPathHelper.LabelComparer)
                .ToDictionary(group => group.Key, group => group.ToList(), CategoryPathHelper.LabelComparer);
            if (groups.Count == 0)
            {
                ReplaceCategoryRows(Array.Empty<ManageAchievementsCategoryMetadataItem>());
                SetCustomCategoryMetadataState(hasOrder: false, hasNames: false, hasArt: false, hasSummaryCategory: false);
                return;
            }

            var categoryOrder = GameCustomDataLookup.GetAchievementCategoryOrder(_gameId, _settings?.Persisted);
            var categoryImages = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted);
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            HasCustomCategoryOrder = categoryOrder != null && categoryOrder.Count > 0;
            HasCustomCategoryArt = categoryImages != null && categoryImages.Count > 0;
            HasCustomSummaryCategory = summaryCategory != null;

            // Tree order rather than a flat first-seen list: siblings stay contiguous, a subtree is
            // never split, and every ancestor gets a row even when it holds nothing itself - which
            // it must, because the single metadata writer rebuilds order and art from the rendered
            // rows and would otherwise drop an intermediate node's art. The same resolution a
            // headless caller uses, so the grid's menu and this tab plan against one tree.
            var orderedLabels = CategoryEditor.ResolveRenderedLabels(
                (_definitionOrderedRows.Count > 0 ? _definitionOrderedRows : _allRows)
                    .Where(row => row != null)
                    .Select(row => row.Category),
                categoryOrder,
                categoryImages?.Keys,
                summaryCategory?.Label);
            var fileStems = AchievementIconCachePathBuilder.BuildCategoryFileStems(orderedLabels);
            var rows = new List<ManageAchievementsCategoryMetadataItem>();

            foreach (var label in orderedLabels)
            {
                groups.TryGetValue(label, out var bucket);

                if (!fileStems.TryGetValue(label, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                // Each node counts only what is labelled exactly this node - a parent that holds
                // no achievements of its own reads 0/0, which is what it is. Rolling the subtree up
                // would make a parent and its children report the same achievements twice over.
                var members = bucket ?? new List<ManageAchievementsCategoryItem>();

                CategoryImageOverrideData imageOverride = null;
                categoryImages?.TryGetValue(label, out imageOverride);
                // The provider label describes this node itself, so it comes from its own bucket.
                // The Default bucket keeps its own identity: after a delete or a merge sends
                // achievements back to it, a shared provider category among them must not make
                // Default wear that category's name and art (which no reset could clear, since
                // Default refuses renames).
                var isDefaultLabel = string.Equals(
                    label,
                    AchievementCategoryHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase);
                var providerCategoryLabel = isDefaultLabel
                    ? label
                    : ResolveSharedCategory(bucket, item => item?.ProviderCategory) ?? label;
                var row = ManageAchievementsCategoryMetadataItem.Create(
                    label,
                    providerCategoryLabel,
                    members,
                    imageOverride,
                    _gameIdText,
                    fileStem,
                    _managedCustomIconService,
                    isSummarySelected: summaryCategory != null &&
                        string.Equals(summaryCategory.Label, label, CategoryPathHelper.LabelComparison),
                    artFallbackSource: _allRows);
                row.CategoryDepth = CategoryPathHelper.GetDepth(label);
                rows.Add(row);
            }

            // Leaves, not full paths: indenting a row changes its path without renaming anything,
            // and comparing paths would report a move as a custom name.
            HasCustomCategoryNames = rows.Any(row => !string.Equals(
                CategoryPathHelper.GetLeafName(row.CategoryLabel),
                CategoryPathHelper.GetLeafName(row.ProviderCategoryLabel),
                CategoryPathHelper.LabelComparison));
            StampIndentAffordances(rows);
            StampCategoryTreeShapes(rows);
            _memberApiNamesByLabel = BuildMemberApiNamesByLabel();
            StampCategoryFilterScopes(rows);
            ReplaceCategoryRows(rows);
        }

        /// <summary>
        /// Gives each row the connectors that place it in the tree. Resolved here rather than on the
        /// row because a lane's continuation depends on what follows in the rendered order, which
        /// only this pass knows. Cleared outright when nothing nests, so a flat list stays flat.
        /// </summary>
        private static void StampCategoryTreeShapes(IReadOnlyList<ManageAchievementsCategoryMetadataItem> rows)
        {
            var paths = rows.Select(row => row?.CategoryLabel).ToList();
            if (!CategoryTreeShapeBuilder.HasNesting(paths))
            {
                foreach (var row in rows)
                {
                    if (row != null)
                    {
                        row.TreeShape = null;
                    }
                }

                return;
            }

            var shapes = CategoryTreeShapeBuilder.Build(paths);
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null)
                {
                    rows[i].TreeShape = shapes[i];
                }
            }
        }

        /// <summary>
        /// Marks which rows have somewhere to indent to. Resolved against the same ordered label
        /// list the gesture itself uses, so the button is enabled exactly when the click would do
        /// something.
        /// </summary>
        private static void StampIndentAffordances(IReadOnlyList<ManageAchievementsCategoryMetadataItem> rows)
        {
            var orderedLabels = rows
                .Where(row => row != null && !row.IsDefaultCategory)
                .Select(row => CategoryPathHelper.NormalizePath(row.CategoryLabel))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();

            foreach (var row in rows.Where(row => row != null))
            {
                row.CanIndent = !row.IsDefaultCategory &&
                    ResolveIndentParent(orderedLabels, CategoryPathHelper.NormalizePath(row.CategoryLabel)) != null;
            }
        }

        private void PersistCurrentCategoryMetadata()
        {
            _isPersistingCategoryMetadata = true;
            try
            {
                var categoryOrder = CategoryRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                    .Select(row => row.CategoryLabel)
                    .ToList();
                var imageOverrides = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);

                foreach (var row in CategoryRows.Where(row => row != null))
                {
                    var category = AchievementCategoryHelper.NormalizeCategoryOrDefault(row.CategoryLabel);
                    if (string.IsNullOrWhiteSpace(category))
                    {
                        continue;
                    }

                    // A row holding an invalid pending edit keeps its last persisted value
                    // in the store; the invalid text stays in the row with its inline error.
                    var art = row.GetPersistableArtOverrideValue();
                    if (string.IsNullOrWhiteSpace(art))
                    {
                        continue;
                    }

                    imageOverrides[category] = new CategoryImageOverrideData
                    {
                        Art = art
                    };
                }

                var summaryRow = CategoryRows.FirstOrDefault(row =>
                    row != null && row.IsSummarySelected && !string.IsNullOrWhiteSpace(row.CategoryLabel));
                var summaryCategory = summaryRow != null
                    ? new GameSummaryCategoryData
                    {
                        Label = summaryRow.CategoryLabel,
                        ProviderLabel = summaryRow.ProviderCategoryLabel
                    }
                    : null;

                // Reordering rows and editing art on categories other than the summary source are
                // per-game display state. Firing the library-wide pass per edit is what made every
                // drag step and every art box feel like the whole library was being rebuilt.
                _achievementOverridesService.SetAchievementCategoryMetadata(
                    _gameId,
                    categoryOrder,
                    imageOverrides,
                    summaryCategory,
                    affectsSummaryData: MarkLibraryRefreshDeferred(CategoryEditor.SummaryArtChanged(
                        GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted),
                        GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted),
                        summaryCategory,
                        imageOverrides)));
                RaiseCategoryMetadataPersisted();

                foreach (var row in CategoryRows.Where(row => row != null && !row.HasArtOverrideValidationError))
                {
                    row.CommitCurrentOverridesAsBaseline();
                }

                HasCustomCategoryOrder = categoryOrder.Count > 0;
                HasCustomCategoryArt = imageOverrides.Count > 0;
                HasCustomSummaryCategory = summaryCategory != null;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving category metadata for gameId={_gameId}");
                SetCategoryImageStatus(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
            }
            finally
            {
                _isPersistingCategoryMetadata = false;
            }
        }

        private void SetCustomCategoryMetadataState(bool hasOrder, bool hasNames, bool hasArt, bool hasSummaryCategory)
        {
            HasCustomCategoryOrder = hasOrder;
            HasCustomCategoryNames = hasNames;
            HasCustomCategoryArt = hasArt;
            HasCustomSummaryCategory = hasSummaryCategory;
        }

        /// <summary>
        /// Records whether a write skipped the library-wide passes, and passes the flag straight
        /// back so it can wrap the argument at the call site. The debt only accumulates: a write
        /// that did fan out reaches the summary listeners through the store, but not the surfaces
        /// that follow the scoped cache invalidation the flush raises -- the friends views among
        /// them -- so clearing on it dropped every earlier edit from those surfaces for good.
        /// </summary>
        private bool MarkLibraryRefreshDeferred(bool affectsSummaryData)
        {
            _hasDeferredLibraryRefresh |= !affectsSummaryData;
            return affectsSummaryData;
        }

        /// <summary>
        /// Brings the library-scope surfaces - the overview grid's per-game achievement rows and
        /// the theme's library-wide lists - onto this game's edited categories in one pass. The
        /// edits themselves are written without those passes, which is what keeps a click on this
        /// tab from costing a whole-library recompute; this pays for them once, on teardown,
        /// instead of once per click.
        /// </summary>
        public void FlushDeferredLibraryRefresh()
        {
            if (!_hasDeferredLibraryRefresh)
            {
                return;
            }

            _hasDeferredLibraryRefresh = false;
            DeferredLibraryRefreshRequired?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseCategoryMetadataPersisted()
        {
            CategoryMetadataPersisted?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshCategoryMetadataState()
        {
            var hasValidationErrors = false;
            foreach (var row in CategoryRows.Where(row => row != null))
            {
                hasValidationErrors |= row.HasValidationErrors;
            }

            HasCategoryImageValidationErrors = hasValidationErrors;
        }

        private void SetCategoryImageStatus(string text, bool isError)
        {
            _categoryImageStatusText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            _categoryImageStatusIsError = isError && !string.IsNullOrWhiteSpace(_categoryImageStatusText);
            OnPropertyChanged(nameof(CategoryImageStatusText));
            OnPropertyChanged(nameof(CategoryImageStatusIsError));
            OnPropertyChanged(nameof(HasCategoryImageStatusText));
        }

        private static string ResolveSharedCategory(
            IEnumerable<ManageAchievementsCategoryItem> source,
            Func<ManageAchievementsCategoryItem, string> selector)
        {
            string category = null;
            foreach (var item in source ?? Enumerable.Empty<ManageAchievementsCategoryItem>())
            {
                var candidate = AchievementCategoryHelper.NormalizeCategoryOrDefault(selector?.Invoke(item));
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (category == null)
                {
                    category = candidate;
                }
                else if (!string.Equals(category, candidate, CategoryPathHelper.LabelComparison))
                {
                    return null;
                }
            }

            return category;
        }

    }
}
