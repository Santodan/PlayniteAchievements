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
    public enum AchievementFilterStateFilter
    {
        All,
        FilteredOut,
        FilteredOutOfSummaries,
        Unfiltered
    }

    public sealed class ManageAchievementsFiltersViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private List<ManageAchievementsFilterItem> _allRows = new List<ManageAchievementsFilterItem>();
        private List<string> _canonicalCategoryLabelFilterOptions = new List<string>();
        private readonly HashSet<string> _selectedCategoryLabelFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _hasAchievements;
        private bool _hasCustomFilters;
        private bool _isUpdatingRows;
        private string _searchText = string.Empty;
        private FilterStateOption _selectedFilterOption;

        public ManageAchievementsFiltersViewModel(
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

            AchievementRows = new ObservableCollection<ManageAchievementsFilterItem>();
            FilterOptions = new ObservableCollection<FilterStateOption>(CreateFilterOptions());
            CategoryLabelFilterOptions = new ObservableCollection<string>();
            TypeFilterOptions = CreateCategoryTypeOptions(() =>
            {
                OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                ApplyFilter();
            });
            _selectedFilterOption = FilterOptions.FirstOrDefault();
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);

            ReloadData();
        }

        public ObservableCollection<ManageAchievementsFilterItem> AchievementRows { get; }

        public ObservableCollection<FilterStateOption> FilterOptions { get; }

        public ObservableCollection<CategoryTypeSelectionOption> TypeFilterOptions { get; }

        public ObservableCollection<string> CategoryLabelFilterOptions { get; }

        public RelayCommand ClearSearchCommand { get; }

        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
        }

        public bool HasCustomFilters
        {
            get => _hasCustomFilters;
            private set => SetValue(ref _hasCustomFilters, value);
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

        public FilterStateOption SelectedFilterOption
        {
            get => _selectedFilterOption;
            set
            {
                if (SetValueAndReturn(ref _selectedFilterOption, value ?? FilterOptions.FirstOrDefault()))
                {
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
                    ? L("LOCPlayAch_Common_Label_Type", "Type")
                    : AchievementCategoryTypeHelper.ToDisplayText(selected);
            }
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
                var revealedStateByApiName = AchievementRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                    .GroupBy(row => row.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().IsRevealed, StringComparer.OrdinalIgnoreCase);

                var hydratedGameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var rawGameData = _gameDataSnapshotProvider.GetRawGameData();
                var rawAchievements = rawGameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();
                var projectionSource = hydratedGameData ?? rawGameData;
                var categoryOverrides = GetCurrentCategoryOverrideMap();
                var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap();
                var filteredApiNames = GameCustomDataLookup.GetFilteredAchievementApiNames(_gameId, _settings?.Persisted);
                var summaryFilteredApiNames = GameCustomDataLookup.GetSummaryFilteredAchievementApiNames(_gameId, _settings?.Persisted);

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

                _canonicalCategoryLabelFilterOptions = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                    orderedAchievements,
                    achievement => ResolveEffectiveCategoryLabel(achievement, categoryOverrides));

                _allRows = orderedAchievements.Select(a =>
                {
                    var apiName = (a.ApiName ?? string.Empty).Trim();
                    var projected = AchievementDisplayItem.Create(
                        projectionSource,
                        a,
                        _settings,
                        playniteGameIdOverride: _gameId);
                    if (projected == null)
                    {
                        return null;
                    }

                    var item = new ManageAchievementsFilterItem(
                        filteredApiNames.Contains(apiName),
                        summaryFilteredApiNames.Contains(apiName),
                        OnRowFilterChanged)
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
                        CategoryLabel = ResolveEffectiveCategoryLabel(a, categoryOverrides),
                        CategoryType = ResolveEffectiveCategoryType(a, categoryTypeOverrides)
                    };

                    return item;
                })
                .Where(a => a != null)
                .ToList();

                HasAchievements = _allRows.Count > 0;
                RefreshCustomFilterState();
                ApplyFilter();
                RefreshCategoryLabelFilterOptions();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading filter rows for gameId={_gameId}");
                _allRows = new List<ManageAchievementsFilterItem>();
                _canonicalCategoryLabelFilterOptions = new List<string>();
                CollectionHelper.SynchronizeCollection(AchievementRows, _allRows);
                CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, new List<string>());
                _selectedCategoryLabelFilters.Clear();
                OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
                HasAchievements = false;
                HasCustomFilters = false;
            }
        }

        public bool ResetFilters()
        {
            if (!HasCustomFilters)
            {
                return false;
            }

            _isUpdatingRows = true;
            try
            {
                foreach (var row in _allRows.Where(row => row != null))
                {
                    row.SetFilterState(isFiltered: false, isSummaryFiltered: false);
                }
            }
            finally
            {
                _isUpdatingRows = false;
            }

            PersistCurrentFilters();
            ApplyFilter();
            return true;
        }

        public bool SetVisibleRowsFiltered(bool isFiltered)
        {
            var rows = AchievementRows
                .Where(row => row != null)
                .ToList();
            if (rows.Count == 0)
            {
                return false;
            }

            var changed = false;
            _isUpdatingRows = true;
            try
            {
                foreach (var row in rows)
                {
                    if (row.IsFiltered == isFiltered)
                    {
                        continue;
                    }

                    row.SetFilterState(isFiltered, row.IsSummaryFiltered);
                    changed = true;
                }
            }
            finally
            {
                _isUpdatingRows = false;
            }

            if (!changed)
            {
                return false;
            }

            PersistCurrentFilters();
            ApplyFilter();
            return true;
        }

        private void OnRowFilterChanged(ManageAchievementsFilterItem item)
        {
            if (_isUpdatingRows)
            {
                return;
            }

            PersistCurrentFilters();
            ApplyFilter();
        }

        private void PersistCurrentFilters()
        {
            var filteredApiNames = _allRows
                .Where(row => row?.IsFiltered == true && !string.IsNullOrWhiteSpace(row.ApiName))
                .Select(row => row.ApiName)
                .ToList();
            var summaryFilteredApiNames = _allRows
                .Where(row => row?.IsSummaryFiltered == true && !string.IsNullOrWhiteSpace(row.ApiName))
                .Select(row => row.ApiName)
                .ToList();

            _achievementOverridesService.SetAchievementFilters(
                _gameId,
                filteredApiNames,
                summaryFilteredApiNames);

            RefreshCustomFilterState();
        }

        private void RefreshCustomFilterState()
        {
            HasCustomFilters = _allRows.Any(row => row?.IsFiltered == true || row?.IsSummaryFiltered == true);
        }

        private void ApplyFilter()
        {
            var filtered = _allRows.AsEnumerable();

            if (!string.IsNullOrEmpty(SearchText))
            {
                filtered = filtered.Where(a =>
                    (a.DisplayName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Description?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.ApiName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.CategoryDisplay?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
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
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.CategoryLabel)));
            }

            switch (SelectedFilterOption?.Value ?? AchievementFilterStateFilter.All)
            {
                case AchievementFilterStateFilter.FilteredOut:
                    filtered = filtered.Where(a => a.IsFiltered);
                    break;
                case AchievementFilterStateFilter.FilteredOutOfSummaries:
                    filtered = filtered.Where(a => a.IsFiltered || a.IsSummaryFiltered);
                    break;
                case AchievementFilterStateFilter.Unfiltered:
                    filtered = filtered.Where(a => !a.IsFiltered && !a.IsSummaryFiltered);
                    break;
            }

            CollectionHelper.SynchronizeCollection(AchievementRows, filtered.ToList());
        }

        private void RefreshCategoryLabelFilterOptions()
        {
            var labels = _allRows
                .Where(row => row != null)
                .Select(row => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.CategoryLabel))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToList();

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
                var category = AchievementCategoryTypeHelper.NormalizeCategory(pair.Value);
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
                return AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryOverride);
            }

            return AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement?.Category);
        }

        private static string ResolveEffectiveCategoryType(
            AchievementDetail achievement,
            IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            var apiName = (achievement?.ApiName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(apiName) &&
                categoryTypeOverrides != null &&
                categoryTypeOverrides.TryGetValue(apiName, out var categoryTypeOverride) &&
                !string.IsNullOrWhiteSpace(categoryTypeOverride))
            {
                return AchievementCategoryTypeHelper.NormalizeOrDefault(categoryTypeOverride);
            }

            return AchievementCategoryTypeHelper.NormalizeOrDefault(achievement?.CategoryType);
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

        private static List<string> GetSelectedCategoryTypeValues(IEnumerable<CategoryTypeSelectionOption> options)
        {
            return (options ?? Enumerable.Empty<CategoryTypeSelectionOption>())
                .Where(option => option?.IsSelected == true)
                .Select(option => option.Value)
                .ToList();
        }

        private ObservableCollection<CategoryTypeSelectionOption> CreateCategoryTypeOptions(Action onSelectionChanged)
        {
            var options = new ObservableCollection<CategoryTypeSelectionOption>(
                AchievementCategoryTypeHelper.AllowedCategoryTypes
                    .Select(type => new CategoryTypeSelectionOption(
                        type,
                        ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName(type))));

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

        private static IEnumerable<FilterStateOption> CreateFilterOptions()
        {
            return new[]
            {
                new FilterStateOption(AchievementFilterStateFilter.All, L("LOCPlayAch_Common_All", "All")),
                new FilterStateOption(AchievementFilterStateFilter.FilteredOut, L("LOCPlayAch_ManageAchievements_Filters_FilteredOut", "Filtered Out")),
                new FilterStateOption(AchievementFilterStateFilter.FilteredOutOfSummaries, L("LOCPlayAch_ManageAchievements_Filters_FilteredOutOfSummaries", "Filtered Out of Summaries")),
                new FilterStateOption(AchievementFilterStateFilter.Unfiltered, L("LOCPlayAch_ManageAchievements_Filters_Unfiltered", "Unfiltered"))
            };
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    public sealed class ManageAchievementsFilterItem : AchievementDisplayItem
    {
        private readonly Action<ManageAchievementsFilterItem> _filterChanged;
        private bool _isFiltered;
        private bool _isSummaryFiltered;

        public ManageAchievementsFilterItem(
            bool isFiltered,
            bool isSummaryFiltered,
            Action<ManageAchievementsFilterItem> filterChanged)
        {
            _isFiltered = isFiltered;
            _isSummaryFiltered = isSummaryFiltered;
            _filterChanged = filterChanged;
        }

        public bool IsFiltered
        {
            get => _isFiltered;
            set
            {
                if (SetValueAndReturn(ref _isFiltered, value))
                {
                    OnPropertyChanged(nameof(IsSummaryFilterEffective));
                    OnPropertyChanged(nameof(CanSetSummaryFilter));
                    _filterChanged?.Invoke(this);
                }
            }
        }

        public bool IsSummaryFiltered
        {
            get => _isSummaryFiltered;
            set
            {
                if (SetValueAndReturn(ref _isSummaryFiltered, value))
                {
                    OnPropertyChanged(nameof(IsSummaryFilterEffective));
                    _filterChanged?.Invoke(this);
                }
            }
        }

        public bool IsSummaryFilterEffective
        {
            get => IsFiltered || IsSummaryFiltered;
            set
            {
                if (!IsFiltered)
                {
                    IsSummaryFiltered = value;
                }
            }
        }

        public bool CanSetSummaryFilter => !IsFiltered;

        public string CategoryDisplay => AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(CategoryLabel);

        public void SetFilterState(bool isFiltered, bool isSummaryFiltered)
        {
            var changed = _isFiltered != isFiltered || _isSummaryFiltered != isSummaryFiltered;
            _isFiltered = isFiltered;
            _isSummaryFiltered = isSummaryFiltered;
            if (!changed)
            {
                return;
            }

            OnPropertyChanged(nameof(IsFiltered));
            OnPropertyChanged(nameof(IsSummaryFiltered));
            OnPropertyChanged(nameof(IsSummaryFilterEffective));
            OnPropertyChanged(nameof(CanSetSummaryFilter));
            _filterChanged?.Invoke(this);
        }
    }

    public sealed class FilterStateOption
    {
        public FilterStateOption(AchievementFilterStateFilter value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public AchievementFilterStateFilter Value { get; }

        public string DisplayName { get; }
    }
}
