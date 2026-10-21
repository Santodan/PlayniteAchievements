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
        private bool _hasAchievements;
        private bool _hasCustomCategoryOrder;
        private bool _hasCustomCategoryNames;
        private bool _hasCustomCategoryArt;
        private bool _hasCustomSummaryCategory;
        private bool _isEnforcingSummarySelection;
        private bool _isPersistingCategoryMetadata;
        private bool _isStampingFilterScopes;
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

            CategoryRows = new BulkObservableCollection<ManageAchievementsCategoryMetadataItem>();
            AchievementTagRegistry.Changed += OnTagsChanged;
            OpenCategoryImagesFolderCommand = new RelayCommand(_ => OpenCategoryImagesFolder());
            IndentCategoryCommand = new RelayCommand(parameter => IndentOrOutdentFromCommand(parameter, indent: true));
            OutdentCategoryCommand = new RelayCommand(parameter => IndentOrOutdentFromCommand(parameter, indent: false));

            ReloadData();
        }
        /// <summary>
        /// Bulk rather than plain observable: every rebuild replaces the whole list, and one Add
        /// notification per row makes the grid realize a container - and lay it out - per row. That
        /// cost lands after the rebuild call returns, which is why it does not show up in the
        /// Categories.Moves spans while still being what a click waits on.
        /// </summary>
        public BulkObservableCollection<ManageAchievementsCategoryMetadataItem> CategoryRows { get; }
        public Guid GameId => _gameId;
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

        public void ReloadData()
        {
            // Rebuilds every achievement row and the category tree behind them, on the UI thread.
            // This is the tab a bulk category session lives in, so it is the scope most likely to
            // name the stall.
            using (PerfScope.Start(_logger, "Manage.Category.ReloadData", thresholdMs: 25))
            {
                ReloadDataCore();
            }
        }

        private void ReloadDataCore()
        {
            try
            {
                var hydratedGameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var rawGameData = _gameDataSnapshotProvider.GetRawGameData();
                var rawAchievements = rawGameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();
                var categoryOverrides = GetCurrentCategoryOverrideMap();
                var tagOverrides = GetCurrentTagOverrideMap();
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

                // Per-game invariants hoisted out of the row loop: the appearance snapshot and
                // category art/order resolution are identical for every row in this pass.
                var appearanceSnapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    projectionSource?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();
                var resolvedCustomData = GameCustomDataLookup.ResolveGameCustomData(_gameId, _settings?.Persisted);

                _allRows = orderedAchievements.Select(a =>
                {
                    var apiName = (a.ApiName ?? string.Empty).Trim();
                    var providerCategory = NormalizeCategory(a.Category);
                    var providerTags = AchievementTagHelper.Normalize(a.Tags);

                    string overrideCategory = null;
                    var hasCategoryOverride = !string.IsNullOrWhiteSpace(apiName) &&
                                              categoryOverrides.TryGetValue(apiName, out overrideCategory) &&
                                              !string.IsNullOrWhiteSpace(overrideCategory);
                    string overrideTags = null;
                    // "" is an override too: the user removed every tag.
                    var hasTagOverride = !string.IsNullOrWhiteSpace(apiName) &&
                                         tagOverrides.TryGetValue(apiName, out overrideTags) &&
                                         overrideTags != null;
                    var effectiveCategory = AchievementCategoryHelper.NormalizeCategoryOrDefault(
                        hasCategoryOverride ? overrideCategory : providerCategory);
                    var effectiveTags = AchievementTagHelper.ApplyOverride(
                        providerTags,
                        hasTagOverride ? overrideTags : null);

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
                        IsRevealed = projected.IsRevealed,
                        ProviderCategory = AchievementCategoryHelper.NormalizeCategoryOrDefault(providerCategory),
                        ProviderTags = AchievementTagHelper.Normalize(providerTags),
                        Category = effectiveCategory,
                        Tags = effectiveTags,
                        ProviderCategoryKind = CategoryKindHelper.Normalize(a.CategoryKind),
                        CategoryKind = CategoryKindHelper.ResolveEffective(
                            resolvedCustomData?.GetStoredCategoryKind(effectiveCategory),
                            a.CategoryKind)
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

                HasAchievements = _allRows.Count > 0;
                RefreshCategoryRows();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading category rows for gameId={_gameId}");
                _allRows = new List<ManageAchievementsCategoryItem>();
                _definitionOrderedRows = new List<ManageAchievementsCategoryItem>();
                ReplaceCategoryRows(Array.Empty<ManageAchievementsCategoryMetadataItem>());
                HasAchievements = false;
                SetCustomCategoryMetadataState(hasOrder: false, hasNames: false, hasArt: false, hasSummaryCategory: false);
                SetCategoryImageStatus(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
                RefreshCategoryMetadataState();
            }
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

        // Tags added, renamed or deleted elsewhere (the Tags tab, settings) reach the menus and rows here.
        private void OnTagsChanged(object sender, AchievementTagsChangedEventArgs e)
        {
            if (e?.GameId != null && e.GameId != _gameId)
            {
                return;
            }

            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                foreach (var row in _allRows.Where(row => row != null))
                {
                    row.RefreshTagPresentation();
                }
            }));
        }

        /// <summary>Stops listening for tag list changes; the tab calls it when it is torn down.</summary>
        public void Detach()
        {
            AchievementTagRegistry.Changed -= OnTagsChanged;
        }

        public static string GetTagDisplayName(string tag, Guid? gameId = null)
        {
            return AchievementClassificationFilter.GetDisplayName(tag, gameId);
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
