using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public enum AchievementFilterStateFilter
    {
        All,
        FilteredOut,
        FilteredOutOfSummaries,
        Unfiltered
    }

    public enum AchievementFilterBulkTarget
    {
        AchievementViews,
        Summaries
    }

    public sealed class ManageAchievementsFiltersViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private List<ManageAchievementsFilterItem> _allRows = new List<ManageAchievementsFilterItem>();
        private readonly SearchTextIndex<ManageAchievementsFilterItem> _searchIndex =
            new SearchTextIndex<ManageAchievementsFilterItem>(item =>
                SearchTextBuilder.ForManageFilter(
                    item?.DisplayName,
                    item?.Description,
                    item?.ApiName,
                    item?.CategoryDisplay,
                    item?.CategoryTypeDisplay));
        private List<string> _canonicalCategoryLabelFilterOptions = new List<string>();
        private readonly HashSet<string> _selectedCategoryLabelFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _hasAchievements;
        private bool _hasCustomFilters;
        private bool _isUpdatingRows;
        private string _searchText = string.Empty;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _showHidden = true;
        private FilterStateOption _selectedFilterOption;
        private FilterBulkTargetOption _selectedBulkTargetOption;

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

            AchievementRows = new BulkObservableCollection<ManageAchievementsFilterItem>();
            FilterOptions = new ObservableCollection<FilterStateOption>(CreateFilterOptions());
            BulkTargetOptions = new ObservableCollection<FilterBulkTargetOption>(CreateBulkTargetOptions());
            CategoryLabelFilterOptions = new ObservableCollection<string>();
            TypeFilterOptions = CreateCategoryTypeOptions(() =>
            {
                OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                ApplyFilter();
            });
            _selectedFilterOption = FilterOptions.FirstOrDefault();
            _selectedBulkTargetOption = BulkTargetOptions.FirstOrDefault();
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);

            ReloadData();
        }

        public ObservableCollection<ManageAchievementsFilterItem> AchievementRows { get; }

        public ObservableCollection<FilterStateOption> FilterOptions { get; }

        public ObservableCollection<FilterBulkTargetOption> BulkTargetOptions { get; }

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

        public FilterBulkTargetOption SelectedBulkTargetOption
        {
            get => _selectedBulkTargetOption;
            set => SetValue(ref _selectedBulkTargetOption, value ?? BulkTargetOptions.FirstOrDefault());
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

        public string SelectedCategoryTypeFilterText
        {
            get
            {
                var selected = GetSelectedCategoryTypeFilterValues();
                return selected.Count == 0
                    ? L("LOCPlayAch_Common_Label_Type")
                    : string.Join(", ", selected.Select(AchievementCategoryTypeHelper.ToCategoryTypeDisplayText));
            }
        }

        public string SelectedCategoryLabelFilterText
        {
            get
            {
                if (_selectedCategoryLabelFilters.Count == 0)
                {
                    return L("LOCPlayAch_Common_Label_Category");
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
            using (PerfScope.Start(_logger, "ManageAchievements.Filters.ReloadData", thresholdMs: 100))
            {
                ReloadDataCore();
            }
        }

        private void ReloadDataCore()
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
                    achievement => ResolveEffectiveCategoryLabel(achievement, categoryOverrides),
                    hydratedGameData?.AchievementCategoryOrder);

                // Per-game invariants hoisted out of the row loop: the appearance snapshot and
                // category art/order resolution are identical for every row in this pass.
                var appearanceSnapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    projectionSource?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();

                _allRows = orderedAchievements.Select(a =>
                {
                    var apiName = (a.ApiName ?? string.Empty).Trim();
                    var projected = AchievementDisplayItem.Create(
                        projectionSource,
                        a,
                        _settings,
                        playniteGameIdOverride: _gameId,
                        appearanceSettings: appearanceSnapshot,
                        categoryMemo: categoryMemo);
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

                _searchIndex.Rebuild(_allRows);
                HasAchievements = _allRows.Count > 0;
                RefreshCustomFilterState();
                ApplyFilter();
                RefreshCategoryLabelFilterOptions();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading filter rows for gameId={_gameId}");
                _allRows = new List<ManageAchievementsFilterItem>();
                _searchIndex.Clear();
                _canonicalCategoryLabelFilterOptions = new List<string>();
                ReplaceAchievementRows(_allRows);
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

        public bool SetVisibleRowsFilterState(bool enabled)
        {
            var target = SelectedBulkTargetOption?.Value ?? AchievementFilterBulkTarget.AchievementViews;
            return SetVisibleRowsFilterState(target, enabled);
        }

        public bool SetVisibleRowsFilterState(AchievementFilterBulkTarget target, bool enabled)
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
                    if (!TryResolveBulkFilterState(
                            target,
                            enabled,
                            row.IsFiltered,
                            row.IsSummaryFiltered,
                            out var nextFiltered,
                            out var nextSummaryFiltered))
                    {
                        continue;
                    }

                    row.SetFilterState(nextFiltered, nextSummaryFiltered);
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

            // A toggle only changes the visible row set when a filter-state filter is
            // active; the toggled row itself updates through its bindings, so the full
            // re-filter (and its collection reset) can be skipped otherwise.
            if ((SelectedFilterOption?.Value ?? AchievementFilterStateFilter.All) != AchievementFilterStateFilter.All)
            {
                ApplyFilter();
            }
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

        public void ToggleReveal(ManageAchievementsFilterItem item)
        {
            if (item == null || !item.CanReveal)
            {
                return;
            }

            item.ToggleReveal();
            _searchIndex.Invalidate(item);
        }

        private void ApplyFilter()
        {
            using (PerfScope.Start(_logger, "ManageAchievements.Filters.ApplyFilter", thresholdMs: 50))
            {
                ApplyFilterCore();
            }
        }

        private void ApplyFilterCore()
        {
            var filtered = _allRows.AsEnumerable();
            var searchQuery = SearchQuery.From(SearchText);

            filtered = filtered.Where(a => ShouldShowByAchievementState(
                a.Unlocked,
                a.Hidden,
                ShowUnlocked,
                ShowLocked,
                ShowHidden));

            if (searchQuery.HasValue)
            {
                filtered = filtered.Where(a => _searchIndex.Matches(a, searchQuery));
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

            var filterState = SelectedFilterOption?.Value ?? AchievementFilterStateFilter.All;
            filtered = filtered.Where(a => ShouldShowByFilterState(
                filterState,
                a.IsFiltered,
                a.IsSummaryFiltered));

            ReplaceAchievementRows(filtered.ToList());
        }

        private void ReplaceAchievementRows(IEnumerable<ManageAchievementsFilterItem> rows)
        {
            CollectionHelper.Replace(AchievementRows, rows);
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

        private static bool ShouldShowByAchievementState(
            bool unlocked,
            bool hidden,
            bool showUnlocked,
            bool showLocked,
            bool showHidden)
        {
            if (!showHidden && hidden && !unlocked)
            {
                return false;
            }

            return unlocked ? showUnlocked : showLocked;
        }

        private static bool TryResolveBulkFilterState(
            AchievementFilterBulkTarget target,
            bool enabled,
            bool isFiltered,
            bool isSummaryFiltered,
            out bool nextFiltered,
            out bool nextSummaryFiltered)
        {
            nextFiltered = isFiltered;
            nextSummaryFiltered = isSummaryFiltered;

            switch (target)
            {
                case AchievementFilterBulkTarget.AchievementViews:
                    if (isFiltered == enabled)
                    {
                        return false;
                    }

                    nextFiltered = enabled;
                    return true;

                case AchievementFilterBulkTarget.Summaries:
                    if (isFiltered || isSummaryFiltered == enabled)
                    {
                        return false;
                    }

                    nextFiltered = false;
                    nextSummaryFiltered = enabled;
                    return true;

                default:
                    return false;
            }
        }

        private static bool ShouldShowByFilterState(
            AchievementFilterStateFilter filterState,
            bool isFiltered,
            bool isSummaryFiltered)
        {
            switch (filterState)
            {
                case AchievementFilterStateFilter.FilteredOut:
                    return isFiltered;
                case AchievementFilterStateFilter.FilteredOutOfSummaries:
                    return isFiltered || isSummaryFiltered;
                case AchievementFilterStateFilter.Unfiltered:
                    return !isFiltered && !isSummaryFiltered;
                default:
                    return true;
            }
        }

        private static IEnumerable<FilterStateOption> CreateFilterOptions()
        {
            return new[]
            {
                new FilterStateOption(AchievementFilterStateFilter.All, L("LOCPlayAch_Common_All")),
                new FilterStateOption(AchievementFilterStateFilter.FilteredOut, L("LOCPlayAch_ManageAchievements_Filters_FilteredOut")),
                new FilterStateOption(AchievementFilterStateFilter.FilteredOutOfSummaries, L("LOCPlayAch_ManageAchievements_Filters_FilteredOutOfSummaries")),
                new FilterStateOption(AchievementFilterStateFilter.Unfiltered, L("LOCPlayAch_ManageAchievements_Filters_Unfiltered"))
            };
        }

        private static IEnumerable<FilterBulkTargetOption> CreateBulkTargetOptions()
        {
            return new[]
            {
                new FilterBulkTargetOption(
                    AchievementFilterBulkTarget.AchievementViews,
                    L("LOCPlayAch_Achievements")),
                new FilterBulkTargetOption(
                    AchievementFilterBulkTarget.Summaries,
                    L("LOCPlayAch_Overview_GameSummaries"))
            };
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
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

    public sealed class FilterBulkTargetOption
    {
        public FilterBulkTargetOption(AchievementFilterBulkTarget value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public AchievementFilterBulkTarget Value { get; }

        public string DisplayName { get; }
    }
}
