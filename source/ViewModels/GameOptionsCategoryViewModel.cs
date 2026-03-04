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
    public sealed class GameOptionsCategoryViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementService _achievementService;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private List<GameOptionsCategoryItem> _allRows = new List<GameOptionsCategoryItem>();
        private bool _hasAchievements;
        private string _searchText = string.Empty;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _showHidden = true;
        private string _statusMessage = string.Empty;
        private bool _hasStatusMessage;
        private readonly string _allFilterOption;
        private string _selectedCategoryLabelFilter;
        private bool _hasCustomOverrides;
        private bool _typeBaseSelected;
        private bool _typeDlcSelected;
        private bool _typeSingleplayerSelected;
        private bool _typeMultiplayerSelected;
        private bool _typeFilterBaseSelected;
        private bool _typeFilterDlcSelected;
        private bool _typeFilterSingleplayerSelected;
        private bool _typeFilterMultiplayerSelected;

        public GameOptionsCategoryViewModel(
            Guid gameId,
            AchievementService achievementService,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            AchievementRows = new ObservableCollection<GameOptionsCategoryItem>();
            CategoryLabelOptions = new ObservableCollection<string>();
            CategoryLabelFilterOptions = new ObservableCollection<string>();
            _allFilterOption = L("LOCPlayAch_GameOptions_Category_Filter_All", "All");
            _selectedCategoryLabelFilter = _allFilterOption;
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);

            ReloadData();
        }

        public ObservableCollection<GameOptionsCategoryItem> AchievementRows { get; }
        public ObservableCollection<string> CategoryLabelOptions { get; }
        public ObservableCollection<string> CategoryLabelFilterOptions { get; }

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

        public bool TypeBaseSelected
        {
            get => _typeBaseSelected;
            set
            {
                if (SetValueAndReturn(ref _typeBaseSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                }
            }
        }

        public bool TypeDlcSelected
        {
            get => _typeDlcSelected;
            set
            {
                if (SetValueAndReturn(ref _typeDlcSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                }
            }
        }

        public bool TypeSingleplayerSelected
        {
            get => _typeSingleplayerSelected;
            set
            {
                if (SetValueAndReturn(ref _typeSingleplayerSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                }
            }
        }

        public bool TypeMultiplayerSelected
        {
            get => _typeMultiplayerSelected;
            set
            {
                if (SetValueAndReturn(ref _typeMultiplayerSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                }
            }
        }

        public string SelectedTypeSelectionText
        {
            get
            {
                var selected = AchievementCategoryTypeHelper.ParseValues(GetSelectedCategoryTypeValue());
                return selected.Count == 0
                    ? L("LOCPlayAch_GameOptions_Category_TypeSelectorPlaceholder", "Select category type(s)")
                    : string.Join(", ", selected);
            }
        }

        public bool TypeFilterBaseSelected
        {
            get => _typeFilterBaseSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterBaseSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
            }
        }

        public bool TypeFilterDlcSelected
        {
            get => _typeFilterDlcSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterDlcSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
            }
        }

        public bool TypeFilterSingleplayerSelected
        {
            get => _typeFilterSingleplayerSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterSingleplayerSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
            }
        }

        public bool TypeFilterMultiplayerSelected
        {
            get => _typeFilterMultiplayerSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterMultiplayerSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
            }
        }

        public string SelectedCategoryTypeFilterText
        {
            get
            {
                var selected = GetSelectedCategoryTypeFilterValues();
                return selected.Count == 0
                    ? L("LOCPlayAch_GameOptions_Category_Filter_Type_All", "All Types")
                    : string.Join(", ", selected);
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetValue(ref _statusMessage, value ?? string.Empty);
        }

        public bool HasStatusMessage
        {
            get => _hasStatusMessage;
            private set => SetValue(ref _hasStatusMessage, value);
        }

        public bool HasCustomOverrides
        {
            get => _hasCustomOverrides;
            private set => SetValue(ref _hasCustomOverrides, value);
        }

        public string SelectedCategoryLabelFilter
        {
            get => _selectedCategoryLabelFilter;
            set
            {
                if (SetValueAndReturn(ref _selectedCategoryLabelFilter, value ?? _allFilterOption))
                {
                    ApplyFilter();
                }
            }
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

                var hydratedGameData = _achievementService.GetGameAchievementData(_gameId);
                var rawGameData = _achievementService.GetRawGameAchievementData(_gameId);
                var rawAchievements = rawGameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();
                var categoryOverrides = GetCurrentCategoryOverrideMap();
                var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap();
                HasCustomOverrides = categoryOverrides.Count > 0 || categoryTypeOverrides.Count > 0;
                var projectionSource = hydratedGameData ?? rawGameData;
                var projectionOptions = AchievementProjectionService.CreateOptions(_settings, projectionSource);

                List<AchievementDetail> orderedAchievements;
                if (hydratedGameData?.AchievementOrder != null && hydratedGameData.AchievementOrder.Count > 0)
                {
                    orderedAchievements = AchievementOrderHelper.ApplyOrder(
                        rawAchievements,
                        a => a.ApiName,
                        hydratedGameData.AchievementOrder);
                }
                else
                {
                    orderedAchievements = rawAchievements
                        .OrderBy(a => a.DisplayName ?? a.ApiName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

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

                    var projected = AchievementProjectionService.CreateDisplayItem(
                        projectionSource,
                        a,
                        projectionOptions,
                        _gameId);
                    if (projected == null)
                    {
                        return null;
                    }

                    return new GameOptionsCategoryItem
                    {
                        GameName = projected.GameName,
                        SortingName = projected.SortingName,
                        PlayniteGameId = projected.PlayniteGameId,
                        ApiName = apiName,
                        DisplayName = projected.DisplayName,
                        Description = projected.Description,
                        IconPath = projected.IconPath,
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
                _allRows = new List<GameOptionsCategoryItem>();
                CollectionHelper.SynchronizeCollection(AchievementRows, _allRows);
                CollectionHelper.SynchronizeCollection(CategoryLabelOptions, new List<string>());
                CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, new List<string> { _allFilterOption });
                SelectedCategoryLabelFilter = _allFilterOption;
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

            _achievementService.SetAchievementCategoryOverrides(
                _gameId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            _achievementService.SetAchievementCategoryTypeOverrides(
                _gameId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            ReloadData();
            return true;
        }

        public bool ApplyBulkToSelection(
            IReadOnlyList<GameOptionsCategoryItem> selectedRows,
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
                StatusMessage = L(
                    "LOCPlayAch_GameOptions_Category_Bulk_Status_NoInput",
                    "Enter a category label and/or select at least one category type.");
                HasStatusMessage = true;
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

            if (!categoryChanged && !categoryTypeChanged)
            {
                return false;
            }

            if (categoryChanged)
            {
                _achievementService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            }

            if (categoryTypeChanged)
            {
                _achievementService.SetAchievementCategoryTypeOverrides(_gameId, categoryTypeOverrideMap);
            }

            StatusMessage = L(
                "LOCPlayAch_GameOptions_Category_Bulk_Status_Applied",
                "Category and category type applied to selected achievements.");
            HasStatusMessage = true;

            ReloadData();
            return true;
        }

        public bool AddCategoryTypesToSelection(
            IReadOnlyList<GameOptionsCategoryItem> selectedRows,
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
                StatusMessage = L(
                    "LOCPlayAch_GameOptions_Category_Bulk_Status_NoTypeInput",
                    "Select at least one category type.");
                HasStatusMessage = true;
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

            _achievementService.SetAchievementCategoryTypeOverrides(_gameId, categoryTypeOverrideMap);
            StatusMessage = L(
                "LOCPlayAch_GameOptions_Category_Context_Status_TypeApplied",
                "Category type added for selected achievements.");
            HasStatusMessage = true;

            ReloadData();
            return true;
        }

        public bool SetCategoryLabelForSelection(
            IReadOnlyList<GameOptionsCategoryItem> selectedRows,
            string categoryLabel)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(categoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedCategory))
            {
                StatusMessage = L(
                    "LOCPlayAch_GameOptions_Category_Bulk_Status_NoLabelInput",
                    "Enter a category label.");
                HasStatusMessage = true;
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

            _achievementService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            StatusMessage = L(
                "LOCPlayAch_GameOptions_Category_Context_Status_LabelApplied",
                "Category label applied to selected achievements.");
            HasStatusMessage = true;

            ReloadData();
            return true;
        }

        public bool ClearSelectionOverrides(IReadOnlyList<GameOptionsCategoryItem> selectedRows)
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
                _achievementService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            }

            if (categoryTypeChanged)
            {
                _achievementService.SetAchievementCategoryTypeOverrides(_gameId, categoryTypeOverrideMap);
            }

            StatusMessage = L(
                "LOCPlayAch_GameOptions_Category_Bulk_Status_Cleared",
                "Cleared category label and category type overrides for selected achievements.");
            HasStatusMessage = true;

            ReloadData();
            return true;
        }

        public bool RenameCategoryLabel(string sourceCategoryLabel, string targetCategoryLabel)
        {
            var normalizedSourceCategory = AchievementCategoryTypeHelper.NormalizeCategory(sourceCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedSourceCategory))
            {
                StatusMessage = L(
                    "LOCPlayAch_GameOptions_Category_Rename_Status_SelectSource",
                    "Select a category label to rename.");
                HasStatusMessage = true;
                return false;
            }

            if (string.Equals(
                normalizedSourceCategory,
                AchievementCategoryTypeHelper.DefaultCategoryLabel,
                StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = L(
                    "LOCPlayAch_GameOptions_Category_Rename_Status_DefaultBlocked",
                    "Default category label cannot be renamed.");
                HasStatusMessage = true;
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
                StatusMessage = L(
                    "LOCPlayAch_GameOptions_Category_Rename_Status_NoMatches",
                    "No achievements match the selected category label.");
                HasStatusMessage = true;
                return false;
            }

            if (!changed)
            {
                StatusMessage = L(
                    "LOCPlayAch_GameOptions_Category_Rename_Status_NoChange",
                    "Selected label is already using the requested name.");
                HasStatusMessage = true;
                return false;
            }

            _achievementService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            StatusMessage = string.Format(
                L(
                    "LOCPlayAch_GameOptions_Category_Rename_Status_Applied",
                    "Renamed category label for {0} achievement(s)."),
                affectedCount);
            HasStatusMessage = true;

            ReloadData();
            return true;
        }

        public void ResetBulkEditorInputs()
        {
            TypeBaseSelected = false;
            TypeDlcSelected = false;
            TypeSingleplayerSelected = false;
            TypeMultiplayerSelected = false;
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

            if (!IsAllFilterOption(SelectedCategoryLabelFilter))
            {
                filtered = filtered.Where(a =>
                    string.Equals(
                        a.CategoryDisplay,
                        SelectedCategoryLabelFilter,
                        StringComparison.OrdinalIgnoreCase));
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

            var categoryLabelFilterOptions = new List<string> { _allFilterOption };
            categoryLabelFilterOptions.AddRange(labels);
            CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, categoryLabelFilterOptions);

            if (!categoryLabelFilterOptions.Any(value =>
                    string.Equals(value, SelectedCategoryLabelFilter, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedCategoryLabelFilter = _allFilterOption;
            }
        }

        private Dictionary<string, string> GetCurrentCategoryOverrideMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_settings?.Persisted?.AchievementCategoryOverrides == null ||
                !_settings.Persisted.AchievementCategoryOverrides.TryGetValue(_gameId, out var raw) ||
                raw == null)
            {
                return map;
            }

            foreach (var pair in raw)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var category = NormalizeCategory(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                map[apiName] = category;
            }

            return map;
        }

        private Dictionary<string, string> GetCurrentCategoryTypeOverrideMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_settings?.Persisted?.AchievementCategoryTypeOverrides == null ||
                !_settings.Persisted.AchievementCategoryTypeOverrides.TryGetValue(_gameId, out var raw) ||
                raw == null)
            {
                return map;
            }

            foreach (var pair in raw)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(categoryType))
                {
                    continue;
                }

                map[apiName] = categoryType;
            }

            return map;
        }

        private static string NormalizeCategory(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private bool IsAllFilterOption(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value, _allFilterOption, StringComparison.OrdinalIgnoreCase);
        }

        private List<string> GetSelectedCategoryTypeFilterValues()
        {
            var selected = new List<string>();
            if (TypeFilterBaseSelected)
            {
                selected.Add("Base");
            }

            if (TypeFilterDlcSelected)
            {
                selected.Add("DLC");
            }

            if (TypeFilterSingleplayerSelected)
            {
                selected.Add("Singleplayer");
            }

            if (TypeFilterMultiplayerSelected)
            {
                selected.Add("Multiplayer");
            }

            return selected;
        }

        private string GetSelectedCategoryTypeValue()
        {
            var selected = new List<string>();
            if (TypeBaseSelected)
            {
                selected.Add("Base");
            }

            if (TypeDlcSelected)
            {
                selected.Add("DLC");
            }

            if (TypeSingleplayerSelected)
            {
                selected.Add("Singleplayer");
            }

            if (TypeMultiplayerSelected)
            {
                selected.Add("Multiplayer");
            }

            return AchievementCategoryTypeHelper.Combine(selected);
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    public sealed class GameOptionsCategoryItem : AchievementDisplayItem
    {
        private bool _isSelected;

        public string Category { get; set; }
        public string CategoryType { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }

        public string CategoryDisplay => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(Category);
        public string CategoryTypeDisplay => AchievementCategoryTypeHelper.ToDisplayText(CategoryType);
    }
}
