using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    public sealed class ManageAchievementsCategoryViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private List<ManageAchievementsCategoryItem> _allRows = new List<ManageAchievementsCategoryItem>();
        private List<string> _canonicalCategoryLabelFilterOptions = new List<string>();
        private bool _hasAchievements;
        private string _searchText = string.Empty;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _showHidden = true;
        private readonly HashSet<string> _selectedCategoryLabelFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _hasCustomOverrides;

        public ManageAchievementsCategoryViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameDataSnapshotProvider = gameDataSnapshotProvider ?? throw new ArgumentNullException(nameof(gameDataSnapshotProvider));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            AchievementRows = new ObservableCollection<ManageAchievementsCategoryItem>();
            CategoryLabelOptions = new ObservableCollection<string>();
            CategoryLabelFilterOptions = new ObservableCollection<string>();
            TypeSelectionOptions = CreateCategoryTypeOptions(() =>
            {
                OnPropertyChanged(nameof(SelectedTypeSelectionText));
            });
            TypeFilterOptions = CreateCategoryTypeOptions(() =>
            {
                OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                ApplyFilter();
            });
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);

            ReloadData();
        }

        public ObservableCollection<ManageAchievementsCategoryItem> AchievementRows { get; }
        public ObservableCollection<string> CategoryLabelOptions { get; }
        public ObservableCollection<string> CategoryLabelFilterOptions { get; }
        public ObservableCollection<CategoryTypeSelectionOption> TypeSelectionOptions { get; }
        public ObservableCollection<CategoryTypeSelectionOption> TypeFilterOptions { get; }

        public RelayCommand ClearSearchCommand { get; }
        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public bool ShowUnlocked
        {
            get => _showUnlocked;
            set
            {
                if (SetValueAndReturn(ref _showUnlocked, value))
                {
                    ApplyFilter();
                }
            }
        }

        public bool ShowLocked
        {
            get => _showLocked;
            set
            {
                if (SetValueAndReturn(ref _showLocked, value))
                {
                    ApplyFilter();
                }
            }
        }

        public bool ShowHidden
        {
            get => _showHidden;
            set
            {
                if (SetValueAndReturn(ref _showHidden, value))
                {
                    ApplyFilter();
                }
            }
        }

        public string SelectedTypeSelectionText
        {
            get
            {
                var selected = GetSelectedCategoryTypeValues(TypeSelectionOptions);
                return selected.Count == 0
                    ? L("LOCPlayAch_Common_Label_Type", "Type")
                    : AchievementCategoryTypeHelper.ToDisplayText(selected);
            }
        }

        public string SelectedCategoryTypeFilterText
        {
            get
            {
                var selected = GetSelectedCategoryTypeFilterValues();
                return selected.Count == 0
                    ? L("LOCPlayAch_Common_Label_Type", "Type")
                    : AchievementCategoryTypeHelper.ToDisplayText(selected);
            }
        }

        public bool HasCustomOverrides
        {
            get => _hasCustomOverrides;
            private set => SetValue(ref _hasCustomOverrides, value);
        }

        public string SelectedCategoryLabelFilterText
        {
            get
            {
                if (_selectedCategoryLabelFilters.Count == 0)
                {
                    return L(
                        "LOCPlayAch_Common_Label_Category",
                        "Category");
                }

                var ordered = CategoryLabelFilterOptions
                    .Where(label => _selectedCategoryLabelFilters.Contains(label))
                    .ToList();
                if (ordered.Count == 0)
                {
                    ordered = _selectedCategoryLabelFilters
                        .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                return string.Join(", ", ordered.Select(AchievementCategoryTypeHelper.ToCategoryLabelDisplayText));
            }
        }

        public bool IsCategoryLabelFilterSelected(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return _selectedCategoryLabelFilters.Contains(value.Trim());
        }

        public void SetCategoryLabelFilterSelected(string value, bool isSelected)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim();
            var changed = isSelected
                ? _selectedCategoryLabelFilters.Add(normalized)
                : _selectedCategoryLabelFilters.Remove(normalized);
            if (!changed)
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
            ApplyFilter();
        }

        public void ReloadData()
        {
            try
            {
                var selectedApiNames = new HashSet<string>(
                    AchievementRows
                        .Where(row => row != null && row.IsSelected && !string.IsNullOrWhiteSpace(row.ApiName))
                        .Select(row => row.ApiName.Trim()),
                    StringComparer.OrdinalIgnoreCase);
                var revealedStateByApiName = AchievementRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                    .GroupBy(row => row.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().IsRevealed, StringComparer.OrdinalIgnoreCase);

                var hydratedGameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var rawGameData = _gameDataSnapshotProvider.GetRawGameData();
                var rawAchievements = rawGameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();
                var categoryOverrides = GetCurrentCategoryOverrideMap();
                var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap();
                HasCustomOverrides = categoryOverrides.Count > 0 || categoryTypeOverrides.Count > 0;
                var projectionSource = hydratedGameData ?? rawGameData;
                List<AchievementDetail> orderedAchievements;
                List<AchievementDetail> canonicalAchievements;
                if (hydratedGameData?.AchievementOrder != null && hydratedGameData.AchievementOrder.Count > 0)
                {
                    orderedAchievements = AchievementOrderHelper.ApplyOrder(
                        rawAchievements,
                        a => a.ApiName,
                        hydratedGameData.AchievementOrder);
                    canonicalAchievements = orderedAchievements;
                }
                else
                {
                    orderedAchievements = rawAchievements
                        .OrderBy(a => a.DisplayName ?? a.ApiName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    canonicalAchievements = rawAchievements;
                }

                _canonicalCategoryLabelFilterOptions = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                    canonicalAchievements,
                    achievement => ResolveEffectiveCategoryLabel(achievement, categoryOverrides));

                _allRows = orderedAchievements.Select(a =>
                {
                    var apiName = (a.ApiName ?? string.Empty).Trim();
                    var providerCategory = NormalizeCategory(a.Category);
                    var providerCategoryType = AchievementCategoryTypeHelper.Normalize(a.CategoryType);

                    string overrideCategory = null;
                    var hasCategoryOverride = !string.IsNullOrWhiteSpace(apiName) &&
                                              categoryOverrides.TryGetValue(apiName, out overrideCategory) &&
                                              !string.IsNullOrWhiteSpace(overrideCategory);
                    string overrideCategoryType = null;
                    var hasCategoryTypeOverride = !string.IsNullOrWhiteSpace(apiName) &&
                                                  categoryTypeOverrides.TryGetValue(apiName, out overrideCategoryType) &&
                                                  !string.IsNullOrWhiteSpace(overrideCategoryType);

                    var effectiveCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                        hasCategoryOverride ? overrideCategory : providerCategory);
                    var effectiveCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(
                        hasCategoryTypeOverride ? overrideCategoryType : providerCategoryType);

                    var projected = AchievementDisplayItem.Create(
                        projectionSource,
                        a,
                        _settings,
                        playniteGameIdOverride: _gameId);
                    if (projected == null)
                    {
                        return null;
                    }

                    return new ManageAchievementsCategoryItem
                    {
                        ProviderKey = projected.ProviderKey,
                        GameName = projected.GameName,
                        SortingName = projected.SortingName,
                        PlayniteGameId = projected.PlayniteGameId,
                        ApiName = apiName,
                        DisplayName = projected.DisplayName,
                        Description = projected.Description,
                        UnlockedIconPath = projected.UnlockedIconPath,
                        LockedIconPath = projected.LockedIconPath,
                        UnlockTimeUtc = projected.UnlockTimeUtc,
                        GlobalPercentUnlocked = projected.GlobalPercentUnlocked,
                        PointsValue = projected.PointsValue,
                        ProgressNum = projected.ProgressNum,
                        ProgressDenom = projected.ProgressDenom,
                        TrophyType = projected.TrophyType,
                        Unlocked = projected.Unlocked,
                        Hidden = projected.Hidden,
                        ShowHiddenIcon = projected.ShowHiddenIcon,
                        ShowHiddenTitle = projected.ShowHiddenTitle,
                        ShowHiddenDescription = projected.ShowHiddenDescription,
                        ShowRarityGlow = projected.ShowRarityGlow,
                        ShowRarityBar = projected.ShowRarityBar,
                        ShowHiddenSuffix = projected.ShowHiddenSuffix,
                        ShowLockedIcon = projected.ShowLockedIcon,
                        UseSeparateLockedIconsWhenAvailable = projected.UseSeparateLockedIconsWhenAvailable,
                        IsRevealed = revealedStateByApiName.TryGetValue(apiName, out var isRevealed)
                            ? isRevealed
                            : projected.IsRevealed,
                        Category = effectiveCategory,
                        CategoryType = effectiveCategoryType,
                        IsSelected = selectedApiNames.Contains(apiName)
                    };
                })
                .Where(a => a != null)
                .ToList();

                HasAchievements = _allRows.Count > 0;
                ApplyFilter();
                RefreshCategoryLabelOptions();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading category rows for gameId={_gameId}");
                _allRows = new List<ManageAchievementsCategoryItem>();
                _canonicalCategoryLabelFilterOptions = new List<string>();
                CollectionHelper.SynchronizeCollection(AchievementRows, _allRows);
                CollectionHelper.SynchronizeCollection(CategoryLabelOptions, new List<string>());
                CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, new List<string>());
                _selectedCategoryLabelFilters.Clear();
                OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
                HasAchievements = false;
                HasCustomOverrides = false;
            }
        }

        public bool ResetCategoryOverrides()
        {
            var categoryOverrides = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap();
            if (categoryOverrides.Count == 0 && categoryTypeOverrides.Count == 0)
            {
                return false;
            }

            _achievementOverridesService.SetAchievementCategoryOverrides(
                _gameId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            _achievementOverridesService.SetAchievementCategoryTypeOverrides(
                _gameId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            ReloadData();
            return true;
        }

        public bool ApplyBulkToSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            string categoryText)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(categoryText);
            var normalizedCategoryType = AchievementCategoryTypeHelper.Normalize(GetSelectedCategoryTypeValue());
            var hasCategoryInput = !string.IsNullOrWhiteSpace(normalizedCategory);
            var hasCategoryTypeInput = !string.IsNullOrWhiteSpace(normalizedCategoryType);
            var selectedCategoryTypes = hasCategoryTypeInput
                ? AchievementCategoryTypeHelper.ParseValues(normalizedCategoryType)
                : new List<string>();
            if (!hasCategoryInput && !hasCategoryTypeInput)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var categoryChanged = false;
            var categoryTypeChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (hasCategoryInput &&
                    (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                     !string.Equals(existingCategory, normalizedCategory, StringComparison.Ordinal)))
                {
                    categoryOverrideMap[apiName] = normalizedCategory;
                    categoryChanged = true;
                }

                if (hasCategoryTypeInput &&
                    selectedCategoryTypes.Count > 0)
                {
                    var currentEffectiveCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType);
                    var mergedCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(
                        AchievementCategoryTypeHelper.Combine(
                            AchievementCategoryTypeHelper.ParseValues(currentEffectiveCategoryType)
                                .Concat(selectedCategoryTypes)));

                    if (!string.Equals(mergedCategoryType, currentEffectiveCategoryType, StringComparison.Ordinal) &&
                        (!categoryTypeOverrideMap.TryGetValue(apiName, out var existingCategoryType) ||
                         !string.Equals(existingCategoryType, mergedCategoryType, StringComparison.Ordinal)))
                    {
                        categoryTypeOverrideMap[apiName] = mergedCategoryType;
                        categoryTypeChanged = true;
                    }
                }
            }

            if (categoryChanged)
            {
                _achievementOverridesService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            }

            if (categoryTypeChanged)
            {
                _achievementOverridesService.SetAchievementCategoryTypeOverrides(_gameId, categoryTypeOverrideMap);
            }

            if (categoryChanged || categoryTypeChanged)
            {
                ReloadData();
            }

            return true;
        }

        public bool AddCategoryTypesToSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            IEnumerable<string> categoryTypesToAdd)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedTypes = AchievementCategoryTypeHelper.Normalize(
                AchievementCategoryTypeHelper.Combine(categoryTypesToAdd));
            var selectedCategoryTypes = AchievementCategoryTypeHelper.ParseValues(normalizedTypes);
            if (selectedCategoryTypes.Count == 0)
            {
                return false;
            }

            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var categoryTypeChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var currentEffectiveCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType);
                var mergedCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(
                    AchievementCategoryTypeHelper.Combine(
                        AchievementCategoryTypeHelper.ParseValues(currentEffectiveCategoryType)
                            .Concat(selectedCategoryTypes)));

                if (!string.Equals(mergedCategoryType, currentEffectiveCategoryType, StringComparison.Ordinal) &&
                    (!categoryTypeOverrideMap.TryGetValue(apiName, out var existingCategoryType) ||
                     !string.Equals(existingCategoryType, mergedCategoryType, StringComparison.Ordinal)))
                {
                    categoryTypeOverrideMap[apiName] = mergedCategoryType;
                    categoryTypeChanged = true;
                }
            }

            if (!categoryTypeChanged)
            {
                return false;
            }

            _achievementOverridesService.SetAchievementCategoryTypeOverrides(_gameId, categoryTypeOverrideMap);
            ReloadData();
            return true;
        }

        public bool SetCategoryLabelForSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            string categoryLabel)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(categoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedCategory))
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                    !string.Equals(existingCategory, normalizedCategory, StringComparison.Ordinal))
                {
                    categoryOverrideMap[apiName] = normalizedCategory;
                    categoryChanged = true;
                }
            }

            if (!categoryChanged)
            {
                return false;
            }

            _achievementOverridesService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            ReloadData();
            return true;
        }

        public bool ClearSelectionOverrides(IReadOnlyList<ManageAchievementsCategoryItem> selectedRows)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var categoryChanged = false;
            var categoryTypeChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (categoryOverrideMap.Remove(apiName))
                {
                    categoryChanged = true;
                }

                if (categoryTypeOverrideMap.Remove(apiName))
                {
                    categoryTypeChanged = true;
                }
            }

            if (!categoryChanged && !categoryTypeChanged)
            {
                return false;
            }

            if (categoryChanged)
            {
                _achievementOverridesService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            }

            if (categoryTypeChanged)
            {
                _achievementOverridesService.SetAchievementCategoryTypeOverrides(_gameId, categoryTypeOverrideMap);
            }

            ReloadData();
            return true;
        }

        public bool RenameCategoryLabel(string sourceCategoryLabel, string targetCategoryLabel)
        {
            var normalizedSourceCategory = AchievementCategoryTypeHelper.NormalizeCategory(sourceCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedSourceCategory))
            {
                return false;
            }

            if (string.Equals(
                normalizedSourceCategory,
                AchievementCategoryTypeHelper.DefaultCategoryLabel,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalizedTargetCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetCategoryLabel);
            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var changed = false;
            var affectedCount = 0;

            foreach (var item in _allRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var effectiveCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.Category);
                if (!string.Equals(effectiveCategory, normalizedSourceCategory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                affectedCount++;
                if (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                    !string.Equals(existingCategory, normalizedTargetCategory, StringComparison.Ordinal))
                {
                    categoryOverrideMap[apiName] = normalizedTargetCategory;
                    changed = true;
                }
            }

            if (affectedCount == 0)
            {
                return false;
            }

            if (!changed)
            {
                return false;
            }

            _achievementOverridesService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            ReloadData();
            return true;
        }

        public void ResetBulkEditorInputs()
        {
            SetCategoryTypeSelections(TypeSelectionOptions, false);
        }

        public void ClearAllSelections()
        {
            foreach (var item in _allRows.Where(row => row != null))
            {
                item.IsSelected = false;
            }
        }

        public List<ManageAchievementsCategoryItem> GetAllSelectedRows()
        {
            return _allRows
                .Where(item => item != null && item.IsSelected)
                .ToList();
        }

        private void ApplyFilter()
        {
            var filtered = _allRows.AsEnumerable();

            if (!ShowHidden)
            {
                filtered = filtered.Where(a => !(a.Hidden && !a.Unlocked));
            }

            filtered = filtered.Where(a => a.Unlocked ? ShowUnlocked : ShowLocked);

            if (!string.IsNullOrEmpty(SearchText))
            {
                filtered = filtered.Where(a =>
                    (a.DisplayName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Description?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.ApiName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Category?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.CategoryTypeDisplay?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var selectedTypeFilters = GetSelectedCategoryTypeFilterValues();
            if (selectedTypeFilters.Count > 0)
            {
                var selectedTypeSet = new HashSet<string>(selectedTypeFilters, StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(a =>
                {
                    var rowTypes = AchievementCategoryTypeHelper.ParseValues(
                        AchievementCategoryTypeHelper.NormalizeOrDefault(a.CategoryType));
                    if (rowTypes.Count == 0)
                    {
                        return false;
                    }

                    // OR semantics: keep rows containing at least one selected type.
                    return rowTypes.Any(selectedTypeSet.Contains);
                });
            }

            if (_selectedCategoryLabelFilters.Count > 0)
            {
                var selectedCategorySet = new HashSet<string>(
                    _selectedCategoryLabelFilters
                        .Select(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(a =>
                    selectedCategorySet.Contains(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.Category)));
            }

            CollectionHelper.SynchronizeCollection(AchievementRows, filtered.ToList());
        }

        private void RefreshCategoryLabelOptions()
        {
            var labels = _allRows
                .Where(row => row != null)
                .Select(row => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var renameableLabels = labels
                .Where(label => !string.Equals(
                    label,
                    AchievementCategoryTypeHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            CollectionHelper.SynchronizeCollection(CategoryLabelOptions, renameableLabels);

            var labelSet = new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase);
            var categoryLabelFilterOptions = (_canonicalCategoryLabelFilterOptions ?? new List<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label) && labelSet.Contains(label))
                .ToList();
            foreach (var label in labels)
            {
                if (!categoryLabelFilterOptions.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    categoryLabelFilterOptions.Add(label);
                }
            }

            CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, categoryLabelFilterOptions);

            if (PruneCategoryLabelFilterSelections(categoryLabelFilterOptions))
            {
                ApplyFilter();
            }

            OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
        }

        private Dictionary<string, string> GetCurrentCategoryOverrideMap()
        {
            var map = GameCustomDataLookup.GetAchievementCategoryOverrides(_gameId, _settings?.Persisted);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var category = NormalizeCategory(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                normalized[apiName] = category;
            }

            return normalized;
        }

        private Dictionary<string, string> GetCurrentCategoryTypeOverrideMap()
        {
            var map = GameCustomDataLookup.GetAchievementCategoryTypeOverrides(_gameId, _settings?.Persisted);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(categoryType))
                {
                    continue;
                }

                normalized[apiName] = categoryType;
            }

            return normalized;
        }

        private static string NormalizeCategory(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string ResolveEffectiveCategoryLabel(
            AchievementDetail achievement,
            IReadOnlyDictionary<string, string> categoryOverrides)
        {
            var apiName = (achievement?.ApiName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(apiName) &&
                categoryOverrides != null &&
                categoryOverrides.TryGetValue(apiName, out var categoryOverride) &&
                !string.IsNullOrWhiteSpace(categoryOverride))
            {
                return categoryOverride;
            }

            return achievement?.Category;
        }

        private bool PruneCategoryLabelFilterSelections(IEnumerable<string> options)
        {
            var optionSet = new HashSet<string>(
                (options ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return _selectedCategoryLabelFilters.RemoveWhere(value => !optionSet.Contains(value)) > 0;
        }

        private List<string> GetSelectedCategoryTypeFilterValues()
        {
            return GetSelectedCategoryTypeValues(TypeFilterOptions);
        }

        private string GetSelectedCategoryTypeValue()
        {
            return AchievementCategoryTypeHelper.Combine(GetSelectedCategoryTypeValues(TypeSelectionOptions));
        }

        private static List<string> GetSelectedCategoryTypeValues(IEnumerable<CategoryTypeSelectionOption> options)
        {
            return (options ?? Enumerable.Empty<CategoryTypeSelectionOption>())
                .Where(option => option?.IsSelected == true)
                .Select(option => option.Value)
                .ToList();
        }

        private static void SetCategoryTypeSelections(IEnumerable<CategoryTypeSelectionOption> options, bool isSelected)
        {
            foreach (var option in options ?? Enumerable.Empty<CategoryTypeSelectionOption>())
            {
                if (option != null)
                {
                    option.IsSelected = isSelected;
                }
            }
        }

        private ObservableCollection<CategoryTypeSelectionOption> CreateCategoryTypeOptions(Action onSelectionChanged)
        {
            var options = new ObservableCollection<CategoryTypeSelectionOption>(
                AchievementCategoryTypeHelper.AllowedCategoryTypes
                    .Select(type => new CategoryTypeSelectionOption(type, GetCategoryTypeDisplayName(type))));

            foreach (var option in options)
            {
                option.PropertyChanged += (_, args) =>
                {
                    if (string.Equals(args?.PropertyName, nameof(CategoryTypeSelectionOption.IsSelected), StringComparison.Ordinal))
                    {
                        onSelectionChanged?.Invoke();
                    }
                };
            }

            return options;
        }

        public static string GetCategoryTypeDisplayName(string categoryType)
        {
            return AchievementCategoryTypeHelper.ToCategoryTypeDisplayText(categoryType);
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    public sealed class ManageAchievementsCategoryItem : AchievementDisplayItem
    {
        private bool _isSelected;

        public string Category
        {
            get => CategoryLabel;
            set
            {
                CategoryLabel = value;
                OnPropertyChanged(nameof(CategoryDisplay));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }

        public string CategoryDisplay => AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(Category);
    }

    public sealed class CategoryTypeSelectionOption : ObservableObject
    {
        private bool _isSelected;

        public CategoryTypeSelectionOption(string value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public string Value { get; }

        public string DisplayName { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }
    }
}
