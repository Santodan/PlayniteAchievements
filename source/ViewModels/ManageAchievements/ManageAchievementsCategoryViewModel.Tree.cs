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
        public bool ResetCategoryOrder()
        {
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
                              !string.Equals(row.CategoryLabel, row.ProviderCategoryLabel, StringComparison.OrdinalIgnoreCase))
                .Select(row => new { Source = row.CategoryLabel, Target = row.ProviderCategoryLabel })
                .ToList();

            var renamed = false;
            foreach (var rename in renames)
            {
                renamed |= RenameCategoryLabel(rename.Source, rename.Target);
            }

            return renamed;
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
                new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase),
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
            var normalizedTarget = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetLabel);
            var targetIndex = source.FindIndex(item =>
                string.Equals(item?.CategoryLabel, normalizedTarget, StringComparison.OrdinalIgnoreCase));
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
                    StringComparer.OrdinalIgnoreCase);
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
                    .Select(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault)
                    .Where(label => !string.IsNullOrWhiteSpace(label)),
                StringComparer.OrdinalIgnoreCase);
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

                var imagesFolderPath = Path.Combine(pluginDataPath, "icon_cache", _gameIdText);
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
            var normalizedSourceCategory = AchievementCategoryTypeHelper.NormalizeCategory(sourceCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedSourceCategory) ||
                string.Equals(
                    normalizedSourceCategory,
                    AchievementCategoryTypeHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                // The Default bucket holds every achievement without an explicit category;
                // renaming it away would leave no fallback bucket.
                return false;
            }

            normalizedSourceCategory = CategoryPathHelper.NormalizePath(normalizedSourceCategory);
            var normalizedTargetCategory = CategoryPathHelper.NormalizePath(targetCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedTargetCategory) ||
                CategoryPathHelper.IsSame(normalizedSourceCategory, normalizedTargetCategory))
            {
                return false;
            }

            // A node cannot become its own descendant, and nothing may nest under Default: it has
            // no provider identity and refuses rename and merge, so a child there is unreachable.
            if (CategoryPathHelper.IsDescendantOf(normalizedTargetCategory, normalizedSourceCategory) ||
                string.Equals(
                    CategoryPathHelper.Split(normalizedTargetCategory)[0],
                    AchievementCategoryTypeHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ApplyCategoryMoves(new[]
            {
                new KeyValuePair<string, string>(normalizedSourceCategory, normalizedTargetCategory)
            });
        }

        /// <summary>
        /// Applies a run of source-to-target label moves as one unit: membership is rewritten in a
        /// single pass over the achievements, the label-keyed metadata plans are chained, and both
        /// halves land in one store write followed by one row rebuild.
        ///
        /// Doing this per move is what made indenting slow. Each store write fans out a synchronous
        /// whole-library recompute, and each row rebuild re-probes every category's art on disk, so
        /// a rename cost two recomputes and a multi-row indent paid that per row.
        /// </summary>
        private bool ApplyCategoryMoves(IReadOnlyList<KeyValuePair<string, string>> moves)
        {
            if (moves == null || moves.Count == 0)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var order = GameCustomDataLookup.GetAchievementCategoryOrder(_gameId, _settings?.Persisted);
            var images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted);
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            var summaryCategoryBefore = summaryCategory;
            var imagesBefore = images;

            var applied = false;
            foreach (var move in moves)
            {
                if (ReassignEffectiveCategoryRows(
                        move.Key,
                        move.Value,
                        categoryOverrideMap,
                        categoryTypeOverrideMap: null,
                        targetGroupTypes: null,
                        rewriteDescendantPaths: true))
                {
                    applied = true;
                }

                // Metadata moves even when no achievement did: an intermediate node can exist purely
                // as ordering and art, and it still has to follow its subtree.
                var plan = CategoryMetadataRenamer.Plan(move.Key, move.Value, order, images, summaryCategory);
                if (plan == null)
                {
                    continue;
                }

                order = plan.Order;
                images = plan.Images;
                summaryCategory = plan.SummaryCategory;
                applied = true;
            }

            if (!applied)
            {
                return false;
            }

            // Moving an achievement between this game's categories changes nothing any library
            // rollup reads, so the write is scoped out of the overview's delta tick. That tick
            // recomputes library-wide state whatever changed, and firing one per click is what
            // made an indent feel like a full library update long after the row had moved.
            _achievementOverridesService.SetAchievementCategoryAssignmentAndMetadata(
                _gameId,
                categoryOverrideMap,
                categoryTypeOverrideMap,
                order,
                images,
                summaryCategory,
                affectsSummaryData: MarkLibraryRefreshDeferred(SummaryArtChanged(
                    summaryCategoryBefore,
                    imagesBefore,
                    summaryCategory,
                    images)));
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            CategoryRowsMoved?.Invoke(this, moves.Select(move => move.Value).ToList());
            return true;
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
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
            return ApplyCategoryMoves(CategoryNestPlanner.PlanNestMoves(
                SnapshotCategoryLabels(),
                labels,
                targetParentLabel));
        }

        /// <summary>Whether <see cref="NestCategoryRowsUnder"/> would move anything.</summary>
        public bool CanNestCategoryRowsUnder(IReadOnlyList<string> labels, string targetParentLabel)
        {
            return CategoryNestPlanner.PlanNestMoves(
                SnapshotCategoryLabels(),
                labels,
                targetParentLabel).Count > 0;
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
                        StringComparison.OrdinalIgnoreCase))
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
            var normalizedSourceCategory = AchievementCategoryTypeHelper.NormalizeCategory(sourceCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedSourceCategory) ||
                string.Equals(
                    normalizedSourceCategory,
                    AchievementCategoryTypeHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Merging the Default bucket away is blocked; merging INTO Default remains
                // a supported way to un-categorize achievements.
                return false;
            }

            var normalizedTargetCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedTargetCategory) ||
                string.Equals(normalizedSourceCategory, normalizedTargetCategory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var targetGroupTypes = ResolveGroupTypesForCategory(normalizedTargetCategory);

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var membershipChanged = ReassignEffectiveCategoryRows(
                normalizedSourceCategory,
                normalizedTargetCategory,
                categoryOverrideMap,
                categoryTypeOverrideMap,
                targetGroupTypes);

            // A node with no achievements moves none, but it still occupies an order slot and can
            // hold art, so the merge proceeds on the metadata alone and folds the node away.
            if (!membershipChanged &&
                !CategoryRows.Any(row => row != null &&
                    CategoryPathHelper.IsSame(row.CategoryLabel, normalizedSourceCategory)))
            {
                return false;
            }

            // One write for both halves: membership and the label-keyed metadata. Two would each
            // fan out a synchronous whole-library recompute.
            var summaryCategoryBefore = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            var imagesBefore = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted);
            var metadata = PlanCategoryMergeMetadata(normalizedSourceCategory, normalizedTargetCategory);
            _achievementOverridesService.SetAchievementCategoryAssignmentAndMetadata(
                _gameId,
                categoryOverrideMap,
                categoryTypeOverrideMap,
                metadata.Order,
                metadata.Images,
                metadata.SummaryCategory,
                affectsSummaryData: MarkLibraryRefreshDeferred(SummaryArtChanged(
                    summaryCategoryBefore,
                    imagesBefore,
                    metadata.SummaryCategory,
                    metadata.Images)));
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            return true;
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
                row.ResetRenameOverrideTextFromCurrentCategory();
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
                row.ResetRenameOverrideTextFromCurrentCategory();
                return false;
            }

            var renamed = RenameCategoryLabel(sourceCategory, targetCategory);
            if (!renamed)
            {
                row.ResetRenameOverrideTextFromCurrentCategory();
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
            if (CategoryRows.Count == 0)
            {
                return null;
            }

            var existing = CategoryRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                .Select(row => row.CategoryLabel)
                .ToList();
            var label = CategoryNameGenerator.GenerateUniqueLabel(
                existing,
                parentPath: null,
                baseLeafName: L("LOCPlayAch_ManageAchievements_Category_NewCategoryName"));
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            var order = existing.ToList();
            order.Add(label);

            // Creating an empty node is pure per-game display state, so the write is scoped out
            // of the library-wide passes like every other order edit.
            _achievementOverridesService.SetAchievementCategoryMetadata(
                _gameId,
                order,
                GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted),
                GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted),
                affectsSummaryData: MarkLibraryRefreshDeferred(false));
            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            return label;
        }

        /// <summary>
        /// Creates an empty sibling copy of each node: same parent, art override copied, unique
        /// leaf name ("DLC (2)"), placed right after the source's subtree. Achievements, the
        /// subtree, and the summary selection (single-choice) are not copied. One write for the
        /// whole batch. Returns the created labels in creation order.
        /// </summary>
        public List<string> DuplicateCategories(IReadOnlyList<string> labels)
        {
            var created = new List<string>();
            var rendered = CategoryRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                .Select(row => row.CategoryLabel)
                .ToList();

            var sources = (labels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(label =>
                    !string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase) &&
                    rendered.Any(existing => CategoryPathHelper.IsSame(existing, label)))
                .ToList();
            if (sources.Count == 0)
            {
                return created;
            }

            var order = rendered.ToList();
            var images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted)
                ?.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                var newLabel = CategoryNameGenerator.GenerateUniqueLabel(
                    order,
                    CategoryPathHelper.GetParentPath(source),
                    CategoryPathHelper.GetLeafName(source));
                if (string.IsNullOrWhiteSpace(newLabel))
                {
                    continue;
                }

                // Beside the whole subtree, not inside it: the copy is a sibling of the source.
                var insertIndex = order.Count;
                for (var i = order.Count - 1; i >= 0; i--)
                {
                    if (CategoryPathHelper.IsSelfOrDescendantOf(order[i], source))
                    {
                        insertIndex = i + 1;
                        break;
                    }
                }

                order.Insert(insertIndex, newLabel);
                if (images.TryGetValue(source, out var sourceArt) && sourceArt != null)
                {
                    images[newLabel] = sourceArt.Clone();
                }

                created.Add(newLabel);
            }

            if (created.Count == 0)
            {
                return created;
            }

            // Copies are empty nodes and the summary selection is untouched, so the write stays
            // scoped out of the library-wide passes.
            _achievementOverridesService.SetAchievementCategoryMetadata(
                _gameId,
                order,
                images,
                GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted),
                affectsSummaryData: MarkLibraryRefreshDeferred(false));
            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            return created;
        }

        /// <summary>
        /// Removes each category and its subtree from the order, the art overrides, and the
        /// summary selection in one write. A label is refused when any achievement is filed under
        /// it or a descendant (those categories are merged away instead), or when it is the
        /// Default bucket. Returns true when anything was removed.
        /// </summary>
        public bool DeleteEmptyCategories(IReadOnlyList<string> labels)
        {
            var rendered = CategoryRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                .Select(row => row.CategoryLabel)
                .ToList();

            var deletable = (labels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(label =>
                    !string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase) &&
                    rendered.Any(existing => CategoryPathHelper.IsSame(existing, label)) &&
                    !_allRows.Any(row => row != null && CategoryPathHelper.IsSelfOrDescendantOf(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category),
                        label)))
                .ToList();
            if (deletable.Count == 0)
            {
                return false;
            }

            var order = rendered
                .Where(label => !deletable.Any(deleted => CategoryPathHelper.IsSelfOrDescendantOf(label, deleted)))
                .ToList();

            var imagesBefore = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted);
            var images = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in imagesBefore ?? new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Key) ||
                    deletable.Any(deleted => CategoryPathHelper.IsSelfOrDescendantOf(pair.Key, deleted)))
                {
                    continue;
                }

                images[pair.Key] = pair.Value.Clone();
            }

            var summaryBefore = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            var summaryCategory = summaryBefore;
            if (summaryCategory != null &&
                deletable.Any(deleted => CategoryPathHelper.IsSelfOrDescendantOf(summaryCategory.Label, deleted)))
            {
                summaryCategory = null;
            }

            _achievementOverridesService.SetAchievementCategoryMetadata(
                _gameId,
                order,
                images,
                summaryCategory,
                affectsSummaryData: MarkLibraryRefreshDeferred(SummaryArtChanged(
                    summaryBefore,
                    imagesBefore,
                    summaryCategory,
                    images)));
            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            return true;
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
            CategoryRows.ReplaceAll(nextRows);

            RefreshCategoryMetadataState();
            OnPropertyChanged(nameof(CanMergeCategories));
        }

        private void CategoryMetadataRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
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
                .GroupBy(row => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
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
            // rows and would otherwise drop an intermediate node's art.
            var sourceLabels = (_definitionOrderedRows.Count > 0 ? _definitionOrderedRows : _allRows)
                .Where(row => row != null)
                .Select(row => row.Category)
                .ToList();

            // A label carrying user state but no achievements still needs a row, or the metadata
            // writer - which rebuilds from the rendered rows - would drop that state. The order
            // list seeds rows too: a user-created category must survive rebuilds and reloads on
            // the strength of the persisted order alone, and the cost is that a stale order entry
            // (an upstream rename, say) surfaces as an empty row the user can delete instead of
            // vanishing silently.
            sourceLabels.AddRange(categoryImages?.Keys ?? Enumerable.Empty<string>());
            sourceLabels.AddRange(categoryOrder ?? (IReadOnlyList<string>)Array.Empty<string>());
            if (summaryCategory?.Label != null)
            {
                sourceLabels.Add(summaryCategory.Label);
            }

            var orderedLabels = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(
                sourceLabels,
                categoryOrder);
            EnsureDefaultCategoryLabel(orderedLabels, categoryOrder);
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
                var providerCategoryLabel = ResolveSharedCategory(bucket, item => item?.ProviderCategory) ?? label;
                var row = ManageAchievementsCategoryMetadataItem.Create(
                    label,
                    providerCategoryLabel,
                    members,
                    imageOverride,
                    _gameIdText,
                    fileStem,
                    _managedCustomIconService,
                    isSummarySelected: summaryCategory != null &&
                        string.Equals(summaryCategory.Label, label, StringComparison.OrdinalIgnoreCase),
                    artFallbackSource: _allRows);
                row.CategoryDepth = CategoryPathHelper.GetDepth(label);
                rows.Add(row);
            }

            // Leaves, not full paths: indenting a row changes its path without renaming anything,
            // and comparing paths would report a move as a custom name.
            HasCustomCategoryNames = rows.Any(row => !string.Equals(
                CategoryPathHelper.GetLeafName(row.CategoryLabel),
                CategoryPathHelper.GetLeafName(row.ProviderCategoryLabel),
                StringComparison.OrdinalIgnoreCase));
            StampIndentAffordances(rows);
            StampCategoryTreeShapes(rows);
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

        // Inserts the Default label when no achievement currently falls into it, at its
        // persisted custom-order position when one exists, otherwise at the end.
        private static void EnsureDefaultCategoryLabel(List<string> orderedLabels, IReadOnlyList<string> categoryOrder)
        {
            if (orderedLabels == null ||
                orderedLabels.Contains(AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            var insertIndex = orderedLabels.Count;
            var defaultOrderIndex = AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndex(
                AchievementCategoryTypeHelper.DefaultCategoryLabel,
                categoryOrder);
            if (defaultOrderIndex != int.MaxValue)
            {
                // Ordered labels precede unordered ones (index int.MaxValue), so the first
                // label ranked after Default marks the insertion point.
                for (var i = 0; i < orderedLabels.Count; i++)
                {
                    if (AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndex(orderedLabels[i], categoryOrder) > defaultOrderIndex)
                    {
                        insertIndex = i;
                        break;
                    }
                }
            }

            // Land on a root boundary. The ranked position can fall between a parent and its
            // children, and Default is a root - dropped there it would split a subtree, which reads
            // as an unrelated row wearing that subtree's connectors.
            while (insertIndex < orderedLabels.Count &&
                   CategoryPathHelper.GetDepth(orderedLabels[insertIndex]) > 1)
            {
                insertIndex++;
            }

            orderedLabels.Insert(insertIndex, AchievementCategoryTypeHelper.DefaultCategoryLabel);
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
                var imageOverrides = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);

                foreach (var row in CategoryRows.Where(row => row != null))
                {
                    var category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.CategoryLabel);
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
                    affectsSummaryData: MarkLibraryRefreshDeferred(SummaryArtChanged(
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
        /// The label-keyed metadata a merge produces. Computed rather than written so the caller
        /// can land it together with the membership rewrite in a single store update.
        /// </summary>
        private CategoryMetadataPlan PlanCategoryMergeMetadata(string sourceCategory, string targetCategory)
        {
            var normalizedSource = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(sourceCategory);
            var normalizedTarget = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetCategory);
            if (string.IsNullOrWhiteSpace(normalizedSource) ||
                string.IsNullOrWhiteSpace(normalizedTarget) ||
                string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return CurrentCategoryMetadataPlan();
            }

            var currentOrder = GameCustomDataLookup.GetAchievementCategoryOrder(_gameId, _settings?.Persisted);
            var currentImages = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted);

            // Collapse the source's order slot onto the target's existing position (dedupe).
            var nextOrder = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in currentOrder ?? Enumerable.Empty<string>())
            {
                var normalized = CategoryPathHelper.NormalizePath(label);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                // The whole subtree collapses onto the target, so a descendant of the source
                // folds too rather than being left pointing at a label that no longer exists.
                if (CategoryPathHelper.IsSelfOrDescendantOf(normalized, normalizedSource))
                {
                    normalized = normalizedTarget;
                }

                if (seen.Add(normalized))
                {
                    nextOrder.Add(normalized);
                }
            }

            // Drop the merged-away subtree's art overrides. Unlike the rename path, a merge does not
            // fold the source's art into the target: the target is left exactly as it was.
            var nextImages = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in currentImages ?? new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase))
            {
                var key = CategoryPathHelper.NormalizePath(pair.Key);
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
                {
                    continue;
                }

                // A merge folds the whole subtree, so a descendant's art goes with it rather than
                // being stranded under a label nothing points at any more.
                if (CategoryPathHelper.IsSelfOrDescendantOf(key, normalizedSource))
                {
                    continue;
                }

                nextImages[key] = pair.Value.Clone();
            }

            // Reset the game-summary-art selection when the merged-away source held it; leave any
            // other selection (including the target's) untouched.
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            if (summaryCategory != null &&
                CategoryPathHelper.IsSelfOrDescendantOf(summaryCategory.Label, normalizedSource))
            {
                summaryCategory = null;
            }

            return new CategoryMetadataPlan
            {
                Order = nextOrder,
                Images = nextImages,
                SummaryCategory = summaryCategory
            };
        }

        /// <summary>
        /// Whether a metadata write can move the game's summary art, which is the only thing this
        /// tab edits that a library-wide surface reads. GameSummaryArtResolver looks at exactly two
        /// things: which category is selected, and that one category's art override.
        /// Order, art on any other category, and which achievement sits in which category are all
        /// per-game display state, so a write that leaves this tuple alone is scoped out of the
        /// summary and projection passes.
        /// </summary>
        private static bool SummaryArtChanged(
            GameSummaryCategoryData before,
            IReadOnlyDictionary<string, CategoryImageOverrideData> beforeImages,
            GameSummaryCategoryData after,
            IReadOnlyDictionary<string, CategoryImageOverrideData> afterImages)
        {
            if (before == null || after == null)
            {
                return !ReferenceEquals(before, after);
            }

            if (!string.Equals(before.Label, after.Label, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(before.ProviderLabel, after.ProviderLabel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A move can fold the source category's art onto an unchanged target label, so the
            // selection matching is not on its own enough to call the art unchanged.
            return !string.Equals(
                ResolveCategoryArtOverride(beforeImages, before.Label),
                ResolveCategoryArtOverride(afterImages, after.Label),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Records whether a write skipped the library-wide passes, and passes the flag straight
        /// back so it can wrap the argument at the call site. A write that did fan out clears the
        /// debt rather than adding to it: it has already brought the library surfaces up to date.
        /// </summary>
        private bool MarkLibraryRefreshDeferred(bool affectsSummaryData)
        {
            _hasDeferredLibraryRefresh = !affectsSummaryData;
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

        private static string ResolveCategoryArtOverride(
            IReadOnlyDictionary<string, CategoryImageOverrideData> images,
            string categoryLabel)
        {
            CategoryImageOverrideData imageOverride = null;
            if (images != null && !string.IsNullOrWhiteSpace(categoryLabel))
            {
                images.TryGetValue(categoryLabel, out imageOverride);
            }

            return (imageOverride?.Art ?? string.Empty).Trim();
        }

        /// <summary>The stored metadata as it stands, for a plan that turns out to be a no-op.</summary>
        private CategoryMetadataPlan CurrentCategoryMetadataPlan()
        {
            return new CategoryMetadataPlan
            {
                Order = GameCustomDataLookup.GetAchievementCategoryOrder(_gameId, _settings?.Persisted)?.ToList(),
                Images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted)
                    ?.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone(), StringComparer.OrdinalIgnoreCase),
                SummaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted)
            };
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
                var candidate = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(selector?.Invoke(item));
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (category == null)
                {
                    category = candidate;
                }
                else if (!string.Equals(category, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return category;
        }

    }
}
