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
    public sealed partial class ManageAchievementsCategoryViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly string _gameIdText;

        private List<ManageAchievementsCategoryItem> _allRows = new List<ManageAchievementsCategoryItem>();

        // Same row instances as _allRows re-sorted into canonical definition/custom order; used as
        // the first-seen fallback for category-row ordering so it matches the other surfaces
        // instead of the alphabetical row listing.
        private List<ManageAchievementsCategoryItem> _definitionOrderedRows = new List<ManageAchievementsCategoryItem>();
        private readonly SearchTextIndex<ManageAchievementsCategoryItem> _searchIndex =
            new SearchTextIndex<ManageAchievementsCategoryItem>(item =>
                SearchTextBuilder.ForManageCategory(
                    item?.DisplayName,
                    item?.Description,
                    item?.ApiName,
                    item?.Category,
                    item?.CategoryTypeDisplay));
        private List<string> _canonicalCategoryLabelFilterOptions = new List<string>();
        private bool _hasAchievements;
        private string _searchText = string.Empty;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _showHidden = true;
        private readonly HashSet<string> _selectedCategoryLabelFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _hasCustomOverrides;
        private bool _hasCustomCategoryOrder;
        private bool _hasCustomCategoryNames;
        private bool _hasCustomCategoryArt;
        private bool _hasCustomSummaryCategory;
        private bool _isEnforcingSummarySelection;
        private bool _isPersistingCategoryMetadata;
        private bool _hasCategoryImageValidationErrors;
        private bool _hasDeferredLibraryRefresh;
        private string _categoryImageStatusText;
        private bool _categoryImageStatusIsError;

        /// <summary>
        /// Raised after category metadata (order, art overrides, summary-category selection)
        /// has been written to the custom data store, so the hosting window can refresh
        /// state that depends on it (e.g. the game cover image).
        /// </summary>
        public event EventHandler CategoryMetadataPersisted;

        /// <summary>
        /// Raised after a move with the labels the moved rows now carry, so the view can keep them
        /// selected. A move rebuilds every row and renames the moved ones, so without this the
        /// selection lands nowhere and the row has to be found and clicked again for each level.
        /// </summary>
        public event EventHandler<IReadOnlyList<string>> CategoryRowsMoved;

        /// <summary>
        /// Raised once, on teardown, when edits were written without the library-wide passes, so
        /// the host can invalidate this game everywhere in a single step. See
        /// <see cref="FlushDeferredLibraryRefresh"/>.
        /// </summary>
        public event EventHandler DeferredLibraryRefreshRequired;

        public ManageAchievementsCategoryViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            ManagedCustomIconService managedCustomIconService,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _gameIdText = gameId.ToString("D");
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameDataSnapshotProvider = gameDataSnapshotProvider ?? throw new ArgumentNullException(nameof(gameDataSnapshotProvider));
            _managedCustomIconService = managedCustomIconService ?? throw new ArgumentNullException(nameof(managedCustomIconService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            AchievementRows = new BulkObservableCollection<ManageAchievementsCategoryItem>();
            CategoryRows = new BulkObservableCollection<ManageAchievementsCategoryMetadataItem>();
            CategoryLabelFilterOptions = new ObservableCollection<string>();
            AssignableCategoryOptions = new ObservableCollection<string>();
            TypeSelectionOptions = CreateCategoryTypeOptions(
                AchievementCategoryTypeHelper.AssignableCategoryTypes,
                () =>
                {
                    OnPropertyChanged(nameof(SelectedTypeSelectionText));
                });
            TypeFilterOptions = CreateCategoryTypeOptions(
                AchievementCategoryTypeHelper.AllowedCategoryTypes,
                () =>
                {
                    OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
                    ApplyFilter();
                });
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
            OpenCategoryImagesFolderCommand = new RelayCommand(_ => OpenCategoryImagesFolder());
            IndentCategoryCommand = new RelayCommand(parameter => IndentOrOutdentFromCommand(parameter, indent: true));
            OutdentCategoryCommand = new RelayCommand(parameter => IndentOrOutdentFromCommand(parameter, indent: false));

            ReloadData();
        }

        public ObservableCollection<ManageAchievementsCategoryItem> AchievementRows { get; }
        /// <summary>
        /// Bulk rather than plain observable: every rebuild replaces the whole list, and one Add
        /// notification per row makes the grid realize a container - and lay it out - per row. That
        /// cost lands after the rebuild call returns, which is why it does not show up in the
        /// Categories.Moves spans while still being what a click waits on.
        /// </summary>
        public BulkObservableCollection<ManageAchievementsCategoryMetadataItem> CategoryRows { get; }
        public ObservableCollection<string> CategoryLabelFilterOptions { get; }

        /// <summary>
        /// Every rendered category label, in tree order - the choices for filing an achievement.
        /// Wider than <see cref="CategoryLabelFilterOptions"/> (achievement-backed labels only)
        /// because a user-created empty category is somewhere to file an achievement, and a nested
        /// one cannot be reached by typing (typed input rejects the path separator).
        /// </summary>
        public ObservableCollection<string> AssignableCategoryOptions { get; }
        public ObservableCollection<CategoryTypeSelectionOption> TypeSelectionOptions { get; }
        public ObservableCollection<CategoryTypeSelectionOption> TypeFilterOptions { get; }

        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand OpenCategoryImagesFolderCommand { get; }

        /// <summary>Nests the row under the sibling above it. Parameter is the row.</summary>
        public RelayCommand IndentCategoryCommand { get; }

        /// <summary>Promotes the row to sit beside its parent. Parameter is the row.</summary>
        public RelayCommand OutdentCategoryCommand { get; }
        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
        }

        /// <summary>
        /// True when there is more than one category, so a category can be merged into another.
        /// </summary>
        public bool CanMergeCategories => CategoryRows.Count > 1;

        internal Models.Settings.PersistedSettings PlacementSettings => _settings?.Persisted;

        internal ILogger PlacementLogger => _logger;

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
                    ? L("LOCPlayAch_Common_Label_Type")
                    : string.Join(", ", selected.Select(AchievementCategoryTypeHelper.ToCategoryTypeDisplayText));
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

        public bool HasCustomOverrides
        {
            get => _hasCustomOverrides;
            private set => SetValue(ref _hasCustomOverrides, value);
        }

        public bool HasCustomCategoryMetadata => HasCustomCategoryOrder || HasCustomCategoryNames || HasCustomCategoryArt || HasCustomSummaryCategory;

        public bool HasCustomCategoryOrder
        {
            get => _hasCustomCategoryOrder;
            private set
            {
                if (SetValueAndReturn(ref _hasCustomCategoryOrder, value))
                {
                    OnPropertyChanged(nameof(HasCustomCategoryMetadata));
                }
            }
        }

        public bool HasCustomCategoryNames
        {
            get => _hasCustomCategoryNames;
            private set
            {
                if (SetValueAndReturn(ref _hasCustomCategoryNames, value))
                {
                    OnPropertyChanged(nameof(HasCustomCategoryMetadata));
                }
            }
        }

        public bool HasCustomCategoryArt
        {
            get => _hasCustomCategoryArt;
            private set
            {
                if (SetValueAndReturn(ref _hasCustomCategoryArt, value))
                {
                    OnPropertyChanged(nameof(HasCustomCategoryMetadata));
                }
            }
        }

        public bool HasCustomSummaryCategory
        {
            get => _hasCustomSummaryCategory;
            private set
            {
                if (SetValueAndReturn(ref _hasCustomSummaryCategory, value))
                {
                    OnPropertyChanged(nameof(HasCustomCategoryMetadata));
                }
            }
        }

        public bool HasCategoryImageValidationErrors
        {
            get => _hasCategoryImageValidationErrors;
            private set
            {
                if (SetValueAndReturn(ref _hasCategoryImageValidationErrors, value))
                {
                    OnPropertyChanged(nameof(CategoryImageStatusText));
                    OnPropertyChanged(nameof(CategoryImageStatusIsError));
                    OnPropertyChanged(nameof(HasCategoryImageStatusText));
                }
            }
        }

        public string CategoryImageStatusText
        {
            get
            {
                if (HasCategoryImageValidationErrors)
                {
                    return L("LOCPlayAch_ManageAchievements_CustomIcons_ValidationError");
                }

                return _categoryImageStatusText;
            }
        }

        public bool CategoryImageStatusIsError => HasCategoryImageValidationErrors || _categoryImageStatusIsError;

        public bool HasCategoryImageStatusText => !string.IsNullOrWhiteSpace(CategoryImageStatusText);

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

                // Leaf names, matching the dropdown this summarizes. A list of full paths sharing long

                // prefixes overflows the button and reads as repetition rather than a selection.

                return string.Join(", ", ordered.Select(AchievementCategoryTypeHelper.ToCategoryLeafDisplayText));
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
                        playniteGameIdOverride: _gameId,
                        appearanceSettings: appearanceSnapshot,
                        categoryMemo: categoryMemo);
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
                        GameIconPath = projected.GameIconPath,
                        GameCoverPath = projected.GameCoverPath,
                        CategoryArtPath = projected.CategoryArtPath,
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
                        ProviderCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(providerCategory),
                        ProviderCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(providerCategoryType),
                        Category = effectiveCategory,
                        CategoryType = effectiveCategoryType,
                        IsSelected = selectedApiNames.Contains(apiName)
                    };
                })
                .Where(a => a != null)
                .ToList();

                var canonicalIndexByApiName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < canonicalAchievements.Count; i++)
                {
                    var canonicalApiName = (canonicalAchievements[i]?.ApiName ?? string.Empty).Trim();
                    if (canonicalApiName.Length > 0 && !canonicalIndexByApiName.ContainsKey(canonicalApiName))
                    {
                        canonicalIndexByApiName[canonicalApiName] = i;
                    }
                }

                _definitionOrderedRows = _allRows
                    .OrderBy(row => canonicalIndexByApiName.TryGetValue(row.ApiName ?? string.Empty, out var index)
                        ? index
                        : int.MaxValue)
                    .ToList();

                _searchIndex.Rebuild(_allRows);
                HasAchievements = _allRows.Count > 0;
                ApplyFilter();
                RefreshCategoryLabelOptions();
                RefreshCategoryRows();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading category rows for gameId={_gameId}");
                _allRows = new List<ManageAchievementsCategoryItem>();
                _definitionOrderedRows = new List<ManageAchievementsCategoryItem>();
                _searchIndex.Clear();
                _canonicalCategoryLabelFilterOptions = new List<string>();
                ReplaceAchievementRows(_allRows);
                ReplaceCategoryRows(Array.Empty<ManageAchievementsCategoryMetadataItem>());
                CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, new List<string>());
                _selectedCategoryLabelFilters.Clear();
                OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
                HasAchievements = false;
                HasCustomOverrides = false;
                SetCustomCategoryMetadataState(hasOrder: false, hasNames: false, hasArt: false, hasSummaryCategory: false);
                SetCategoryImageStatus(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
                RefreshCategoryMetadataState();
            }
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

        public void ToggleReveal(ManageAchievementsCategoryItem item)
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
            var filtered = _allRows.AsEnumerable();
            var searchQuery = SearchQuery.From(SearchText);

            if (!ShowHidden)
            {
                filtered = filtered.Where(a => !(a.Hidden && !a.Unlocked));
            }

            filtered = filtered.Where(a => a.Unlocked ? ShowUnlocked : ShowLocked);

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

            ReplaceAchievementRows(filtered.ToList());
        }

        private void ReplaceAchievementRows(IEnumerable<ManageAchievementsCategoryItem> rows)
        {
            CollectionHelper.Replace(AchievementRows, rows);
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

        private static string NormalizeCategory(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string NormalizeText(string value)
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

        private ObservableCollection<CategoryTypeSelectionOption> CreateCategoryTypeOptions(
            IReadOnlyList<string> categoryTypes,
            Action onSelectionChanged)
        {
            var options = new ObservableCollection<CategoryTypeSelectionOption>(
                categoryTypes
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

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
