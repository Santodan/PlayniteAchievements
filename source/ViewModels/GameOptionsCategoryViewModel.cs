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
        private readonly HashSet<string> _selectedCategoryLabelFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _hasCustomOverrides;
        private bool _typeDefaultSelected;
        private bool _typeBaseSelected;
        private bool _typeDlcSelected;
        private bool _typeSingleplayerSelected;
        private bool _typeMultiplayerSelected;
        private bool _typeCollectableSelected;
        private bool _typeMissableSelected;
        private bool _typeDifficultySelected;
        private bool _typeStackableSelected;
        private bool _typeFilterDefaultSelected;
        private bool _typeFilterBaseSelected;
        private bool _typeFilterDlcSelected;
        private bool _typeFilterSingleplayerSelected;
        private bool _typeFilterMultiplayerSelected;
        private bool _typeFilterCollectableSelected;
        private bool _typeFilterMissableSelected;
        private bool _typeFilterDifficultySelected;
        private bool _typeFilterStackableSelected;

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

        public bool TypeDefaultSelected
        {
            get => _typeDefaultSelected;
            set
            {
                if (SetValueAndReturn(ref _typeDefaultSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
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

        public bool TypeCollectableSelected
        {
            get => _typeCollectableSelected;
            set
            {
                if (SetValueAndReturn(ref _typeCollectableSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                }
            }
        }

        public bool TypeMissableSelected
        {
            get => _typeMissableSelected;
            set
            {
                if (SetValueAndReturn(ref _typeMissableSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                }
            }
        }

        public bool TypeDifficultySelected
        {
            get => _typeDifficultySelected;
            set
            {
                if (SetValueAndReturn(ref _typeDifficultySelected, value))
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                }
            }
        }

        public bool TypeStackableSelected
        {
            get => _typeStackableSelected;
            set
            {
                if (SetValueAndReturn(ref _typeStackableSelected, value))
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
                    ? L("LOCPlayAch_GameOptions_Category_TypeSelectorPlaceholder", "Type")
                    : string.Join(", ", selected);
            }
        }

        public bool TypeFilterDefaultSelected
        {
            get => _typeFilterDefaultSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterDefaultSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
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

        public bool TypeFilterCollectableSelected
        {
            get => _typeFilterCollectableSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterCollectableSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
            }
        }

        public bool TypeFilterMissableSelected
        {
            get => _typeFilterMissableSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterMissableSelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
            }
        }

        public bool TypeFilterDifficultySelected
        {
            get => _typeFilterDifficultySelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterDifficultySelected, value))
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                }
            }
        }

        public bool TypeFilterStackableSelected
        {
            get => _typeFilterStackableSelected;
            set
            {
                if (SetValueAndReturn(ref _typeFilterStackableSelected, value))
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
                    ? L("LOCPlayAch_GameOptions_Category_TypeSelectorPlaceholder", "Type")
                    : string.Join(", ", selected);
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
                        "LOCPlayAch_GameOptions_Category_Filter_LabelSelectorPlaceholder",
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

                return string.Join(", ", ordered);
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
                        ShowRarityGlow = projected.ShowRarityGlow,
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
                _achievementService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            }

            if (categoryTypeChanged)
            {
                _achievementService.SetAchievementCategoryTypeOverrides(_gameId, categoryTypeOverrideMap);
            }

            if (categoryChanged || categoryTypeChanged)
            {
                ReloadData();
            }

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

            _achievementService.SetAchievementCategoryOverrides(_gameId, categoryOverrideMap);
            ReloadData();
            return true;
        }

        public void ResetBulkEditorInputs()
        {
            TypeDefaultSelected = false;
            TypeBaseSelected = false;
            TypeDlcSelected = false;
            TypeSingleplayerSelected = false;
            TypeMultiplayerSelected = false;
            TypeCollectableSelected = false;
            TypeMissableSelected = false;
            TypeDifficultySelected = false;
            TypeStackableSelected = false;
        }

        public void ClearAllSelections()
        {
            foreach (var item in _allRows.Where(row => row != null))
            {
                item.IsSelected = false;
            }
        }

        public List<GameOptionsCategoryItem> GetAllSelectedRows()
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
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.CategoryDisplay)));
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

            var categoryLabelFilterOptions = new List<string>(labels);
            CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, categoryLabelFilterOptions);

            if (PruneCategoryLabelFilterSelections(categoryLabelFilterOptions))
            {
                ApplyFilter();
            }

            OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
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

        private bool PruneCategoryLabelFilterSelections(IEnumerable<string> options)
        {
            var optionSet = new HashSet<string>(
                (options ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return _selectedCategoryLabelFilters.RemoveWhere(value => !optionSet.Contains(value)) > 0;
        }

        private List<string> GetSelectedCategoryTypeFilterValues()
        {
            var selected = new List<string>();
            if (TypeFilterDefaultSelected)
            {
                selected.Add("Default");
            }

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

            if (TypeFilterCollectableSelected)
            {
                selected.Add("Collectable");
            }

            if (TypeFilterMissableSelected)
            {
                selected.Add("Missable");
            }

            if (TypeFilterDifficultySelected)
            {
                selected.Add("Difficulty");
            }

            if (TypeFilterStackableSelected)
            {
                selected.Add("Stackable");
            }

            return selected;
        }

        private string GetSelectedCategoryTypeValue()
        {
            var selected = new List<string>();
            if (TypeDefaultSelected)
            {
                selected.Add("Default");
            }

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

            if (TypeCollectableSelected)
            {
                selected.Add("Collectable");
            }

            if (TypeMissableSelected)
            {
                selected.Add("Missable");
            }

            if (TypeDifficultySelected)
            {
                selected.Add("Difficulty");
            }

            if (TypeStackableSelected)
            {
                selected.Add("Stackable");
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

        public string Category
        {
            get => CategoryLabel;
            set => CategoryLabel = value;
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }

        public string CategoryDisplay => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(Category);
    }
}
