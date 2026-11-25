using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.Services.UI;
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
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly IFriendCacheManager _friendCache;
        private readonly Captures.CaptureLibraryService _captureLibrary;
        private readonly Func<RefreshRequest, string, bool, Action<bool>, Task> _runRefreshWithGlobalProgressAsync;
        private readonly Action<Guid> _openManageAchievementsView;
        // Injected rather than resolved here so this service stays free of the overrides service
        // and the custom-data store, which the theme test build does not compile.
        private readonly Action<AchievementMarkerTarget> _toggleAchievementCapstone;
        private readonly Action<AchievementMarkerTarget> _toggleAchievementGoal;
        private readonly Func<AchievementHotkeyTargetResolution> _resolveRunningGameTarget;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly FullscreenWindowService _windowService;
        private readonly ThemeRuntimeState _runtimeState = new ThemeRuntimeState();
        private GameSummaryItemBuilder _gameSummaryItemBuilder;

        private GameSummaryItemBuilder GameSummaryItemBuilder =>
            _gameSummaryItemBuilder ??
            (_gameSummaryItemBuilder = new GameSummaryItemBuilder(_api, _logger));

        private readonly object _refreshLock = new object();

        // The latest library state read; an older one is dropped unread or unapplied.
        private int _libraryReadVersion;

        // The latest Playnite game change; the bound lists reread after the last of a burst.
        private int _libraryGamesChangeVersion;
        private static readonly TimeSpan LibraryRefreshDelay = TimeSpan.FromMilliseconds(500);

        // A window that edits one game's custom data in bulk -- the Manage Achievements editor --
        // raises a change per edit, and each one would rebuild the whole library's theme lists:
        // roughly forty fresh collections of cloned summaries, per edit, none of it visible while
        // that window is up. Counted rather than a flag, since one window per game can be open.
        private int _librarySuspensionCount;
        private bool _librarySuspensionHasPendingRefresh;
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
        private volatile bool _hasFriendThemeConsumers;

        // The friend lists and the rows of the friend scope, read once a theme binds friend data,
        // and the library-wide achievement lists, each read once a theme binds it.
        private IReadScheduler _readScheduler;
        private readonly FriendListsSource _friendLists;
        private PagedRows<FriendAchievementDisplayItem> _friendRows;
        private readonly Dictionary<string, LiveQuery<List<AchievementDetail>>> _libraryLists =
            new Dictionary<string, LiveQuery<List<AchievementDetail>>>(StringComparer.Ordinal);

        private bool _fullscreenInitialized;
        private bool _hasLoadedLibraryState;

        // Runs once the library state is applied; set by a command that needs it before it was read.
        private Action _afterLibraryState;
        private readonly Dictionary<Guid, RelayCommand> _openViewAchievementsCommands =
            new Dictionary<Guid, RelayCommand>();
        private readonly Dictionary<Guid, RelayCommand> _openManageAchievementsCommands =
            new Dictionary<Guid, RelayCommand>();

        internal ThemeIntegrationService(
            IPlayniteAPI api,
            RefreshRuntime refreshRuntime,
            AchievementDataService achievementDataService,
            RefreshEntryPoint refreshEntryPoint,
            PlayniteAchievementsSettings settings,
            FullscreenWindowService windowService,
            ILogger logger,
            Func<RefreshRequest, string, bool, Action<bool>, Task> runRefreshWithGlobalProgressAsync = null,
            Action<Guid> openManageAchievementsView = null,
            IFriendCacheManager friendCache = null,
            Func<AchievementHotkeyTargetResolution> resolveRunningGameTarget = null,
            Action<AchievementMarkerTarget> toggleAchievementCapstone = null,
            Action<AchievementMarkerTarget> toggleAchievementGoal = null,
            Captures.CaptureLibraryService captureLibrary = null,
            IReadScheduler readScheduler = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _refreshCoordinator = refreshEntryPoint ?? throw new ArgumentNullException(nameof(refreshEntryPoint));
            _friendCache = friendCache;
            _readScheduler = readScheduler;
            _runRefreshWithGlobalProgressAsync = runRefreshWithGlobalProgressAsync ?? RunRefreshWithoutGlobalProgressAsync;
            _openManageAchievementsView = openManageAchievementsView;
            _toggleAchievementCapstone = toggleAchievementCapstone;
            _toggleAchievementGoal = toggleAchievementGoal;
            _resolveRunningGameTarget = resolveRunningGameTarget ?? ResolveRunningGameTargetFromApi;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
            _logger = logger;
            _friendLists = new FriendListsSource(
                () => _friendCache?.FriendReads,
                () => _settings?.Persisted,
                () => ReadScheduler,
                FriendReadWindow,
                ex => _logger?.Error(ex, "Failed to load friend lists."));
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
            _settings.ToggleAchievementCapstoneCommand = new RelayCommand(ToggleAchievementCapstoneCommand);
            _settings.ToggleAchievementGoalCommand = new RelayCommand(ToggleAchievementGoalCommand);
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
            AchievementTagRegistry.Changed += AchievementTagRegistry_Changed;
            _captureLibrary = captureLibrary;
            if (_captureLibrary != null)
            {
                _captureLibrary.CapturesChanged += CaptureLibrary_CapturesChanged;
            }

            // The friend runtime state is built on demand: the first read of a friend data
            // property (or a friend scope command) registers demand and triggers the build.
            // Themes without friend bindings never pay the full friends-overview snapshot cost.
            _settings.ModernTheme.FriendDataRequested = EnsureFriendThemeDataLoaded;
            _settings.ModernTheme.LibraryListRequested = EnsureLibraryListLoaded;
            _settings.ModernTheme.CategorySummariesRequested = EnsureCategorySummariesLoaded;
        }

        public void Dispose()
        {
            try { _settings.ModernTheme.FriendDataRequested = null; } catch { }
            try { _settings.ModernTheme.LibraryListRequested = null; } catch { }
            try { _settings.ModernTheme.CategorySummariesRequested = null; } catch { }
            try { _settings.DynamicThemeDefaultsChanged -= Settings_DynamicThemeDefaultsChanged; } catch { }
            try { _refreshService.CacheInvalidated -= RefreshService_CacheInvalidated; } catch { }
            AchievementTagRegistry.Changed -= AchievementTagRegistry_Changed;
            try
            {
                if (_captureLibrary != null)
                {
                    _captureLibrary.CapturesChanged -= CaptureLibrary_CapturesChanged;
                }
            }
            catch { }
            _friendLists.Changed -= FriendLists_Changed;
            _friendLists.Dispose();
            _friendRows?.Dispose();
            foreach (var list in _libraryLists.Values)
            {
                list.Dispose();
            }

            Interlocked.Increment(ref _libraryReadVersion);

            lock (_updateGate)
            {
                try { _activeUpdateCts?.Cancel(); } catch { }
                try { _activeUpdateCts?.Dispose(); } catch { }
                _activeUpdateCts = null;
            }

        }

        // The tag counts carry each tag's name, color and icon. A game's own tags change with
        // its custom data, which NotifyCustomDataChanged covers; the global list does not.
        private void AchievementTagRegistry_Changed(object sender, AchievementTagsChangedEventArgs e)
        {
            if (e?.GameId == null)
            {
                NotifyCustomDataChanged(null);
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

        public void EnsureAllGamesThemeDataLoaded(bool forceRefresh = false)
        {
            try
            {
                if (!forceRefresh && _hasLoadedLibraryState)
                {
                    ApplyDynamicOptionBindings();
                    return;
                }

                ReadLibraryState(TimeSpan.Zero);
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
            if (_hasFriendThemeConsumers || !_friendLists.Ensure())
            {
                return;
            }

            _hasFriendThemeConsumers = true;
            _friendLists.Changed += FriendLists_Changed;
            if (_friendLists.Current != null)
            {
                FriendLists_Changed(_friendLists, EventArgs.Empty);
            }
        }

        private void FriendLists_Changed(object sender, EventArgs e) =>
            ApplyFriendState(new FriendRuntimeState(_friendLists.Current));

        /// <summary>The friend lists shared by the theme and the friends widgets.</summary>
        internal FriendListsSource FriendLists => _friendLists;

        /// <summary>Reads the friend lists and rows again, for a setting they show.</summary>
        public void ReloadFriendThemeData()
        {
            _friendLists.Reload();
            _friendRows?.SetQuery(_friendRows.Query);
        }

        /// <summary>
        /// A header click of the theme friend grid on a column no theme sort key names: the sort key
        /// is the column, which the list's query reads and the theme sort commands never accept.
        /// </summary>
        internal void SortFriendAchievements(string sortMemberPath, string directionKey)
        {
            var view = _runtimeState.FriendAchievements;
            view.HasUserSelection = true;
            view.SortKey = sortMemberPath;
            view.SortDirectionKey = directionKey;
            ApplyDynamicFriendBindings();
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        /// <summary>
        /// The rows of the friend scope, as the Friends Overview grid reads them: every friend's
        /// unlocks with a time, a provider, friend or game's unlocks, or a friend and game's every
        /// achievement; then the list's filter and sort.
        /// </summary>
        private AchievementQuery FriendRowsQuery(FriendRuntimeState state)
        {
            var scope = _runtimeState.FriendScope;
            var friend = state.Lists.FindFriend(scope.UserKey);
            var game = state.Lists.FindGame(scope.GameKey);
            var query = FriendsOverviewLists.Scope(AchievementQuery.AllFriends, friend, game)
                .WithFriendNames(state.Lists.FriendNames)
                .WhereProvider(FriendOverviewProjection.IsAllScope(scope.ProviderKey) ? null : scope.ProviderKey);
            query = friend != null && game != null
                ? query
                : friend != null || game != null || !FriendOverviewProjection.IsAllScope(scope.ProviderKey)
                    ? query.WhereUnlocked(true)
                    : query.WhereUnlocked(true).WhereUnlockTimeKnown();
            var view = _runtimeState.FriendAchievements;
            return DynamicThemeFilterEvaluator.ApplyAchievementSort(
                DynamicThemeFilterEvaluator.ApplyAchievementFilters(query, view.FilterKey),
                view.SortKey,
                view.SortDirectionKey);
        }

        private IReadScheduler ReadScheduler =>
            _readScheduler ??= new DispatcherReadScheduler(
                _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);

        /// <summary>
        /// Reads a library-wide achievement list the first time a theme reads it, and again each
        /// time the data under it changes. Uncapped, as themes have always had them.
        /// </summary>
        public void EnsureLibraryListLoaded(string name)
        {
            if (_libraryLists.ContainsKey(name) || !(LibraryListQuery(name) is AchievementQuery query))
            {
                return;
            }

            LiveQuery<List<AchievementDetail>> list = null;
            list = new LiveQuery<List<AchievementDetail>>(
                _achievementDataService.CacheReads?.Feed,
                query,
                // Each read builds the query again, so the games Playnite hides and the days the
                // window covers are those of the read.
                read => LibraryRuntimeStateBuilder.Details(_achievementDataService.ReadRows(LibraryListQuery(name) ?? read), _api),
                ReadScheduler,
                // A whole-library list reads every row, so a refresh's changes are read every half minute.
                () => _refreshService.IsRebuilding ? TimeSpan.FromSeconds(30) : LiveQuery<object>.DefaultWindow,
                ex => _logger?.Error(ex, "Failed to load the theme list " + name + "."));
            _libraryLists[name] = list;
            list.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LiveQuery<object>.Result))
                {
                    ApplyLibraryList(name, list.Result);
                }
            };
            if (list.Result != null)
            {
                ApplyLibraryList(name, list.Result);
            }
        }

        private AchievementQuery LibraryListQuery(string name) =>
            LibraryListQuery(name, _achievementDataService.SummaryQuery(), _runtimeState.LibraryAchievements);

        /// <summary>
        /// The library lists a theme has read so far and their row counts, for diagnostics. Loads
        /// nothing: the theme getters themselves start a read.
        /// </summary>
        internal string DescribeLoadedLibraryLists()
        {
            var loaded = _libraryLists.ToArray();
            return loaded.Length == 0
                ? "none"
                : string.Join(",", loaded.Select(pair => pair.Key + "=" + (pair.Value.Result?.Count ?? 0)));
        }

        // The rows each library list reads of the library under the summary rules (all): goals
        // first in the whole-library lists, in the list's order.
        internal static AchievementQuery LibraryListQuery(string name, AchievementQuery all, LibraryAchievementViewState view)
        {
            var descending = name.EndsWith("Desc", StringComparison.Ordinal);
            switch (name)
            {
                case nameof(ModernThemeBindings.AllAchievementsUnlockAsc):
                case nameof(ModernThemeBindings.AllAchievementsUnlockDesc):
                    return all.WithGoalsFirst().OrderBy(AchievementSort.UnlockTime, descending);
                case nameof(ModernThemeBindings.AllAchievementsRarityAsc):
                case nameof(ModernThemeBindings.AllAchievementsRarityDesc):
                    return all.WithGoalsFirst().OrderBy(AchievementSort.Rarity, descending);
                case nameof(ModernThemeBindings.MostRecentUnlocks):
                    return all.WhereUnlocked(true).WhereUnlockTimeKnown().OrderBy(AchievementSort.UnlockTime, descending: true);
                case nameof(ModernThemeBindings.RarestRecentUnlocks):
                    return RarestRecentQuery(all);
                case nameof(ModernThemeBindings.DynamicLibraryAchievements):
                    var query = all.WithGoalsFirst().WhereProvider(IsAllScopeKey(view.ProviderKey) ? null : view.ProviderKey);
                    query = !IsAllScopeKey(view.GameKey) && Guid.TryParse(view.GameKey, out var gameId) ? query.ForGames(new[] { gameId }) : query;
                    return DynamicThemeFilterEvaluator.ApplyAchievementSort(
                        DynamicThemeFilterEvaluator.ApplyAchievementFilters(query, view.FilterKey),
                        view.SortKey,
                        view.SortDirectionKey);
                default:
                    return null;
            }
        }

        // The rarest unlocks of the last 180 days, rarest first.
        internal static AchievementQuery RarestRecentQuery(AchievementQuery all) =>
            all.WhereUnlocked(true).WhereUnlockTimeKnown().WhereUnlockedBetween(DateTime.Today.AddDays(-180), null).OrderBy(AchievementSort.Rarity);

        private void ApplyLibraryList(string name, List<AchievementDetail> items)
        {
            items = items ?? new List<AchievementDetail>();
            switch (name)
            {
                case nameof(ModernThemeBindings.AllAchievementsUnlockAsc):
                    _settings.ModernTheme.AllAchievementsUnlockAsc = items;
                    break;
                case nameof(ModernThemeBindings.AllAchievementsUnlockDesc):
                    _settings.ModernTheme.AllAchievementsUnlockDesc = items;
                    break;
                case nameof(ModernThemeBindings.AllAchievementsRarityAsc):
                    _settings.ModernTheme.AllAchievementsRarityAsc = items;
                    break;
                case nameof(ModernThemeBindings.AllAchievementsRarityDesc):
                    _settings.ModernTheme.AllAchievementsRarityDesc = items;
                    break;
                case nameof(ModernThemeBindings.MostRecentUnlocks):
                    _settings.ModernTheme.MostRecentUnlocks = items;
                    break;
                case nameof(ModernThemeBindings.RarestRecentUnlocks):
                    _settings.ModernTheme.RarestRecentUnlocks = items;
                    break;
                default:
                    AttachAchievementCommands(items);
                    _settings.ModernTheme.DynamicLibraryAchievements = items;
                    break;
            }

            NotifySettingProperties(name);
        }

        // Points a library list read at its current query; a list no theme read is left unread.
        private void UpdateLibraryListQuery(string name)
        {
            if (_libraryLists.TryGetValue(name, out var list) &&
                LibraryListQuery(name) is AchievementQuery query &&
                AchievementQuerySql.KeyOf(query) != AchievementQuerySql.KeyOf(list.Query))
            {
                list.SetQuery(query);
            }
        }

        // A refresh writes every few hundred milliseconds; its changes are read every few seconds.
        private TimeSpan FriendReadWindow() =>
            _refreshService.IsRebuilding ? TimeSpan.FromSeconds(3) : LiveQuery<object>.DefaultWindow;

        /// <param name="refreshLibraryState">
        /// False when the change cannot move anything the library-wide lists read. The selected-game
        /// surface still rebuilds, so a per-game edit (a category move, a goal reorder) is visible
        /// where the user made it without paying for a whole-library theme rebuild per edit.
        /// </param>
        public void NotifyCustomDataChanged(Guid? gameId, bool refreshLibraryState = true)
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

                // Only rebuild the selected-game surface when it is actually showing the game
                // that changed. RequestUpdate repoints that surface at whatever game it is given,
                // so running it for some other game — which any library-scope edit would do —
                // would swap the selected game out from under the user. Library-scope lists are
                // covered by the library refresh below.
                if (shouldRefreshSelectedGame)
                {
                    // Rebuilds the selected-game theme surface, including that game's achievement
                    // lists. In the Manage window the edited game is almost always the selected
                    // one, so this runs on every edit burst.
                    using (Common.PerfScope.Start(_logger, "Theme.NotifyCustomData.SelectedGame", thresholdMs: 10))
                    {
                        RequestUpdate(resolvedGameId.Value, forceRefresh: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to refresh selected-game theme state after custom-data change.");
            }

            if (!refreshLibraryState)
            {
                return;
            }

            try
            {
                // Suspended while the Manage window is open, so this should log nothing during
                // an editing session. If it does, the suspension is not holding.
                using (var scope = Common.PerfScope.Start(_logger, "Theme.NotifyCustomData.Library", thresholdMs: 10))
                {
                    scope?.SetContext("hasLoadedLibraryState=" + _hasLoadedLibraryState);

                    if (_hasLoadedLibraryState)
                    {
                        RequestLibraryRefresh();
                    }
                    else if (IsFullscreen() && _fullscreenInitialized)
                    {
                        RequestRefresh();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to refresh library theme state after custom-data change.");
            }
        }

        /// <summary>
        /// Playnite games were added, removed, edited or hidden, or summaries started or stopped
        /// leaving hidden games out. The library state and the bound library lists carry their
        /// names and art, so each is read again once a burst of changes ends.
        /// </summary>
        public void NotifyLibraryGamesChanged()
        {
            if (_hasLoadedLibraryState)
            {
                RequestLibraryRefresh();
            }

            var version = Interlocked.Increment(ref _libraryGamesChangeVersion);
            ReadScheduler.PostAfter(LibraryRefreshDelay, () =>
            {
                if (version == _libraryGamesChangeVersion)
                {
                    foreach (var list in _libraryLists.Values)
                    {
                        list.SetQuery(list.Query);
                    }
                }
            });
        }

        /// <summary>
        /// Force-refreshes the selected-game theme surface after a game's captures on disk changed,
        /// so capture-path bindings update without reselecting the game. Only fires when the changed
        /// capture folder belongs to the game the surface is showing (a null folder means every game
        /// changed). The library-wide lists deliberately wait for their next natural rebuild — a
        /// full-library rebuild per saved capture is too heavy.
        /// </summary>
        private void CaptureLibrary_CapturesChanged(object sender, Captures.CapturesChangedEventArgs e)
        {
            try
            {
                var resolvedGameId = _appliedGameId ?? _requestedGameId ?? ResolveSelectedGameIdForThemeUpdate();
                if (!resolvedGameId.HasValue || resolvedGameId.Value == Guid.Empty)
                {
                    return;
                }

                var captureFolderName = e?.FolderName;
                if (captureFolderName != null)
                {
                    var gameName = _api?.Database?.Games?.Get(resolvedGameId.Value)?.Name;
                    var selectedFolder = UnlockScreenshotService.SanitizeCaptureGameName(gameName);
                    if (!string.Equals(selectedFolder, captureFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                RequestUpdate(resolvedGameId.Value, forceRefresh: true);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to refresh selected-game theme state after captures change.");
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

        // Typed, or IsFull and ChangedGameIds are invisible and every per-game edit pays for a
        // whole-library theme refresh. A scoped invalidation names the games that moved.
        private void RefreshService_CacheInvalidated(object sender, CacheInvalidatedEventArgs e)
        {
            var isScoped = e?.IsFull == false;

            Guid? selectedGameId = null;
            try
            {
                selectedGameId = ResolveSelectedGameIdForThemeUpdate();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ThemeIntegration] Failed to resolve the selected game after cache invalidation.");
            }

            // A scoped change repaints the whole fullscreen surface only when it touched the
            // game that surface is showing.
            var scopeTouchesSelectedGame =
                !isScoped ||
                (selectedGameId.HasValue && e.ChangedGameIds.Contains(selectedGameId.Value));

            if (IsFullscreen() && _fullscreenInitialized && scopeTouchesSelectedGame)
            {
                RequestRefresh();
            }

            try
            {
                if (selectedGameId.HasValue && scopeTouchesSelectedGame)
                {
                    RequestUpdate(selectedGameId);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ThemeIntegration] Failed to request selected-game theme update after cache invalidation.");
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
                        () => SelectedGameRuntimeStateBuilder.Build(gameId.Value, gameData, GameSummaryItemBuilder, _settings),
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
            // Mark as initialized to prevent other code paths from triggering refresh
            if (!_fullscreenInitialized)
            {
                _fullscreenInitialized = true;
            }

            // Read now, superseding a pending read; the window shows the state when it lands.
            ReadLibraryState(TimeSpan.Zero);

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

            WithLibraryState(() =>
            {
                var viewState = _runtimeState.LibraryAchievements;
                var providerKey = ResolveGameSummaryProviderKeyForGame(_runtimeState.Library, gameId);

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
            });
        }

        private void FilterDynamicGameSummariesByRunningGame()
        {
            if (!TryResolveRunningGameId(
                nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByRunningGameCommand),
                out var gameId))
            {
                return;
            }

            WithLibraryState(() =>
            {
                var viewState = _runtimeState.GameSummaries;
                var providerKey = ResolveGameSummaryProviderKeyForGame(_runtimeState.Library, gameId);

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
            });
        }

        // Runs apply now when the library state is loaded, else once its read lands.
        private void WithLibraryState(Action apply)
        {
            EnsureAllGamesThemeDataLoaded();
            if (_hasLoadedLibraryState)
            {
                apply();
            }
            else
            {
                _afterLibraryState += apply;
            }
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

        private void ToggleAchievementCapstoneCommand(object parameter)
        {
            if (!TryResolveThemeCommandAchievement(
                parameter,
                nameof(PlayniteAchievementsSettings.ToggleAchievementCapstoneCommand),
                out var target))
            {
                return;
            }

            _toggleAchievementCapstone?.Invoke(target);
        }

        private void ToggleAchievementGoalCommand(object parameter)
        {
            if (!TryResolveThemeCommandAchievement(
                parameter,
                nameof(PlayniteAchievementsSettings.ToggleAchievementGoalCommand),
                out var target))
            {
                return;
            }

            _toggleAchievementGoal?.Invoke(target);
        }

        /// <summary>
        /// Resolves the achievement a marker toggle acts on. Unlike
        /// <see cref="TryResolveThemeCommandGameId"/> this never falls back to the selected game:
        /// without an achievement there is no meaningful target, and a library-scope row must act
        /// on its own game rather than whatever is selected.
        /// </summary>
        private bool TryResolveThemeCommandAchievement(
            object parameter,
            string commandName,
            out AchievementMarkerTarget target)
        {
            target = default(AchievementMarkerTarget);

            if (parameter == DependencyProperty.UnsetValue)
            {
                parameter = null;
            }

            switch (parameter)
            {
                case AchievementDetail achievement when achievement.Game != null:
                    target = new AchievementMarkerTarget(
                        achievement.Game.Id,
                        achievement.ApiName,
                        achievement.IsCapstone,
                        achievement.IsGoal,
                        achievement.Unlocked);
                    break;
                // A friend's row describes their progress, not the user's, so it can never be the
                // target of a marker the user owns.
                case FriendAchievementDisplayItem _:
                    break;
                case AchievementDisplayItem item when item.PlayniteGameId.HasValue:
                    target = new AchievementMarkerTarget(
                        item.PlayniteGameId.Value,
                        item.ApiName,
                        item.IsCapstone,
                        item.IsGoal,
                        item.Unlocked);
                    break;
            }

            if (!target.IsValid)
            {
                LogInvalidCommandParameter(commandName, parameter);
                return false;
            }

            return true;
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
                    gameData,
                    GameSummaryItemBuilder,
                    _settings);

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
            RequestLibraryRefresh(requireFullscreen: true);
        }

        /// <summary>
        /// Holds off whole-library theme rebuilds until the matching <see cref="ResumeLibraryRefresh"/>,
        /// for a caller about to make a burst of per-game edits. Requests that arrive while held
        /// are collapsed into one rebuild issued on release; the per-game selected surface is not
        /// affected, so the game being edited still repaints as it changes.
        /// </summary>
        public void SuspendLibraryRefresh()
        {
            lock (_refreshLock)
            {
                _librarySuspensionCount++;
            }
        }

        /// <summary>
        /// Releases one <see cref="SuspendLibraryRefresh"/>. The last release issues the single
        /// rebuild standing in for everything that was held.
        /// </summary>
        public void ResumeLibraryRefresh()
        {
            bool shouldRefresh;
            lock (_refreshLock)
            {
                if (_librarySuspensionCount > 0)
                {
                    _librarySuspensionCount--;
                }

                if (_librarySuspensionCount > 0)
                {
                    return;
                }

                shouldRefresh = _librarySuspensionHasPendingRefresh;
                _librarySuspensionHasPendingRefresh = false;
            }

            if (shouldRefresh)
            {
                RequestLibraryRefresh();
            }
        }

#if TEST
        internal bool HasDeferredLibraryRefreshForTests
        {
            get
            {
                lock (_refreshLock)
                {
                    return _librarySuspensionHasPendingRefresh;
                }
            }
        }

        internal void RequestLibraryRefreshForTests()
        {
            RequestLibraryRefresh();
        }
#endif

        private void RequestLibraryRefresh(bool requireFullscreen = false)
        {
            if (requireFullscreen && !IsFullscreen())
            {
                return;
            }

            lock (_refreshLock)
            {
                if (_librarySuspensionCount > 0)
                {
                    // The fullscreen gate above has already been answered for this request, so the
                    // deferred one does not re-ask it.
                    _librarySuspensionHasPendingRefresh = true;

                    // An in-flight rebuild from just before the suspension is abandoned: it would
                    // publish state built from data the burst is still changing.
                    Interlocked.Increment(ref _libraryReadVersion);
                    return;
                }
            }

            ReadLibraryState(LibraryRefreshDelay);
        }

        /// <summary>
        /// Reads the library state off the UI thread after <paramref name="delay"/> and applies it
        /// on the UI thread, unless a later request has superseded it by then.
        /// </summary>
        private void ReadLibraryState(TimeSpan delay)
        {
            var version = Interlocked.Increment(ref _libraryReadVersion);
            Action read = () => ReadScheduler.Run(() =>
            {
                if (version != _libraryReadVersion)
                {
                    return;
                }

                LibraryRuntimeState state;
                try
                {
                    state = BuildLibraryState();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to refresh theme integration snapshot.");
                    return;
                }

                ReadScheduler.Post(() =>
                {
                    if (version == _libraryReadVersion)
                    {
                        _hasLoadedLibraryState = true;
                        ApplyLibraryState(state);
                        var after = _afterLibraryState;
                        _afterLibraryState = null;
                        after?.Invoke();
                    }
                });
            });
            if (delay > TimeSpan.Zero)
            {
                ReadScheduler.PostAfter(delay, read);
            }
            else
            {
                read();
            }
        }

        // The library's summary, its ten newest unlocks and its ten rarest of the last 180 days.
        private LibraryRuntimeState BuildLibraryState()
        {
            var rarest = _achievementDataService.ReadRows(RarestRecentQuery(_achievementDataService.SummaryQuery()).Page(ThemeRecentUnlockSummaryLimit));
            return LibraryRuntimeStateBuilder.BuildFromCachedSummary(
                _achievementDataService.GetCachedSummaryDataForTheme(ThemeRecentUnlockSummaryLimit),
                rarest,
                _api,
                CancellationToken.None);
        }

        private void ApplyLibraryState(LibraryRuntimeState state)
        {
            _runtimeState.Library = state ?? new LibraryRuntimeState();
            Common.LeakWatch.Track("ThemeLibraryState", _runtimeState.Library);
            var library = _runtimeState.Library;
            PruneGameCommandCaches(library.AllGamesWithAchievements);

            // One projected instance per game for this whole pass; see the memo's own note.
            _gameSummaryProjectionMemo = new Dictionary<Guid, GameAchievementSummary>();
            try
            {
                ApplyLibraryStateCore(library);
            }
            finally
            {
                _gameSummaryProjectionMemo = null;
            }
        }

        private void ApplyLibraryStateCore(LibraryRuntimeState library)
        {
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
            _settings.ModernTheme.TotalTagStats = library.TotalTagStats;
            _settings.ModernTheme.CollectorScore = library.CollectorScore;
            _settings.ModernTheme.CollectorLevel = library.CollectorLevel;
            _settings.ModernTheme.CollectorLevelProgress = library.CollectorLevelProgress;
            _settings.ModernTheme.CollectorRank = library.CollectorRank;
            _settings.ModernTheme.PrestigeScore = library.PrestigeScore;
            _settings.ModernTheme.PrestigeLevel = library.PrestigeLevel;
            _settings.ModernTheme.PrestigeLevelProgress = library.PrestigeLevelProgress;
            _settings.ModernTheme.PrestigeRank = library.PrestigeRank;
            _settings.ModernTheme.CollectorMastery = library.CollectorMastery;
            _settings.ModernTheme.PrestigeMastery = library.PrestigeMastery;
            _settings.ModernTheme.GamerscoreScore = library.GamerscoreScore;
            _settings.ModernTheme.GamerscoreTotal = library.GamerscoreTotal;
            _settings.ModernTheme.EpicXpScore = library.EpicXpScore;
            _settings.ModernTheme.EpicXpTotal = library.EpicXpTotal;
            _settings.ModernTheme.RetroPointsScore = library.RetroPointsScore;
            _settings.ModernTheme.RetroPointsTotal = library.RetroPointsTotal;
            _settings.ModernTheme.PointsScores = library.PointsScores;
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
            _settings.ModernTheme.LocalGames = ProjectGameSummaries(library.LocalGames);
            _settings.ModernTheme.UbisoftGames = ProjectGameSummaries(library.UbisoftGames);
            _settings.ModernTheme.RPCS3Games = ProjectGameSummaries(library.RPCS3Games);
            _settings.ModernTheme.XeniaGames = ProjectGameSummaries(library.XeniaGames);
            _settings.ModernTheme.ShadPS4Games = ProjectGameSummaries(library.ShadPS4Games);
            _settings.ModernTheme.GameJoltGames = ProjectGameSummaries(library.GameJoltGames);
            _settings.ModernTheme.RiotGames = ProjectGameSummaries(library.RiotGames);
            _settings.ModernTheme.FFXIVGames = ProjectGameSummaries(library.FFXIVGames);
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

            ApplyDynamicLibraryAchievementBindings(updateOptions: false);
            ApplyDynamicGameSummaryBindings(updateOptions: false);
            ApplyDynamicOptionBindings();

            NotifySettingProperties(ThemeDelegatedPropertyCatalog.CompatibilityAllGames);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.ModernAllGamesCore);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicAllGames);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
        }

        private void PersistScoreSnapshot(LibraryRuntimeState library)
        {
            try
            {
                if (library == null || !library.HasData)
                {
                    return;
                }

                var persisted = _settings?.Persisted;
                if (persisted == null)
                {
                    return;
                }

                if (persisted.LastAllGamesCollectorScore == library.CollectorScore &&
                    persisted.LastAllGamesCollectorLevel == library.CollectorLevel &&
                    Math.Abs(persisted.LastAllGamesCollectorLevelProgress - library.CollectorLevelProgress) < 0.0001 &&
                    string.Equals(persisted.LastAllGamesCollectorRank, library.CollectorRank, StringComparison.Ordinal) &&
                    persisted.LastAllGamesPrestigeScore == library.PrestigeScore &&
                    persisted.LastAllGamesPrestigeLevel == library.PrestigeLevel &&
                    Math.Abs(persisted.LastAllGamesPrestigeLevelProgress - library.PrestigeLevelProgress) < 0.0001 &&
                    string.Equals(persisted.LastAllGamesPrestigeRank, library.PrestigeRank, StringComparison.Ordinal))
                {
                    return;
                }

#if !TEST
                PublishScoreProgressNotifications(persisted, library);
#endif

                persisted.LastAllGamesCollectorScore = library.CollectorScore;
                persisted.LastAllGamesCollectorLevel = library.CollectorLevel;
                persisted.LastAllGamesCollectorLevelProgress = library.CollectorLevelProgress;
                persisted.LastAllGamesCollectorRank = library.CollectorRank;
                persisted.LastAllGamesPrestigeScore = library.PrestigeScore;
                persisted.LastAllGamesPrestigeLevel = library.PrestigeLevel;
                persisted.LastAllGamesPrestigeLevelProgress = library.PrestigeLevelProgress;
                persisted.LastAllGamesPrestigeRank = library.PrestigeRank;

#if !TEST
                PlayniteAchievementsPlugin.Instance?.PersistSettingsForUiSilently();
#endif
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Debug(ex, "Failed to persist all-games score snapshot.");
            }
        }

#if !TEST
        private void PublishScoreProgressNotifications(PersistedSettings persisted, LibraryRuntimeState library)
        {
            // A zero snapshot represents startup/migration, not an earned progression event.
            if (persisted == null || library == null ||
                (persisted.LastAllGamesCollectorScore <= 0 && persisted.LastAllGamesPrestigeScore <= 0))
            {
                return;
            }

            var local = ProviderRegistry.Settings<Providers.Local.LocalSettings>();
            if (local?.EnableActiveGameMonitoring != true)
            {
                return;
            }

            var publisher = new NotificationPublisher(_api, _settings, _logger);
            PublishScoreProgress(
                publisher,
                "Collection",
                local.CollectionProgressNotifications,
                local,
                persisted.LastAllGamesCollectorScore,
                persisted.LastAllGamesCollectorLevel,
                persisted.LastAllGamesCollectorRank,
                library.CollectorScore,
                library.CollectorLevel,
                library.CollectorRank);
            PublishScoreProgress(
                publisher,
                "Prestige",
                local.PrestigeProgressNotifications,
                local,
                persisted.LastAllGamesPrestigeScore,
                persisted.LastAllGamesPrestigeLevel,
                persisted.LastAllGamesPrestigeRank,
                library.PrestigeScore,
                library.PrestigeLevel,
                library.PrestigeRank);
        }

        private static void PublishScoreProgress(
            NotificationPublisher publisher,
            string scoreName,
            Providers.Local.ScoreProgressNotificationSettings notification,
            Providers.Local.LocalSettings local,
            int oldScore,
            int oldLevel,
            string oldRank,
            int newScore,
            int newLevel,
            string newRank)
        {
            if (notification == null || oldScore <= 0 || newScore <= oldScore)
            {
                return;
            }

            if (notification.NotifyTierUp &&
                !string.Equals(oldRank, newRank, StringComparison.Ordinal))
            {
                publisher.SendScoreProgressNotification(
                    scoreName,
                    "tier",
                    newLevel,
                    newRank,
                    notification,
                    local);
            }
            else if (notification.NotifyLevelUp && newLevel > oldLevel)
            {
                publisher.SendScoreProgressNotification(
                    scoreName,
                    "level",
                    newLevel,
                    newRank,
                    notification,
                    local);
            }
        }
#endif

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
            _settings.ModernTheme.TagStats = state.TagStats;
            _settings.ModernTheme.SelectedGameSummary = state.SelectedGameSummary;
            _settings.ModernTheme.AchievementDefaultOrder = state.AchievementDefaultOrder;
            _settings.ModernTheme.AllAchievements = state.AllAchievements;
            _settings.ModernTheme.AchievementsNewestFirst = state.AchievementsNewestFirst;
            _settings.ModernTheme.AchievementsOldestFirst = state.AchievementsOldestFirst;
            _settings.ModernTheme.AchievementsRarityAsc = state.AchievementsRarityAsc;
            _settings.ModernTheme.AchievementsRarityDesc = state.AchievementsRarityDesc;
            _settings.ModernTheme.LatestAchievementData = state.LatestAchievementData;

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
            _settings.ModernTheme.LatestAchievementData = null;

            _settings.ModernTheme.Common = EmptyRarityStats;
            _settings.ModernTheme.Uncommon = EmptyRarityStats;
            _settings.ModernTheme.Rare = EmptyRarityStats;
            _settings.ModernTheme.UltraRare = EmptyRarityStats;
            _settings.ModernTheme.RareAndUltraRare = EmptyRarityStats;
            _settings.ModernTheme.TagStats = new List<AchievementTagStats>();
            _settings.ModernTheme.SelectedGameSummary = null;
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

        /// <summary>
        /// Projected summaries for the rebuild currently running, keyed by game id, so a game
        /// appearing in several of the theme lists is projected once.
        /// </summary>
        /// <remarks>
        /// <see cref="ApplyLibraryState"/> calls <see cref="ProjectGameSummaries"/> around two
        /// dozen times over lists that overlap heavily -- the ascending and descending pairs,
        /// the all-games list, the completed and platinum pairs, and one provider bucket all
        /// contain the same game -- so a library of N games produced roughly 7N fresh summaries
        /// and twice that many command instances per refresh, all with identical content.
        ///
        /// Sharing one instance across those lists is safe: the type is an ObservableObject
        /// whose content is read-only after construction, and WPF is content to see the same
        /// item in several collections. It is also what lets an edit to one game notify every
        /// surface showing it.
        /// </remarks>
        private Dictionary<Guid, GameAchievementSummary> _gameSummaryProjectionMemo;

        private ObservableCollection<GameAchievementSummary> ProjectGameSummaries(IEnumerable<GameAchievementSummary> items)
        {
            var memo = _gameSummaryProjectionMemo;
            if (memo != null)
            {
                var reused = new List<GameAchievementSummary>();
                var fresh = new List<GameAchievementSummary>();
                foreach (var item in items ?? Enumerable.Empty<GameAchievementSummary>())
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (!memo.TryGetValue(item.GameId, out var projectedItem))
                    {
                        projectedItem = CreateProjectedGameSummary(item);
                        memo[item.GameId] = projectedItem;
                        fresh.Add(projectedItem);
                    }

                    reused.Add(projectedItem);
                }

                // Only the ones built here need their commands attached; the rest already have
                // them from the list that first projected them.
                AttachGameSummaryCommands(fresh);
                return new ObservableCollection<GameAchievementSummary>(reused);
            }

            var projected = (items ?? Enumerable.Empty<GameAchievementSummary>())
                .Where(item => item != null)
                .Select(CreateProjectedGameSummary)
                .ToList();

            AttachGameSummaryCommands(projected);
            return new ObservableCollection<GameAchievementSummary>(projected);
        }

        private GameAchievementSummary CreateProjectedGameSummary(GameAchievementSummary item)
        {
            return new GameAchievementSummary(
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
                sortingName: item.SortingName);
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

        // Reads the scope's rows once friend lists are loaded; the theme's list virtualizes them.
        private void UpdateFriendRowsQuery(FriendRuntimeState state)
        {
            var reads = _friendCache?.FriendReads;
            if (_friendLists.Current == null || reads == null)
            {
                return;
            }

            var query = FriendRowsQuery(state);
            if (_friendRows != null)
            {
                if (AchievementQuerySql.KeyOf(query) != AchievementQuerySql.KeyOf(_friendRows.Query))
                {
                    _friendRows.SetQuery(query, fromStart: true);
                }

                return;
            }

            _friendRows = new PagedRows<FriendAchievementDisplayItem>(
                reads.Feed,
                query,
                reads.SortedKeys,
                (read, keys) =>
                {
                    var rows = reads.Rows(read, keys);
                    (_friendLists.Current ?? FriendsOverviewLists.Empty).StampIdentity(rows, _settings?.Persisted);
                    AttachFriendAchievementCommands(rows);
                    return rows;
                },
                () => new FriendAchievementDisplayItem { IsPlaceholder = true },
                ReadScheduler,
                window: FriendReadWindow,
                onError: ex => _logger?.Error(ex, "Failed to load friend theme rows."),
                update: (kept, fresh) => kept.RefreshFrom(fresh));
            _settings.ModernTheme.DynamicFriendAchievements = _friendRows;
        }

        private void ApplyFriendState(FriendRuntimeState state)
        {
            _runtimeState.Friends = state ?? FriendRuntimeState.Empty;
            ApplyDynamicFriendBindings(updateOptions: false);
            NotifySettingProperties(ThemeDelegatedPropertyCatalog.DynamicFriends);
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
                var friend = state.Lists.FindFriend(scope.UserKey);
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
                (_settings.OpenManageAchievementsWindow, (item, command) => item.OpenManageAchievementsWindow = command),
                (_settings.ToggleAchievementCapstoneCommand, (item, command) => item.ToggleAchievementCapstoneCommand = command),
                (_settings.ToggleAchievementGoalCommand, (item, command) => item.ToggleAchievementGoalCommand = command));
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
                    OptionKeys = DynamicThemeOptionGroups.LiveAchievementCategoryTypeFilterKeys,
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
                    OptionKeys = DynamicThemeOptionGroups.LiveAchievementCategoryTypeFilterKeys,
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

        /// <summary>
        /// Builds the category summaries the first time a theme reads them, and on each selection
        /// after that, so themes that never bind them never build the selected game's display items.
        /// </summary>
        private void EnsureCategorySummariesLoaded()
        {
            if (!_hasCategorySummaryConsumers)
            {
                _hasCategorySummaryConsumers = true;
                ApplyDynamicCategorySummaryBindings();
            }
        }

        private bool _hasCategorySummaryConsumers;

        private void ApplyDynamicCategorySummaryBindings()
        {
            if (_hasCategorySummaryConsumers)
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
            }

            ApplyDynamicListKeyBindings(CategorySummariesListBinding);
            ApplyDynamicListOptionBindings(CategorySummariesListBinding);
        }

        private static int CountDistinctCategoryLabels(IReadOnlyList<AchievementDisplayItem> items)
        {
            var labels = new HashSet<string>(CategoryPathHelper.LabelComparer);
            foreach (var item in items ?? (IReadOnlyList<AchievementDisplayItem>)Array.Empty<AchievementDisplayItem>())
            {
                if (item != null)
                {
                    labels.Add(AchievementCategoryHelper.NormalizeCategoryOrDefault(item.CategoryLabel));
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
                    categoryKind: (item as CategorySummaryItem)?.CategoryKind));
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
                    // Leaf, matching the options list this label summarizes.
                    : AchievementCategoryHelper.ToCategoryLeafDisplayText(categoryKey);
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
            UpdateLibraryListQuery(nameof(ModernThemeBindings.DynamicLibraryAchievements));
            ApplyDynamicListKeyBindings(LibraryAchievementListBinding);
            _settings.ModernTheme.DynamicLibraryAchievementsProviderOptions = DynamicThemeOptionFactory.CreateProviderOptions(
                (state.AllGamesWithAchievements ?? Enumerable.Empty<GameAchievementSummary>()).Select(item => item?.ProviderKey),
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
            var providerKeys = (state.AllGamesWithAchievements ??
                    Enumerable.Empty<GameAchievementSummary>())
                .Select(item => item?.ProviderKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToList();

            // Local is a first-class personal provider, but older Local cache rows can pass
            // through startup projections that predate provider-platform normalization.
            // Keep the filter available even while such rows are being normalized so fullscreen
            // themes never lose the only route for selecting Local games.
            if (!providerKeys.Any(key =>
                    string.Equals(key, "Local", StringComparison.OrdinalIgnoreCase)))
            {
                providerKeys.Add("Local");
            }

            _settings.ModernTheme.DynamicGameSummaries = ProjectGameSummaries(items);
            ApplyDynamicListKeyBindings(GameSummaryListBinding);
            _settings.ModernTheme.DynamicGameSummariesProviderOptions = DynamicThemeOptionFactory.CreateProviderOptions(
                providerKeys,
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
            UpdateFriendRowsQuery(state);

            var scope = _runtimeState.FriendScope;
            var friendItems = BuildDynamicFriendSummaries(state, _runtimeState.FriendSummaries);
            var gameItems = BuildDynamicFriendGameSummaries(state, _runtimeState.FriendGameSummaries);
            AttachFriendSummaryCommands(friendItems);

            _settings.ModernTheme.DynamicFriendSummaries = new ObservableCollection<FriendSummaryItem>(friendItems);
            _settings.ModernTheme.DynamicFriendGameSummaries = ProjectFriendGameSummaries(gameItems);

            _settings.ModernTheme.DynamicFriendScopeProviderKey = scope.ProviderKey;
            _settings.ModernTheme.DynamicFriendScopeProviderLabel = DynamicThemeLabels.GetProviderLabel(scope.ProviderKey);
            _settings.ModernTheme.DynamicFriendScopeUserKey = scope.UserKey;
            _settings.ModernTheme.DynamicFriendScopeUserLabel = GetFriendScopeUserLabel(state, scope.UserKey);
            _settings.ModernTheme.DynamicFriendScopeGameKey = scope.GameKey;
            _settings.ModernTheme.DynamicFriendScopeGameLabel = GetFriendScopeGameLabel(state, scope.GameKey);

            // Surface the scoped friend as a single bindable item so themes can show that friend's
            // whole-library rarity/trophy rollup next to their scoped achievements list. Null when no
            // individual friend is scoped (FindFriend returns null for the all-friends scope).
            var scopeSummary = state.Lists.FindFriend(scope.UserKey);
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
            var selectedFriend = state?.Lists.FindFriend(userKey);
            IEnumerable<FriendGameSummaryItem> games = selectedFriend != null
                ? state.Lists.GamesOf(selectedFriend)
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

            var friend = state?.Lists.FindFriend(userKey);
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

            var game = state?.Lists.FindGame(gameKey);
            return !string.IsNullOrWhiteSpace(game?.GameName)
                ? game.GameName
                : gameKey ?? string.Empty;
        }

        // The library state's games in game-option order, kept until the state is rebuilt.
        private Tuple<List<GameAchievementSummary>, List<DynamicThemeOption>> _sortedGameOptions;

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
            var sortedGames = _sortedGameOptions;
            if (sortedGames == null || sortedGames.Item1 != library.AllGamesWithAchievements)
            {
                sortedGames = _sortedGameOptions = Tuple.Create(
                    library.AllGamesWithAchievements,
                    DynamicThemeOptionFactory.SortGames(library.AllGamesWithAchievements));
            }

            _settings.ModernTheme.DynamicAchievementGameOptions = DynamicThemeOptionFactory.CreateGameOptions(
                sortedGames.Item2,
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
                DynamicThemeOptionGroups.GetAchievementCategoryTypeFilterKeys(
                    Guid.TryParse(selectedGameKey, out var selectedTagGameId) ? selectedTagGameId : (Guid?)null),
                DynamicThemeOptionGroups.GetGroupSelection(selectedGameView.FilterKey, DynamicThemeOptionGroups.AchievementCategoryTypeGroup, DynamicThemeOptionGroups.AchievementFilterGroupMap),
                _settings.SetDynamicAchievementsCategoryTypeFilterCommand,
                // The selected game's own tags name themselves too, not only the global ones.
                getLabel: key => AchievementClassificationFilter.IsFilterKey(key) &&
                                 !string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase)
                    ? AchievementClassificationFilter.GetDisplayName(key, selectedTagGameId)
                    : null);
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
                AchievementCategoryHelper.NormalizeCategoryOrDefault(item?.Category),
                categoryLabelKey,
                CategoryPathHelper.LabelComparison));
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
            // pin the list to a permanently empty scope. The key comes from a theme, so it
            // resolves loosely: an exact label wins, else the one label differing only in case.
            var normalized = AchievementCategoryHelper.NormalizeCategoryOrDefault(raw);
            var achievements = _runtimeState.SelectedGame?.AllAchievements ?? EmptyAchievementList;
            key = CategoryPathHelper.ResolveLoose(
                normalized,
                achievements.Select(item => AchievementCategoryHelper.NormalizeCategoryOrDefault(item?.Category)));
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
                AchievementCategoryHelper.NormalizeCategoryOrDefault(item?.Category),
                key,
                CategoryPathHelper.LabelComparison));
            if (!exists)
            {
                viewState.ResetCategoryScope();
            }
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
            if (state == null || !state.HasData)
            {
                return new List<FriendSummaryItem>();
            }

            var scope = _runtimeState.FriendScope;
            var selectedGame = state.Lists.FindGame(scope.GameKey);
            IEnumerable<FriendSummaryItem> source = state.Friends ?? Enumerable.Empty<FriendSummaryItem>();
            source = source.Where(friend => FriendMatchesProvider(friend, scope.ProviderKey));
            if (selectedGame != null)
            {
                source = source.Where(friend => state.Lists.HasPairData(friend, selectedGame));
            }

            source = ApplyDynamicFilterPredicates(source, viewState.FilterKey, FriendSummaryFilterPredicates);
            var sorted = FriendSummarySortTable.Sort(source, viewState).ToList();
            // Pin favorites ahead of non-favorites while preserving the sorted order within each group
            // (Where is stable), so favorites lead regardless of the theme's active sort key.
            return sorted
                .Where(friend => friend?.IsFavorite == true)
                .Concat(sorted.Where(friend => friend?.IsFavorite != true))
                .ToList();
        }

        private List<FriendGameSummaryItem> BuildDynamicFriendGameSummaries(
            FriendRuntimeState state,
            FriendGameSummaryViewState viewState)
        {
            if (state == null || !state.HasData)
            {
                return new List<FriendGameSummaryItem>();
            }

            var scope = _runtimeState.FriendScope;
            var selectedFriend = state.Lists.FindFriend(scope.UserKey);
            var selectedGame = state.Lists.FindGame(scope.GameKey);
            IEnumerable<FriendGameSummaryItem> source = selectedFriend != null
                ? state.Lists.GamesOf(selectedFriend)
                : state.AggregateGames ?? Enumerable.Empty<FriendGameSummaryItem>();

            source = ApplyProviderFilter(source, scope.ProviderKey, item => item.ProviderKey);

            if (selectedGame != null)
            {
                source = source.Where(game => FriendOverviewProjection.IsSameGame(game, selectedGame));
            }

            source = ApplyDynamicFilterPredicates(source, viewState.FilterKey, FriendGameSummaryFilterPredicates);
            return FriendGameSummarySortTable.Sort(source, viewState).ToList();
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
                if (key == null)
                {
                    continue;
                }

                if (predicates.TryGetValue(key, out var predicate))
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






