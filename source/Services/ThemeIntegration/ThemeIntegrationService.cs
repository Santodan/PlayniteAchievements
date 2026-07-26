using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.Services.UI;
#if !TEST
using PlayniteAchievements.Services.Library;
#endif
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    /// <summary>
    /// Service for theme integration, handling both per-game and all-games
    /// achievement data for desktop and fullscreen themes. Manages snapshot
    /// building, refresh commands, and theme surface updates.
    /// </summary>
    public sealed class ThemeIntegrationService : IDisposable
    {
        private static readonly List<AchievementDetail> EmptyAchievementList = new List<AchievementDetail>();
        private static readonly AchievementRarityStats EmptyRarityStats = new AchievementRarityStats();

        private delegate bool CommandParameterNormalizer(object parameter, out string normalizedKey);

        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly RefreshRuntime _refreshService;
        private readonly AchievementDataService _achievementDataService;
#if !TEST
        private readonly LibraryProjectionService _libraryProjectionService;
#endif
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly IFriendCacheManager _friendCache;
        private readonly FriendsOverviewDataCoordinator _friendsOverviewDataCoordinator;
        private readonly bool _ownsFriendsOverviewDataCoordinator;
        private readonly Func<RefreshRequest, string, bool, Action<bool>, Task> _runRefreshWithGlobalProgressAsync;
        private readonly Action<Guid> _openManageAchievementsView;
        private readonly Func<AchievementHotkeyTargetResolution> _resolveRunningGameTarget;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly FullscreenWindowService _windowService;
        private readonly ThemeRuntimeState _runtimeState = new ThemeRuntimeState();

        private readonly object _refreshLock = new object();
        private CancellationTokenSource _refreshCts;
        private static readonly TimeSpan LibraryRefreshDelay = TimeSpan.FromMilliseconds(500);
#if TEST
        private static readonly TimeSpan FriendRefreshDelay = TimeSpan.Zero;
#else
        private static readonly TimeSpan FriendRefreshDelay = TimeSpan.FromMilliseconds(500);
#endif
        private const int ThemeRecentUnlockSummaryLimit = 10;

        private readonly object _updateGate = new object();
        private Task _updateRunner;
        private int _requestVersion;
        private int _processedVersion;
        private Guid? _requestedGameId;
        private bool _requestedForceSelectedGameRefresh;
        private CancellationTokenSource _activeUpdateCts;
        private Guid? _appliedGameId;
        private DateTime _appliedLastUpdatedUtc;
        private readonly object _friendRefreshGate = new object();
        private Task _friendRefreshRunner;
        private CancellationTokenSource _friendRefreshCts;
        private int _friendRefreshRequestVersion;
        private int _friendRefreshAppliedVersion;
        private volatile bool _hasFriendThemeConsumers;

        // On-demand locked-row rows for the dynamic friend pair scope (friend + game both
        // selected). The friend runtime state carries unlocked rows only; the selected game's
        // full rows (locked included) are fetched once per game key and swapped in, mirroring
        // FriendsOverviewViewModel's pair comparison.
        private List<FriendAchievementDisplayItem> _dynamicFriendPairRows;
        private string _dynamicFriendPairKey;
        private string _dynamicFriendPairKeyInFlight;
        private int _dynamicFriendPairFetchVersion;

        // Sticky-true once any theme friend binding is read; consulted by the friends overview
        // coordinator's release path so a friend-consuming theme keeps the snapshot alive.
        internal bool HasFriendThemeConsumers => _hasFriendThemeConsumers;

        private bool _fullscreenInitialized;
        private bool _hasLoadedLibraryState;
        private bool _lastLibraryRefreshIncludedHeavyAchievementLists = true;
        private readonly Dictionary<Guid, RelayCommand> _openViewAchievementsCommands =
            new Dictionary<Guid, RelayCommand>();
        private readonly Dictionary<Guid, RelayCommand> _openManageAchievementsCommands =
            new Dictionary<Guid, RelayCommand>();

        internal ThemeIntegrationService(
            IPlayniteAPI api,
            RefreshRuntime refreshRuntime,
            AchievementDataService achievementDataService,
#if !TEST
            LibraryProjectionService libraryProjectionService,
#endif
            RefreshEntryPoint refreshEntryPoint,
            PlayniteAchievementsSettings settings,
            FullscreenWindowService windowService,
            ILogger logger,
            Func<RefreshRequest, string, bool, Action<bool>, Task> runRefreshWithGlobalProgressAsync = null,
            Action<Guid> openManageAchievementsView = null,
            IFriendCacheManager friendCache = null,
            FriendsOverviewDataCoordinator friendsOverviewDataCoordinator = null,
            Func<AchievementHotkeyTargetResolution> resolveRunningGameTarget = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
#if !TEST
            _libraryProjectionService = libraryProjectionService;
#endif
            _refreshCoordinator = refreshEntryPoint ?? throw new ArgumentNullException(nameof(refreshEntryPoint));
            _friendCache = friendCache;
            _ownsFriendsOverviewDataCoordinator = friendsOverviewDataCoordinator == null && friendCache != null;
            _friendsOverviewDataCoordinator = friendsOverviewDataCoordinator ??
                (friendCache != null
                    ? new FriendsOverviewDataCoordinator(friendCache, () => _settings?.Persisted, logger)
                    : null);
            _runRefreshWithGlobalProgressAsync = runRefreshWithGlobalProgressAsync ?? RunRefreshWithoutGlobalProgressAsync;
            _openManageAchievementsView = openManageAchievementsView;
            _resolveRunningGameTarget = resolveRunningGameTarget ?? ResolveRunningGameTargetFromApi;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
            _logger = logger;
            _settings.DynamicThemeDefaultsChanged += Settings_DynamicThemeDefaultsChanged;

            var openOverviewCommand = new RelayCommand(_ => OpenOverviewWindow());
            var openSelectedGameCommand = new RelayCommand(OpenSelectedGameWindowCommand);
            var openViewAchievementsCommand = new RelayCommand(OpenViewAchievementsWindowCommand);
            var openManageAchievementsCommand = new RelayCommand(OpenManageAchievementsWindow);

            // Command surfaces referenced by themes.
            _settings.OpenFullscreenAchievementWindow = openSelectedGameCommand;
            _settings.OpenAchievementWindow = openOverviewCommand;
            _settings.OpenGameAchievementWindow = openSelectedGameCommand;
            _settings.OpenViewAchievementsWindow = openViewAchievementsCommand;
            _settings.OpenManageAchievementsWindow = openManageAchievementsCommand;
            _settings.SetDynamicAchievementsGameCommand = new RelayCommand(SetDynamicAchievementsGame);
            _settings.FilterDynamicAchievementsByRunningGameCommand = new RelayCommand(_ => FilterDynamicAchievementsByRunningGame());
            _settings.SingleGameRefreshCommand = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Single));
            _settings.RecentRefreshCommand = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Recent));
            _settings.FavoritesRefreshCommand = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Favorites));
            _settings.FullRefreshCommand = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Full));
            _settings.InstalledRefreshCommand = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Installed));
            _settings.SetDynamicAchievementsFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicAchievementsFilterCommand),
                (object parameter, out string key) => DynamicThemeFilterExpression.TryNormalize(
                    parameter,
                    DynamicThemeOptionGroups.AchievementFilterKeyMap,
                    out key),
                () => _runtimeState.SelectedGameAchievements.FilterKey,
                key =>
                {
                    _runtimeState.SelectedGameAchievements.HasUserSelection = true;
                    _runtimeState.SelectedGameAchievements.FilterKey = key;
                },
                ApplyDynamicSelectedGameBindings,
                ThemeDelegatedPropertyCatalog.SingleGameTheme);
            _settings.SortDynamicAchievementsCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SortDynamicAchievementsCommand),
                DynamicThemeOptionGroups.SelectedGameAchievementSortKeyMap,
                () => _runtimeState.SelectedGameAchievements.SortKey,
                key =>
                {
                    _runtimeState.SelectedGameAchievements.HasUserSelection = true;
                    _runtimeState.SelectedGameAchievements.SortKey = key;
                },
                ApplyDynamicSelectedGameBindings,
                ThemeDelegatedPropertyCatalog.SingleGameTheme);
            _settings.SetDynamicAchievementsSortDirectionCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicAchievementsSortDirectionCommand),
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                () => _runtimeState.SelectedGameAchievements.SortDirectionKey,
                key =>
                {
                    _runtimeState.SelectedGameAchievements.HasUserSelection = true;
                    _runtimeState.SelectedGameAchievements.SortDirectionKey = key;
                },
                ApplyDynamicSelectedGameBindings,
                ThemeDelegatedPropertyCatalog.SingleGameTheme);
            _settings.SetDynamicAchievementsCategoryLabelFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicAchievementsCategoryLabelFilterCommand),
                TryNormalizeSelectedGameCategoryLabel,
                () => _runtimeState.SelectedGameAchievements.CategoryLabelKey,
                key =>
                {
                    _runtimeState.SelectedGameAchievements.HasUserSelection = true;
                    _runtimeState.SelectedGameAchievements.CategoryLabelKey = key;
                },
                ApplyDynamicSelectedGameBindings,
                ThemeDelegatedPropertyCatalog.SingleGameTheme);
            _settings.SetDynamicCategorySummariesFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicCategorySummariesFilterCommand),
                DynamicThemeOptionGroups.CategorySummaryFilterKeyMap,
                () => _runtimeState.CategorySummaries.FilterKey,
                key =>
                {
                    _runtimeState.CategorySummaries.HasUserSelection = true;
                    _runtimeState.CategorySummaries.FilterKey = key;
                },
                ApplyDynamicCategorySummaryBindings,
                ThemeDelegatedPropertyCatalog.SingleGameTheme);
            _settings.SortDynamicCategorySummariesCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SortDynamicCategorySummariesCommand),
                DynamicThemeOptionGroups.CategorySummarySortKeyMap,
                () => _runtimeState.CategorySummaries.SortKey,
                key =>
                {
                    _runtimeState.CategorySummaries.HasUserSelection = true;
                    _runtimeState.CategorySummaries.SortKey = key;
                },
                ApplyDynamicCategorySummaryBindings,
                ThemeDelegatedPropertyCatalog.SingleGameTheme);
            _settings.SetDynamicCategorySummariesSortDirectionCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicCategorySummariesSortDirectionCommand),
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                () => _runtimeState.CategorySummaries.SortDirectionKey,
                key =>
                {
                    _runtimeState.CategorySummaries.HasUserSelection = true;
                    _runtimeState.CategorySummaries.SortDirectionKey = key;
                },
                ApplyDynamicCategorySummaryBindings,
                ThemeDelegatedPropertyCatalog.SingleGameTheme);
            _settings.FilterDynamicLibraryAchievementsByProviderCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.FilterDynamicLibraryAchievementsByProviderCommand),
                TryNormalizeProviderKey,
                () => _runtimeState.LibraryAchievements.ProviderKey,
                key =>
                {
                    _runtimeState.LibraryAchievements.HasUserSelection = true;
                    _runtimeState.LibraryAchievements.ProviderKey = key;
                },
                ApplyDynamicLibraryAchievementBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.FilterDynamicLibraryAchievementsByRunningGameCommand = new RelayCommand(_ => FilterDynamicLibraryAchievementsByRunningGame());
            _settings.SetDynamicLibraryAchievementsFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsFilterCommand),
                (object parameter, out string key) => DynamicThemeFilterExpression.TryNormalize(
                    parameter,
                    DynamicThemeOptionGroups.AchievementFilterKeyMap,
                    out key),
                () => _runtimeState.LibraryAchievements.FilterKey,
                key =>
                {
                    _runtimeState.LibraryAchievements.HasUserSelection = true;
                    _runtimeState.LibraryAchievements.FilterKey = key;
                },
                ApplyDynamicLibraryAchievementBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.SortDynamicLibraryAchievementsCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SortDynamicLibraryAchievementsCommand),
                DynamicThemeOptionGroups.LibraryAchievementSortKeyMap,
                () => _runtimeState.LibraryAchievements.SortKey,
                key =>
                {
                    _runtimeState.LibraryAchievements.HasUserSelection = true;
                    _runtimeState.LibraryAchievements.SortKey = key;
                },
                ApplyDynamicLibraryAchievementBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.SetDynamicLibraryAchievementsSortDirectionCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsSortDirectionCommand),
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                () => _runtimeState.LibraryAchievements.SortDirectionKey,
                key =>
                {
                    _runtimeState.LibraryAchievements.HasUserSelection = true;
                    _runtimeState.LibraryAchievements.SortDirectionKey = key;
                },
                ApplyDynamicLibraryAchievementBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.FilterDynamicGameSummariesByProviderCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByProviderCommand),
                TryNormalizeProviderKey,
                () => _runtimeState.GameSummaries.ProviderKey,
                key =>
                {
                    _runtimeState.GameSummaries.HasUserSelection = true;
                    _runtimeState.GameSummaries.ProviderKey = key;
                },
                ApplyDynamicGameSummaryBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.FilterDynamicGameSummariesByRunningGameCommand = new RelayCommand(_ => FilterDynamicGameSummariesByRunningGame());
            _settings.SetDynamicGameSummariesFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesFilterCommand),
                (object parameter, out string key) => DynamicThemeFilterExpression.TryNormalize(
                    parameter,
                    DynamicThemeOptionGroups.GameSummaryFilterKeyMap,
                    out key),
                () => _runtimeState.GameSummaries.FilterKey,
                key =>
                {
                    _runtimeState.GameSummaries.HasUserSelection = true;
                    _runtimeState.GameSummaries.FilterKey = key;
                },
                ApplyDynamicGameSummaryBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.SortDynamicGameSummariesCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SortDynamicGameSummariesCommand),
                DynamicThemeOptionGroups.GameSummarySortKeyMap,
                () => _runtimeState.GameSummaries.SortKey,
                key =>
                {
                    _runtimeState.GameSummaries.HasUserSelection = true;
                    _runtimeState.GameSummaries.SortKey = key;
                },
                ApplyDynamicGameSummaryBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.SetDynamicGameSummariesSortDirectionCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesSortDirectionCommand),
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                () => _runtimeState.GameSummaries.SortDirectionKey,
                key =>
                {
                    _runtimeState.GameSummaries.HasUserSelection = true;
                    _runtimeState.GameSummaries.SortDirectionKey = key;
                },
                ApplyDynamicGameSummaryBindings,
                ThemeDelegatedPropertyCatalog.DynamicAllGames);
            _settings.SetDynamicFriendScopeProviderCommand = new RelayCommand(SetDynamicFriendScopeProvider);
            _settings.SetDynamicFriendScopeUserCommand = new RelayCommand(SetDynamicFriendScopeUser);
            _settings.SetDynamicFriendScopeGameCommand = new RelayCommand(SetDynamicFriendScopeGame);
            _settings.ResetDynamicFriendScopeCommand = new RelayCommand(_ => ResetDynamicFriendScope());
            _settings.FilterDynamicFriendSummariesByRunningGameCommand = new RelayCommand(_ => FilterDynamicFriendScopeByRunningGame(nameof(PlayniteAchievementsSettings.FilterDynamicFriendSummariesByRunningGameCommand)));
            _settings.SetDynamicFriendSummariesFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicFriendSummariesFilterCommand),
                (object parameter, out string key) => DynamicThemeFilterExpression.TryNormalize(
                    parameter,
                    DynamicThemeOptionGroups.FriendSummaryFilterKeyMap,
                    out key),
                () => _runtimeState.FriendSummaries.FilterKey,
                key =>
                {
                    _runtimeState.FriendSummaries.HasUserSelection = true;
                    _runtimeState.FriendSummaries.FilterKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.SortDynamicFriendSummariesCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SortDynamicFriendSummariesCommand),
                DynamicThemeOptionGroups.FriendSummarySortKeyMap,
                () => _runtimeState.FriendSummaries.SortKey,
                key =>
                {
                    _runtimeState.FriendSummaries.HasUserSelection = true;
                    _runtimeState.FriendSummaries.SortKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.SetDynamicFriendSummariesSortDirectionCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicFriendSummariesSortDirectionCommand),
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                () => _runtimeState.FriendSummaries.SortDirectionKey,
                key =>
                {
                    _runtimeState.FriendSummaries.HasUserSelection = true;
                    _runtimeState.FriendSummaries.SortDirectionKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.FilterDynamicFriendGameSummariesByRunningGameCommand = new RelayCommand(_ => FilterDynamicFriendScopeByRunningGame(nameof(PlayniteAchievementsSettings.FilterDynamicFriendGameSummariesByRunningGameCommand)));
            _settings.SetDynamicFriendGameSummariesFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicFriendGameSummariesFilterCommand),
                (object parameter, out string key) => DynamicThemeFilterExpression.TryNormalize(
                    parameter,
                    DynamicThemeOptionGroups.GameSummaryFilterKeyMap,
                    out key),
                () => _runtimeState.FriendGameSummaries.FilterKey,
                key =>
                {
                    _runtimeState.FriendGameSummaries.HasUserSelection = true;
                    _runtimeState.FriendGameSummaries.FilterKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.SortDynamicFriendGameSummariesCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SortDynamicFriendGameSummariesCommand),
                DynamicThemeOptionGroups.GameSummarySortKeyMap,
                () => _runtimeState.FriendGameSummaries.SortKey,
                key =>
                {
                    _runtimeState.FriendGameSummaries.HasUserSelection = true;
                    _runtimeState.FriendGameSummaries.SortKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.SetDynamicFriendGameSummariesSortDirectionCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicFriendGameSummariesSortDirectionCommand),
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                () => _runtimeState.FriendGameSummaries.SortDirectionKey,
                key =>
                {
                    _runtimeState.FriendGameSummaries.HasUserSelection = true;
                    _runtimeState.FriendGameSummaries.SortDirectionKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.FilterDynamicFriendAchievementsByRunningGameCommand = new RelayCommand(_ => FilterDynamicFriendScopeByRunningGame(nameof(PlayniteAchievementsSettings.FilterDynamicFriendAchievementsByRunningGameCommand)));
            _settings.SetDynamicFriendAchievementsFilterCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsFilterCommand),
                (object parameter, out string key) => DynamicThemeFilterExpression.TryNormalize(
                    parameter,
                    DynamicThemeOptionGroups.AchievementFilterKeyMap,
                    out key),
                () => _runtimeState.FriendAchievements.FilterKey,
                key =>
                {
                    _runtimeState.FriendAchievements.HasUserSelection = true;
                    _runtimeState.FriendAchievements.FilterKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.SortDynamicFriendAchievementsCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SortDynamicFriendAchievementsCommand),
                DynamicThemeOptionGroups.LibraryAchievementSortKeyMap,
                () => _runtimeState.FriendAchievements.SortKey,
                key =>
                {
                    _runtimeState.FriendAchievements.HasUserSelection = true;
                    _runtimeState.FriendAchievements.SortKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.SetDynamicFriendAchievementsSortDirectionCommand = CreateDynamicCommand(
                nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsSortDirectionCommand),
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                () => _runtimeState.FriendAchievements.SortDirectionKey,
                key =>
                {
                    _runtimeState.FriendAchievements.HasUserSelection = true;
                    _runtimeState.FriendAchievements.SortDirectionKey = key;
                },
                ApplyDynamicFriendBindings,
                ThemeDelegatedPropertyCatalog.DynamicFriends);
            _settings.ResetDynamicAchievementsCommand = new RelayCommand(_ => ResetDynamicAchievementsToDefaults());
            _settings.ResetDynamicCategorySummariesCommand = new RelayCommand(_ => ResetDynamicCategorySummariesToDefaults());
            _settings.ResetDynamicLibraryAchievementsCommand = new RelayCommand(_ => ResetDynamicLibraryAchievementsToDefaults());
            _settings.ResetDynamicGameSummariesCommand = new RelayCommand(_ => ResetDynamicGameSummariesToDefaults());

            _runtimeState.Friends = FriendRuntimeState.Empty;
            ApplyDynamicThemeDefaultsFromSettings(notify: false);
            ApplyDynamicSelectedGameBindings(updateOptions: false);
            ApplyDynamicLibraryAchievementBindings(updateOptions: false);
            ApplyDynamicGameSummaryBindings(updateOptions: false);
            ApplyDynamicFriendBindings(updateOptions: false);
            ApplyDynamicOptionBindings();

            _refreshService.CacheInvalidated += RefreshService_CacheInvalidated;
            if (_friendCache != null)
            {
                _friendCache.FriendCacheInvalidated += FriendCache_FriendCacheInvalidated;
            }

            if (_friendsOverviewDataCoordinator != null)
            {
                _friendsOverviewDataCoordinator.SnapshotInvalidated += FriendsOverviewCoordinator_SnapshotInvalidated;
            }

            // The friend runtime state is built on demand: the first read of a friend data
            // property (or a friend scope command) registers demand and triggers the build.
            // Themes without friend bindings never pay the full friends-overview snapshot cost.
            _settings.ModernTheme.FriendDataRequested = EnsureFriendThemeDataLoaded;
        }

        public void Dispose()
        {
            try { _settings.ModernTheme.FriendDataRequested = null; } catch { }
            try { _settings.DynamicThemeDefaultsChanged -= Settings_DynamicThemeDefaultsChanged; } catch { }
            try { _refreshService.CacheInvalidated -= RefreshService_CacheInvalidated; } catch { }
            try
            {
                if (_friendsOverviewDataCoordinator != null)
                {
                    _friendsOverviewDataCoordinator.SnapshotInvalidated -= FriendsOverviewCoordinator_SnapshotInvalidated;
                }
            }
            catch { }
            try
            {
                if (_friendCache != null)
                {
                    _friendCache.FriendCacheInvalidated -= FriendCache_FriendCacheInvalidated;
                }
            }
            catch { }

            lock (_refreshLock)
            {
                try { _refreshCts?.Cancel(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                _refreshCts = null;
            }

            lock (_updateGate)
            {
                try { _activeUpdateCts?.Cancel(); } catch { }
                try { _activeUpdateCts?.Dispose(); } catch { }
                _activeUpdateCts = null;
            }

            lock (_friendRefreshGate)
            {
                try { _friendRefreshCts?.Cancel(); } catch { }
                try { _friendRefreshCts?.Dispose(); } catch { }
                _friendRefreshCts = null;
            }

            if (_ownsFriendsOverviewDataCoordinator)
            {
                try { _friendsOverviewDataCoordinator?.Dispose(); } catch { }
            }
        }

        public void NotifySelectionChanged(Guid? selectedGameId)
        {
            if (!selectedGameId.HasValue)
            {
                return;
            }

            EnsureFullscreenInitialized();
        }

        public void RequestUpdate(Guid? gameId, bool forceRefresh = false)
        {
            lock (_updateGate)
            {
                _requestVersion++;
                _requestedGameId = gameId;
                _requestedForceSelectedGameRefresh |= forceRefresh;
                if (_updateRunner == null || _updateRunner.IsCompleted)
                {
                    _updateRunner = RunUpdateLoopAsync();
                }
            }
        }

        public void EnsureAllGamesThemeDataLoaded(
            bool includeHeavyAchievementLists = true,
            bool forceRefresh = false)
        {
            try
            {
                if (!forceRefresh &&
                    _hasLoadedLibraryState &&
                    (!includeHeavyAchievementLists || _lastLibraryRefreshIncludedHeavyAchievementLists))
                {
                    ApplyDynamicOptionBindings();
                    return;
                }

                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(
                        new Action(() => PopulateAllGamesDataSync(includeHeavyAchievementLists)),
                        DispatcherPriority.Background);
                    return;
                }

                PopulateAllGamesDataSync(includeHeavyAchievementLists);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to ensure all-games theme data is loaded.");
            }
        }

        // Friend counterpart of EnsureAllGamesThemeDataLoaded: invoked from the
        // ModernThemeBindings friend getter hook and the friend scope commands. The first call
        // marks friend theme data as consumed and starts the initial build; later calls are a
        // volatile-read fast path so it is safe on the UI thread during binding.
        public void EnsureFriendThemeDataLoaded()
        {
            if (_hasFriendThemeConsumers || _friendCache == null)
            {
                return;
            }

            _hasFriendThemeConsumers = true;
            RequestFriendStateRefresh();
        }

        public void NotifyCustomDataChanged(Guid? gameId)
        {
            try
            {
                var resolvedGameId = gameId.HasValue && gameId.Value != Guid.Empty
                    ? gameId
                    : ResolveSelectedGameIdForThemeUpdate();
                var shouldRefreshSelectedGame = resolvedGameId.HasValue &&
                    ((_appliedGameId.HasValue && _appliedGameId.Value == resolvedGameId.Value) ||
                     (_requestedGameId.HasValue && _requestedGameId.Value == resolvedGameId.Value) ||
                     (_settings?.SelectedGame?.Id == resolvedGameId.Value));

                if (shouldRefreshSelectedGame)
                {
                    RequestUpdate(resolvedGameId.Value, forceRefresh: true);
                }
                else if (resolvedGameId.HasValue)
                {
                    RequestUpdate(resolvedGameId.Value, forceRefresh: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to refresh selected-game theme state after custom-data change.");
            }

            try
            {
                if (_hasLoadedLibraryState)
                {
                    RequestLibraryRefresh(_lastLibraryRefreshIncludedHeavyAchievementLists);
                }
                else if (IsFullscreen() && _fullscreenInitialized)
                {
                    RequestRefresh();
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to refresh library theme state after custom-data change.");
            }
        }

        private bool IsFullscreen()
        {
            try
            {
                return _api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureFullscreenInitialized()
        {
            if (!IsFullscreen() || _fullscreenInitialized)
            {
                return;
            }

            _fullscreenInitialized = true;
            RequestRefresh();
        }

        private void RefreshService_CacheInvalidated(object sender, EventArgs e)
        {
            if (IsFullscreen() && _fullscreenInitialized)
            {
                RequestRefresh();
            }

            try
            {
                var id = ResolveSelectedGameIdForThemeUpdate();
                if (id.HasValue)
                {
                    RequestUpdate(id);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ThemeIntegration] Failed to request selected-game theme update after cache invalidation.");
            }

            _friendsOverviewDataCoordinator?.Invalidate();
        }

        private void FriendCache_FriendCacheInvalidated(object sender, FriendCacheInvalidatedEventArgs e)
        {
            _friendsOverviewDataCoordinator?.Invalidate(e);
        }

        // Every invalidation path funnels through the coordinator (cache invalidation handlers
        // here, the plugin's friend-cache handler, and the post-start warm), so its
        // SnapshotInvalidated event is the single trigger for consumed rebuilds. Staleness is
        // always recorded; without consumers the rebuild (and its full friends-overview
        // snapshot) is skipped.
        private void FriendsOverviewCoordinator_SnapshotInvalidated(object sender, EventArgs e)
        {
            if (_hasFriendThemeConsumers)
            {
                RequestFriendStateRefresh();
            }
        }

        private Guid? ResolveSelectedGameIdForThemeUpdate()
        {
            try
            {
                var selectedGame = _settings?.SelectedGame;
                if (selectedGame != null && selectedGame.Id != Guid.Empty)
                {
                    return selectedGame.Id;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ThemeIntegration] Failed to read selected game from settings.");
            }

            return GetSingleSelectedGameId();
        }

        private Guid? GetSingleSelectedGameId()
        {
            try
            {
                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    return dispatcher.Invoke(
                        new Func<Guid?>(GetSingleSelectedGameIdFromMainView),
                        DispatcherPriority.Background);
                }

                return GetSingleSelectedGameIdFromMainView();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to resolve selected game for fullscreen achievement window.");
                return null;
            }
        }

        private Guid? GetSingleSelectedGameIdFromMainView()
        {
            var selected = _api?.MainView?.SelectedGames?
                    .Where(g => g != null)
                    .Take(2)
                    .ToList();

            if (selected == null || selected.Count != 1)
            {
                return null;
            }

            return selected[0].Id;
        }

        private async Task RunUpdateLoopAsync()
        {
            while (true)
            {
                int version;
                Guid? gameId;
                bool forceRefresh;
                CancellationToken token;

                lock (_updateGate)
                {
                    if (_processedVersion == _requestVersion)
                    {
                        return;
                    }

                    version = _requestVersion;
                    gameId = _requestedGameId;
                    forceRefresh = _requestedForceSelectedGameRefresh;
                    _requestedForceSelectedGameRefresh = false;
                    _processedVersion = version;

                    try { _activeUpdateCts?.Cancel(); } catch { }
                    try { _activeUpdateCts?.Dispose(); } catch { }
                    _activeUpdateCts = new CancellationTokenSource();
                    token = _activeUpdateCts.Token;
                }

                if (!gameId.HasValue)
                {
                    await ApplyClearAsync(version).ConfigureAwait(false);
                    continue;
                }

                GameAchievementData gameData = null;
                try
                {
                    gameData = await Task.Run(() => _achievementDataService.GetVisibleGameAchievementData(gameId.Value), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Theme integration update failed to fetch game data.");
                }

                if (token.IsCancellationRequested)
                {
                    continue;
                }

                if (gameData == null || !gameData.HasAchievements)
                {
                    await ApplyClearAsync(version, gameId).ConfigureAwait(false);
                    continue;
                }

                if (!forceRefresh &&
                    _appliedGameId.HasValue &&
                    _appliedGameId.Value == gameId.Value &&
                    _appliedLastUpdatedUtc == gameData.LastUpdatedUtc)
                {
                    continue;
                }

                SelectedGameRuntimeState state = null;
                try
                {
                    state = await Task.Run(
                        () => SelectedGameRuntimeStateBuilder.Build(gameId.Value, gameData),
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Theme integration update failed while building selected-game state.");
                }

                if (token.IsCancellationRequested)
                {
                    continue;
                }

                Action applyState = () =>
                {
                    if (!IsLatest(version))
                    {
                        return;
                    }

                    if (state == null || !state.HasAchievements)
                    {
                        ClearSingleGameThemeProperties(gameId);
                        return;
                    }

                    ApplySelectedGameState(state);
                    _appliedGameId = gameId;
                    _appliedLastUpdatedUtc = gameData.LastUpdatedUtc;
                };

                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    applyState();
                }
                else
                {
                    await dispatcher.InvokeAsync(applyState, DispatcherPriority.Background).Task.ConfigureAwait(false);
                }
            }
        }

        private Task ApplyClearAsync(int version, Guid? selectedGameId = null)
        {
            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                ClearSingleGameThemeProperties(selectedGameId);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() =>
            {
                if (!IsLatest(version))
                {
                    return;
                }

                ClearSingleGameThemeProperties(selectedGameId);
            }, DispatcherPriority.Background).Task;
        }

        private bool IsLatest(int version)
        {
            lock (_updateGate)
            {
                return version == _requestVersion;
            }
        }

        #region Window Operations (delegated to FullscreenWindowService)

        private void OpenOverviewWindow()
        {
            // Cancel any pending async refresh to prevent it from overwriting our data
            lock (_refreshLock)
            {
                try { _refreshCts?.Cancel(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                _refreshCts = null;
            }

            // Mark as initialized to prevent other code paths from triggering refresh
            if (!_fullscreenInitialized)
            {
                _fullscreenInitialized = true;
            }

            // Immediately populate all-games data on UI thread before opening window
            PopulateAllGamesDataSync(includeHeavyAchievementLists: true);

            _windowService.OpenOverviewWindow();
        }

        private void OpenSelectedGameWindowCommand(object parameter)
        {
            if (TryResolveThemeCommandGameId(parameter, out var gameId))
            {
                OpenViewAchievementsWindow(gameId);
                return;
            }

            OpenSelectedGameWindow();
        }

        private void OpenSelectedGameWindow()
        {
            var id = GetSingleSelectedGameId();
            if (!id.HasValue)
            {
                return;
            }

            OpenViewAchievementsWindow(id.Value);
        }

        private void SetDynamicAchievementsGame(object parameter)
        {
            if (!TryResolveThemeCommandGameId(parameter, out var gameId))
            {
                LogInvalidCommandParameter(nameof(PlayniteAchievementsSettings.SetDynamicAchievementsGameCommand), parameter);
                return;
            }

            ApplyDynamicAchievementsGame(gameId);
        }

        private void FilterDynamicAchievementsByRunningGame()
        {
            if (!TryResolveRunningGameId(
                nameof(PlayniteAchievementsSettings.FilterDynamicAchievementsByRunningGameCommand),
                out var gameId))
            {
                return;
            }

            ApplyDynamicAchievementsGame(gameId);
        }

        private void ApplyDynamicAchievementsGame(Guid gameId)
        {
            _runtimeState.SelectedGameAchievements.GameKey = gameId.ToString("D");
            _runtimeState.SelectedGameAchievements.GameLabel = ResolveGameLabel(gameId);
            PopulateSingleGameDataSync(gameId);
        }

        private void FilterDynamicLibraryAchievementsByRunningGame()
        {
            if (!TryResolveRunningGameId(
                nameof(PlayniteAchievementsSettings.FilterDynamicLibraryAchievementsByRunningGameCommand),
                out var gameId))
            {
                return;
            }

            EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: true);

            var state = _runtimeState.Library ?? new LibraryRuntimeState();
            var viewState = _runtimeState.LibraryAchievements;
            var providerKey = ResolveLibraryProviderKeyForGame(state, gameId);

            viewState.HasUserSelection = true;
            viewState.GameKey = gameId.ToString("D");
            viewState.GameLabel = ResolveGameLabel(gameId);
            if (!ProviderScopeMatches(viewState.ProviderKey, providerKey))
            {
                viewState.ProviderKey = string.IsNullOrWhiteSpace(providerKey)
                    ? DynamicThemeViewKeys.All
                    : providerKey;
            }

            ApplyDynamicLibraryAchievementBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicAllGames);
        }

        private void FilterDynamicGameSummariesByRunningGame()
        {
            if (!TryResolveRunningGameId(
                nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByRunningGameCommand),
                out var gameId))
            {
                return;
            }

            EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: true);

            var state = _runtimeState.Library ?? new LibraryRuntimeState();
            var viewState = _runtimeState.GameSummaries;
            var providerKey = ResolveGameSummaryProviderKeyForGame(state, gameId);

            viewState.HasUserSelection = true;
            viewState.GameKey = gameId.ToString("D");
            viewState.GameLabel = ResolveGameLabel(gameId);
            if (!ProviderScopeMatches(viewState.ProviderKey, providerKey))
            {
                viewState.ProviderKey = string.IsNullOrWhiteSpace(providerKey)
                    ? DynamicThemeViewKeys.All
                    : providerKey;
            }

            ApplyDynamicGameSummaryBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicAllGames);
        }

        private void FilterDynamicFriendScopeByRunningGame(string commandName)
        {
            EnsureFriendThemeDataLoaded();
            if (!TryResolveRunningGameId(commandName, out var gameId))
            {
                return;
            }

            var state = _runtimeState.Friends ?? FriendRuntimeState.Empty;
            var game = FindFriendGameScopeCandidate(state, gameId);
            if (game == null)
            {
                _logger?.Debug($"Ignored {commandName}: running game '{gameId:D}' is not available in friend theme data.");
                return;
            }

            var targetGameKey = FriendOverviewProjection.GetGameScopeKey(game);
            if (FriendOverviewProjection.IsAllScope(targetGameKey))
            {
                _logger?.Debug($"Ignored {commandName}: running game '{gameId:D}' has no friend game scope key.");
                return;
            }

            var scope = _runtimeState.FriendScope;
            if (!ProviderScopeMatches(scope.ProviderKey, game.ProviderKey))
            {
                scope.ProviderKey = string.IsNullOrWhiteSpace(game.ProviderKey)
                    ? DynamicThemeViewKeys.All
                    : game.ProviderKey;
            }

            if (!FriendOverviewProjection.IsAllScope(scope.UserKey) &&
                !GetFriendGameScopeCandidates(state, scope.ProviderKey, scope.UserKey)
                    .Any(candidate => string.Equals(
                        FriendOverviewProjection.GetGameScopeKey(candidate),
                        targetGameKey,
                        StringComparison.OrdinalIgnoreCase)))
            {
                scope.UserKey = DynamicThemeViewKeys.All;
            }

            scope.GameKey = targetGameKey;
            ApplyDynamicFriendBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        private void OpenManageAchievementsWindow(object parameter)
        {
            if (!TryResolveThemeCommandGameId(parameter, out var gameId))
            {
                return;
            }

            _openManageAchievementsView?.Invoke(gameId);
        }

        private void OpenViewAchievementsWindowCommand(object parameter)
        {
            if (!TryResolveThemeCommandGameId(parameter, out var gameId))
            {
                return;
            }

            OpenViewAchievementsWindow(gameId);
        }

        private bool TryResolveThemeCommandGameId(object parameter, out Guid gameId)
        {
            gameId = Guid.Empty;

            if (parameter == DependencyProperty.UnsetValue)
            {
                parameter = null;
            }

            switch (parameter)
            {
                case DynamicThemeOption option when Guid.TryParse(option.Key, out var optionId) && optionId != Guid.Empty:
                    gameId = optionId;
                    return true;
                case GameAchievementSummary summary when summary.GameId != Guid.Empty:
                    gameId = summary.GameId;
                    return true;
                case AchievementDetail achievement when achievement.Game?.Id != Guid.Empty:
                    gameId = achievement.Game.Id;
                    return true;
                case AchievementDisplayItem item when item.PlayniteGameId.HasValue && item.PlayniteGameId.Value != Guid.Empty:
                    gameId = item.PlayniteGameId.Value;
                    return true;
                case GameSummaryItem gameItem when gameItem.PlayniteGameId.HasValue && gameItem.PlayniteGameId.Value != Guid.Empty:
                    gameId = gameItem.PlayniteGameId.Value;
                    return true;
                case Game game when game.Id != Guid.Empty:
                    gameId = game.Id;
                    return true;
                case Guid id when id != Guid.Empty:
                    gameId = id;
                    return true;
                case string idText when Guid.TryParse(idText, out var parsedId) && parsedId != Guid.Empty:
                    gameId = parsedId;
                    return true;
            }

            var selectedId = ResolveSelectedGameIdForThemeUpdate();
            if (!selectedId.HasValue || selectedId.Value == Guid.Empty)
            {
                return false;
            }

            gameId = selectedId.Value;
            return true;
        }

        private bool TryResolveRunningGameId(string commandName, out Guid gameId)
        {
            gameId = Guid.Empty;

            try
            {
                var target = _resolveRunningGameTarget?.Invoke() ?? AchievementHotkeyTargetResolution.NoTarget;
                if (target.HasTarget && target.GameId != Guid.Empty)
                {
                    gameId = target.GameId;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to resolve running game for {commandName}.");
            }

            _logger?.Debug($"Ignored {commandName}: no running Playnite game.");
            return false;
        }

        private AchievementHotkeyTargetResolution ResolveRunningGameTargetFromApi()
        {
            try
            {
                var runningGames = _api?.Database?.Games?
                    .Where(game => game != null && game.Id != Guid.Empty && game.IsRunning)
                    .ToList() ?? new List<Game>();

                return AchievementHotkeyTargetResolver.ResolveRunningGame(runningGames, Array.Empty<Guid>());
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to resolve running game from Playnite API.");
                return AchievementHotkeyTargetResolution.NoTarget;
            }
        }

        private string ResolveGameLabel(Guid gameId)
        {
            var summary = _runtimeState.Library?.AllGamesWithAchievements?
                .FirstOrDefault(item => item != null && item.GameId == gameId);
            if (!string.IsNullOrWhiteSpace(summary?.Name))
            {
                return summary.Name;
            }

            var selectedGame = _settings.SelectedGame;
            if (selectedGame != null && selectedGame.Id == gameId && !string.IsNullOrWhiteSpace(selectedGame.Name))
            {
                return selectedGame.Name;
            }

            var achievementGameName = _runtimeState.SelectedGame?.AllAchievements?
                .Select(item => item?.Game)
                .FirstOrDefault(game => game != null && game.Id == gameId && !string.IsNullOrWhiteSpace(game.Name))
                ?.Name;
            return !string.IsNullOrWhiteSpace(achievementGameName)
                ? achievementGameName
                : gameId.ToString("D");
        }

        public void OpenViewAchievementsWindow(Guid gameId)
        {
            EnsureFullscreenInitialized();

            // Synchronously populate single-game data before opening the window.
            // This prevents the race condition where the window opens before
            // async theme updates complete, showing stale data from the previous selection.
            PopulateSingleGameDataSync(gameId);

            _windowService.OpenViewAchievementsWindow(gameId);
        }

        #endregion

        #region Refresh Operations

        public Task RunFullscreenRefreshRequestAsync(
            RefreshRequest request,
            string errorLogMessage,
            bool validateAuthentication,
            Action<bool> onCompleted = null)
        {
            request ??= new RefreshRequest();

            EnsureFullscreenInitialized();
            _logger?.Info($"RunFullscreenRefreshRequestAsync: Starting mode={request.Mode}, singleGameId={request.SingleGameId}, explicitGameCount={request.GameIds?.Count ?? 0}, validateAuthentication={validateAuthentication}");

            var singleGameId = request.SingleGameId;
            if (!singleGameId.HasValue && request.Mode == RefreshModeType.Single)
            {
                singleGameId = GetSingleSelectedGameId();
            }

            var resolvedErrorMessage = !string.IsNullOrWhiteSpace(errorLogMessage)
                ? errorLogMessage
                : request.Mode.HasValue
                    ? GetLocalizedRefreshFailureMessage(request.Mode.Value)
                    : ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed");

            return _runRefreshWithGlobalProgressAsync(
                request,
                resolvedErrorMessage,
                validateAuthentication,
                success =>
                {
                    if (success)
                    {
                        _logger?.Info($"RunFullscreenRefreshRequestAsync: Completed, success={success}, mode={request.Mode}, singleGameId={singleGameId}");
                    }

                    if (singleGameId.HasValue)
                    {
                        try { RequestUpdate(singleGameId.Value); } catch (Exception ex) { _logger?.Debug(ex, "[ThemeIntegration] Failed to request theme update after fullscreen refresh."); }
                    }

                    try { if (IsFullscreen() && _fullscreenInitialized) RequestRefresh(); } catch (Exception ex) { _logger?.Debug(ex, "[ThemeIntegration] Failed to request fullscreen state refresh after refresh completion."); }

                    try { onCompleted?.Invoke(success); } catch (Exception ex) { _logger?.Debug(ex, "[ThemeIntegration] Refresh completion callback failed."); }
                });
        }

        private void RefreshWithMode(RefreshModeType mode)
        {
            Guid? gameIdForThemeUpdate = null;

            if (mode == RefreshModeType.Single)
            {
                var id = GetSingleSelectedGameId();
                if (!id.HasValue)
                {
                    return;
                }
                gameIdForThemeUpdate = id;
            }

            EnsureFullscreenInitialized();
            RunAchievementRefresh(mode, gameIdForThemeUpdate);
        }

        private void RunAchievementRefresh(RefreshModeType mode, Guid? gameIdForThemeUpdate)
        {
            var errorLogMessage = GetLocalizedRefreshFailureMessage(mode);

            var isFullscreen = IsFullscreen();
            _logger?.Info($"RunAchievementRefresh: mode={mode}, isFullscreen={isFullscreen}, gameId={gameIdForThemeUpdate}");

            if (isFullscreen)
            {
                RunAchievementRefreshWithGlobalProgress(mode, gameIdForThemeUpdate, errorLogMessage);
            }
            else
            {
                RunAchievementRefreshWithProgressWindow(mode, gameIdForThemeUpdate, errorLogMessage);
            }
        }

        private void RunAchievementRefreshWithGlobalProgress(
            RefreshModeType mode,
            Guid? gameIdForThemeUpdate,
            string errorLogMessage)
        {
            var request = new RefreshRequest
            {
                Mode = mode,
                SingleGameId = mode == RefreshModeType.Single ? gameIdForThemeUpdate : null
            };

            _ = _runRefreshWithGlobalProgressAsync(
                request,
                errorLogMessage,
                false,
                success =>
                {
                    if (success)
                    {
                        _logger?.Info($"RunAchievementRefreshWithGlobalProgress: Completed, success={success}, gameId={gameIdForThemeUpdate}");
                    }

                    if (gameIdForThemeUpdate.HasValue)
                    {
                        try { RequestUpdate(gameIdForThemeUpdate.Value); } catch (Exception ex) { _logger?.Debug(ex, "[ThemeIntegration] Failed to request theme update after refresh."); }
                    }

                    try { if (IsFullscreen() && _fullscreenInitialized) RequestRefresh(); } catch (Exception ex) { _logger?.Debug(ex, "[ThemeIntegration] Failed to request fullscreen state refresh after refresh completion."); }
                });
        }

        private Task RunRefreshWithoutGlobalProgressAsync(
            RefreshRequest request,
            string errorLogMessage,
            bool validateAuthentication,
            Action<bool> onCompleted)
        {
            request ??= new RefreshRequest();

            return _refreshCoordinator.ExecuteAsync(
                request,
                new RefreshExecutionPolicy
                {
                    ValidateAuthentication = validateAuthentication,
                    SwallowExceptions = true,
                    ErrorLogMessage = errorLogMessage,
                    OnRefreshCompleted = success =>
                    {
                        try
                        {
                            onCompleted?.Invoke(success);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "Refresh completion callback failed.");
                        }
                    }
                });
        }

        private static string GetLocalizedRefreshFailureMessage(RefreshModeType mode)
        {
            var modeName = ResourceProvider.GetString(mode.GetResourceKey());
            var format = ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed");
            if (!string.IsNullOrWhiteSpace(format) && !string.IsNullOrWhiteSpace(modeName))
            {
                return string.Format(format, modeName);
            }

            return ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed");
        }

        private void RunAchievementRefreshWithProgressWindow(
            RefreshModeType mode,
            Guid? gameIdForThemeUpdate,
            string errorLogMessage)
        {
            _logger?.Info($"RunAchievementRefreshWithProgressWindow: Starting fullscreen refresh, mode={mode}, gameId={gameIdForThemeUpdate}");

            var request = new RefreshRequest
            {
                Mode = mode,
                SingleGameId = mode == RefreshModeType.Single ? gameIdForThemeUpdate : null
            };

            _ = _refreshCoordinator.ExecuteAsync(
                request,
                new RefreshExecutionPolicy
                {
                    ValidateAuthentication = false,
                    UseProgressWindow = true,
                    SwallowExceptions = true,
                    ProgressSingleGameId = gameIdForThemeUpdate,
                    ErrorLogMessage = errorLogMessage,
                    OnRefreshCompleted = (success) =>
                    {
                        _logger?.Info($"RunAchievementRefreshWithProgressWindow: Completed, success={success}, gameId={gameIdForThemeUpdate}");
                        if (success)
                        {
                            if (gameIdForThemeUpdate.HasValue)
                            {
                                try { RequestUpdate(gameIdForThemeUpdate.Value); } catch (Exception ex) { _logger?.Debug(ex, "[ThemeIntegration] Failed to request theme update after refresh."); }
                            }
                            try { if (IsFullscreen()) RequestRefresh(); } catch (Exception ex) { _logger?.Debug(ex, "[ThemeIntegration] Failed to request fullscreen state refresh after refresh completion."); }
                        }
                    }
                });
        }

        #endregion

        #region Snapshot Building and Refresh

        private void PopulateAllGamesDataSync(bool includeHeavyAchievementLists)
        {
            try
            {
                _logger?.Info("PopulateAllGamesDataSync: Starting to populate all-games achievement data.");

                var state = BuildLibraryState(
                    includeHeavyAchievementLists,
                    CancellationToken.None,
                    out var usedCachedSummary,
                    out var hydratedCount);
                if (usedCachedSummary)
                {
                    _logger?.Info($"PopulateAllGamesDataSync: Built from cached summary data. AllGamesWithAchievements count={state.AllGamesWithAchievements?.Count ?? 0}.");
                }
                else
                {
                    _logger?.Info($"PopulateAllGamesDataSync: Found {hydratedCount.GetValueOrDefault()} total game data entries.");
                }

                _logger?.Info($"PopulateAllGamesDataSync: State created - TotalTrophies={state.TotalTrophies}, PlatinumTrophies={state.PlatinumTrophies}, GoldTrophies={state.GoldTrophies}, Rank={state.Rank}");

                _hasLoadedLibraryState = true;
                _lastLibraryRefreshIncludedHeavyAchievementLists = includeHeavyAchievementLists;
                ApplyLibraryState(state);

                _logger?.Info($"PopulateAllGamesDataSync: Applied snapshot. AllGamesWithAchievements count={_settings.LegacyTheme.AllGamesWithAchievements?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to populate all-games data synchronously.");
            }
        }

        /// <summary>
        /// Synchronously populates single-game achievement data for the specified game.
        /// Uses existing cached data without triggering a refresh.
        /// Called on desktop game selection changes to populate modern theme bindings for theme controls.
        /// </summary>
        /// <param name="gameId">The ID of the game to populate data for.</param>
        internal void PopulateSingleGameDataSync(Guid gameId)
        {
            // UI-thread cache read: when this stalls for seconds it is usually waiting on the
            // store lock held by a background projection warm, not doing its own work.
            using (PerfScope.Start(_logger, "ThemeIntegration.PopulateSingleGameDataSync", thresholdMs: 250))
            try
            {
                var gameData = _achievementDataService.GetVisibleGameAchievementData(gameId);
                var state = SelectedGameRuntimeStateBuilder.Build(
                    gameId,
                    gameData);

                if (state != null && state.HasAchievements)
                {
                    ApplySelectedGameState(state);
                    _appliedGameId = gameId;
                    _appliedLastUpdatedUtc = gameData?.LastUpdatedUtc ?? default;
                }
                else
                {
                    ClearSingleGameThemeProperties(gameId);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to populate single-game data synchronously for game {gameId}.");
                ClearSingleGameThemeProperties(gameId);
            }
        }

        private void RequestRefresh()
        {
            RequestLibraryRefresh(includeHeavyAchievementLists: false, requireFullscreen: true);
        }

        private void RequestLibraryRefresh(
            bool includeHeavyAchievementLists,
            bool requireFullscreen = false)
        {
            if (requireFullscreen && !IsFullscreen())
            {
                return;
            }

            CancellationToken token;
            lock (_refreshLock)
            {
                try { _refreshCts?.Cancel(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                _refreshCts = new CancellationTokenSource();
                token = _refreshCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(LibraryRefreshDelay, token).ConfigureAwait(false);

                    var state = BuildLibraryState(
                        includeHeavyAchievementLists,
                        token,
                        out _,
                        out _);

                    Action applyState = () =>
                    {
                        _hasLoadedLibraryState = true;
                        _lastLibraryRefreshIncludedHeavyAchievementLists = includeHeavyAchievementLists;
                        ApplyLibraryState(state);
                    };

                    var uiDispatcher = _api.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                    if (uiDispatcher == null)
                    {
                        applyState();
                    }
                    else
                    {
                        uiDispatcher.InvokeIfNeeded(applyState, DispatcherPriority.Background);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to refresh theme integration snapshot.");
                }
            }, token);
        }

        private LibraryRuntimeState BuildLibraryState(
            bool includeHeavyAchievementLists,
            CancellationToken token,
            out bool usedCachedSummary,
            out int? hydratedCount)
        {
            token.ThrowIfCancellationRequested();

            usedCachedSummary = false;
            hydratedCount = null;

#if !TEST
            if (_libraryProjectionService != null)
            {
                if (!includeHeavyAchievementLists)
                {
                    return _libraryProjectionService.GetThemeLightState(
                        ThemeRecentUnlockSummaryLimit,
                        token,
                        out usedCachedSummary,
                        out hydratedCount);
                }

                usedCachedSummary = false;
                return _libraryProjectionService.GetThemeFullState(token, out hydratedCount);
            }
#endif

            if (!includeHeavyAchievementLists)
            {
                var summaryData = _achievementDataService.GetCachedSummaryDataForTheme(ThemeRecentUnlockSummaryLimit);
                if (summaryData != null)
                {
                    usedCachedSummary = true;
                    return LibraryRuntimeStateBuilder.BuildFromCachedSummary(summaryData, _api, token);
                }
            }

            var allData = _achievementDataService.GetAllVisibleGameAchievementDataForTheme() ?? new List<GameAchievementData>();
            hydratedCount = allData.Count;
            token.ThrowIfCancellationRequested();

            return LibraryRuntimeStateBuilder.Build(
                allData,
                _api,
                token,
                includeHeavyAchievementLists);
        }

        private void ApplyLibraryState(LibraryRuntimeState state)
        {
            _runtimeState.Library = state ?? new LibraryRuntimeState();
            var library = _runtimeState.Library;
            PruneGameCommandCaches(library.AllGamesWithAchievements);

            _settings.ModernTheme.CompletedGamesAsc = ProjectGameSummaries(library.CompletedGamesAsc);
            _settings.ModernTheme.CompletedGamesDesc = ProjectGameSummaries(library.CompletedGamesDesc);
            _settings.ModernTheme.GameSummariesAsc = ProjectGameSummaries(library.GameSummariesAsc);
            _settings.ModernTheme.GameSummariesDesc = ProjectGameSummaries(library.GameSummariesDesc);
            _settings.ModernTheme.TotalCommon = library.TotalCommon;
            _settings.ModernTheme.TotalUncommon = library.TotalUncommon;
            _settings.ModernTheme.TotalRare = library.TotalRare;
            _settings.ModernTheme.TotalUltraRare = library.TotalUltraRare;
            _settings.ModernTheme.TotalRareAndUltraRare = library.TotalRareAndUltraRare;
            _settings.ModernTheme.TotalOverall = library.TotalOverall;
            _settings.ModernTheme.CollectorScore = library.CollectorScore;
            _settings.ModernTheme.CollectorLevel = library.CollectorLevel;
            _settings.ModernTheme.CollectorLevelProgress = library.CollectorLevelProgress;
            _settings.ModernTheme.CollectorRank = library.CollectorRank;
            _settings.ModernTheme.PrestigeScore = library.PrestigeScore;
            _settings.ModernTheme.PrestigeLevel = library.PrestigeLevel;
            _settings.ModernTheme.PrestigeLevelProgress = library.PrestigeLevelProgress;
            _settings.ModernTheme.PrestigeRank = library.PrestigeRank;
            _settings.ModernTheme.SteamGames = ProjectGameSummaries(library.SteamGames);
            _settings.ModernTheme.GOGGames = ProjectGameSummaries(library.GOGGames);
            _settings.ModernTheme.EpicGames = ProjectGameSummaries(library.EpicGames);
            _settings.ModernTheme.BattleNetGames = ProjectGameSummaries(library.BattleNetGames);
            _settings.ModernTheme.EAGames = ProjectGameSummaries(library.EAGames);
            _settings.ModernTheme.XboxGames = ProjectGameSummaries(library.XboxGames);
            _settings.ModernTheme.PSNGames = ProjectGameSummaries(library.PSNGames);
            _settings.ModernTheme.RetroAchievementsGames = ProjectGameSummaries(library.RetroAchievementsGames);
            _settings.ModernTheme.AppleGames = ProjectGameSummaries(library.AppleGames);
            _settings.ModernTheme.GooglePlayGames = ProjectGameSummaries(library.GooglePlayGames);
            _settings.ModernTheme.HoyoverseGames = ProjectGameSummaries(library.HoyoverseGames);
            _settings.ModernTheme.UbisoftGames = ProjectGameSummaries(library.UbisoftGames);
            _settings.ModernTheme.RPCS3Games = ProjectGameSummaries(library.RPCS3Games);
            _settings.ModernTheme.XeniaGames = ProjectGameSummaries(library.XeniaGames);
            _settings.ModernTheme.ShadPS4Games = ProjectGameSummaries(library.ShadPS4Games);
            _settings.ModernTheme.ManualGames = ProjectGameSummaries(library.ManualGames);
            _settings.ModernTheme.MostRecentUnlocksTop3 = library.MostRecentUnlocksTop3;
            _settings.ModernTheme.MostRecentUnlocksTop5 = library.MostRecentUnlocksTop5;
            _settings.ModernTheme.MostRecentUnlocksTop10 = library.MostRecentUnlocksTop10;
            _settings.ModernTheme.RarestRecentUnlocksTop3 = library.RarestRecentUnlocksTop3;
            _settings.ModernTheme.RarestRecentUnlocksTop5 = library.RarestRecentUnlocksTop5;
            _settings.ModernTheme.RarestRecentUnlocksTop10 = library.RarestRecentUnlocksTop10;

            _settings.LegacyTheme.HasDataAllGames = library.HasData;
            _settings.LegacyTheme.GamesWithAchievements = ProjectGameSummaries(library.GameSummariesDesc);
            _settings.LegacyTheme.TotalTrophies = library.TotalTrophies;
            _settings.LegacyTheme.PlatinumTrophies = library.PlatinumTrophies;
            _settings.LegacyTheme.GoldTrophies = library.GoldTrophies;
            _settings.LegacyTheme.SilverTrophies = library.SilverTrophies;
            _settings.LegacyTheme.BronzeTrophies = library.BronzeTrophies;
            _settings.LegacyTheme.Level = library.Level;
            _settings.LegacyTheme.LevelProgress = library.LevelProgress;
            _settings.LegacyTheme.Rank = !string.IsNullOrWhiteSpace(library.Rank) ? library.Rank : "Bronze5";

            _settings.LegacyTheme.AllGamesWithAchievements = ProjectGameSummaries(library.AllGamesWithAchievements);
            _settings.PlatinumGames = ProjectGameSummaries(library.PlatinumGames);
            _settings.LegacyTheme.PlatinumGamesAscending = ProjectGameSummaries(library.PlatinumGamesAscending);
            _settings.LegacyTheme.GSTotal = library.TotalTrophies > 0 ? library.TotalTrophies.ToString() : "0";
            _settings.LegacyTheme.GSPlat = library.TotalTrophies > 0 ? library.PlatinumTrophies.ToString() : "0";
            _settings.LegacyTheme.GS90 = library.TotalTrophies > 0 ? library.GoldTrophies.ToString() : "0";
            _settings.LegacyTheme.GS30 = library.TotalTrophies > 0 ? library.SilverTrophies.ToString() : "0";
            _settings.LegacyTheme.GS15 = library.TotalTrophies > 0 ? library.BronzeTrophies.ToString() : "0";
            _settings.LegacyTheme.GSScore = library.TotalTrophies > 0 ? library.Score.ToString("N0") : "0";
            _settings.LegacyTheme.GSLevel = library.TotalTrophies > 0 ? library.Level.ToString() : "0";
            _settings.LegacyTheme.GSLevelProgress = library.TotalTrophies > 0 ? library.LevelProgress : 0;
            _settings.LegacyTheme.GSRank = library.TotalTrophies > 0 && !string.IsNullOrWhiteSpace(library.Rank) ? library.Rank : "Bronze5";

            var shouldUpdateHeavyLists = library.HeavyListsBuilt || !library.HasData;
            if (shouldUpdateHeavyLists)
            {
                _settings.ModernTheme.AllAchievementsUnlockAsc = library.AllAchievementsUnlockAsc;
                _settings.ModernTheme.AllAchievementsUnlockDesc = library.AllAchievementsUnlockDesc;
                _settings.ModernTheme.AllAchievementsRarityAsc = library.AllAchievementsRarityAsc;
                _settings.ModernTheme.AllAchievementsRarityDesc = library.AllAchievementsRarityDesc;
                _settings.ModernTheme.MostRecentUnlocks = library.MostRecentUnlocks;
                _settings.ModernTheme.RarestRecentUnlocks = library.RarestRecentUnlocks;
            }

            ApplyDynamicLibraryAchievementBindings(updateOptions: false);
            ApplyDynamicGameSummaryBindings(updateOptions: false);
            ApplyDynamicOptionBindings();

            NotifySettingProperties(ThemeDelegatedPropertyCatalog.CompatibilityAllGames);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.ModernAllGamesCore);
            if (shouldUpdateHeavyLists)
            {
                NotifySettingProperties(ThemeDelegatedPropertyCatalog.ModernAllGamesHeavy);
            }
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicAllGames);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        #endregion

        #region Per-Game Theme Integration

        /// <summary>
        /// Apply a single-game snapshot to theme-exposed settings properties.
        /// Intended to be executed on the UI thread.
        /// </summary>
        private void ApplySelectedGameState(SelectedGameRuntimeState state)
        {
            if (state == null || !state.HasAchievements)
            {
                ClearSingleGameThemeProperties();
                return;
            }

            _runtimeState.SelectedGame = state;
            _runtimeState.SelectedGameAchievements.GameKey = state.GameId.ToString("D");
            _runtimeState.SelectedGameAchievements.GameLabel = ResolveGameLabel(state.GameId);
            ValidateSelectedGameCategoryScope(state);
            _settings.ModernTheme.HasAchievements = true;
            _settings.ModernTheme.SelectedGameId = state.GameId;
            _settings.ModernTheme.HasCustomAchievementOrder = state.HasCustomAchievementOrder;
            _settings.ModernTheme.IsCompleted = state.IsCompleted;
            _settings.ModernTheme.AchievementCount = state.AchievementCount;
            _settings.ModernTheme.UnlockedCount = state.UnlockedCount;
            _settings.ModernTheme.LockedCount = state.LockedCount;
            _settings.ModernTheme.ProgressPercentage = state.ProgressPercentage;
            _settings.ModernTheme.Common = state.Common;
            _settings.ModernTheme.Uncommon = state.Uncommon;
            _settings.ModernTheme.Rare = state.Rare;
            _settings.ModernTheme.UltraRare = state.UltraRare;
            _settings.ModernTheme.RareAndUltraRare = state.RareAndUltraRare;
            _settings.ModernTheme.AchievementDefaultOrder = state.AchievementDefaultOrder;
            _settings.ModernTheme.AllAchievements = state.AllAchievements;
            _settings.ModernTheme.AchievementsNewestFirst = state.AchievementsNewestFirst;
            _settings.ModernTheme.AchievementsOldestFirst = state.AchievementsOldestFirst;
            _settings.ModernTheme.AchievementsRarityAsc = state.AchievementsRarityAsc;
            _settings.ModernTheme.AchievementsRarityDesc = state.AchievementsRarityDesc;

            _settings.LegacyTheme.HasData = true;
            _settings.LegacyTheme.Total = state.AchievementCount;
            _settings.LegacyTheme.Unlocked = state.UnlockedCount;
            _settings.LegacyTheme.Percent = state.ProgressPercentage;
            _settings.LegacyTheme.Is100Percent = state.UnlockedCount == state.AchievementCount && state.AchievementCount > 0;
            _settings.LegacyTheme.Locked = state.LockedCount;
            _settings.LegacyTheme.TotalGamerScore = 0;
            _settings.LegacyTheme.EstimateTimeToUnlock = string.Empty;
            _settings.LegacyTheme.ListAchievements = state.AllAchievements;
            _settings.LegacyTheme.ListAchUnlockDateAsc = state.AchievementsOldestFirst;
            _settings.LegacyTheme.ListAchUnlockDateDesc = state.AchievementsNewestFirst;

            ApplyDynamicSelectedGameBindings();

            NotifySettingProperties(ThemeDelegatedPropertyCatalog.SingleGameTheme);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.SingleGameLegacy);
        }

        /// <summary>
        /// Clear per-game theme properties when no game is selected or game has no achievements.
        /// </summary>
        public void ClearSingleGameThemeProperties(Guid? selectedGameId = null)
        {
            _runtimeState.SelectedGame = SelectedGameRuntimeState.Empty;
            _runtimeState.SelectedGameAchievements.GameKey = selectedGameId?.ToString("D") ?? string.Empty;
            _runtimeState.SelectedGameAchievements.GameLabel = selectedGameId.HasValue
                ? ResolveGameLabel(selectedGameId.Value)
                : string.Empty;
            _settings.ModernTheme.HasAchievements = false;
            _settings.ModernTheme.SelectedGameId = selectedGameId;
            _settings.ModernTheme.HasCustomAchievementOrder = false;
            _settings.ModernTheme.IsCompleted = false;
            _settings.ModernTheme.AchievementCount = 0;
            _settings.ModernTheme.UnlockedCount = 0;
            _settings.ModernTheme.LockedCount = 0;
            _settings.ModernTheme.ProgressPercentage = 0;

            _settings.ModernTheme.AchievementDefaultOrder = EmptyAchievementList;
            _settings.ModernTheme.AllAchievements = EmptyAchievementList;
            _settings.ModernTheme.AchievementsNewestFirst = EmptyAchievementList;
            _settings.ModernTheme.AchievementsOldestFirst = EmptyAchievementList;
            _settings.ModernTheme.AchievementsRarityAsc = EmptyAchievementList;
            _settings.ModernTheme.AchievementsRarityDesc = EmptyAchievementList;

            _settings.ModernTheme.Common = EmptyRarityStats;
            _settings.ModernTheme.Uncommon = EmptyRarityStats;
            _settings.ModernTheme.Rare = EmptyRarityStats;
            _settings.ModernTheme.UltraRare = EmptyRarityStats;
            _settings.ModernTheme.RareAndUltraRare = EmptyRarityStats;
            _settings.LegacyTheme.HasData = false;
            _settings.LegacyTheme.Total = 0;
            _settings.LegacyTheme.Unlocked = 0;
            _settings.LegacyTheme.Percent = 0;

            _settings.LegacyTheme.Is100Percent = false;
            _settings.LegacyTheme.Locked = 0;
            _settings.LegacyTheme.TotalGamerScore = 0;
            _settings.LegacyTheme.EstimateTimeToUnlock = string.Empty;

            _settings.LegacyTheme.ListAchievements = EmptyAchievementList;
            _settings.LegacyTheme.ListAchUnlockDateAsc = EmptyAchievementList;
            _settings.LegacyTheme.ListAchUnlockDateDesc = EmptyAchievementList;

            _appliedGameId = null;
            _appliedLastUpdatedUtc = default;
            _runtimeState.SelectedGameAchievements.ResetCategoryScope();

            ApplyDynamicSelectedGameBindings();

            NotifySettingProperties(ThemeDelegatedPropertyCatalog.SingleGameTheme);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.SingleGameLegacy);
        }

        #endregion

        private ObservableCollection<GameAchievementSummary> ProjectGameSummaries(IEnumerable<GameAchievementSummary> items)
        {
            var projected = (items ?? Enumerable.Empty<GameAchievementSummary>())
                .Select(item => new GameAchievementSummary(
                    item.GameId,
                    item.Name,
                    item.Platform,
                    item.CoverImagePath,
                    item.Progress,
                    item.GoldCount,
                    item.SilverCount,
                    item.BronzeCount,
                    item.IsCompleted,
                    item.LastUnlockDate,
                    GetOpenViewAchievementsCommand(item.GameId),
                    item.Common,
                    item.Uncommon,
                    item.Rare,
                    item.UltraRare,
                    item.RareAndUltraRare,
                    item.Overall,
                    item.ProviderKey,
                    item.ProviderName,
                    item.LastPlayed,
                    item.UnlockedCount,
                    item.AchievementCount,
                    openManageAchievementsWindow: GetOpenManageAchievementsCommand(item.GameId),
                    sortingName: item.SortingName))
                .ToList();

            AttachGameSummaryCommands(projected);
            return new ObservableCollection<GameAchievementSummary>(projected);
        }

        private ObservableCollection<FriendGameAchievementSummary> ProjectFriendGameSummaries(
            IEnumerable<FriendGameSummaryItem> items)
        {
            var projected = (items ?? Enumerable.Empty<FriendGameSummaryItem>())
                .Where(item => item != null)
                .Select(item =>
                {
                    var gameId = item.PlayniteGameId ?? Guid.Empty;
                    var hasLocalGame = gameId != Guid.Empty;
                    var common = AchievementGameStats.CreateRarityStats(item.CommonCount, item.TotalCommonPossible);
                    var uncommon = AchievementGameStats.CreateRarityStats(item.UncommonCount, item.TotalUncommonPossible);
                    var rare = AchievementGameStats.CreateRarityStats(item.RareCount, item.TotalRarePossible);
                    var ultraRare = AchievementGameStats.CreateRarityStats(item.UltraRareCount, item.TotalUltraRarePossible);

                    return new FriendGameAchievementSummary(
                        gameId,
                        item.GameName,
                        item.Provider,
                        item.GameCoverPath,
                        item.FriendCompletionPercent,
                        item.RareCount + item.UltraRareCount,
                        item.UncommonCount,
                        item.CommonCount,
                        item.IsCompleted,
                        item.LastFriendUnlockUtc?.ToLocalTime() ?? DateTime.MinValue,
                        hasLocalGame ? GetOpenViewAchievementsCommand(gameId) : null,
                        common,
                        uncommon,
                        rare,
                        ultraRare,
                        null,
                        null,
                        item.ProviderKey,
                        item.Provider,
                        item.LastFriendPlayedUtc,
                        item.UniqueFriendUnlockedAchievementsCount,
                        item.TotalAchievements,
                        openManageAchievementsWindow: hasLocalGame ? GetOpenManageAchievementsCommand(gameId) : null,
                        sortingName: item.SortingName)
                    {
                        AppId = item.AppId,
                        ProviderGameKey = item.ProviderGameKey,
                        FriendCount = item.FriendCount,
                        FriendsWithUnlocksCount = item.FriendsWithUnlocksCount,
                        FriendUnlockedAchievementsCount = item.FriendUnlockedAchievementsCount,
                        UniqueFriendUnlockedAchievementsCount = item.UniqueFriendUnlockedAchievementsCount,
                        CollectionScore = item.CollectionScore,
                        CollectionScoreTotal = item.CollectionScoreTotal,
                        PrestigeScore = item.PrestigeScore,
                        PrestigeScoreTotal = item.PrestigeScoreTotal,
                        Points = item.Points,
                        TrophyPlatinumCount = item.TrophyPlatinumCount,
                        TrophyGoldCount = item.TrophyGoldCount,
                        TrophySilverCount = item.TrophySilverCount,
                        TrophyBronzeCount = item.TrophyBronzeCount,
                        TrophyPlatinumTotal = item.TrophyPlatinumTotal,
                        TrophyGoldTotal = item.TrophyGoldTotal,
                        TrophySilverTotal = item.TrophySilverTotal,
                        TrophyBronzeTotal = item.TrophyBronzeTotal,
                        LastFriendUnlockUtc = item.LastFriendUnlockUtc,
                        TotalFriendPlaytimeMinutes = item.TotalFriendPlaytimeMinutes,
                        AverageFriendPlaytimeMinutes = item.AverageFriendPlaytimeMinutes,
                        LastFriendPlayedUtc = item.LastFriendPlayedUtc,
                        LastFriendScrapedUtc = item.LastFriendScrapedUtc,
                        LastFriendScrapeStatus = item.LastFriendScrapeStatus
                    };
                })
                .ToList();

            AttachFriendGameSummaryCommands(projected);
            return new ObservableCollection<FriendGameAchievementSummary>(projected);
        }

        private RelayCommand GetOpenViewAchievementsCommand(Guid gameId)
        {
            if (!_openViewAchievementsCommands.TryGetValue(gameId, out var command))
            {
                command = new RelayCommand(_ => OpenViewAchievementsWindow(gameId));
                _openViewAchievementsCommands[gameId] = command;
            }

            return command;
        }

        private RelayCommand GetOpenManageAchievementsCommand(Guid gameId)
        {
            if (!_openManageAchievementsCommands.TryGetValue(gameId, out var command))
            {
                command = new RelayCommand(_ => OpenManageAchievementsWindow(gameId));
                _openManageAchievementsCommands[gameId] = command;
            }

            return command;
        }

        private void PruneGameCommandCaches(IEnumerable<GameAchievementSummary> currentGames)
        {
            var currentGameIds = new HashSet<Guid>(
                (currentGames ?? Enumerable.Empty<GameAchievementSummary>())
                    .Where(item => item != null && item.GameId != Guid.Empty)
                    .Select(item => item.GameId));
            PruneCommandCache(_openViewAchievementsCommands, currentGameIds);
            PruneCommandCache(_openManageAchievementsCommands, currentGameIds);
        }

        private static void PruneCommandCache(
            Dictionary<Guid, RelayCommand> commands,
            HashSet<Guid> currentGameIds)
        {
            if (commands == null || commands.Count == 0)
            {
                return;
            }

            var staleKeys = commands.Keys
                .Where(key => !currentGameIds.Contains(key))
                .ToList();
            for (var i = 0; i < staleKeys.Count; i++)
            {
                commands.Remove(staleKeys[i]);
            }
        }

        private void Settings_DynamicThemeDefaultsChanged(object sender, EventArgs e)
        {
            ApplyDynamicThemeDefaultsFromSettings(notify: true);
        }

        private void ApplyDynamicThemeDefaultsFromSettings(bool notify)
        {
            var selectedChanged = ApplySelectedGameAchievementDefaultsFromSettings();
            selectedChanged |= ApplyCategorySummaryDefaultsFromSettings();
            var libraryChanged = ApplyLibraryAchievementDefaultsFromSettings();
            var summariesChanged = ApplyGameSummaryDefaultsFromSettings();

            if (selectedChanged)
            {
                ApplyDynamicSelectedGameBindings();
                if (notify)
                {
                    NotifySettingProperties(ThemeDelegatedPropertyCatalog.SingleGameTheme);
                }
            }

            if (libraryChanged || summariesChanged)
            {
                if (libraryChanged)
                {
                    ApplyDynamicLibraryAchievementBindings(updateOptions: false);
                }

                if (summariesChanged)
                {
                    ApplyDynamicGameSummaryBindings(updateOptions: false);
                }

                ApplyDynamicOptionBindings();

                if (notify)
                {
                    NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicAllGames);
                }
            }
        }

        private bool ApplySelectedGameAchievementDefaultsFromSettings()
        {
            var state = _runtimeState.SelectedGameAchievements;
            var filterKey = NormalizeDefaultFilterKey(
                _settings.ModernTheme.DynamicAchievementsDefaultFilterKey,
                DynamicThemeOptionGroups.AchievementFilterKeyMap,
                state.DefaultFilterKey,
                nameof(PlayniteAchievementsSettings.DynamicAchievementsDefaultFilterKey));
            var sortKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicAchievementsDefaultSortKey,
                DynamicThemeOptionGroups.SelectedGameAchievementSortKeyMap,
                state.DefaultSortKey,
                DynamicThemeViewKeys.UnlockTime,
                nameof(PlayniteAchievementsSettings.DynamicAchievementsDefaultSortKey));
            var directionKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicAchievementsDefaultSortDirectionKey,
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                state.DefaultSortDirectionKey,
                DynamicThemeViewKeys.Descending,
                nameof(PlayniteAchievementsSettings.DynamicAchievementsDefaultSortDirectionKey));

            var changed = state.ApplyDefaults(DynamicThemeViewKeys.All, filterKey, sortKey, directionKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicAchievementsDefaultFilterKey, filterKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicAchievementsDefaultSortKey, sortKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicAchievementsDefaultSortDirectionKey, directionKey);
            return changed;
        }

        private bool ApplyCategorySummaryDefaultsFromSettings()
        {
            var state = _runtimeState.CategorySummaries;
            var filterKey = NormalizeDefaultFilterKey(
                _settings.ModernTheme.DynamicCategorySummariesDefaultFilterKey,
                DynamicThemeOptionGroups.CategorySummaryFilterKeyMap,
                state.DefaultFilterKey,
                nameof(PlayniteAchievementsSettings.DynamicCategorySummariesDefaultFilterKey));
            var sortKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicCategorySummariesDefaultSortKey,
                DynamicThemeOptionGroups.CategorySummarySortKeyMap,
                state.DefaultSortKey,
                DynamicThemeViewKeys.Default,
                nameof(PlayniteAchievementsSettings.DynamicCategorySummariesDefaultSortKey));
            var directionKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicCategorySummariesDefaultSortDirectionKey,
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                state.DefaultSortDirectionKey,
                DynamicThemeViewKeys.Descending,
                nameof(PlayniteAchievementsSettings.DynamicCategorySummariesDefaultSortDirectionKey));

            var changed = state.ApplyDefaults(DynamicThemeViewKeys.All, filterKey, sortKey, directionKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicCategorySummariesDefaultFilterKey, filterKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicCategorySummariesDefaultSortKey, sortKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicCategorySummariesDefaultSortDirectionKey, directionKey);
            return changed;
        }

        private bool ApplyLibraryAchievementDefaultsFromSettings()
        {
            var state = _runtimeState.LibraryAchievements;
            var providerKey = NormalizeProviderKeyValue(
                _settings.ModernTheme.DynamicLibraryAchievementsDefaultProviderKey,
                state.DefaultProviderKey);
            var filterKey = NormalizeDefaultFilterKey(
                _settings.ModernTheme.DynamicLibraryAchievementsDefaultFilterKey,
                DynamicThemeOptionGroups.AchievementFilterKeyMap,
                state.DefaultFilterKey,
                nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsDefaultFilterKey));
            var sortKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicLibraryAchievementsDefaultSortKey,
                DynamicThemeOptionGroups.LibraryAchievementSortKeyMap,
                state.DefaultSortKey,
                DynamicThemeViewKeys.UnlockTime,
                nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsDefaultSortKey));
            var directionKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicLibraryAchievementsDefaultSortDirectionKey,
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                state.DefaultSortDirectionKey,
                DynamicThemeViewKeys.Descending,
                nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsDefaultSortDirectionKey));

            var changed = state.ApplyDefaults(providerKey, filterKey, sortKey, directionKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicLibraryAchievementsDefaultProviderKey, providerKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicLibraryAchievementsDefaultFilterKey, filterKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicLibraryAchievementsDefaultSortKey, sortKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicLibraryAchievementsDefaultSortDirectionKey, directionKey);
            return changed;
        }

        private bool ApplyGameSummaryDefaultsFromSettings()
        {
            var state = _runtimeState.GameSummaries;
            var providerKey = NormalizeProviderKeyValue(
                _settings.ModernTheme.DynamicGameSummariesDefaultProviderKey,
                state.DefaultProviderKey);
            var filterKey = NormalizeDefaultFilterKey(
                _settings.ModernTheme.DynamicGameSummariesDefaultFilterKey,
                DynamicThemeOptionGroups.GameSummaryFilterKeyMap,
                state.DefaultFilterKey,
                nameof(PlayniteAchievementsSettings.DynamicGameSummariesDefaultFilterKey));
            var sortKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicGameSummariesDefaultSortKey,
                DynamicThemeOptionGroups.GameSummarySortKeyMap,
                state.DefaultSortKey,
                DynamicThemeViewKeys.LastUnlock,
                nameof(PlayniteAchievementsSettings.DynamicGameSummariesDefaultSortKey));
            var directionKey = NormalizeDefaultKey(
                _settings.ModernTheme.DynamicGameSummariesDefaultSortDirectionKey,
                DynamicThemeOptionGroups.SortDirectionKeyMap,
                state.DefaultSortDirectionKey,
                DynamicThemeViewKeys.Descending,
                nameof(PlayniteAchievementsSettings.DynamicGameSummariesDefaultSortDirectionKey));

            var changed = state.ApplyDefaults(providerKey, filterKey, sortKey, directionKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicGameSummariesDefaultProviderKey, providerKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicGameSummariesDefaultFilterKey, filterKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicGameSummariesDefaultSortKey, sortKey);
            changed |= !KeysEqual(_settings.ModernTheme.DynamicGameSummariesDefaultSortDirectionKey, directionKey);
            return changed;
        }

        private void ResetDynamicAchievementsToDefaults()
        {
            var state = _runtimeState.SelectedGameAchievements;
            state.ResetToDefault();
            state.ResetCategoryScope();
            ApplyDynamicSelectedGameBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.SingleGameTheme);
        }

        private void ResetDynamicLibraryAchievementsToDefaults()
        {
            var state = _runtimeState.LibraryAchievements;
            state.ResetToDefault();
            state.ResetGameScope();
            ApplyDynamicLibraryAchievementBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicAllGames);
        }

        private void ResetDynamicGameSummariesToDefaults()
        {
            var state = _runtimeState.GameSummaries;
            state.ResetToDefault();
            state.ResetGameScope();
            ApplyDynamicGameSummaryBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicAllGames);
        }

        private void SetDynamicFriendScopeProvider(object parameter)
        {
            EnsureFriendThemeDataLoaded();
            var key = NormalizeFriendProviderScopeKey(parameter);
            if (string.IsNullOrWhiteSpace(key))
            {
                LogInvalidCommandParameter(nameof(PlayniteAchievementsSettings.SetDynamicFriendScopeProviderCommand), parameter);
                return;
            }

            var scope = _runtimeState.FriendScope;
            if (string.Equals(scope.ProviderKey, key, StringComparison.Ordinal))
            {
                return;
            }

            scope.ProviderKey = key;
            scope.UserKey = DynamicThemeViewKeys.All;
            scope.GameKey = DynamicThemeViewKeys.All;
            ApplyDynamicFriendBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        private void SetDynamicFriendScopeUser(object parameter)
        {
            EnsureFriendThemeDataLoaded();
            var key = NormalizeFriendUserScopeKey(parameter);
            if (string.IsNullOrWhiteSpace(key))
            {
                LogInvalidCommandParameter(nameof(PlayniteAchievementsSettings.SetDynamicFriendScopeUserCommand), parameter);
                return;
            }

            var scope = _runtimeState.FriendScope;
            if (string.Equals(scope.UserKey, key, StringComparison.Ordinal))
            {
                return;
            }

            scope.UserKey = key;
            scope.GameKey = DynamicThemeViewKeys.All;
            ApplyDynamicFriendBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        private void SetDynamicFriendScopeGame(object parameter)
        {
            EnsureFriendThemeDataLoaded();
            var key = NormalizeFriendGameScopeKey(parameter);
            if (string.IsNullOrWhiteSpace(key))
            {
                LogInvalidCommandParameter(nameof(PlayniteAchievementsSettings.SetDynamicFriendScopeGameCommand), parameter);
                return;
            }

            var scope = _runtimeState.FriendScope;
            if (string.Equals(scope.GameKey, key, StringComparison.Ordinal))
            {
                return;
            }

            scope.GameKey = key;
            ApplyDynamicFriendBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        private void ResetDynamicFriendScope()
        {
            EnsureFriendThemeDataLoaded();
            _runtimeState.FriendScope.Reset();
            ApplyDynamicFriendBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        private void RequestFriendStateRefresh()
        {
            if (_friendCache == null)
            {
                return;
            }

            lock (_friendRefreshGate)
            {
                _friendRefreshRequestVersion++;
                try { _friendRefreshCts?.Cancel(); } catch { }
                try { _friendRefreshCts?.Dispose(); } catch { }
                _friendRefreshCts = new CancellationTokenSource();

                if (_friendRefreshRunner == null || _friendRefreshRunner.IsCompleted)
                {
                    _friendRefreshRunner = Task.Run(RunFriendStateRefreshLoop);
                }
            }
        }

        private async Task RunFriendStateRefreshLoop()
        {
            while (true)
            {
                int version;
                CancellationToken token;
                lock (_friendRefreshGate)
                {
                    version = _friendRefreshRequestVersion;
                    token = _friendRefreshCts?.Token ?? CancellationToken.None;
                }

                try
                {
                    await Task.Delay(FriendRefreshDelay, token).ConfigureAwait(false);
                    var state = await BuildFriendStateAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    void Apply()
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        lock (_friendRefreshGate)
                        {
                            if (version < _friendRefreshRequestVersion ||
                                version <= _friendRefreshAppliedVersion)
                            {
                                return;
                            }

                            _friendRefreshAppliedVersion = version;
                        }

                        ApplyFriendState(state, notify: true);
                    }

                    var uiDispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                    if (uiDispatcher == null)
                    {
                        Apply();
                    }
                    else
                    {
                        uiDispatcher.InvokeIfNeeded(Apply, DispatcherPriority.Background);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to refresh friend theme runtime state.");
                }

                lock (_friendRefreshGate)
                {
                    if (version >= _friendRefreshRequestVersion)
                    {
                        return;
                    }
                }
            }
        }

        private async Task<FriendRuntimeState> BuildFriendStateAsync(CancellationToken token)
        {
            if (_friendsOverviewDataCoordinator == null)
            {
                return FriendRuntimeState.Empty;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                FriendsOverviewSnapshot snapshot;
                using (PerfScope.Start(_logger, "ThemeFriends.BuildState", thresholdMs: 25))
                {
                    snapshot = await _friendsOverviewDataCoordinator
                        .GetSnapshotAsync(token)
                        .ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                return new FriendRuntimeState(snapshot?.Projection);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to build friend theme runtime state.");
                return FriendRuntimeState.Empty;
            }
        }

        private void ApplyFriendState(FriendRuntimeState state, bool notify)
        {
            _runtimeState.Friends = state ?? FriendRuntimeState.Empty;
            // The pair rows were loaded against the previous snapshot's cache state; drop them
            // so a still-selected pair re-fetches during the binding rebuild below.
            ResetDynamicFriendPairRows();
            ValidateDynamicFriendScope(_runtimeState.Friends);
            ApplyDynamicFriendBindings(updateOptions: false);
            if (notify)
            {
                NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
            }
        }

        private void ValidateDynamicFriendScope(FriendRuntimeState state)
        {
            state = state ?? FriendRuntimeState.Empty;
            var scope = _runtimeState.FriendScope;

            if (!FriendOverviewProjection.IsAllScope(scope.ProviderKey) &&
                !state.Friends.Any(friend => FriendMatchesProvider(friend, scope.ProviderKey)) &&
                !state.AggregateGames.Any(game => string.Equals(game?.ProviderKey, scope.ProviderKey, StringComparison.OrdinalIgnoreCase)))
            {
                scope.ProviderKey = DynamicThemeViewKeys.All;
                scope.UserKey = DynamicThemeViewKeys.All;
                scope.GameKey = DynamicThemeViewKeys.All;
            }

            if (!FriendOverviewProjection.IsAllScope(scope.UserKey))
            {
                var friend = state.Projection?.FindFriend(scope.UserKey);
                if (friend == null ||
                    !FriendMatchesProvider(friend, scope.ProviderKey))
                {
                    scope.UserKey = DynamicThemeViewKeys.All;
                    scope.GameKey = DynamicThemeViewKeys.All;
                }
            }

            if (!FriendOverviewProjection.IsAllScope(scope.GameKey) &&
                !GetFriendGameScopeCandidates(state, scope.ProviderKey, scope.UserKey)
                    .Any(game => string.Equals(
                        FriendOverviewProjection.GetGameScopeKey(game),
                        scope.GameKey,
                        StringComparison.OrdinalIgnoreCase)))
            {
                scope.GameKey = DynamicThemeViewKeys.All;
            }
        }

        private string NormalizeFriendProviderScopeKey(object parameter)
        {
            var raw = ExtractProviderKeyParameter(parameter);
            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return DynamicThemeViewKeys.All;
            }

            return FindCanonicalProviderKey(raw) ?? raw;
        }

        private string NormalizeFriendUserScopeKey(object parameter)
        {
            var raw = ExtractFriendUserScopeKey(parameter);
            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return DynamicThemeViewKeys.All;
            }

            return (_runtimeState.Friends?.Friends ?? Enumerable.Empty<FriendSummaryItem>())
                .Select(FriendOverviewProjection.GetFriendScopeKey)
                .FirstOrDefault(key => string.Equals(key, raw, StringComparison.OrdinalIgnoreCase)) ?? raw;
        }

        private string NormalizeFriendGameScopeKey(object parameter)
        {
            var raw = ExtractFriendGameScopeKey(parameter);
            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return DynamicThemeViewKeys.All;
            }

            var state = _runtimeState.Friends ?? FriendRuntimeState.Empty;
            var scope = _runtimeState.FriendScope;
            return GetFriendGameScopeCandidates(state, scope.ProviderKey, scope.UserKey)
                .Select(FriendOverviewProjection.GetGameScopeKey)
                .FirstOrDefault(key => string.Equals(key, raw, StringComparison.OrdinalIgnoreCase)) ?? raw;
        }

        private static string ExtractFriendUserScopeKey(object parameter)
        {
            if (parameter == DependencyProperty.UnsetValue)
            {
                return null;
            }

            switch (parameter)
            {
                case DynamicThemeOption option:
                    return option.Key;
                case FriendSummaryItem friend:
                    return FriendOverviewProjection.GetFriendScopeKey(friend);
                case FriendAchievementDisplayItem achievement:
                    return FriendOverviewProjection.GetFriendScopeKey(achievement);
                default:
                    return DynamicThemeFilterExpression.NormalizeParameter(parameter);
            }
        }

        private static string ExtractFriendGameScopeKey(object parameter)
        {
            if (parameter == DependencyProperty.UnsetValue)
            {
                return null;
            }

            switch (parameter)
            {
                case DynamicThemeOption option:
                    return option.Key;
                case FriendGameSummaryItem game:
                    return FriendOverviewProjection.BuildGameUnlockKey(game.ProviderKey, game.ProviderGameKey, game.AppId, game.PlayniteGameId);
                case FriendGameAchievementSummary summary:
                    return FriendOverviewProjection.BuildGameUnlockKey(
                        summary.ProviderKey,
                        summary.ProviderGameKey,
                        summary.AppId,
                        summary.GameId != Guid.Empty ? summary.GameId : (Guid?)null);
                case FriendAchievementDisplayItem achievement:
                    return FriendOverviewProjection.BuildGameUnlockKey(
                        achievement.ProviderKey,
                        achievement.ProviderGameKey,
                        achievement.AppId,
                        achievement.PlayniteGameId);
                default:
                    return DynamicThemeFilterExpression.NormalizeParameter(parameter);
            }
        }

        private static bool ProviderMatches(string itemProviderKey, string scopeProviderKey)
        {
            return FriendOverviewProjection.IsAllScope(scopeProviderKey) ||
                   string.Equals(itemProviderKey, scopeProviderKey, StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeDefaultKey(
            string rawKey,
            IReadOnlyDictionary<string, string> keyMap,
            string currentKey,
            string fallbackKey,
            string sourceName)
        {
            if (DynamicThemeFilterExpression.TryNormalizeOne(rawKey, keyMap, out var normalized))
            {
                return normalized;
            }

            if (!string.IsNullOrWhiteSpace(rawKey))
            {
                LogInvalidCommandParameter(sourceName, rawKey);
            }

            return !string.IsNullOrWhiteSpace(currentKey) ? currentKey : fallbackKey;
        }

        private string NormalizeDefaultFilterKey(
            string rawKey,
            IReadOnlyDictionary<string, string> keyMap,
            string currentKey,
            string sourceName)
        {
            if (DynamicThemeFilterExpression.TryNormalize(rawKey, keyMap, out var normalized))
            {
                return normalized;
            }

            if (!string.IsNullOrWhiteSpace(rawKey))
            {
                LogInvalidCommandParameter(sourceName, rawKey);
            }

            return !string.IsNullOrWhiteSpace(currentKey) ? currentKey : DynamicThemeViewKeys.All;
        }

        private string NormalizeProviderKeyValue(
            object parameter,
            string currentKey)
        {
            var raw = ExtractProviderKeyParameter(parameter);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return !string.IsNullOrWhiteSpace(currentKey) ? currentKey : DynamicThemeViewKeys.All;
            }

            if (string.Equals(raw, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return DynamicThemeViewKeys.All;
            }

            return FindCanonicalProviderKey(raw) ?? raw;
        }

        private static string ExtractProviderKeyParameter(object parameter)
        {
            if (parameter == DependencyProperty.UnsetValue)
            {
                return null;
            }

            switch (parameter)
            {
                case DynamicThemeOption option:
                    return option.Key;
                case AchievementDetail achievement:
                    return achievement.ProviderKey;
                case AchievementDisplayItem displayItem:
                    return displayItem.ProviderKey;
                case GameAchievementSummary summary:
                    return summary.ProviderKey;
                case GameSummaryItem game:
                    return game.ProviderKey;
                case FriendSummaryItem friend:
                    return GetFriendProviderScopeKeys(friend).FirstOrDefault() ?? friend.ProviderKey;
                default:
                    return DynamicThemeFilterExpression.NormalizeParameter(parameter);
            }
        }

        private string FindCanonicalProviderKey(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return null;
            }

            var knownProviderKey = DynamicThemeOptionGroups.KnownProviderKeys
                .FirstOrDefault(key => string.Equals(key, providerKey, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(knownProviderKey))
            {
                return knownProviderKey;
            }

            return EnumerateKnownProviderKeys()
                .FirstOrDefault(key => string.Equals(key, providerKey, StringComparison.OrdinalIgnoreCase));
        }

        private static bool KeysEqual(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
        }

        private ICommand CreateDynamicCommand(
            string commandName,
            IReadOnlyDictionary<string, string> keyMap,
            Func<string> getCurrentKey,
            Action<string> setCurrentKey,
            Action applyBindings,
            string[] propertyNamesToNotify)
        {
            return CreateDynamicCommand(
                commandName,
                (object parameter, out string normalizedKey) => DynamicThemeFilterExpression.TryNormalizeOne(
                    parameter,
                    keyMap,
                    out normalizedKey),
                getCurrentKey,
                setCurrentKey,
                applyBindings,
                propertyNamesToNotify);
        }

        private ICommand CreateDynamicCommand(
            string commandName,
            CommandParameterNormalizer normalizer,
            Func<string> getCurrentKey,
            Action<string> setCurrentKey,
            Action applyBindings,
            string[] propertyNamesToNotify)
        {
            return new RelayCommand(parameter => ExecuteDynamicCommand(
                parameter,
                commandName,
                normalizer,
                getCurrentKey,
                setCurrentKey,
                applyBindings,
                propertyNamesToNotify));
        }

        private void ExecuteDynamicCommand(
            object parameter,
            string commandName,
            CommandParameterNormalizer normalizer,
            Func<string> getCurrentKey,
            Action<string> setCurrentKey,
            Action applyBindings,
            string[] propertyNamesToNotify)
        {
            if (normalizer == null || getCurrentKey == null || setCurrentKey == null || applyBindings == null)
            {
                return;
            }

            if (!normalizer(parameter, out var normalizedKey))
            {
                LogInvalidCommandParameter(commandName, parameter);
                return;
            }

            if (string.Equals(getCurrentKey(), normalizedKey, StringComparison.Ordinal))
            {
                return;
            }

            setCurrentKey(normalizedKey);
            applyBindings();
            NotifySettingProperties(propertyNamesToNotify);
        }

        private static ICommand CreateItemCommand(ICommand rootCommand, object owner)
        {
            return rootCommand == null
                ? null
                : new RelayCommand(parameter => ExecuteRootCommand(rootCommand, parameter ?? owner));
        }

        private static void ExecuteRootCommand(ICommand command, object parameter)
        {
            if (command?.CanExecute(parameter) == true)
            {
                command.Execute(parameter);
            }
        }

        /// <summary>
        /// Attach per-item wrapper commands for one dynamic collection. Each binding pairs a
        /// root command with the setter that stores its item-scoped wrapper on the item.
        /// </summary>
        private void AttachItemCommands<T>(
            IEnumerable<T> items,
            params (ICommand Root, Action<T, ICommand> Assign)[] commandBindings)
            where T : class
        {
            foreach (var item in items ?? Enumerable.Empty<T>())
            {
                if (item == null)
                {
                    continue;
                }

                foreach (var binding in commandBindings)
                {
                    binding.Assign(item, CreateItemCommand(binding.Root, item));
                }
            }
        }

        private void AttachAchievementCommands(IEnumerable<AchievementDetail> items)
        {
            AttachItemCommands<AchievementDetail>(
                items,
                (_settings.SetDynamicAchievementsGameCommand, (item, command) => item.SetDynamicAchievementsGameCommand = command),
                (_settings.FilterDynamicLibraryAchievementsByProviderCommand, (item, command) => item.FilterDynamicLibraryAchievementsByProviderCommand = command),
                (_settings.OpenViewAchievementsWindow, (item, command) => item.OpenViewAchievementsWindow = command),
                (_settings.OpenManageAchievementsWindow, (item, command) => item.OpenManageAchievementsWindow = command));
        }

        private void AttachGameSummaryCommands(IEnumerable<GameAchievementSummary> items)
        {
            AttachItemCommands<GameAchievementSummary>(
                items,
                (_settings.SetDynamicAchievementsGameCommand, (item, command) => item.SetDynamicAchievementsGameCommand = command),
                (_settings.FilterDynamicGameSummariesByProviderCommand, (item, command) => item.FilterDynamicGameSummariesByProviderCommand = command));
        }

        private void AttachFriendSummaryCommands(IEnumerable<FriendSummaryItem> items)
        {
            AttachItemCommands<FriendSummaryItem>(
                items,
                (_settings.SetDynamicFriendScopeProviderCommand, (item, command) => item.SetDynamicFriendScopeProviderCommand = command),
                (_settings.SetDynamicFriendScopeUserCommand, (item, command) => item.SetDynamicFriendScopeUserCommand = command));
        }

        private void AttachFriendGameSummaryCommands(IEnumerable<FriendGameAchievementSummary> items)
        {
            AttachItemCommands<FriendGameAchievementSummary>(
                items,
                (_settings.SetDynamicAchievementsGameCommand, (item, command) => item.SetDynamicAchievementsGameCommand = command),
                (_settings.FilterDynamicGameSummariesByProviderCommand, (item, command) => item.FilterDynamicGameSummariesByProviderCommand = command),
                (_settings.SetDynamicFriendScopeProviderCommand, (item, command) => item.SetDynamicFriendScopeProviderCommand = command),
                (_settings.SetDynamicFriendScopeGameCommand, (item, command) => item.SetDynamicFriendScopeGameCommand = command));
        }

        private void AttachFriendAchievementCommands(IEnumerable<FriendAchievementDisplayItem> items)
        {
            AttachItemCommands<FriendAchievementDisplayItem>(
                items,
                (_settings.SetDynamicAchievementsGameCommand, (item, command) => item.SetDynamicAchievementsGameCommand = command),
                (_settings.FilterDynamicLibraryAchievementsByProviderCommand, (item, command) => item.FilterDynamicLibraryAchievementsByProviderCommand = command),
                (_settings.OpenViewAchievementsWindow, (item, command) => item.OpenViewAchievementsWindow = command),
                (_settings.OpenManageAchievementsWindow, (item, command) => item.OpenManageAchievementsWindow = command),
                (_settings.SetDynamicFriendScopeProviderCommand, (item, command) => item.SetDynamicFriendScopeProviderCommand = command),
                (_settings.SetDynamicFriendScopeUserCommand, (item, command) => item.SetDynamicFriendScopeUserCommand = command),
                (_settings.SetDynamicFriendScopeGameCommand, (item, command) => item.SetDynamicFriendScopeGameCommand = command));
        }

        /// <summary>
        /// Options for one grouped filter facet of a dynamic collection (e.g. rarity or trophy),
        /// published as an option collection whose selection is derived from the composite filter key.
        /// </summary>
        private sealed class DynamicGroupOptionBinding
        {
            public IReadOnlyList<string> OptionKeys;
            public IReadOnlyList<string> GroupKeys;
            public IReadOnlyDictionary<string, string> GroupMap;
            public Func<ICommand> Command;
            public Action<ObservableCollection<DynamicThemeOption>> SetOptions;
        }

        /// <summary>
        /// Descriptor for one ModernTheme dynamic collection: where its view state lives, which
        /// ModernTheme key/label/default/option properties it publishes, the option key groups it
        /// offers, and the commands bound to its option lists. Optional setters (provider, game,
        /// defaults, group facets) are null for collections that do not publish them.
        /// </summary>
        private sealed class DynamicListBinding
        {
            public Func<DynamicThemeListViewState> ViewState;
            public string SortLabelFallbackKey;

            public Action<string> SetProviderKey;
            public Action<string> SetProviderLabel;
            public Func<string> GetGameKey;
            public Func<string> GetGameLabel;
            public Action<string> SetGameKey;
            public Action<string> SetGameLabel;
            public Action<string> SetFilterKey;
            public Action<string> SetFilterLabel;
            public Action<string> SetSortKey;
            public Action<string> SetSortLabel;
            public Action<string> SetSortDirectionKey;
            public Action<string> SetSortDirectionLabel;
            public Action<string> SetDefaultProviderKey;
            public Action<string> SetDefaultFilterKey;
            public Action<string> SetDefaultSortKey;
            public Action<string> SetDefaultSortDirectionKey;

            public IReadOnlyList<string> FilterOptionKeys;
            public Func<ICommand> FilterCommand;
            public Action<ObservableCollection<DynamicThemeOption>> SetFilterOptions;
            public IReadOnlyList<string> SortOptionKeys;
            public Func<ICommand> SortCommand;
            public Action<ObservableCollection<DynamicThemeOption>> SetSortOptions;
            public Func<ICommand> SortDirectionCommand;
            public Action<ObservableCollection<DynamicThemeOption>> SetSortDirectionOptions;
            public IReadOnlyList<DynamicGroupOptionBinding> GroupFilterOptions;
        }

        /// <summary>
        /// Publish the key, label, and default-key properties for one dynamic collection.
        /// </summary>
        private static void ApplyDynamicListKeyBindings(DynamicListBinding binding)
        {
            var viewState = binding.ViewState();
            if (binding.SetProviderKey != null)
            {
                binding.SetProviderKey(viewState.ProviderKey);
                binding.SetProviderLabel(DynamicThemeLabels.GetProviderLabel(viewState.ProviderKey));
            }

            if (binding.SetGameKey != null)
            {
                binding.SetGameKey(binding.GetGameKey());
                binding.SetGameLabel(binding.GetGameLabel());
            }

            binding.SetFilterKey(viewState.FilterKey);
            binding.SetFilterLabel(DynamicThemeLabels.GetLabel(viewState.FilterKey, DynamicThemeViewKeys.All));
            binding.SetSortKey(viewState.SortKey);
            binding.SetSortLabel(DynamicThemeLabels.GetLabel(viewState.SortKey, binding.SortLabelFallbackKey));
            binding.SetSortDirectionKey(viewState.SortDirectionKey);
            binding.SetSortDirectionLabel(DynamicThemeLabels.GetLabel(viewState.SortDirectionKey, DynamicThemeViewKeys.Descending));

            binding.SetDefaultProviderKey?.Invoke(viewState.DefaultProviderKey);
            binding.SetDefaultFilterKey?.Invoke(viewState.DefaultFilterKey);
            binding.SetDefaultSortKey?.Invoke(viewState.DefaultSortKey);
            binding.SetDefaultSortDirectionKey?.Invoke(viewState.DefaultSortDirectionKey);
        }

        /// <summary>
        /// Publish the filter/sort/direction option collections for one dynamic collection,
        /// followed by its grouped filter facet options.
        /// </summary>
        private static void ApplyDynamicListOptionBindings(DynamicListBinding binding)
        {
            var viewState = binding.ViewState();
            binding.SetFilterOptions(DynamicThemeOptionFactory.CreateOptions(
                binding.FilterOptionKeys,
                viewState.FilterKey,
                binding.FilterCommand()));
            binding.SetSortOptions(DynamicThemeOptionFactory.CreateOptions(
                binding.SortOptionKeys,
                viewState.SortKey,
                binding.SortCommand()));
            binding.SetSortDirectionOptions(DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.SortDirectionKeys,
                viewState.SortDirectionKey,
                binding.SortDirectionCommand()));

            foreach (var group in binding.GroupFilterOptions ?? Array.Empty<DynamicGroupOptionBinding>())
            {
                group.SetOptions(DynamicThemeOptionFactory.CreateOptions(
                    group.OptionKeys,
                    DynamicThemeOptionGroups.GetGroupSelection(viewState.FilterKey, group.GroupKeys, group.GroupMap),
                    group.Command()));
            }
        }

        private DynamicListBinding _selectedGameListBinding;

        private DynamicListBinding SelectedGameListBinding => _selectedGameListBinding ??= new DynamicListBinding
        {
            ViewState = () => _runtimeState.SelectedGameAchievements,
            SortLabelFallbackKey = DynamicThemeViewKeys.UnlockTime,
            SetFilterKey = value => _settings.ModernTheme.DynamicAchievementsFilterKey = value,
            SetFilterLabel = value => _settings.ModernTheme.DynamicAchievementsFilterLabel = value,
            SetSortKey = value => _settings.ModernTheme.DynamicAchievementsSortKey = value,
            SetSortLabel = value => _settings.ModernTheme.DynamicAchievementsSortLabel = value,
            SetSortDirectionKey = value => _settings.ModernTheme.DynamicAchievementsSortDirectionKey = value,
            SetSortDirectionLabel = value => _settings.ModernTheme.DynamicAchievementsSortDirectionLabel = value,
            SetDefaultFilterKey = value => _settings.ModernTheme.DynamicAchievementsDefaultFilterKey = value,
            SetDefaultSortKey = value => _settings.ModernTheme.DynamicAchievementsDefaultSortKey = value,
            SetDefaultSortDirectionKey = value => _settings.ModernTheme.DynamicAchievementsDefaultSortDirectionKey = value,
            FilterOptionKeys = DynamicThemeOptionGroups.AchievementFilterKeys,
            FilterCommand = () => _settings.SetDynamicAchievementsFilterCommand,
            SetFilterOptions = value => _settings.ModernTheme.DynamicAchievementsFilterOptions = value,
            SortOptionKeys = DynamicThemeOptionGroups.SelectedGameAchievementSortKeys,
            SortCommand = () => _settings.SortDynamicAchievementsCommand,
            SetSortOptions = value => _settings.ModernTheme.DynamicAchievementsSortOptions = value,
            SortDirectionCommand = () => _settings.SetDynamicAchievementsSortDirectionCommand,
            SetSortDirectionOptions = value => _settings.ModernTheme.DynamicAchievementsSortDirectionOptions = value,
        };

        private DynamicListBinding _categorySummariesListBinding;

        private DynamicListBinding CategorySummariesListBinding => _categorySummariesListBinding ??= new DynamicListBinding
        {
            ViewState = () => _runtimeState.CategorySummaries,
            SortLabelFallbackKey = DynamicThemeViewKeys.Default,
            SetFilterKey = value => _settings.ModernTheme.DynamicCategorySummariesFilterKey = value,
            SetFilterLabel = value => _settings.ModernTheme.DynamicCategorySummariesFilterLabel = value,
            SetSortKey = value => _settings.ModernTheme.DynamicCategorySummariesSortKey = value,
            SetSortLabel = value => _settings.ModernTheme.DynamicCategorySummariesSortLabel = value,
            SetSortDirectionKey = value => _settings.ModernTheme.DynamicCategorySummariesSortDirectionKey = value,
            SetSortDirectionLabel = value => _settings.ModernTheme.DynamicCategorySummariesSortDirectionLabel = value,
            SetDefaultFilterKey = value => _settings.ModernTheme.DynamicCategorySummariesDefaultFilterKey = value,
            SetDefaultSortKey = value => _settings.ModernTheme.DynamicCategorySummariesDefaultSortKey = value,
            SetDefaultSortDirectionKey = value => _settings.ModernTheme.DynamicCategorySummariesDefaultSortDirectionKey = value,
            FilterOptionKeys = DynamicThemeOptionGroups.CategorySummaryFilterKeys,
            FilterCommand = () => _settings.SetDynamicCategorySummariesFilterCommand,
            SetFilterOptions = value => _settings.ModernTheme.DynamicCategorySummariesFilterOptions = value,
            SortOptionKeys = DynamicThemeOptionGroups.CategorySummarySortKeys,
            SortCommand = () => _settings.SortDynamicCategorySummariesCommand,
            SetSortOptions = value => _settings.ModernTheme.DynamicCategorySummariesSortOptions = value,
            SortDirectionCommand = () => _settings.SetDynamicCategorySummariesSortDirectionCommand,
            SetSortDirectionOptions = value => _settings.ModernTheme.DynamicCategorySummariesSortDirectionOptions = value,
        };

        private DynamicListBinding _libraryAchievementListBinding;

        private DynamicListBinding LibraryAchievementListBinding => _libraryAchievementListBinding ??= new DynamicListBinding
        {
            ViewState = () => _runtimeState.LibraryAchievements,
            SortLabelFallbackKey = DynamicThemeViewKeys.UnlockTime,
            SetProviderKey = value => _settings.ModernTheme.DynamicLibraryAchievementsProviderKey = value,
            SetProviderLabel = value => _settings.ModernTheme.DynamicLibraryAchievementsProviderLabel = value,
            GetGameKey = () => _runtimeState.LibraryAchievements.GameKey,
            GetGameLabel = () => _runtimeState.LibraryAchievements.GameLabel,
            SetGameKey = value => _settings.ModernTheme.DynamicLibraryAchievementsGameKey = value,
            SetGameLabel = value => _settings.ModernTheme.DynamicLibraryAchievementsGameLabel = value,
            SetFilterKey = value => _settings.ModernTheme.DynamicLibraryAchievementsFilterKey = value,
            SetFilterLabel = value => _settings.ModernTheme.DynamicLibraryAchievementsFilterLabel = value,
            SetSortKey = value => _settings.ModernTheme.DynamicLibraryAchievementsSortKey = value,
            SetSortLabel = value => _settings.ModernTheme.DynamicLibraryAchievementsSortLabel = value,
            SetSortDirectionKey = value => _settings.ModernTheme.DynamicLibraryAchievementsSortDirectionKey = value,
            SetSortDirectionLabel = value => _settings.ModernTheme.DynamicLibraryAchievementsSortDirectionLabel = value,
            SetDefaultProviderKey = value => _settings.ModernTheme.DynamicLibraryAchievementsDefaultProviderKey = value,
            SetDefaultFilterKey = value => _settings.ModernTheme.DynamicLibraryAchievementsDefaultFilterKey = value,
            SetDefaultSortKey = value => _settings.ModernTheme.DynamicLibraryAchievementsDefaultSortKey = value,
            SetDefaultSortDirectionKey = value => _settings.ModernTheme.DynamicLibraryAchievementsDefaultSortDirectionKey = value,
            FilterOptionKeys = DynamicThemeOptionGroups.AchievementFilterKeys,
            FilterCommand = () => _settings.SetDynamicLibraryAchievementsFilterCommand,
            SetFilterOptions = value => _settings.ModernTheme.DynamicLibraryAchievementsFilterOptions = value,
            SortOptionKeys = DynamicThemeOptionGroups.LibraryAchievementSortKeys,
            SortCommand = () => _settings.SortDynamicLibraryAchievementsCommand,
            SetSortOptions = value => _settings.ModernTheme.DynamicLibraryAchievementsSortOptions = value,
            SortDirectionCommand = () => _settings.SetDynamicLibraryAchievementsSortDirectionCommand,
            SetSortDirectionOptions = value => _settings.ModernTheme.DynamicLibraryAchievementsSortDirectionOptions = value,
            GroupFilterOptions = new[]
            {
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementStatusFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementStatusGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicLibraryAchievementsStatusFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicLibraryAchievementStatusFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementProgressFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementProgressGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicLibraryAchievementsProgressFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicLibraryAchievementProgressFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementRarityFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementRarityGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicLibraryAchievementsRarityFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicLibraryAchievementRarityFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementTrophyFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementTrophyGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicLibraryAchievementsTrophyFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicLibraryAchievementTrophyFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementCategoryTypeFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementCategoryTypeGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicLibraryAchievementsCategoryTypeFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicLibraryAchievementCategoryTypeFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementCustomizationFilterKeys,
                    GroupKeys = DynamicThemeOptionGroups.AchievementCustomizationGroups,
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicLibraryAchievementsCustomizationFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicLibraryAchievementCustomizationFilterOptions = value,
                },
            },
        };

        private DynamicListBinding _gameSummaryListBinding;

        private DynamicListBinding GameSummaryListBinding => _gameSummaryListBinding ??= new DynamicListBinding
        {
            ViewState = () => _runtimeState.GameSummaries,
            SortLabelFallbackKey = DynamicThemeViewKeys.LastUnlock,
            SetProviderKey = value => _settings.ModernTheme.DynamicGameSummariesProviderKey = value,
            SetProviderLabel = value => _settings.ModernTheme.DynamicGameSummariesProviderLabel = value,
            GetGameKey = () => _runtimeState.GameSummaries.GameKey,
            GetGameLabel = () => _runtimeState.GameSummaries.GameLabel,
            SetGameKey = value => _settings.ModernTheme.DynamicGameSummariesGameKey = value,
            SetGameLabel = value => _settings.ModernTheme.DynamicGameSummariesGameLabel = value,
            SetFilterKey = value => _settings.ModernTheme.DynamicGameSummariesFilterKey = value,
            SetFilterLabel = value => _settings.ModernTheme.DynamicGameSummariesFilterLabel = value,
            SetSortKey = value => _settings.ModernTheme.DynamicGameSummariesSortKey = value,
            SetSortLabel = value => _settings.ModernTheme.DynamicGameSummariesSortLabel = value,
            SetSortDirectionKey = value => _settings.ModernTheme.DynamicGameSummariesSortDirectionKey = value,
            SetSortDirectionLabel = value => _settings.ModernTheme.DynamicGameSummariesSortDirectionLabel = value,
            SetDefaultProviderKey = value => _settings.ModernTheme.DynamicGameSummariesDefaultProviderKey = value,
            SetDefaultFilterKey = value => _settings.ModernTheme.DynamicGameSummariesDefaultFilterKey = value,
            SetDefaultSortKey = value => _settings.ModernTheme.DynamicGameSummariesDefaultSortKey = value,
            SetDefaultSortDirectionKey = value => _settings.ModernTheme.DynamicGameSummariesDefaultSortDirectionKey = value,
            FilterOptionKeys = DynamicThemeOptionGroups.GameSummaryFilterKeys,
            FilterCommand = () => _settings.SetDynamicGameSummariesFilterCommand,
            SetFilterOptions = value => _settings.ModernTheme.DynamicGameSummariesFilterOptions = value,
            SortOptionKeys = DynamicThemeOptionGroups.GameSummarySortKeys,
            SortCommand = () => _settings.SortDynamicGameSummariesCommand,
            SetSortOptions = value => _settings.ModernTheme.DynamicGameSummariesSortOptions = value,
            SortDirectionCommand = () => _settings.SetDynamicGameSummariesSortDirectionCommand,
            SetSortDirectionOptions = value => _settings.ModernTheme.DynamicGameSummariesSortDirectionOptions = value,
        };

        private DynamicListBinding _friendSummariesListBinding;

        private DynamicListBinding FriendSummariesListBinding => _friendSummariesListBinding ??= new DynamicListBinding
        {
            ViewState = () => _runtimeState.FriendSummaries,
            SortLabelFallbackKey = DynamicThemeViewKeys.LastUnlock,
            SetFilterKey = value => _settings.ModernTheme.DynamicFriendSummariesFilterKey = value,
            SetFilterLabel = value => _settings.ModernTheme.DynamicFriendSummariesFilterLabel = value,
            SetSortKey = value => _settings.ModernTheme.DynamicFriendSummariesSortKey = value,
            SetSortLabel = value => _settings.ModernTheme.DynamicFriendSummariesSortLabel = value,
            SetSortDirectionKey = value => _settings.ModernTheme.DynamicFriendSummariesSortDirectionKey = value,
            SetSortDirectionLabel = value => _settings.ModernTheme.DynamicFriendSummariesSortDirectionLabel = value,
            FilterOptionKeys = DynamicThemeOptionGroups.FriendSummaryFilterKeys,
            FilterCommand = () => _settings.SetDynamicFriendSummariesFilterCommand,
            SetFilterOptions = value => _settings.ModernTheme.DynamicFriendSummariesFilterOptions = value,
            SortOptionKeys = DynamicThemeOptionGroups.FriendSummarySortKeys,
            SortCommand = () => _settings.SortDynamicFriendSummariesCommand,
            SetSortOptions = value => _settings.ModernTheme.DynamicFriendSummariesSortOptions = value,
            SortDirectionCommand = () => _settings.SetDynamicFriendSummariesSortDirectionCommand,
            SetSortDirectionOptions = value => _settings.ModernTheme.DynamicFriendSummariesSortDirectionOptions = value,
            GroupFilterOptions = new[]
            {
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.FriendSummaryFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.FriendLastUnlockGroup },
                    GroupMap = DynamicThemeOptionGroups.FriendSummaryFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendSummariesLastUnlockFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendSummaryLastUnlockFilterOptions = value,
                },
            },
        };

        private DynamicListBinding _friendGameSummariesListBinding;

        private DynamicListBinding FriendGameSummariesListBinding => _friendGameSummariesListBinding ??= new DynamicListBinding
        {
            ViewState = () => _runtimeState.FriendGameSummaries,
            SortLabelFallbackKey = DynamicThemeViewKeys.LastUnlock,
            SetFilterKey = value => _settings.ModernTheme.DynamicFriendGameSummariesFilterKey = value,
            SetFilterLabel = value => _settings.ModernTheme.DynamicFriendGameSummariesFilterLabel = value,
            SetSortKey = value => _settings.ModernTheme.DynamicFriendGameSummariesSortKey = value,
            SetSortLabel = value => _settings.ModernTheme.DynamicFriendGameSummariesSortLabel = value,
            SetSortDirectionKey = value => _settings.ModernTheme.DynamicFriendGameSummariesSortDirectionKey = value,
            SetSortDirectionLabel = value => _settings.ModernTheme.DynamicFriendGameSummariesSortDirectionLabel = value,
            FilterOptionKeys = DynamicThemeOptionGroups.GameSummaryFilterKeys,
            FilterCommand = () => _settings.SetDynamicFriendGameSummariesFilterCommand,
            SetFilterOptions = value => _settings.ModernTheme.DynamicFriendGameSummariesFilterOptions = value,
            SortOptionKeys = DynamicThemeOptionGroups.GameSummarySortKeys,
            SortCommand = () => _settings.SortDynamicFriendGameSummariesCommand,
            SetSortOptions = value => _settings.ModernTheme.DynamicFriendGameSummariesSortOptions = value,
            SortDirectionCommand = () => _settings.SetDynamicFriendGameSummariesSortDirectionCommand,
            SetSortDirectionOptions = value => _settings.ModernTheme.DynamicFriendGameSummariesSortDirectionOptions = value,
            GroupFilterOptions = new[]
            {
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.GameProgressFilterKeys,
                    GroupKeys = DynamicThemeOptionGroups.GameProgressGroups,
                    GroupMap = DynamicThemeOptionGroups.GameSummaryFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendGameSummariesProgressFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendGameProgressFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.GameActivityFilterKeys,
                    GroupKeys = DynamicThemeOptionGroups.GameActivityGroups,
                    GroupMap = DynamicThemeOptionGroups.GameSummaryFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendGameSummariesActivityFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendGameActivityFilterOptions = value,
                },
            },
        };

        private DynamicListBinding _friendAchievementsListBinding;

        private DynamicListBinding FriendAchievementsListBinding => _friendAchievementsListBinding ??= new DynamicListBinding
        {
            ViewState = () => _runtimeState.FriendAchievements,
            SortLabelFallbackKey = DynamicThemeViewKeys.UnlockTime,
            SetFilterKey = value => _settings.ModernTheme.DynamicFriendAchievementsFilterKey = value,
            SetFilterLabel = value => _settings.ModernTheme.DynamicFriendAchievementsFilterLabel = value,
            SetSortKey = value => _settings.ModernTheme.DynamicFriendAchievementsSortKey = value,
            SetSortLabel = value => _settings.ModernTheme.DynamicFriendAchievementsSortLabel = value,
            SetSortDirectionKey = value => _settings.ModernTheme.DynamicFriendAchievementsSortDirectionKey = value,
            SetSortDirectionLabel = value => _settings.ModernTheme.DynamicFriendAchievementsSortDirectionLabel = value,
            FilterOptionKeys = DynamicThemeOptionGroups.AchievementFilterKeys,
            FilterCommand = () => _settings.SetDynamicFriendAchievementsFilterCommand,
            SetFilterOptions = value => _settings.ModernTheme.DynamicFriendAchievementsFilterOptions = value,
            SortOptionKeys = DynamicThemeOptionGroups.LibraryAchievementSortKeys,
            SortCommand = () => _settings.SortDynamicFriendAchievementsCommand,
            SetSortOptions = value => _settings.ModernTheme.DynamicFriendAchievementsSortOptions = value,
            SortDirectionCommand = () => _settings.SetDynamicFriendAchievementsSortDirectionCommand,
            SetSortDirectionOptions = value => _settings.ModernTheme.DynamicFriendAchievementsSortDirectionOptions = value,
            GroupFilterOptions = new[]
            {
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementStatusFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementStatusGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendAchievementsStatusFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendAchievementStatusFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementProgressFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementProgressGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendAchievementsProgressFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendAchievementProgressFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementRarityFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementRarityGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendAchievementsRarityFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendAchievementRarityFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementTrophyFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementTrophyGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendAchievementsTrophyFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendAchievementTrophyFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementCategoryTypeFilterKeys,
                    GroupKeys = new[] { DynamicThemeOptionGroups.AchievementCategoryTypeGroup },
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendAchievementsCategoryTypeFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendAchievementCategoryTypeFilterOptions = value,
                },
                new DynamicGroupOptionBinding
                {
                    OptionKeys = DynamicThemeOptionGroups.AchievementCustomizationFilterKeys,
                    GroupKeys = DynamicThemeOptionGroups.AchievementCustomizationGroups,
                    GroupMap = DynamicThemeOptionGroups.AchievementFilterGroupMap,
                    Command = () => _settings.SetDynamicFriendAchievementsCustomizationFilterCommand,
                    SetOptions = value => _settings.ModernTheme.DynamicFriendAchievementCustomizationFilterOptions = value,
                },
            },
        };

        private void ApplyDynamicSelectedGameBindings()
        {
            ApplyDynamicSelectedGameBindings(updateOptions: true);
        }

        private void ApplyDynamicSelectedGameBindings(bool updateOptions)
        {
            var state = _runtimeState.SelectedGame ?? SelectedGameRuntimeState.Empty;
            var viewState = _runtimeState.SelectedGameAchievements;
            var items = BuildDynamicSelectedGameAchievements(state, viewState);
            AttachAchievementCommands(items);

            _settings.ModernTheme.DynamicAchievements = items;
            ApplyDynamicListKeyBindings(SelectedGameListBinding);
            ApplyDynamicCategoryLabelBindings(state, viewState);
            ApplyDynamicListOptionBindings(SelectedGameListBinding);
            ApplyDynamicCategorySummaryBindings();
            if (updateOptions)
            {
                ApplyDynamicOptionBindings();
            }
        }

        private void ApplyDynamicCategorySummaryBindings()
        {
            var viewState = _runtimeState.CategorySummaries;
            var displayItems = _settings.ModernTheme.AllAchievementDisplayItems;
            // Mirrors the in-plugin grid's HasMultipleCategories gate: a lone (localized)
            // "Default" card on every game is noise, so single-category games publish an
            // empty list and themes hide the section via HasCategorySummaries.
            var hasCategories = CountDistinctCategoryLabels(displayItems) >= 2;
            if (!hasCategories)
            {
                _settings.ModernTheme.DynamicCategorySummaries = new ObservableCollection<GameAchievementSummary>();
            }
            else
            {
                var summaries = CategorySummaryBuilder.Build(displayItems);
                summaries = FilterCategorySummaries(summaries, viewState.FilterKey);
                summaries = SortCategorySummaries(summaries, viewState.SortKey, viewState.SortDirectionKey);
                _settings.ModernTheme.DynamicCategorySummaries = ProjectCategorySummaries(summaries, displayItems);
            }

            _settings.ModernTheme.HasCategorySummaries = hasCategories;
            ApplyDynamicListKeyBindings(CategorySummariesListBinding);
            ApplyDynamicListOptionBindings(CategorySummariesListBinding);
        }

        private static int CountDistinctCategoryLabels(IReadOnlyList<AchievementDisplayItem> items)
        {
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items ?? (IReadOnlyList<AchievementDisplayItem>)Array.Empty<AchievementDisplayItem>())
            {
                if (item != null)
                {
                    labels.Add(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel));
                }
            }

            return labels.Count;
        }

        private static List<GameSummaryItem> FilterCategorySummaries(List<GameSummaryItem> items, string filterKey)
        {
            switch (filterKey)
            {
                case DynamicThemeViewKeys.Completed:
                    return items.Where(item => item?.IsCompleted == true).ToList();
                case DynamicThemeViewKeys.Incomplete:
                    return items.Where(item => item != null && !item.IsCompleted).ToList();
                default:
                    return items;
            }
        }

        private static List<GameSummaryItem> SortCategorySummaries(
            List<GameSummaryItem> items,
            string sortKey,
            string sortDirectionKey)
        {
            IOrderedEnumerable<GameSummaryItem> ordered;
            switch (sortKey)
            {
                case DynamicThemeViewKeys.Name:
                    ordered = items.OrderBy(
                        item => item?.SortingName ?? item?.GameName,
                        StringComparer.CurrentCultureIgnoreCase);
                    break;
                case DynamicThemeViewKeys.Progress:
                    ordered = items
                        .OrderBy(item => item?.Progression ?? 0)
                        .ThenBy(item => item?.GameName, StringComparer.CurrentCultureIgnoreCase);
                    break;
                default:
                    // Default preserves builder order: the game's custom category order,
                    // regardless of direction.
                    return items;
            }

            if (string.Equals(sortDirectionKey, DynamicThemeViewKeys.Descending, StringComparison.OrdinalIgnoreCase))
            {
                return ordered.Reverse().ToList();
            }

            return ordered.ToList();
        }

        private ObservableCollection<GameAchievementSummary> ProjectCategorySummaries(
            List<GameSummaryItem> items,
            IReadOnlyList<AchievementDisplayItem> displayItems)
        {
            var gameId = _runtimeState.SelectedGame?.GameId ?? Guid.Empty;
            var providerKey = (displayItems ?? (IReadOnlyList<AchievementDisplayItem>)Array.Empty<AchievementDisplayItem>())
                .Select(item => item?.ProviderKey)
                .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key)) ?? string.Empty;
            var providerName = ProviderRegistry.GetLocalizedName(providerKey);
            var hasGame = gameId != Guid.Empty;

            var projected = new List<GameAchievementSummary>();
            foreach (var item in items ?? Enumerable.Empty<GameSummaryItem>())
            {
                if (item == null)
                {
                    continue;
                }

                var common = AchievementGameStats.CreateRarityStats(item.CommonCount, item.TotalCommonPossible);
                var uncommon = AchievementGameStats.CreateRarityStats(item.UncommonCount, item.TotalUncommonPossible);
                var rare = AchievementGameStats.CreateRarityStats(item.RareCount, item.TotalRarePossible);
                var ultraRare = AchievementGameStats.CreateRarityStats(item.UltraRareCount, item.TotalUltraRarePossible);
                var rareAndUltraRare = AchievementRarityStatsCombiner.Combine(rare, ultraRare);
                var overall = AchievementRarityStatsCombiner.Combine(common, uncommon, rare, ultraRare);

                projected.Add(new GameAchievementSummary(
                    gameId,
                    item.GameName,
                    providerName,
                    item.GameCoverPath ?? item.GameLogo,
                    item.Progression,
                    item.RareCount + item.UltraRareCount,
                    item.UncommonCount,
                    item.CommonCount,
                    item.IsCompleted,
                    item.LastUnlockUtc.HasValue ? item.LastUnlockUtc.Value.ToLocalTime() : DateTime.MinValue,
                    hasGame ? GetOpenViewAchievementsCommand(gameId) : null,
                    common,
                    uncommon,
                    rare,
                    ultraRare,
                    rareAndUltraRare,
                    overall,
                    providerKey,
                    providerName,
                    null,
                    item.UnlockedAchievements,
                    item.TotalAchievements,
                    openManageAchievementsWindow: hasGame ? GetOpenManageAchievementsCommand(gameId) : null,
                    sortingName: item.SortingName ?? item.GameName,
                    categoryType: (item as CategorySummaryItem)?.CategoryType));
            }

            return new ObservableCollection<GameAchievementSummary>(projected);
        }

        private void ResetDynamicCategorySummariesToDefaults()
        {
            var state = _runtimeState.CategorySummaries;
            state.ResetToDefault();
            ApplyDynamicCategorySummaryBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.SingleGameTheme);
        }

        private void ApplyDynamicCategoryLabelBindings(
            SelectedGameRuntimeState state,
            SelectedGameAchievementViewState viewState)
        {
            var categoryKey = viewState.CategoryLabelKey ?? DynamicThemeViewKeys.All;
            _settings.ModernTheme.DynamicAchievementsCategoryLabelFilterKey = categoryKey;
            _settings.ModernTheme.DynamicAchievementsCategoryLabelFilterLabel =
                string.Equals(categoryKey, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase)
                    ? DynamicThemeLabels.GetLabel(DynamicThemeViewKeys.All, DynamicThemeViewKeys.All)
                    : AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(categoryKey);
            _settings.ModernTheme.DynamicAchievementCategoryLabelFilterOptions =
                DynamicThemeOptionFactory.CreateCategoryLabelOptions(
                    state?.AllAchievements,
                    categoryKey,
                    _settings.SetDynamicAchievementsCategoryLabelFilterCommand);
        }

        private void ApplyDynamicLibraryAchievementBindings()
        {
            ApplyDynamicLibraryAchievementBindings(updateOptions: true);
        }

        private void ApplyDynamicLibraryAchievementBindings(bool updateOptions)
        {
            var state = _runtimeState.Library ?? new LibraryRuntimeState();
            var viewState = _runtimeState.LibraryAchievements;
            var items = BuildDynamicLibraryAchievements(state, viewState);
            AttachAchievementCommands(items);

            _settings.ModernTheme.DynamicLibraryAchievements = items;
            ApplyDynamicListKeyBindings(LibraryAchievementListBinding);
            _settings.ModernTheme.DynamicLibraryAchievementsProviderOptions = DynamicThemeOptionFactory.CreateProviderOptions(
                EnumerateLibraryAchievementProviderKeys(state),
                viewState.ProviderKey,
                _settings.FilterDynamicLibraryAchievementsByProviderCommand);
            ApplyDynamicListOptionBindings(LibraryAchievementListBinding);
            if (updateOptions)
            {
                ApplyDynamicOptionBindings();
            }
        }

        private void ApplyDynamicGameSummaryBindings()
        {
            ApplyDynamicGameSummaryBindings(updateOptions: true);
        }

        private void ApplyDynamicGameSummaryBindings(bool updateOptions)
        {
            var state = _runtimeState.Library ?? new LibraryRuntimeState();
            var viewState = _runtimeState.GameSummaries;
            var items = BuildDynamicGameSummaries(state, viewState);
            AttachGameSummaryCommands(items);

            _settings.ModernTheme.DynamicGameSummaries = ProjectGameSummaries(items);
            ApplyDynamicListKeyBindings(GameSummaryListBinding);
            _settings.ModernTheme.DynamicGameSummariesProviderOptions = DynamicThemeOptionFactory.CreateProviderOptions(
                (state.AllGamesWithAchievements ?? Enumerable.Empty<GameAchievementSummary>()).Select(item => item?.ProviderKey),
                viewState.ProviderKey,
                _settings.FilterDynamicGameSummariesByProviderCommand);
            ApplyDynamicListOptionBindings(GameSummaryListBinding);
            if (updateOptions)
            {
                ApplyDynamicOptionBindings();
            }
        }

        private void ApplyDynamicFriendBindings()
        {
            ApplyDynamicFriendBindings(updateOptions: true);
        }

        private void ApplyDynamicFriendBindings(bool updateOptions)
        {
            var state = _runtimeState.Friends ?? FriendRuntimeState.Empty;
            ValidateDynamicFriendScope(state);

            var scope = _runtimeState.FriendScope;
            var friendItems = BuildDynamicFriendSummaries(state, _runtimeState.FriendSummaries);
            var gameItems = BuildDynamicFriendGameSummaries(state, _runtimeState.FriendGameSummaries);
            var achievementItems = BuildDynamicFriendAchievements(state, _runtimeState.FriendAchievements);
            AttachFriendSummaryCommands(friendItems);
            AttachFriendAchievementCommands(achievementItems);

            _settings.ModernTheme.DynamicFriendSummaries = new ObservableCollection<FriendSummaryItem>(friendItems);
            _settings.ModernTheme.DynamicFriendGameSummaries = ProjectFriendGameSummaries(gameItems);
            _settings.ModernTheme.DynamicFriendAchievements = new ObservableCollection<FriendAchievementDisplayItem>(achievementItems);

            _settings.ModernTheme.DynamicFriendScopeProviderKey = scope.ProviderKey;
            _settings.ModernTheme.DynamicFriendScopeProviderLabel = DynamicThemeLabels.GetProviderLabel(scope.ProviderKey);
            _settings.ModernTheme.DynamicFriendScopeUserKey = scope.UserKey;
            _settings.ModernTheme.DynamicFriendScopeUserLabel = GetFriendScopeUserLabel(state, scope.UserKey);
            _settings.ModernTheme.DynamicFriendScopeGameKey = scope.GameKey;
            _settings.ModernTheme.DynamicFriendScopeGameLabel = GetFriendScopeGameLabel(state, scope.GameKey);

            // Surface the scoped friend as a single bindable item so themes can show that friend's
            // whole-library rarity/trophy rollup next to their scoped achievements list. Null when no
            // individual friend is scoped (FindFriend returns null for the all-friends scope).
            var scopeSummary = state.Projection?.FindFriend(scope.UserKey);
            if (scopeSummary != null)
            {
                AttachFriendSummaryCommands(new[] { scopeSummary });
            }

            _settings.ModernTheme.DynamicFriendScopeSummary = scopeSummary;

            ApplyDynamicListKeyBindings(FriendSummariesListBinding);
            ApplyDynamicListKeyBindings(FriendGameSummariesListBinding);
            ApplyDynamicListKeyBindings(FriendAchievementsListBinding);

            _settings.ModernTheme.DynamicFriendScopeProviderOptions = CreateFriendProviderOptions(
                state,
                scope.ProviderKey,
                _settings.SetDynamicFriendScopeProviderCommand);
            _settings.ModernTheme.DynamicFriendScopeUserOptions = CreateFriendUserOptions(
                state,
                scope.ProviderKey,
                scope.UserKey,
                _settings.SetDynamicFriendScopeUserCommand);
            _settings.ModernTheme.DynamicFriendScopeGameOptions = CreateFriendGameOptions(
                state,
                scope.ProviderKey,
                scope.UserKey,
                scope.GameKey,
                _settings.SetDynamicFriendScopeGameCommand);

            ApplyDynamicListOptionBindings(FriendSummariesListBinding);
            ApplyDynamicListOptionBindings(FriendGameSummariesListBinding);
            ApplyDynamicListOptionBindings(FriendAchievementsListBinding);

            if (updateOptions)
            {
                ApplyDynamicOptionBindings();
            }
        }

        private static ObservableCollection<DynamicThemeOption> CreateFriendProviderOptions(
            FriendRuntimeState state,
            string selectedKey,
            ICommand applyCommand)
        {
            var providerKeys = (state?.Friends ?? Enumerable.Empty<FriendSummaryItem>())
                .SelectMany(GetFriendProviderScopeKeys)
                .Concat((state?.AggregateGames ?? Enumerable.Empty<FriendGameSummaryItem>()).Select(game => game?.ProviderKey));
            return DynamicThemeOptionFactory.CreateProviderOptions(providerKeys, selectedKey, applyCommand);
        }

        private static ObservableCollection<DynamicThemeOption> CreateFriendUserOptions(
            FriendRuntimeState state,
            string providerKey,
            string selectedKey,
            ICommand applyCommand)
        {
            var friends = (state?.Friends ?? Enumerable.Empty<FriendSummaryItem>())
                .Where(friend => FriendMatchesProvider(friend, providerKey))
                .GroupBy(FriendOverviewProjection.GetFriendScopeKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var friend = group.First();
                    return new DynamicThemeOption(
                        group.Key,
                        !string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.DisplayName : group.Key,
                        group.Sum(item => Math.Max(0, item?.UnlockedAchievementsCount ?? 0)));
                })
                .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return CreateScopedOptions(friends, selectedKey, applyCommand);
        }

        private static ObservableCollection<DynamicThemeOption> CreateFriendGameOptions(
            FriendRuntimeState state,
            string providerKey,
            string userKey,
            string selectedKey,
            ICommand applyCommand)
        {
            var options = GetFriendGameScopeCandidates(state, providerKey, userKey)
                .GroupBy(FriendOverviewProjection.GetGameScopeKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var game = group.First();
                    return new DynamicThemeOption(
                        group.Key,
                        !string.IsNullOrWhiteSpace(game.GameName) ? game.GameName : group.Key,
                        group.Sum(item => Math.Max(0, item?.UnlockedAchievements ?? 0)));
                })
                .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return CreateScopedOptions(options, selectedKey, applyCommand);
        }

        private static IEnumerable<FriendGameSummaryItem> GetFriendGameScopeCandidates(
            FriendRuntimeState state,
            string providerKey,
            string userKey)
        {
            var projection = state?.Projection;
            var selectedFriend = projection?.FindFriend(userKey);
            IEnumerable<FriendGameSummaryItem> games = selectedFriend != null
                ? projection.GetSelectedFriendGames(selectedFriend)
                : state?.AggregateGames ?? Enumerable.Empty<FriendGameSummaryItem>();

            return games.Where(game => game != null && ProviderMatches(game.ProviderKey, providerKey));
        }

        private static bool FriendMatchesProvider(FriendSummaryItem friend, string scopeProviderKey)
        {
            if (friend == null)
            {
                return false;
            }

            return FriendOverviewProjection.IsAllScope(scopeProviderKey) ||
                   GetFriendProviderScopeKeys(friend)
                       .Any(provider => string.Equals(provider, scopeProviderKey, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> GetFriendProviderScopeKeys(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(friend.ProviderKey) &&
                !string.Equals(friend.ProviderKey, FriendOverviewProjection.MergedProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                yield return friend.ProviderKey;
            }

            foreach (var provider in friend.MemberProviderKeys ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    yield return provider;
                }
            }
        }

        private static ObservableCollection<DynamicThemeOption> CreateScopedOptions(
            IList<DynamicThemeOption> scopedOptions,
            string selectedKey,
            ICommand applyCommand)
        {
            scopedOptions = scopedOptions ?? Array.Empty<DynamicThemeOption>();
            var options = new List<DynamicThemeOption>
            {
                new DynamicThemeOption(
                    DynamicThemeViewKeys.All,
                    DynamicThemeLabels.GetLabel(DynamicThemeViewKeys.All, DynamicThemeViewKeys.All),
                    scopedOptions.Sum(option => option?.Count ?? 0),
                    IsThemeOptionSelected(DynamicThemeViewKeys.All, selectedKey),
                    applyCommand)
            };

            options.AddRange(scopedOptions
                .Where(option => option != null)
                .Select(option => new DynamicThemeOption(
                    option.Key,
                    option.Label,
                    option.Count,
                    IsThemeOptionSelected(option.Key, selectedKey),
                    applyCommand)));

            if (!FriendOverviewProjection.IsAllScope(selectedKey) &&
                options.All(option => !string.Equals(option.Key, selectedKey, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new DynamicThemeOption(
                    selectedKey,
                    selectedKey,
                    isSelected: true,
                    applyCommand: applyCommand));
            }

            return new ObservableCollection<DynamicThemeOption>(options);
        }

        private static bool IsThemeOptionSelected(string key, string selectedKey)
        {
            return string.Equals(key ?? string.Empty, selectedKey ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetFriendScopeUserLabel(FriendRuntimeState state, string userKey)
        {
            if (FriendOverviewProjection.IsAllScope(userKey))
            {
                return DynamicThemeLabels.GetLabel(DynamicThemeViewKeys.All, DynamicThemeViewKeys.All);
            }

            var friend = state?.Projection?.FindFriend(userKey);
            return !string.IsNullOrWhiteSpace(friend?.DisplayName)
                ? friend.DisplayName
                : userKey ?? string.Empty;
        }

        private static string GetFriendScopeGameLabel(FriendRuntimeState state, string gameKey)
        {
            if (FriendOverviewProjection.IsAllScope(gameKey))
            {
                return DynamicThemeLabels.GetLabel(DynamicThemeViewKeys.All, DynamicThemeViewKeys.All);
            }

            var game = state?.Projection?.FindGame(gameKey);
            return !string.IsNullOrWhiteSpace(game?.GameName)
                ? game.GameName
                : gameKey ?? string.Empty;
        }

        private void ApplyDynamicOptionBindings()
        {
            var library = _runtimeState.Library ?? new LibraryRuntimeState();
            var selectedGameView = _runtimeState.SelectedGameAchievements;
            var selectedGameKey = selectedGameView.GameKey;
            if (string.IsNullOrWhiteSpace(selectedGameKey) && _settings.ModernTheme.SelectedGameId.HasValue)
            {
                selectedGameKey = _settings.ModernTheme.SelectedGameId.Value.ToString("D");
            }

            var selectedGameLabel = selectedGameView.GameLabel;
            if (string.IsNullOrWhiteSpace(selectedGameLabel) && Guid.TryParse(selectedGameKey, out var selectedGameId))
            {
                selectedGameLabel = ResolveGameLabel(selectedGameId);
            }

            _settings.ModernTheme.DynamicAchievementsGameKey = selectedGameKey ?? string.Empty;
            _settings.ModernTheme.DynamicAchievementsGameLabel = selectedGameLabel ?? string.Empty;
            _settings.ModernTheme.DynamicAchievementGameOptions = DynamicThemeOptionFactory.CreateGameOptions(
                library.AllGamesWithAchievements,
                selectedGameKey,
                _settings.SetDynamicAchievementsGameCommand);

            _settings.ModernTheme.DynamicAchievementStatusFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.AchievementStatusFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(selectedGameView.FilterKey, DynamicThemeOptionGroups.AchievementStatusGroup, DynamicThemeOptionGroups.AchievementFilterGroupMap),
                _settings.SetDynamicAchievementsStatusFilterCommand);
            _settings.ModernTheme.DynamicAchievementProgressFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.AchievementProgressFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(selectedGameView.FilterKey, DynamicThemeOptionGroups.AchievementProgressGroup, DynamicThemeOptionGroups.AchievementFilterGroupMap),
                _settings.SetDynamicAchievementsProgressFilterCommand);
            _settings.ModernTheme.DynamicAchievementRarityFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.AchievementRarityFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(selectedGameView.FilterKey, DynamicThemeOptionGroups.AchievementRarityGroup, DynamicThemeOptionGroups.AchievementFilterGroupMap),
                _settings.SetDynamicAchievementsRarityFilterCommand);
            _settings.ModernTheme.DynamicAchievementTrophyFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.AchievementTrophyFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(selectedGameView.FilterKey, DynamicThemeOptionGroups.AchievementTrophyGroup, DynamicThemeOptionGroups.AchievementFilterGroupMap),
                _settings.SetDynamicAchievementsTrophyFilterCommand);
            _settings.ModernTheme.DynamicAchievementCategoryTypeFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.AchievementCategoryTypeFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(selectedGameView.FilterKey, DynamicThemeOptionGroups.AchievementCategoryTypeGroup, DynamicThemeOptionGroups.AchievementFilterGroupMap),
                _settings.SetDynamicAchievementsCategoryTypeFilterCommand);
            _settings.ModernTheme.DynamicAchievementCustomizationFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.AchievementCustomizationFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(selectedGameView.FilterKey, DynamicThemeOptionGroups.AchievementCustomizationGroups, DynamicThemeOptionGroups.AchievementFilterGroupMap),
                _settings.SetDynamicAchievementsCustomizationFilterCommand);
            _settings.ModernTheme.DynamicGameProgressFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.GameProgressFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(_runtimeState.GameSummaries.FilterKey, DynamicThemeOptionGroups.GameProgressGroups, DynamicThemeOptionGroups.GameSummaryFilterGroupMap),
                _settings.SetDynamicGameSummariesProgressFilterCommand);
            _settings.ModernTheme.DynamicGameActivityFilterOptions = DynamicThemeOptionFactory.CreateOptions(
                DynamicThemeOptionGroups.GameActivityFilterKeys,
                DynamicThemeOptionGroups.GetGroupSelection(_runtimeState.GameSummaries.FilterKey, DynamicThemeOptionGroups.GameActivityGroups, DynamicThemeOptionGroups.GameSummaryFilterGroupMap),
                _settings.SetDynamicGameSummariesActivityFilterCommand);
        }

        private List<AchievementDetail> BuildDynamicSelectedGameAchievements(
            SelectedGameRuntimeState state,
            SelectedGameAchievementViewState viewState)
        {
            if (state == null || !state.HasAchievements)
            {
                return EmptyAchievementList;
            }

            IEnumerable<AchievementDetail> source = SelectSelectedGameAchievementSource(state, viewState);
            source = DynamicThemeFilterEvaluator.ApplyAchievementFilters(source, viewState.FilterKey);
            source = ApplyCategoryLabelFilter(source, viewState.CategoryLabelKey);
            return source.ToList();
        }

        private static IEnumerable<AchievementDetail> ApplyCategoryLabelFilter(
            IEnumerable<AchievementDetail> source,
            string categoryLabelKey)
        {
            if (string.IsNullOrWhiteSpace(categoryLabelKey) ||
                string.Equals(categoryLabelKey, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            return source.Where(item => string.Equals(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item?.Category),
                categoryLabelKey,
                StringComparison.OrdinalIgnoreCase));
        }

        private bool TryNormalizeSelectedGameCategoryLabel(object parameter, out string key)
        {
            var raw = parameter?.ToString();
            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                key = DynamicThemeViewKeys.All;
                return true;
            }

            // Accept only labels present in the current game so a bad theme binding cannot
            // pin the list to a permanently empty scope.
            var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(raw);
            var achievements = _runtimeState.SelectedGame?.AllAchievements ?? EmptyAchievementList;
            key = achievements
                .Select(item => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item?.Category))
                .FirstOrDefault(label => string.Equals(label, normalized, StringComparison.OrdinalIgnoreCase));
            return key != null;
        }

        private void ValidateSelectedGameCategoryScope(SelectedGameRuntimeState state)
        {
            var viewState = _runtimeState.SelectedGameAchievements;
            var key = viewState.CategoryLabelKey;
            if (string.IsNullOrWhiteSpace(key) ||
                string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var exists = (state?.AllAchievements ?? EmptyAchievementList).Any(item => string.Equals(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item?.Category),
                key,
                StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                viewState.ResetCategoryScope();
            }
        }

        private List<AchievementDetail> BuildDynamicLibraryAchievements(
            LibraryRuntimeState state,
            LibraryAchievementViewState viewState)
        {
            if (state == null || !state.HasData)
            {
                return EmptyAchievementList;
            }

            IEnumerable<AchievementDetail> source = SelectLibraryAchievementSource(state, viewState);
            source = ApplyProviderFilter(source, viewState.ProviderKey);
            source = ApplyGameFilter(source, viewState.GameKey, item => item.Game?.Id ?? Guid.Empty);
            source = DynamicThemeFilterEvaluator.ApplyAchievementFilters(source, viewState.FilterKey);
            return source.ToList();
        }

        private List<GameAchievementSummary> BuildDynamicGameSummaries(
            LibraryRuntimeState state,
            GameSummaryViewState viewState)
        {
            if (state == null || !state.HasData)
            {
                return new List<GameAchievementSummary>();
            }

            IEnumerable<GameAchievementSummary> source = state.AllGamesWithAchievements ?? Enumerable.Empty<GameAchievementSummary>();
            source = ApplyProviderFilter(source, viewState.ProviderKey);
            source = ApplyGameFilter(source, viewState.GameKey, item => item.GameId);
            source = DynamicThemeFilterEvaluator.ApplyGameSummaryFilters(source, viewState.FilterKey);
            return GameSummarySortTable.Sort(source, viewState).ToList();
        }

        private List<FriendSummaryItem> BuildDynamicFriendSummaries(
            FriendRuntimeState state,
            FriendSummaryViewState viewState)
        {
            var projection = state?.Projection;
            if (projection == null || !state.HasData)
            {
                return new List<FriendSummaryItem>();
            }

            var scope = _runtimeState.FriendScope;
            var selectedGame = projection.FindGame(scope.GameKey);
            IEnumerable<FriendSummaryItem> source = state.Friends ?? Enumerable.Empty<FriendSummaryItem>();
            source = source.Where(friend => FriendMatchesProvider(friend, scope.ProviderKey));
            if (selectedGame != null)
            {
                source = source.Where(friend => projection.HasFriendGamePairData(friend, selectedGame));
            }

            source = ApplyDynamicFilterPredicates(source, viewState.FilterKey, FriendSummaryFilterPredicates);
            return FriendSummarySortTable.Sort(source, viewState).ToList();
        }

        private List<FriendGameSummaryItem> BuildDynamicFriendGameSummaries(
            FriendRuntimeState state,
            FriendGameSummaryViewState viewState)
        {
            var projection = state?.Projection;
            if (projection == null || !state.HasData)
            {
                return new List<FriendGameSummaryItem>();
            }

            var scope = _runtimeState.FriendScope;
            var selectedFriend = projection.FindFriend(scope.UserKey);
            var selectedGame = projection.FindGame(scope.GameKey);
            IEnumerable<FriendGameSummaryItem> source = selectedFriend != null
                ? projection.GetSelectedFriendGames(selectedFriend)
                : state.AggregateGames ?? Enumerable.Empty<FriendGameSummaryItem>();

            source = ApplyProviderFilter(source, scope.ProviderKey, item => item.ProviderKey);

            if (selectedGame != null)
            {
                source = source.Where(game => FriendOverviewProjection.IsSameGame(game, selectedGame));
            }

            source = ApplyDynamicFilterPredicates(source, viewState.FilterKey, FriendGameSummaryFilterPredicates);
            return FriendGameSummarySortTable.Sort(source, viewState).ToList();
        }

        private List<FriendAchievementDisplayItem> BuildDynamicFriendAchievements(
            FriendRuntimeState state,
            FriendAchievementViewState viewState)
        {
            var projection = state?.Projection;
            if (projection == null || !state.HasData)
            {
                return new List<FriendAchievementDisplayItem>();
            }

            var scope = _runtimeState.FriendScope;
            var selectedFriend = projection.FindFriend(scope.UserKey);
            var selectedGame = projection.FindGame(scope.GameKey);
            var hasScopedSelection =
                !FriendOverviewProjection.IsAllScope(scope.ProviderKey) ||
                selectedFriend != null ||
                selectedGame != null;

            // Same selection logic as the friends overview: only the single friend + single
            // game pair shows locked rows (loaded on demand); every other scoped view stays on
            // the snapshot's unlocked rows, and no scope keeps the recent-unlocks feed.
            IEnumerable<FriendAchievementDisplayItem> source =
                selectedFriend != null && selectedGame != null
                    ? ResolveDynamicFriendPairSource(state, selectedGame)
                    : hasScopedSelection
                        ? state.AllAchievements ?? Enumerable.Empty<FriendAchievementDisplayItem>()
                        : state.RecentUnlocks ?? Enumerable.Empty<FriendAchievementDisplayItem>();

            source = ApplyProviderFilter(source, scope.ProviderKey, item => item.ProviderKey);
            if (selectedFriend != null)
            {
                source = source.Where(achievement => FriendOverviewProjection.IsSameFriend(achievement, selectedFriend));
            }

            if (selectedGame != null)
            {
                source = source.Where(achievement => FriendOverviewProjection.IsSameGame(achievement, selectedGame));
            }

            source = ApplyDynamicFilterPredicates(source, viewState.FilterKey, FriendAchievementFilterPredicates);
            return FriendAchievementSortTable.Sort(source, viewState).ToList();
        }

        // Returns the pair scope's achievement source: the on-demand full rows when loaded for
        // the selected game (a loaded-but-empty result falls back instead of blanking the
        // lists), otherwise the snapshot's unlocked rows while the fetch runs.
        private IEnumerable<FriendAchievementDisplayItem> ResolveDynamicFriendPairSource(
            FriendRuntimeState state,
            FriendGameSummaryItem selectedGame)
        {
            IEnumerable<FriendAchievementDisplayItem> fallback =
                state.AllAchievements ?? Enumerable.Empty<FriendAchievementDisplayItem>();
            var key = FriendOverviewProjection.GetGameScopeKey(selectedGame);
            if (FriendOverviewProjection.IsAllScope(key))
            {
                return fallback;
            }

            if (_dynamicFriendPairRows != null &&
                string.Equals(_dynamicFriendPairKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return _dynamicFriendPairRows.Count > 0 ? _dynamicFriendPairRows : fallback;
            }

            BeginDynamicFriendPairFetch(key, selectedGame);
            return fallback;
        }

        private void BeginDynamicFriendPairFetch(string key, FriendGameSummaryItem game)
        {
            if (_friendCache == null ||
                game == null ||
                string.Equals(_dynamicFriendPairKeyInFlight, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _dynamicFriendPairKeyInFlight = key;
            var version = Interlocked.Increment(ref _dynamicFriendPairFetchVersion);
            var gameScope = FriendCacheChange.ForGameDefinition(
                game.ProviderKey,
                game.AppId,
                game.ProviderGameKey);
            _ = FetchDynamicFriendPairRowsAsync(version, key, gameScope);
        }

        private async Task FetchDynamicFriendPairRowsAsync(int version, string key, FriendCacheChange gameScope)
        {
            List<FriendAchievementDisplayItem> rows = null;
            try
            {
                rows = await Task
                    .Run(() => _friendCache.LoadFriendGameAchievementData(gameScope)?.AllAchievements)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load dynamic friend pair achievement rows.");
            }

            void Apply()
            {
                if (version != Volatile.Read(ref _dynamicFriendPairFetchVersion))
                {
                    return;
                }

                _dynamicFriendPairKeyInFlight = null;
                _dynamicFriendPairKey = key;
                _dynamicFriendPairRows = rows ?? new List<FriendAchievementDisplayItem>();
                ApplyDynamicFriendBindings();
                NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
            }

            var uiDispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (uiDispatcher == null)
            {
                Apply();
            }
            else
            {
                uiDispatcher.InvokeIfNeeded(Apply, DispatcherPriority.Background);
            }
        }

        // Drops the on-demand pair rows and discards any in-flight fetch result; the next
        // pair-scoped binding build re-fetches against the fresh cache state.
        private void ResetDynamicFriendPairRows()
        {
            Interlocked.Increment(ref _dynamicFriendPairFetchVersion);
            _dynamicFriendPairRows = null;
            _dynamicFriendPairKey = null;
            _dynamicFriendPairKeyInFlight = null;
        }

        /// <summary>
        /// Apply the composable filter keys of a filter expression to a dynamic collection using
        /// a per-item-type predicate table. Keys without a predicate are ignored.
        /// </summary>
        private static IEnumerable<T> ApplyDynamicFilterPredicates<T>(
            IEnumerable<T> source,
            string filterKey,
            IReadOnlyDictionary<string, Func<T, bool>> predicates)
        {
            var items = source ?? Enumerable.Empty<T>();
            foreach (var key in DynamicThemeFilterExpression.Enumerate(filterKey))
            {
                if (key != null && predicates.TryGetValue(key, out var predicate))
                {
                    items = items.Where(predicate);
                }
            }

            return items;
        }

        private static readonly IReadOnlyDictionary<string, Func<FriendSummaryItem, bool>> FriendSummaryFilterPredicates =
            new Dictionary<string, Func<FriendSummaryItem, bool>>(StringComparer.Ordinal)
            {
                [DynamicThemeViewKeys.HasLastUnlock] = item => item?.LastUnlockUtc.HasValue == true,
                [DynamicThemeViewKeys.NoLastUnlock] = item => item != null && !item.LastUnlockUtc.HasValue,
            };

        private static readonly IReadOnlyDictionary<string, Func<FriendGameSummaryItem, bool>> FriendGameSummaryFilterPredicates =
            new Dictionary<string, Func<FriendGameSummaryItem, bool>>(StringComparer.Ordinal)
            {
                [DynamicThemeViewKeys.Completed] = item => item?.IsCompleted == true,
                [DynamicThemeViewKeys.Incomplete] = item => item != null && !item.IsCompleted,
                [DynamicThemeViewKeys.Started] = item => item != null && item.UnlockedAchievements > 0,
                [DynamicThemeViewKeys.NotStarted] = item => item != null && item.UnlockedAchievements <= 0,
                [DynamicThemeViewKeys.Played] = item => item?.LastPlayed.HasValue == true || item?.PlaytimeSeconds > 0,
                [DynamicThemeViewKeys.Unplayed] = item => item != null && !item.LastPlayed.HasValue && item.PlaytimeSeconds == 0,
                [DynamicThemeViewKeys.HasLastUnlock] = item => item?.LastUnlockUtc.HasValue == true,
                [DynamicThemeViewKeys.NoLastUnlock] = item => item != null && !item.LastUnlockUtc.HasValue,
            };

        private static readonly IReadOnlyDictionary<string, Func<FriendAchievementDisplayItem, bool>> FriendAchievementFilterPredicates =
            CreateFriendAchievementFilterPredicates();

        private static IReadOnlyDictionary<string, Func<FriendAchievementDisplayItem, bool>> CreateFriendAchievementFilterPredicates()
        {
            Func<FriendAchievementDisplayItem, bool> Rarity(string key) =>
                item => item != null && string.Equals(item.Rarity.ToString(), key, StringComparison.OrdinalIgnoreCase);
            Func<FriendAchievementDisplayItem, bool> Trophy(string key) =>
                item => string.Equals(item?.TrophyType, key, StringComparison.OrdinalIgnoreCase);
            // CategoryType is multi-valued ("Base|DLC"); ParseValues canonicalizes aliases.
            Func<FriendAchievementDisplayItem, bool> CategoryType(string key) =>
                item => item != null && AchievementCategoryTypeHelper.ParseValues(item.CategoryType)
                    .Contains(key, StringComparer.OrdinalIgnoreCase);

            return new Dictionary<string, Func<FriendAchievementDisplayItem, bool>>(StringComparer.Ordinal)
            {
                [DynamicThemeViewKeys.Unlocked] = item => item?.Unlocked == true,
                [DynamicThemeViewKeys.Locked] = item => item != null && !item.Unlocked,
                [DynamicThemeViewKeys.InProgress] = item => item != null && !item.Unlocked && item.HasProgress,
                [DynamicThemeViewKeys.NoProgress] = item => item != null && !item.HasProgress,
                [DynamicThemeViewKeys.HasNotes] = item => !string.IsNullOrWhiteSpace(item?.AchievementNote),
                [DynamicThemeViewKeys.NoNotes] = item => string.IsNullOrWhiteSpace(item?.AchievementNote),
                [DynamicThemeViewKeys.Capstone] = item => item?.IsCapstone == true,
                [DynamicThemeViewKeys.Common] = Rarity(DynamicThemeViewKeys.Common),
                [DynamicThemeViewKeys.Uncommon] = Rarity(DynamicThemeViewKeys.Uncommon),
                [DynamicThemeViewKeys.Rare] = Rarity(DynamicThemeViewKeys.Rare),
                [DynamicThemeViewKeys.UltraRare] = Rarity(DynamicThemeViewKeys.UltraRare),
                [DynamicThemeViewKeys.Platinum] = Trophy(DynamicThemeViewKeys.Platinum),
                [DynamicThemeViewKeys.Gold] = Trophy(DynamicThemeViewKeys.Gold),
                [DynamicThemeViewKeys.Silver] = Trophy(DynamicThemeViewKeys.Silver),
                [DynamicThemeViewKeys.Bronze] = Trophy(DynamicThemeViewKeys.Bronze),
                ["Base"] = CategoryType("Base"),
                ["DLC"] = CategoryType("DLC"),
                ["Singleplayer"] = CategoryType("Singleplayer"),
                ["Multiplayer"] = CategoryType("Multiplayer"),
                ["Collectable"] = CategoryType("Collectable"),
                ["Missable"] = CategoryType("Missable"),
                ["Difficulty"] = CategoryType("Difficulty"),
                ["Stackable"] = CategoryType("Stackable"),
                [AchievementCategoryTypeHelper.SoftcoreCategoryType] = CategoryType(AchievementCategoryTypeHelper.SoftcoreCategoryType),
                [AchievementCategoryTypeHelper.HardcoreCategoryType] = CategoryType(AchievementCategoryTypeHelper.HardcoreCategoryType),
            };
        }

        private delegate IOrderedEnumerable<T> DynamicSortPrimary<T>(IEnumerable<T> source, bool descending, StringComparer nameComparer);

        private delegate IOrderedEnumerable<T> DynamicSortTieBreak<T>(IOrderedEnumerable<T> source, StringComparer nameComparer);

        private sealed class DynamicSortSpec<T>
        {
            public DynamicSortSpec(DynamicSortPrimary<T> primary, DynamicSortTieBreak<T>[] tieBreaks)
            {
                Primary = primary;
                TieBreaks = tieBreaks;
            }

            public DynamicSortPrimary<T> Primary { get; }

            public DynamicSortTieBreak<T>[] TieBreaks { get; }
        }

        /// <summary>
        /// Declarative sort-key table for one dynamic collection. The user-selected sort direction
        /// applies to the primary key only; tie-breaks always run in their declared fixed direction.
        /// Name-typed primary keys use the culture-aware ignore-case comparer resolved per sort call.
        /// </summary>
        private sealed class DynamicSortTable<T>
        {
            private readonly Dictionary<string, DynamicSortSpec<T>> _specs =
                new Dictionary<string, DynamicSortSpec<T>>(StringComparer.Ordinal);
            private DynamicSortSpec<T> _default;

            public void Add(string sortKey, DynamicSortSpec<T> spec)
            {
                _specs[sortKey] = spec;
            }

            public void SetDefault(DynamicSortSpec<T> spec)
            {
                _default = spec;
            }

            public DynamicSortSpec<T> ByName(Func<T, string> selector, params DynamicSortTieBreak<T>[] tieBreaks)
            {
                return new DynamicSortSpec<T>(
                    (source, descending, nameComparer) => descending
                        ? source.OrderByDescending(selector, nameComparer)
                        : source.OrderBy(selector, nameComparer),
                    tieBreaks);
            }

            public DynamicSortSpec<T> ByValue<TKey>(Func<T, TKey> selector, params DynamicSortTieBreak<T>[] tieBreaks)
            {
                return new DynamicSortSpec<T>(
                    (source, descending, nameComparer) => descending
                        ? source.OrderByDescending(selector)
                        : source.OrderBy(selector),
                    tieBreaks);
            }

            public DynamicSortTieBreak<T> ThenByName(Func<T, string> selector)
            {
                return (source, nameComparer) => source.ThenBy(selector, nameComparer);
            }

            public DynamicSortTieBreak<T> ThenByValueDescending<TKey>(Func<T, TKey> selector)
            {
                return (source, nameComparer) => source.ThenByDescending(selector);
            }

            public IEnumerable<T> Sort(IEnumerable<T> source, DynamicThemeListViewState viewState)
            {
                source ??= Enumerable.Empty<T>();
                var descending = !string.Equals(viewState?.SortDirectionKey, DynamicThemeViewKeys.Ascending, StringComparison.Ordinal);
                var nameComparer = StringComparer.CurrentCultureIgnoreCase;
                var sortKey = viewState?.SortKey;
                var spec = sortKey != null && _specs.TryGetValue(sortKey, out var match) ? match : _default;
                var ordered = spec.Primary(source, descending, nameComparer);
                foreach (var tieBreak in spec.TieBreaks ?? Array.Empty<DynamicSortTieBreak<T>>())
                {
                    ordered = tieBreak(ordered, nameComparer);
                }

                return ordered;
            }
        }

        private static readonly DynamicSortTable<FriendSummaryItem> FriendSummarySortTable = CreateFriendSummarySortTable();

        private static DynamicSortTable<FriendSummaryItem> CreateFriendSummarySortTable()
        {
            var table = new DynamicSortTable<FriendSummaryItem>();
            var thenByDisplayName = table.ThenByName(item => item?.DisplayName ?? string.Empty);
            var bySharedGamesCount = table.ByValue(item => item?.SharedGamesCount ?? 0, thenByDisplayName);
            table.Add(DynamicThemeViewKeys.Name, table.ByName(item => item?.DisplayName ?? string.Empty));
            table.Add(DynamicThemeViewKeys.Provider, table.ByName(item => item?.ProviderDisplayName ?? item?.ProviderKey ?? string.Empty, thenByDisplayName));
            table.Add(DynamicThemeViewKeys.UnlockedCount, table.ByValue(item => item?.UnlockedAchievementsCount ?? 0, thenByDisplayName));
            table.Add(DynamicThemeViewKeys.SharedGamesCount, bySharedGamesCount);
            table.Add(DynamicThemeViewKeys.AchievementCount, bySharedGamesCount);
            table.SetDefault(table.ByValue(item => item?.LastUnlockUtc ?? DateTime.MinValue, thenByDisplayName));
            return table;
        }

        private static readonly DynamicSortTable<FriendGameSummaryItem> FriendGameSummarySortTable = CreateFriendGameSummarySortTable();

        private static DynamicSortTable<FriendGameSummaryItem> CreateFriendGameSummarySortTable()
        {
            var table = new DynamicSortTable<FriendGameSummaryItem>();
            var thenByGameName = table.ThenByName(item => GetFriendGameSortingName(item));
            table.Add(DynamicThemeViewKeys.Name, table.ByName(GetFriendGameSortingName));
            table.Add(DynamicThemeViewKeys.Provider, table.ByName(item => item?.Provider ?? item?.ProviderKey ?? string.Empty, thenByGameName));
            table.Add(DynamicThemeViewKeys.Progress, table.ByValue(item => item?.Progression ?? 0, thenByGameName));
            table.Add(DynamicThemeViewKeys.LastPlayed, table.ByValue(
                item => item?.LastPlayed ?? DateTime.MinValue,
                table.ThenByValueDescending(item => item?.LastUnlockUtc ?? DateTime.MinValue)));
            table.Add(DynamicThemeViewKeys.UnlockedCount, table.ByValue(item => item?.UnlockedAchievements ?? 0, thenByGameName));
            table.Add(DynamicThemeViewKeys.AchievementCount, table.ByValue(item => item?.TotalAchievements ?? 0, thenByGameName));
            table.SetDefault(table.ByValue(item => item?.LastUnlockUtc ?? DateTime.MinValue, thenByGameName));
            return table;
        }

        private static string GetFriendGameSortingName(FriendGameSummaryItem item)
        {
            return string.IsNullOrWhiteSpace(item?.SortingName)
                ? item?.GameName ?? string.Empty
                : item.SortingName;
        }

        private static readonly DynamicSortTable<FriendAchievementDisplayItem> FriendAchievementSortTable = CreateFriendAchievementSortTable();

        private static DynamicSortTable<FriendAchievementDisplayItem> CreateFriendAchievementSortTable()
        {
            var table = new DynamicSortTable<FriendAchievementDisplayItem>();
            var tieBreaks = new[]
            {
                table.ThenByName(item => item?.FriendName ?? string.Empty),
                table.ThenByName(item => item?.GameName ?? string.Empty),
                table.ThenByName(item => item?.DisplayName ?? string.Empty),
            };
            table.Add(DynamicThemeViewKeys.Name, table.ByValue<object>(item => item?.DisplayName ?? string.Empty, tieBreaks));
            table.Add(DynamicThemeViewKeys.Game, table.ByValue<object>(item => item?.GameName ?? string.Empty, tieBreaks));
            table.Add(DynamicThemeViewKeys.Provider, table.ByValue<object>(item => item?.ProviderKey ?? string.Empty, tieBreaks));
            table.Add(DynamicThemeViewKeys.Rarity, table.ByValue<object>(item => item?.RaritySortValue ?? double.MaxValue, tieBreaks));
            table.Add(DynamicThemeViewKeys.Status, table.ByValue<object>(item => item?.Unlocked == true ? 1 : 0, tieBreaks));
            table.Add(DynamicThemeViewKeys.Progress, table.ByValue<object>(item => item?.ProgressPercent ?? 0d, tieBreaks));
            table.Add(DynamicThemeViewKeys.Points, table.ByValue<object>(item => item?.Points ?? 0, tieBreaks));
            table.Add(DynamicThemeViewKeys.CollectionScore, table.ByValue<object>(item => item?.CollectionScore ?? 0, tieBreaks));
            table.Add(DynamicThemeViewKeys.PrestigeScore, table.ByValue<object>(item => item?.PrestigeScore ?? 0, tieBreaks));
            table.Add(DynamicThemeViewKeys.TrophyType, table.ByValue<object>(item => item?.TrophyType ?? string.Empty, tieBreaks));
            table.Add(DynamicThemeViewKeys.CategoryType, table.ByValue<object>(item => item?.CategoryType ?? string.Empty, tieBreaks));
            table.Add(DynamicThemeViewKeys.CategoryLabel, table.ByValue<object>(item => item?.CategoryLabel ?? string.Empty, tieBreaks));
            table.Add(DynamicThemeViewKeys.Notes, table.ByValue<object>(item => item?.AchievementNote ?? string.Empty, tieBreaks));
            table.SetDefault(table.ByValue<object>(item => item?.UnlockTimeUtc ?? DateTime.MinValue, tieBreaks));
            return table;
        }

        private static readonly DynamicSortTable<GameAchievementSummary> GameSummarySortTable = CreateGameSummarySortTable();

        private static DynamicSortTable<GameAchievementSummary> CreateGameSummarySortTable()
        {
            var table = new DynamicSortTable<GameAchievementSummary>();
            var thenByLastUnlockDescending = table.ThenByValueDescending(item => item?.LastUnlockDate ?? DateTime.MinValue);
            var thenByProgressDescending = table.ThenByValueDescending(item => item?.Progress ?? 0);
            var thenByName = table.ThenByName(GetGameSummarySortingName);
            table.Add(DynamicThemeViewKeys.Name, table.ByName(GetGameSummarySortingName, thenByLastUnlockDescending));
            table.Add(DynamicThemeViewKeys.Provider, table.ByName(item => item?.ProviderName ?? item?.ProviderKey ?? string.Empty, thenByName));
            table.Add(DynamicThemeViewKeys.Progress, table.ByValue(item => item?.Progress ?? 0, thenByLastUnlockDescending, thenByName));
            table.Add(DynamicThemeViewKeys.LastPlayed, table.ByValue(item => item?.LastPlayed ?? DateTime.MinValue, thenByLastUnlockDescending, thenByName));
            table.Add(DynamicThemeViewKeys.UnlockedCount, table.ByValue(item => item?.UnlockedCount ?? 0, thenByProgressDescending, thenByLastUnlockDescending, thenByName));
            table.Add(DynamicThemeViewKeys.AchievementCount, table.ByValue(item => item?.AchievementCount ?? 0, thenByProgressDescending, thenByLastUnlockDescending, thenByName));
            table.SetDefault(table.ByValue(item => item?.LastUnlockDate ?? DateTime.MinValue, thenByProgressDescending, thenByName));
            return table;
        }

        private static string GetGameSummarySortingName(GameAchievementSummary item)
        {
            return string.IsNullOrWhiteSpace(item?.SortingName)
                ? item?.Name ?? string.Empty
                : item.SortingName;
        }

        private static IEnumerable<AchievementDetail> SelectSelectedGameAchievementSource(
            SelectedGameRuntimeState state,
            SelectedGameAchievementViewState viewState)
        {
            return AchievementSortHelper.ResolveSelectedGameAchievements(
                state,
                viewState?.SortKey,
                viewState?.SortDirectionKey);
        }

        private static IEnumerable<AchievementDetail> SelectLibraryAchievementSource(
            LibraryRuntimeState state,
            LibraryAchievementViewState viewState)
        {
            return AchievementSortHelper.ResolveLibraryAchievements(
                state,
                viewState?.SortKey,
                viewState?.SortDirectionKey);
        }

        private static IEnumerable<TItem> ApplyGameFilter<TItem>(
            IEnumerable<TItem> source,
            string gameKey,
            Func<TItem, Guid> gameIdSelector)
            where TItem : class
        {
            var items = source ?? Enumerable.Empty<TItem>();
            if (IsAllScopeKey(gameKey) || !Guid.TryParse(gameKey, out var gameId) || gameId == Guid.Empty)
            {
                return items;
            }

            return items.Where(item => item != null && gameIdSelector(item) == gameId);
        }

        private static IEnumerable<AchievementDetail> ApplyProviderFilter(
            IEnumerable<AchievementDetail> source,
            string providerKey)
        {
            return ApplyProviderFilter(source, providerKey, item => item.ProviderKey);
        }

        private static IEnumerable<GameAchievementSummary> ApplyProviderFilter(
            IEnumerable<GameAchievementSummary> source,
            string providerKey)
        {
            return ApplyProviderFilter(source, providerKey, item => item.ProviderKey);
        }

        private static IEnumerable<TItem> ApplyProviderFilter<TItem>(
            IEnumerable<TItem> source,
            string providerKey,
            Func<TItem, string> providerKeySelector)
            where TItem : class
        {
            var items = source ?? Enumerable.Empty<TItem>();
            if (string.IsNullOrWhiteSpace(providerKey) ||
                string.Equals(providerKey, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return items;
            }

            return items.Where(item =>
                item != null &&
                string.Equals(providerKeySelector(item), providerKey, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsAllScopeKey(string key)
        {
            return string.IsNullOrWhiteSpace(key) ||
                   string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ProviderScopeMatches(string scopeProviderKey, string providerKey)
        {
            return IsAllScopeKey(scopeProviderKey) ||
                   (!string.IsNullOrWhiteSpace(providerKey) &&
                    string.Equals(scopeProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveLibraryProviderKeyForGame(LibraryRuntimeState state, Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return null;
            }

            var providerKey = (state?.AllAchievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(item => item?.Game?.Id == gameId)
                .Select(item => item?.ProviderKey)
                .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));
            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                return providerKey;
            }

            return ResolveGameSummaryProviderKeyForGame(state, gameId);
        }

        private static string ResolveGameSummaryProviderKeyForGame(LibraryRuntimeState state, Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return null;
            }

            return (state?.AllGamesWithAchievements ?? Enumerable.Empty<GameAchievementSummary>())
                .Where(item => item != null && item.GameId == gameId)
                .Select(item => item?.ProviderKey)
                .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));
        }

        private FriendGameSummaryItem FindFriendGameScopeCandidate(FriendRuntimeState state, Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return null;
            }

            var scope = _runtimeState.FriendScope;
            var candidates = (state?.AggregateGames ?? Enumerable.Empty<FriendGameSummaryItem>())
                .Where(game => game?.PlayniteGameId == gameId)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            if (!FriendOverviewProjection.IsAllScope(scope.ProviderKey))
            {
                var scopedCandidate = candidates.FirstOrDefault(game =>
                    string.Equals(game.ProviderKey, scope.ProviderKey, StringComparison.OrdinalIgnoreCase));
                if (scopedCandidate != null)
                {
                    return scopedCandidate;
                }
            }

            return candidates
                .OrderByDescending(game => game?.FriendUnlockedAchievementsCount ?? 0)
                .ThenBy(game => game?.GameName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
        }

        private static IEnumerable<string> EnumerateLibraryAchievementProviderKeys(LibraryRuntimeState state)
        {
            var achievements = GetAvailableLibraryAchievements(state);
            if (achievements.Count > 0)
            {
                return achievements.Select(item => item?.ProviderKey);
            }

            return (state?.AllGamesWithAchievements ?? Enumerable.Empty<GameAchievementSummary>())
                .Select(item => item?.ProviderKey);
        }

        private static List<AchievementDetail> GetAvailableLibraryAchievements(LibraryRuntimeState state)
        {
            if (state == null)
            {
                return new List<AchievementDetail>();
            }

            return state.AllAchievements ?? new List<AchievementDetail>();
        }

        private bool TryNormalizeProviderKey(object parameter, out string providerKey)
        {
            var raw = ExtractProviderKeyParameter(parameter);
            if (string.IsNullOrWhiteSpace(raw))
            {
                providerKey = null;
                return false;
            }

            if (string.Equals(raw, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                providerKey = DynamicThemeViewKeys.All;
                return true;
            }

            providerKey = FindCanonicalProviderKey(raw) ?? raw;
            return true;
        }

        private IEnumerable<string> EnumerateKnownProviderKeys()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> YieldKnown(IEnumerable<string> keys)
            {
                foreach (var key in keys ?? Enumerable.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    {
                        yield return key;
                    }
                }
            }

            foreach (var key in YieldKnown(
                (_runtimeState?.SelectedGame?.AllAchievements ?? Enumerable.Empty<AchievementDetail>())
                .Select(item => item?.ProviderKey)))
            {
                yield return key;
            }

            foreach (var key in YieldKnown(
                (_runtimeState?.Library?.AllAchievements ?? Enumerable.Empty<AchievementDetail>())
                .Select(item => item?.ProviderKey)))
            {
                yield return key;
            }

            foreach (var key in YieldKnown(
                (_runtimeState?.Library?.AllGamesWithAchievements ?? Enumerable.Empty<GameAchievementSummary>())
                .Select(item => item?.ProviderKey)))
            {
                yield return key;
            }
        }

        private void LogInvalidCommandParameter(string commandName, object parameter)
        {
            _logger?.Debug($"Ignored invalid parameter for {commandName}: '{parameter ?? "null"}'.");
        }

        private void NotifySettingProperties(params string[] propertyNames)
        {
            if (propertyNames == null || propertyNames.Length == 0)
            {
                return;
            }

            for (int i = 0; i < propertyNames.Length; i++)
            {
                var name = propertyNames[i];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _settings.OnPropertyChanged(name);
                }
            }
        }
    }
}






