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
    public sealed class ManageAchievementsCategoryViewModel : ObservableObject
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
        private string _categoryImageStatusText;
        private bool _categoryImageStatusIsError;

        /// <summary>
        /// Raised after category metadata (order, art overrides, summary-category selection)
        /// has been written to the custom data store, so the hosting window can refresh
        /// state that depends on it (e.g. the game cover image).
        /// </summary>
        public event EventHandler CategoryMetadataPersisted;

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
            CategoryRows = new ObservableCollection<ManageAchievementsCategoryMetadataItem>();
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
            OpenCategoryImagesFolderCommand = new RelayCommand(_ => OpenCategoryImagesFolder());

            ReloadData();
        }

        public ObservableCollection<ManageAchievementsCategoryItem> AchievementRows { get; }
        public ObservableCollection<ManageAchievementsCategoryMetadataItem> CategoryRows { get; }
        public ObservableCollection<string> CategoryLabelOptions { get; }
        public ObservableCollection<string> CategoryLabelFilterOptions { get; }
        public ObservableCollection<CategoryTypeSelectionOption> TypeSelectionOptions { get; }
        public ObservableCollection<CategoryTypeSelectionOption> TypeFilterOptions { get; }

        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand OpenCategoryImagesFolderCommand { get; }
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
                CollectionHelper.SynchronizeCollection(CategoryLabelOptions, new List<string>());
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

        public bool ResetCategoryOverrides()
        {
            var categoryOverrides = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap();
            if (categoryOverrides.Count == 0 && categoryTypeOverrides.Count == 0)
            {
                return false;
            }

            var emptyCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var emptyCategoryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PersistCategoryOverrideMaps(emptyCategories, emptyCategoryTypes);
            ApplyCategoryOverrideMapsToRows(emptyCategories, emptyCategoryTypes);
            return true;
        }

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

            if (categoryChanged || categoryTypeChanged)
            {
                PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
                ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            }

            return true;
        }

        public bool SetCategoryTypeForSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            string categoryType,
            bool isSelected)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedType = AchievementCategoryTypeHelper.Normalize(categoryType);
            if (string.IsNullOrWhiteSpace(normalizedType))
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
                var updatedCategoryType = AchievementCategoryTypeHelper.WithCategoryType(
                    currentEffectiveCategoryType, normalizedType, isSelected);

                if (string.Equals(updatedCategoryType, currentEffectiveCategoryType, StringComparison.Ordinal))
                {
                    continue;
                }

                var providerCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.ProviderCategoryType);
                if (string.Equals(updatedCategoryType, providerCategoryType, StringComparison.Ordinal))
                {
                    // Result matches the provider value: drop the override so the row is no
                    // longer flagged as customized.
                    if (categoryTypeOverrideMap.Remove(apiName))
                    {
                        categoryTypeChanged = true;
                    }
                }
                else if (!categoryTypeOverrideMap.TryGetValue(apiName, out var existingCategoryType) ||
                         !string.Equals(existingCategoryType, updatedCategoryType, StringComparison.Ordinal))
                {
                    categoryTypeOverrideMap[apiName] = updatedCategoryType;
                    categoryTypeChanged = true;
                }
            }

            if (!categoryTypeChanged)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
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

            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
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

            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            return true;
        }

        public bool RenameCategoryLabel(string sourceCategoryLabel, string targetCategoryLabel)
        {
            var normalizedSourceCategory = AchievementCategoryTypeHelper.NormalizeCategory(sourceCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedSourceCategory))
            {
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
            if (string.IsNullOrWhiteSpace(normalizedSourceCategory))
            {
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

        /// <summary>
        /// Re-points every achievement whose effective category equals <paramref name="normalizedSource"/>
        /// to <paramref name="normalizedTarget"/> in <paramref name="categoryOverrideMap"/>. When
        /// <paramref name="categoryTypeOverrideMap"/> is non-null (merge), also replaces each moved
        /// achievement's group-based type tags with <paramref name="targetGroupTypes"/>. An override is
        /// removed rather than set when the resulting value matches the achievement's provider default.
        /// Returns true when at least one achievement was affected and something changed.
        /// </summary>
        private bool ReassignEffectiveCategoryRows(
            string normalizedSource,
            string normalizedTarget,
            Dictionary<string, string> categoryOverrideMap,
            Dictionary<string, string> categoryTypeOverrideMap,
            IReadOnlyList<string> targetGroupTypes)
        {
            var affectedCount = 0;
            var changed = false;

            foreach (var item in _allRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var effectiveCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.Category);
                if (!string.Equals(effectiveCategory, normalizedSource, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                affectedCount++;

                var providerCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.ProviderCategory);
                if (string.Equals(providerCategory, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    if (categoryOverrideMap.Remove(apiName))
                    {
                        changed = true;
                    }
                }
                else if (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                         !string.Equals(existingCategory, normalizedTarget, StringComparison.Ordinal))
                {
                    categoryOverrideMap[apiName] = normalizedTarget;
                    changed = true;
                }

                if (categoryTypeOverrideMap == null)
                {
                    continue;
                }

                var newType = AchievementCategoryTypeHelper.NormalizeOrDefault(
                    AchievementCategoryTypeHelper.ReplaceGroupTypes(item.CategoryType, targetGroupTypes));
                var providerType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.ProviderCategoryType);
                if (string.Equals(newType, providerType, StringComparison.OrdinalIgnoreCase))
                {
                    if (categoryTypeOverrideMap.Remove(apiName))
                    {
                        changed = true;
                    }
                }
                else if (!categoryTypeOverrideMap.TryGetValue(apiName, out var existingType) ||
                         !string.Equals(existingType, newType, StringComparison.Ordinal))
                {
                    categoryTypeOverrideMap[apiName] = newType;
                    changed = true;
                }
            }

            return affectedCount > 0 && changed;
        }

        /// <summary>
        /// The group-based type signature (Base/DLC/Update/Subset) shared by the achievements currently
        /// in <paramref name="targetLabel"/>, picking the most common signature so a coherent single
        /// group wins (never Base+DLC). Empty when the target category carries no group-based type.
        /// </summary>
        private IReadOnlyList<string> ResolveGroupTypesForCategory(string targetLabel)
        {
            var normalizedTarget = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetLabel);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var bySignature = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            foreach (var item in _allRows.Where(row => row != null))
            {
                var effectiveCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.Category);
                if (!string.Equals(effectiveCategory, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var group = AchievementCategoryTypeHelper.GetGroupTypeComponents(item.CategoryType);
                var signature = string.Join("|", group);
                counts.TryGetValue(signature, out var count);
                counts[signature] = count + 1;
                if (!bySignature.ContainsKey(signature))
                {
                    bySignature[signature] = group;
                }
            }

            if (counts.Count == 0)
            {
                return Array.Empty<string>();
            }

            var bestSignature = counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .First()
                .Key;

            return bySignature[bestSignature];
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
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(orderedLabels);
            var rows = new List<ManageAchievementsCategoryMetadataItem>();

            foreach (var label in orderedLabels)
            {
                if (!groups.TryGetValue(label, out var bucket) || bucket.Count == 0)
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
                        string.Equals(summaryCategory.Label, label, StringComparison.OrdinalIgnoreCase)));
            }

            HasCustomCategoryNames = rows.Any(row =>
                !string.Equals(row.CategoryLabel, row.ProviderCategoryLabel, StringComparison.OrdinalIgnoreCase));
            ReplaceCategoryRows(rows);
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

        private void PersistCategoryOverrideMaps(
            IReadOnlyDictionary<string, string> categoryOverrideMap,
            IReadOnlyDictionary<string, string> categoryTypeOverrideMap)
        {
            _achievementOverridesService.SetAchievementCategoryOverrides(
                _gameId,
                categoryOverrideMap,
                categoryTypeOverrideMap);
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

            if (currentImages != null &&
                currentImages.TryGetValue(normalizedSource, out var sourceImages) &&
                sourceImages != null)
            {
                if (!nextImages.TryGetValue(normalizedTarget, out var targetImages) || targetImages == null)
                {
                    nextImages[normalizedTarget] = sourceImages.Clone();
                }
                else if (string.IsNullOrWhiteSpace(targetImages.Art))
                {
                    targetImages.Art = sourceImages.Art;
                }
            }

            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            if (summaryCategory != null &&
                string.Equals(summaryCategory.Label, normalizedSource, StringComparison.OrdinalIgnoreCase))
            {
                summaryCategory = new GameSummaryCategoryData
                {
                    Label = normalizedTarget,
                    ProviderLabel = summaryCategory.ProviderLabel
                };
            }

            _achievementOverridesService.SetAchievementCategoryMetadata(_gameId, nextOrder, nextImages, summaryCategory);
            RaiseCategoryMetadataPersisted();
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

        private void ApplyCategoryOverrideMapsToRows(
            IReadOnlyDictionary<string, string> categoryOverrideMap,
            IReadOnlyDictionary<string, string> categoryTypeOverrideMap)
        {
            foreach (var item in _allRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var category = categoryOverrideMap != null &&
                               categoryOverrideMap.TryGetValue(apiName, out var categoryOverride) &&
                               !string.IsNullOrWhiteSpace(categoryOverride)
                    ? categoryOverride
                    : item.ProviderCategory;
                var categoryType = categoryTypeOverrideMap != null &&
                                   categoryTypeOverrideMap.TryGetValue(apiName, out var categoryTypeOverride) &&
                                   !string.IsNullOrWhiteSpace(categoryTypeOverride)
                    ? categoryTypeOverride
                    : item.ProviderCategoryType;

                item.Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(category);
                item.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(categoryType);
            }

            _searchIndex.Rebuild(_allRows);
            HasCustomOverrides =
                (categoryOverrideMap?.Count ?? 0) > 0 ||
                (categoryTypeOverrideMap?.Count ?? 0) > 0;
            RefreshCategoryLabelOptions();

            // Rows update their bound Type/Category cells in place via property change, so a
            // full collection rebuild is unnecessary for an edit that does not change which
            // rows are visible. Only re-filter when a filter keyed on category, type, or search
            // text is active; otherwise skip ApplyFilter to avoid the ReplaceAll Reset that
            // regenerates every DataGrid row and causes a visible flicker.
            if (IsVisibilityFilteredByCategoryEdit())
            {
                ApplyFilter();
            }
        }

        private bool IsVisibilityFilteredByCategoryEdit()
        {
            return SearchQuery.From(SearchText).HasValue
                || GetSelectedCategoryTypeFilterValues().Count > 0
                || _selectedCategoryLabelFilters.Count > 0;
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

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }

    public sealed class ManageAchievementsCategoryItem : AchievementDisplayItem
    {
        private bool _isSelected;

        public string ProviderCategory { get; set; }

        public string ProviderCategoryType { get; set; }

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

    public sealed class ManageAchievementsCategoryMetadataItem : ObservableObject
    {
        private readonly string _gameIdText;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private string _baselineArtOverrideValue;
        private string _artOverrideValue;
        private string _renameOverrideText;
        private bool _baselineIsSummarySelected;
        private bool _isSummarySelected;

        private ManageAchievementsCategoryMetadataItem(
            string gameIdText,
            string fileStem,
            ManagedCustomIconService managedCustomIconService)
        {
            _gameIdText = gameIdText;
            FileStem = fileStem;
            _managedCustomIconService = managedCustomIconService;
        }

        public string CategoryLabel { get; private set; }

        public string CategoryDisplay => AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(CategoryLabel);

        public string ProviderCategoryLabel { get; private set; }

        public string ProviderCategoryDisplay => AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(ProviderCategoryLabel);

        public string RenameOverrideText
        {
            get => _renameOverrideText;
            set
            {
                var nextValue = value ?? string.Empty;
                if (SetValueAndReturn(ref _renameOverrideText, nextValue))
                {
                    OnPropertyChanged(nameof(HasRenameOverride));
                }
            }
        }

        public bool HasRenameOverride => !string.IsNullOrWhiteSpace(GetNormalizedRenameOverrideValue());

        public string FileStem { get; }

        public int TotalAchievements { get; private set; }

        public int UnlockedAchievements { get; private set; }

        public string ProgressText => $"{UnlockedAchievements:N0}/{TotalAchievements:N0}";

        public string DefaultArtPath { get; private set; }

        public string ArtOverrideValue
        {
            get => _artOverrideValue;
            set => SetOverrideValue(value);
        }

        public string ArtOverrideText
        {
            get => GetDisplayOverrideValue();
            set => SetOverrideValue(value);
        }

        public string ArtPreviewPath => BuildPreviewPath(
            ResolvePreviewOverrideValue(GetNormalizedArtOverrideValue()) ?? DefaultArtPath);

        public bool HasArtOverrideValidationError => !IsValidOverrideValueOrBlank(ArtOverrideValue);

        public bool HasValidationErrors => HasArtOverrideValidationError;

        public bool IsSummarySelected
        {
            get => _isSummarySelected;
            set
            {
                if (SetValueAndReturn(ref _isSummarySelected, value))
                {
                    OnPropertyChanged(nameof(HasChanges));
                }
            }
        }

        public bool HasChanges =>
            !string.Equals(GetNormalizedArtOverrideValue(), _baselineArtOverrideValue, StringComparison.Ordinal) ||
            _isSummarySelected != _baselineIsSummarySelected;

        public static ManageAchievementsCategoryMetadataItem Create(
            string categoryLabel,
            string providerCategoryLabel,
            IReadOnlyList<ManageAchievementsCategoryItem> achievements,
            CategoryImageOverrideData imageOverride,
            string gameIdText,
            string fileStem,
            ManagedCustomIconService managedCustomIconService,
            bool isSummarySelected = false)
        {
            var normalizedLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryLabel);
            var normalizedProviderLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(providerCategoryLabel);
            var playniteGameId = Guid.TryParse(gameIdText, out var parsedGameId) ? parsedGameId : (Guid?)null;
            var row = new ManageAchievementsCategoryMetadataItem(gameIdText, fileStem, managedCustomIconService)
            {
                CategoryLabel = normalizedLabel,
                ProviderCategoryLabel = normalizedProviderLabel,
                TotalAchievements = achievements?.Count ?? 0,
                UnlockedAchievements = achievements?.Count(item => item?.Unlocked == true) ?? 0,
                // Provider-supplied defaults are the true revert target, ahead of game art.
                // They are keyed by the provider label so renamed rows still find them.
                DefaultArtPath = CategoryDefaultImageResolver.Resolve(playniteGameId, normalizedProviderLabel) ??
                                 ResolveSharedImage(achievements, item => item?.GameIconPath) ??
                                 ResolveSharedImage(achievements, item => item?.GameCoverPath)
            };

            row._renameOverrideText = string.Equals(row.CategoryLabel, row.ProviderCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : row.CategoryLabel;
            row._baselineArtOverrideValue = row.ResolveOverrideInputValue(imageOverride?.Art);
            row._artOverrideValue = row._baselineArtOverrideValue ?? string.Empty;
            row._baselineIsSummarySelected = isSummarySelected;
            row._isSummarySelected = isSummarySelected;
            return row;
        }

        public void SetOverrideValue(string value)
        {
            var nextValue = ResolveOverrideInputValue(value) ?? string.Empty;
            if (string.Equals(_artOverrideValue, nextValue, StringComparison.Ordinal))
            {
                return;
            }

            _artOverrideValue = nextValue;
            NotifyOverrideStateChanged();
        }

        public void ClearOverride()
        {
            SetOverrideValue(null);
        }

        public void CommitCurrentOverridesAsBaseline()
        {
            _baselineArtOverrideValue = GetNormalizedArtOverrideValue();
            _baselineIsSummarySelected = _isSummarySelected;
            NotifyOverrideStateChanged();
        }

        /// <summary>
        /// The art value to write to the store: the current value when valid, otherwise
        /// the last persisted one, so an invalid pending edit never reaches the store.
        /// </summary>
        public string GetPersistableArtOverrideValue()
        {
            return HasArtOverrideValidationError
                ? NormalizeOverrideValue(_baselineArtOverrideValue)
                : GetNormalizedArtOverrideValue();
        }

        public string GetNormalizedArtOverrideValue()
        {
            return NormalizeOverrideValue(ArtOverrideValue);
        }

        public string GetNormalizedRenameOverrideValue()
        {
            var normalized = (RenameOverrideText ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public void ResetRenameOverrideTextFromCurrentCategory()
        {
            RenameOverrideText = string.Equals(CategoryLabel, ProviderCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : CategoryLabel;
        }

        private string GetDisplayOverrideValue()
        {
            return _managedCustomIconService.GetManagedDisplayPath(_artOverrideValue, _gameIdText) ?? string.Empty;
        }

        private string ResolveOverrideInputValue(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return _managedCustomIconService.ResolveManagedDisplayPath(normalized, _gameIdText);
        }

        private string ResolvePreviewOverrideValue(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (Path.IsPathRooted(normalized) || IsHttpUrl(normalized) || Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                return normalized;
            }

            return normalized;
        }

        private bool IsValidOverrideValueOrBlank(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (IsHttpUrl(normalized))
            {
                return Uri.TryCreate(normalized, UriKind.Absolute, out _);
            }

            return IsManagedLocalOverride(normalized) || IsExistingRootedPath(normalized);
        }

        private bool IsManagedLocalOverride(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Path.IsPathRooted(value) &&
                   File.Exists(value) &&
                   _managedCustomIconService.IsManagedCustomIconPath(value, _gameIdText);
        }

        private static bool IsExistingRootedPath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Path.IsPathRooted(value) &&
                   File.Exists(value);
        }

        private void NotifyOverrideStateChanged()
        {
            OnPropertyChanged(nameof(ArtOverrideValue));
            OnPropertyChanged(nameof(ArtOverrideText));
            OnPropertyChanged(nameof(HasArtOverrideValidationError));
            OnPropertyChanged(nameof(ArtPreviewPath));
            OnPropertyChanged(nameof(HasValidationErrors));
            OnPropertyChanged(nameof(HasChanges));
        }

        private static string ResolveSharedImage(
            IEnumerable<ManageAchievementsCategoryItem> source,
            Func<ManageAchievementsCategoryItem, string> selector)
        {
            string image = null;
            foreach (var item in source ?? Enumerable.Empty<ManageAchievementsCategoryItem>())
            {
                var candidate = selector?.Invoke(item);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (image == null)
                {
                    image = candidate;
                }
                else if (!string.Equals(image, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return image;
        }

        private static string BuildPreviewPath(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            return string.IsNullOrWhiteSpace(normalized)
                ? AchievementIconResolver.GetDefaultIcon()
                : AchievementIconResolver.ApplyCacheBust(normalized);
        }

        private static string NormalizeOverrideValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
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
