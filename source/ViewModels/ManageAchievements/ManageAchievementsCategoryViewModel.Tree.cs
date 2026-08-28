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

            var normalizedTargetCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedTargetCategory) ||
                string.Equals(normalizedSourceCategory, normalizedTargetCategory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            if (!ReassignEffectiveCategoryRows(
                    normalizedSourceCategory,
                    normalizedTargetCategory,
                    categoryOverrideMap,
                    categoryTypeOverrideMap: null,
                    targetGroupTypes: null))
            {
                return false;
            }

            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            RenameCategoryMetadata(normalizedSourceCategory, normalizedTargetCategory);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            RefreshCategoryRows();
            return true;
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

            var sourceCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.CategoryLabel);
            var targetCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                row.GetNormalizedRenameOverrideValue() ?? row.ProviderCategoryLabel);
            if (string.IsNullOrWhiteSpace(sourceCategory) ||
                string.IsNullOrWhiteSpace(targetCategory) ||
                string.Equals(sourceCategory, targetCategory, StringComparison.OrdinalIgnoreCase))
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

            var orderedLabels = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                _definitionOrderedRows.Count > 0 ? _definitionOrderedRows : _allRows,
                row => row?.Category,
                categoryOrder);
            EnsureDefaultCategoryLabel(orderedLabels, categoryOrder);
            var fileStems = AchievementIconCachePathBuilder.BuildCategoryFileStems(orderedLabels);
            var rows = new List<ManageAchievementsCategoryMetadataItem>();

            foreach (var label in orderedLabels)
            {
                // The Default row is kept even with no achievements in it so its art can
                // be set and selected for game summaries ahead of time.
                if ((!groups.TryGetValue(label, out var bucket) || bucket.Count == 0) &&
                    !string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!fileStems.TryGetValue(label, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                CategoryImageOverrideData imageOverride = null;
                categoryImages?.TryGetValue(label, out imageOverride);
                var providerCategoryLabel = ResolveSharedCategory(bucket, item => item?.ProviderCategory) ?? label;
                rows.Add(ManageAchievementsCategoryMetadataItem.Create(
                    label,
                    providerCategoryLabel,
                    bucket,
                    imageOverride,
                    _gameIdText,
                    fileStem,
                    _managedCustomIconService,
                    isSummarySelected: summaryCategory != null &&
                        string.Equals(summaryCategory.Label, label, StringComparison.OrdinalIgnoreCase),
                    artFallbackSource: _allRows));
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
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(label);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (string.Equals(normalized, normalizedSource, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalizedTarget;
                }

                if (seen.Add(normalized))
                {
                    nextOrder.Add(normalized);
                }
            }

            // Drop the source's per-category art override. Unlike the rename path, a merge does not
            // fold the source's art into the target: the target is left exactly as it was.
            var nextImages = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in currentImages ?? new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase))
            {
                var key = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
                {
                    continue;
                }

                if (string.Equals(key, normalizedSource, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                nextImages[key] = pair.Value.Clone();
            }

            // Reset the game-summary-art selection when the merged-away source held it; leave any
            // other selection (including the target's) untouched.
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            if (summaryCategory != null &&
                string.Equals(summaryCategory.Label, normalizedSource, StringComparison.OrdinalIgnoreCase))
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
