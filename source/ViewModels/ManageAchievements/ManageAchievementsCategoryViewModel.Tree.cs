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
            _achievementOverridesService.SetAchievementCategoryMetadata(_gameId, Array.Empty<string>(), images, summaryCategory);
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

            CollectionHelper.SynchronizeCollection(CategoryRows, reordered);
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

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var rowsChanged = ReassignEffectiveCategoryRows(
                normalizedSourceCategory,
                normalizedTargetCategory,
                categoryOverrideMap,
                categoryTypeOverrideMap: null,
                targetGroupTypes: null,
                rewriteDescendantPaths: true);

            if (rowsChanged)
            {
                var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
                PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
                ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            }

            // Metadata moves even when no achievement did: an intermediate node can exist purely
            // as ordering and art, and it still has to follow its subtree.
            RenameCategoryMetadata(normalizedSourceCategory, normalizedTargetCategory);
            RefreshCategoryRows();
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
        /// Moves a category under a new parent, keeping its leaf name. A null parent makes it a root.
        /// </summary>
        public bool ReparentCategory(string sourcePath, string newParentPath)
        {
            return RenameCategoryLabel(sourcePath, CategoryPathHelper.Reparent(sourcePath, newParentPath));
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

            // Deepest first so moving one row cannot invalidate another's resolved parent, and
            // dragged descendants are skipped because their ancestor carries them along.
            var targets = labels
                .Select(CategoryPathHelper.NormalizePath)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            targets = targets
                .Where(label => !targets.Any(other => CategoryPathHelper.IsDescendantOf(label, other)))
                .OrderByDescending(CategoryPathHelper.GetDepth)
                .ToList();

            var changed = false;
            foreach (var label in targets)
            {
                var parent = indent ? ResolveIndentParent(label) : CategoryPathHelper.GetParentPath(
                    CategoryPathHelper.GetParentPath(label) ?? label);

                if (indent && parent == null)
                {
                    continue;
                }

                if (!indent && CategoryPathHelper.GetDepth(label) <= 1)
                {
                    continue;
                }

                if (ReparentCategory(label, parent))
                {
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// The row directly above <paramref name="label"/> that shares its parent. Null when the
        /// row is already first among its siblings, since there is nothing to nest under.
        /// </summary>
        private string ResolveIndentParent(string label)
        {
            var parent = CategoryPathHelper.GetParentPath(label);
            string previousSibling = null;

            foreach (var row in CategoryRows)
            {
                var candidate = CategoryPathHelper.NormalizePath(row?.CategoryLabel);
                if (CategoryPathHelper.IsSame(candidate, label))
                {
                    break;
                }

                if (string.Equals(
                        CategoryPathHelper.GetParentPath(candidate) ?? string.Empty,
                        parent ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase) &&
                    !row.IsDefaultCategory)
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
            if (!ReassignEffectiveCategoryRows(
                    normalizedSourceCategory,
                    normalizedTargetCategory,
                    categoryOverrideMap,
                    categoryTypeOverrideMap,
                    targetGroupTypes))
            {
                return false;
            }

            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            MergeCategoryMetadata(normalizedSourceCategory, normalizedTargetCategory);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
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

        private void ReplaceCategoryRows(IEnumerable<ManageAchievementsCategoryMetadataItem> rows)
        {
            foreach (var row in CategoryRows)
            {
                row.PropertyChanged -= CategoryMetadataRow_PropertyChanged;
            }

            CategoryRows.Clear();
            foreach (var row in rows ?? Enumerable.Empty<ManageAchievementsCategoryMetadataItem>())
            {
                row.PropertyChanged += CategoryMetadataRow_PropertyChanged;
                CategoryRows.Add(row);
            }

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
            // writer - which rebuilds from the rendered rows - would drop that state. Deliberately
            // not seeded from the order list: a stale entry there would surface as a phantom row.
            sourceLabels.AddRange(categoryImages?.Keys ?? Enumerable.Empty<string>());
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

                CategoryImageOverrideData imageOverride = null;
                categoryImages?.TryGetValue(label, out imageOverride);
                var providerCategoryLabel = ResolveSharedCategory(bucket, item => item?.ProviderCategory) ?? label;
                var row = ManageAchievementsCategoryMetadataItem.Create(
                    label,
                    providerCategoryLabel,
                    bucket,
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

            HasCustomCategoryNames = rows.Any(row =>
                !string.Equals(row.CategoryLabel, row.ProviderCategoryLabel, StringComparison.OrdinalIgnoreCase));
            ReplaceCategoryRows(rows);
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

                _achievementOverridesService.SetAchievementCategoryMetadata(
                    _gameId,
                    categoryOrder,
                    imageOverrides,
                    summaryCategory);
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

        private void RenameCategoryMetadata(string sourceCategory, string targetCategory)
        {
            var renamed = CategoryMetadataRenamer.Rename(
                _gameId,
                sourceCategory,
                targetCategory,
                _achievementOverridesService,
                _settings?.Persisted);

            if (renamed)
            {
                RaiseCategoryMetadataPersisted();
            }
        }

        private void MergeCategoryMetadata(string sourceCategory, string targetCategory)
        {
            var normalizedSource = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(sourceCategory);
            var normalizedTarget = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetCategory);
            if (string.IsNullOrWhiteSpace(normalizedSource) ||
                string.IsNullOrWhiteSpace(normalizedTarget) ||
                string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return;
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

            _achievementOverridesService.SetAchievementCategoryMetadata(_gameId, nextOrder, nextImages, summaryCategory);
            RaiseCategoryMetadataPersisted();
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
