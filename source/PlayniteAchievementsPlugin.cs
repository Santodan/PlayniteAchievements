using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.ThemeIntegration;
using PlayniteAchievements.Services.ThemeMigration;
using PlayniteAchievements.Services.Tagging;
using PlayniteAchievements.Services.UI;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Shell;
using System.Windows.Threading;
using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin : GenericPlugin
    {
        private readonly ILogger _logger;


        // Set Properties before constructor runs
        private static readonly GenericPluginProperties _pluginProperties = new GenericPluginProperties
        {
            HasSettings = true
        };

        private static readonly string[] ProviderDisplayOrder =
        {
            "Steam", "Epic", "GOG", "BattleNet", "EA", "Ubisoft", "PSN", "Xbox", "GooglePlay", "Apple", "FFXIV", "RetroAchievements", "RPCS3", "ShadPS4", "Xenia", "Manual", "Exophase", "Hoyoverse"
        };

        private static readonly string[] ProviderRefreshOrder =
        {
            "Manual", "FFXIV", "Exophase", "Steam", "Epic", "GOG", "BattleNet", "EA", "Hoyoverse", "RPCS3", "ShadPS4", "PSN", "Xenia", "Xbox", "RetroAchievements"
        };

        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly RefreshRuntime _refreshService;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly LibraryProjectionService _libraryProjectionService;
        private readonly ICacheManager _cacheManager;
        private readonly IFriendCacheManager _friendCacheManager;
        private readonly FriendsOverviewDataCoordinator _friendsOverviewDataCoordinator;
        private readonly FriendGameAchievementsDataCoordinator _friendGameAchievementsDataCoordinator;
        private readonly FriendsRecentUnlocksDataCoordinator _friendsRecentUnlocksDataCoordinator;
        private readonly MemoryImageService _imageService;
        private readonly DiskImageService _diskImageService;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly NotificationPublisher _notifications;
        private readonly ProviderRegistry _providerRegistry;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly SubscriptionCollection _eventSubscriptions = new SubscriptionCollection();

        private readonly BackgroundUpdater _backgroundUpdates;
        private readonly InGameAchievementPoller _inGamePoller;
        private readonly ActiveGameWindowTracker _windowTracker;
        private readonly ToastNotificationService _toastNotifications;
        private readonly Services.Recording.UnlockRecordingService _unlockRecordings;

        /// <summary>
        /// Started process ids of currently running games (from OnGameStarted), used to identify
        /// each game's window/monitor for unlock screenshots and recordings. Order tracks most
        /// recently started first. Guarded by <see cref="_runningGamesLock"/>.
        /// </summary>
        private readonly object _runningGamesLock = new object();
        private readonly List<Guid> _runningGameOrder = new List<Guid>();
        private readonly Dictionary<Guid, int?> _startedProcessIds = new Dictionary<Guid, int?>();
        private readonly RefreshEntryPoint _refreshCoordinator;
        private bool _applicationStarted;

        // Top panel item
        private PlayniteAchievementsTopPanelItem _topPanelItem;

        // Theme integration
        private readonly FullscreenWindowService _fullscreenWindowService;
        private readonly ThemeIntegrationService _themeIntegrationService;
        private readonly ThemeControlRegistry _themeControlRegistry;
        private readonly AchievementResourceService _resourceService;
        private readonly PluginWindowService _windowService;
        private readonly AchievementHotkeyTargetResolver _achievementHotkeyTargetResolver;
        private readonly AchievementHotkeyService _achievementHotkeyService;
        private readonly FullscreenControllerNavigationService _fullscreenControllerNavigationService;
        private readonly ThemeAutoMigrationService _themeAutoMigrationService;

        // Tagging
        private readonly object _tagSyncGate = new object();
        private readonly HashSet<Guid> _pendingTagSyncIds = new HashSet<Guid>();
        private bool _tagSyncDrainRunning;
        private TagSyncService _tagSyncService;

        public override Guid Id { get; } =
            Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");

        public PlayniteAchievementsSettings Settings => _settingsViewModel.Settings;
        public ProviderRegistry ProviderRegistry => _providerRegistry;
        public GameCustomDataStore GameCustomDataStore => _gameCustomDataStore;
        public IReadOnlyList<IDataProvider> Providers => _refreshService?.Providers;
        public RefreshRuntime RefreshRuntime => _refreshService;
        public AchievementOverridesService AchievementOverridesService => _achievementOverridesService;
        public AchievementDataService AchievementDataService => _achievementDataService;
        public MemoryImageService ImageService => _imageService;
        public DiskImageService DiskImageService => _diskImageService;
        public ManagedCustomIconService ManagedCustomIconService => _managedCustomIconService;
        public ThemeIntegrationService ThemeIntegrationService => _themeIntegrationService;
        public ThemeIntegrationService ThemeUpdateService => _themeIntegrationService;
        public TagSyncService TagSyncService => _tagSyncService;
        internal RefreshEntryPoint RefreshEntryPoint => _refreshCoordinator;
        public static PlayniteAchievementsPlugin Instance { get; private set; }

        /// <summary>
        /// Event raised when plugin settings are saved. Used to refresh UI components
        /// that display authentication status or other settings-dependent information.
        /// </summary>
        public static event EventHandler SettingsSaved;
        public static event EventHandler<AchievementUnlockedEventArgs> AchievementUnlocked;

        /// <summary>
        /// Raises the SettingsSaved event to notify listeners that settings have changed. Every
        /// subscriber is a UI control, so when this is invoked from a background thread (e.g. a friend
        /// roster merge during refresh) the event is marshaled onto the UI dispatcher to avoid
        /// cross-thread access exceptions. On the UI thread it is raised inline as before.
        /// </summary>
        public static void NotifySettingsSaved()
        {
            var handler = SettingsSaved;
            if (handler == null)
            {
                return;
            }

            var dispatcher = Instance?.PlayniteApi?.MainView?.UIDispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => handler.Invoke(null, EventArgs.Empty)));
                return;
            }

            handler.Invoke(null, EventArgs.Empty);
        }

        public static void NotifyAchievementUnlocked(AchievementUnlockedEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            var handler = AchievementUnlocked;
            if (handler == null)
            {
                return;
            }

            var dispatcher = Instance?.PlayniteApi?.MainView?.UIDispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => handler.Invoke(null, args)));
                return;
            }

            handler.Invoke(null, args);
        }

        private void TryWarmCustomDataCache()
        {
            if (_gameCustomDataStore == null)
            {
                return;
            }

            try
            {
                using (PerfScope.StartStartup(_logger, "PluginCtor.CustomDataWarmup", thresholdMs: 50))
                {
                    var rows = _gameCustomDataStore.LoadAll();
                    _logger?.Debug($"Preloaded {rows?.Count ?? 0} game custom-data rows.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to warm game custom-data cache during plugin startup.");
            }
        }

        /// <summary>
        /// Directory containing the plugin's shipped localization dictionaries, resolved
        /// from the installed assembly location. Returns null when it cannot be resolved.
        /// </summary>
        private string GetPluginLocalizationDirectory()
        {
            try
            {
                var installDirectory = Path.GetDirectoryName(typeof(PlayniteAchievementsPlugin).Assembly.Location);
                return string.IsNullOrEmpty(installDirectory)
                    ? null
                    : Path.Combine(installDirectory, "Localization");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to resolve plugin localization directory.");
                return null;
            }
        }

        public void PersistSettingsForUi()
        {
            try
            {
                _providerRegistry?.PersistAllProviderSettings(false);
                SavePluginSettings(_settingsViewModel.Settings);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist plugin settings.");
            }

            NotifySettingsSaved();
        }

        // Public bridge method for external helpers/themes that used to target SuccessStory via reflection.
        // AnikiHelper (PlayniteAchievements-based) will call this when available.
        // AnikiHelper's bridge reflects the name "RequestSingleGameScanAsync"; keep this alias
        // so its per-game refresh keeps working.
        public Task RequestSingleGameScanAsync(Guid playniteGameId)
        {
            return RequestSingleGameRefreshAsync(playniteGameId);
        }

        public Task RequestSingleGameRefreshAsync(Guid playniteGameId)
        {
            return _refreshCoordinator?.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Single,
                SingleGameId = playniteGameId
            }) ?? Task.CompletedTask;
        }

        // Invoked by AchievementHotkeyService on F5. Refreshes the plugin view shown in the
        // active/topmost window (the single-game View Achievements window, or the Overview as a
        // standalone window or open sidebar view) regardless of which element holds focus.
        // Returns true when a plugin view handled it, so the service can suppress Playnite's own
        // F5 library update.
        private bool TryRefreshActivePluginView()
        {
            var window = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive)
                ?? Application.Current?.MainWindow;
            if (window == null)
            {
                return false;
            }

            // View Achievements is always its own window; the Overview is either its own window
            // or hosted inside Playnite's main window as the sidebar view.
            var singleGame = VisualTreeHelpers.FindVisualChild<ViewAchievementsControl>(window);
            if (singleGame != null && singleGame.IsVisible)
            {
                singleGame.TriggerHotkeyRefresh();
                return true;
            }

            var friendsSingleGame = VisualTreeHelpers.FindVisualChild<ViewFriendsAchievementsControl>(window);
            if (friendsSingleGame != null && friendsSingleGame.IsVisible)
            {
                friendsSingleGame.TriggerHotkeyRefresh();
                return true;
            }

            var overview = VisualTreeHelpers.FindVisualChild<OverviewControl>(window);
            if (overview != null && overview.IsVisible)
            {
                overview.TriggerHotkeyRefresh();
                return true;
            }

            return false;
        }

        // Invoked by AchievementHotkeyService on the category-mode hotkey. Flips category mode on
        // an eligible achievement grid hosted by the active/topmost window (a plugin window, or
        // the Overview sidebar view inside Playnite's main window) regardless of which element
        // holds focus. A grid whose keyboard focus scope contains the caret wins over the others;
        // otherwise the first eligible grid in visual order is used. Returns true when a grid
        // flipped, so the service can mark the key handled; unrelated windows report false and
        // the key passes through.
        private bool TryFlipCategoryModeInActiveView()
        {
            var window = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive)
                ?? Application.Current?.MainWindow;
            if (window == null)
            {
                return false;
            }

            var grids = VisualTreeHelpers.FindVisualChildren<Views.Controls.AchievementDataGridControl>(window)
                .OrderByDescending(grid => grid.IsKeyboardFocusWithin);
            foreach (var grid in grids)
            {
                if (grid.TryFlipCategoryModeFromHotkey())
                {
                    return true;
                }
            }

            return false;
        }

        public PlayniteAchievementsPlugin(IPlayniteAPI api) : base(api)
        {
            // Initialize logging system first
            PluginLogger.Initialize(GetPluginUserDataPath());
            _logger = PluginLogger.GetLogger(nameof(PlayniteAchievementsPlugin));
            _themeControlRegistry = new ThemeControlRegistry();
            _resourceService = new AchievementResourceService(_logger);

            using (PerfScope.StartStartup(_logger, "PluginCtor.Total", thresholdMs: 50))
            {
                Properties = _pluginProperties;

                Instance = this;
                _logger.Info("PlayniteAchievementsPlugin initializing...");

                // Phase 1: Load settings and chart plumbing used by theme controls.
                using (PerfScope.StartStartup(_logger, "PluginCtor.SettingsLoad", thresholdMs: 50))
                {
                    _settingsViewModel = new PlayniteAchievementsSettingsViewModel(this);
                }

                AchievementRarityResolver.RoundDisplayPercentages =
                    _settingsViewModel?.Settings?.Persisted?.RoundRarityPercentages ?? false;

                FormattingCulture.Initialize(() => _settingsViewModel?.Settings?.Persisted?.GlobalLanguage);

                // NECESSARY TO MAKE SURE CHARTS WORK
                var Circle = LiveCharts.Wpf.DefaultGeometries.Circle;
                var panel = new WpfToolkit.Controls.VirtualizingWrapPanel();
                // NECESSARY DO NOT REMOVE

                // Configure LiveCharts mapper for PieSliceChartData so the tooltip can bind to custom data
                var pieSliceMapper = Mappers.Pie<PieSliceChartData>()
                    .Value(data => data.ChartValue);
                Charting.For<PieSliceChartData>(pieSliceMapper);

                var settings = _settingsViewModel.Settings;
                var pluginUserDataPath = GetPluginUserDataPath();
                _manualSourceRegistry = new ManualSourceRegistry(_logger, settings, PlayniteApi, pluginUserDataPath);

                // Create provider registry
                _providerRegistry = new ProviderRegistry(settings, ProviderDisplayOrder, _logger, _manualSourceRegistry);
                _providerRegistry.SyncFromSettings(settings.Persisted);
                settings.Persisted?.MigrateLegacyProviderFriends();
                _gameCustomDataStore = _settingsViewModel.GameCustomDataStore;
                _gameCustomDataStore.AttachRuntimeSettings(settings);
                TryWarmCustomDataCache();

                List<IDataProvider> providers;
                using (PerfScope.StartStartup(_logger, "PluginCtor.ProviderCreation", thresholdMs: 50))
                {
                    providers = _providerRegistry.CreateProviders(settings, PlayniteApi, pluginUserDataPath);
                }

                // Phase 3: Wire core services, refresh pipeline, and tagging.
                using (PerfScope.StartStartup(_logger, "PluginCtor.RefreshServiceCreation", thresholdMs: 50))
                {
                    _diskImageService = new DiskImageService(_logger, pluginUserDataPath);
                    CategoryDefaultImageResolver.DiskImageServiceAccessor = () => _diskImageService;
                    _managedCustomIconService = new ManagedCustomIconService(_diskImageService, _logger);
                    GameSummaryArtResolver.ManagedCustomIconServiceAccessor = () => _managedCustomIconService;
                    _imageService = new MemoryImageService(_logger, _diskImageService);
                    _gameCustomDataStore.AttachManagedCustomIconService(_managedCustomIconService);

                    _refreshService = new RefreshRuntime(api, settings, _logger, this, providers, _diskImageService, _managedCustomIconService, _providerRegistry, ProviderRefreshOrder, onRefreshCompleted: payload => HandleRefreshAuthNotifications(payload));
                    _cacheManager = _refreshService.Cache;
                    _friendCacheManager = _cacheManager as Services.Friends.IFriendCacheManager;
                    _friendsOverviewDataCoordinator = new FriendsOverviewDataCoordinator(
                        _friendCacheManager,
                        () => _settingsViewModel?.Settings?.Persisted,
                        _logger);
                    _friendGameAchievementsDataCoordinator = new FriendGameAchievementsDataCoordinator(
                        _friendCacheManager,
                        () => _settingsViewModel?.Settings?.Persisted,
                        _logger);
                    _friendsRecentUnlocksDataCoordinator = new FriendsRecentUnlocksDataCoordinator(
                        _friendCacheManager,
                        _friendsOverviewDataCoordinator,
                        () => _settingsViewModel?.Settings?.Persisted,
                        _logger);
                    if (_friendCacheManager != null)
                    {
                        _friendCacheManager.FriendCacheInvalidated += FriendCacheManager_FriendCacheInvalidated;
                        _eventSubscriptions.Add(() => _friendCacheManager.FriendCacheInvalidated -= FriendCacheManager_FriendCacheInvalidated);
                    }

                    _cacheManager.CacheInvalidated += (_, __) =>
                    {
                        InvalidateStartPageData();
                        InvalidateFriendDataCoordinators();
                    };
                    // Bitmap eviction is scoped instead of wholesale: normal refreshes never
                    // rewrite icon files in place (in-place overwrites are handled by the
                    // DiskImageService.ImageFileOverwritten hook inside MemoryImageService), so
                    // only true resets wipe the memory cache and removals evict per game.
                    _cacheManager.CacheDeltaUpdated += (_, args) =>
                    {
                        try
                        {
                            switch (args?.OperationType)
                            {
                                case CacheDeltaOperationType.FullReset:
                                    _imageService?.Clear();
                                    break;
                                case CacheDeltaOperationType.Remove:
                                    _imageService?.EvictByUriSegment(args.Key);
                                    break;
                            }
                        }
                        catch { }
                    };
                    _achievementOverridesService = new AchievementOverridesService(
                        _gameCustomDataStore,
                        _cacheManager,
                        _logger);
                    _achievementDataService = new AchievementDataService(
                        _cacheManager,
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        _logger,
                        _gameCustomDataStore);
                    _libraryProjectionService = new LibraryProjectionService(
                        _achievementDataService,
                        providers,
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        _cacheManager,
                        _gameCustomDataStore,
                        _logger,
                        isRefreshActive: () => _refreshService?.IsRebuilding == true);
                    _gameCustomDataStore.AttachAchievementDataService(_achievementDataService);

                    // Reconcile the cache DB's AchievementFilters mirror against custom data
                    // before any summary read (theme wiring, projection warm). Synchronous and
                    // cheap when unchanged; also covers legacy custom-data migrations that
                    // bypass CustomDataChanged.
                    using (PerfScope.StartStartup(_logger, "PluginCtor.AchievementFilterResync", thresholdMs: 50))
                    {
                        _achievementDataService.SyncAllAchievementFiltersFromCustomData();
                    }

                    _notifications = new NotificationPublisher(api, settings, _logger);
                    _refreshCoordinator = new RefreshEntryPoint(
                        _refreshService,
                        _logger,
                        runWithProgressWindow: ShowRefreshProgressControlAndRun);
                    _windowTracker = new ActiveGameWindowTracker(_logger);
                    _toastNotifications = new ToastNotificationService(
                        PlayniteApi,
                        settings,
                        _logger,
                        () => _resourceService.EnsureAchievementResourcesLoaded(_settingsViewModel.Settings),
                        GetProcessIdForGame,
                        _windowTracker);
                    _unlockRecordings = new Services.Recording.UnlockRecordingService(
                        PlayniteApi,
                        settings,
                        _logger,
                        pluginUserDataPath,
                        GetProcessIdForGame,
                        _toastNotifications,
                        key => Services.UI.ProviderNotificationPolicy.Resolve(settings?.Persisted, key).Recordings,
                        _windowTracker);
                    _inGamePoller = new InGameAchievementPoller(
                        PlayniteApi,
                        settings,
                        _logger,
                        _cacheManager,
                        _refreshService,
                        providers,
                        (request, policy) => _refreshCoordinator.ExecuteAsync(request, policy),
                        NotifyAchievementUnlocked);
                    _backgroundUpdates = new BackgroundUpdater(_refreshCoordinator, _refreshService, _cacheManager, settings, _logger, _notifications, null);

                    // Create tag sync service
                    _tagSyncService = new TagSyncService(
                        PlayniteApi,
                        _logger,
                        settings.Persisted,
                        GetPluginLocalizationDirectory());
                    _tagSyncService.InitializeAndSubscribeTaggingSettings();

                    _fullscreenControllerNavigationService = new FullscreenControllerNavigationService(
                        PlayniteApi,
                        _logger);

                    _windowService = new PluginWindowService(
                        PlayniteApi,
                        _logger,
                        _refreshService,
                        _refreshCoordinator,
                        _cacheManager,
                        PersistSettingsForUi,
                        _achievementOverridesService,
                        _achievementDataService,
                        _libraryProjectionService,
                        _gameCustomDataStore,
                        _settingsViewModel.Settings,
                        _manualSourceRegistry,
                        () => _resourceService.EnsureAchievementResourcesLoaded(_settingsViewModel.Settings),
                        _fullscreenControllerNavigationService,
                        _friendsOverviewDataCoordinator,
                        _friendGameAchievementsDataCoordinator);

                    _achievementHotkeyTargetResolver = new AchievementHotkeyTargetResolver(PlayniteApi, _logger);
                    _achievementHotkeyService = new AchievementHotkeyService(
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        _achievementHotkeyTargetResolver,
                        _logger,
                        gameId => _windowService.ToggleViewAchievementsWindowFromHotkey(gameId),
                        gameId => _windowService.ToggleManageAchievementsViewFromHotkey(gameId),
                        ToggleOverviewWindowFromHotkey,
                        () => OpenSettingsView(),
                        TryFlipCategoryModeInActiveView,
                        TryRefreshActivePluginView);

                    _themeAutoMigrationService = new ThemeAutoMigrationService(
                        _logger,
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        () => SavePluginSettings(_settingsViewModel.Settings),
                        themeName => _notifications?.ShowThemeAutoMigrated(themeName));

                    SubscribePluginEventHandlers();
                }

                // Phase 4: Connect theme runtime services and custom controls.
                using (PerfScope.StartStartup(_logger, "PluginCtor.ThemeServicesWiring", thresholdMs: 50))
                {
                    Action<Guid?> requestUpdate = (id) => _themeIntegrationService?.RequestUpdate(id);

                    _fullscreenWindowService = new FullscreenWindowService(
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        requestUpdate);

                    _themeIntegrationService = new ThemeIntegrationService(
                        PlayniteApi,
                        _refreshService,
                        _achievementDataService,
                        _libraryProjectionService,
                        _refreshCoordinator,
                        _settingsViewModel.Settings,
                        _fullscreenWindowService,
                        _logger,
                        _windowService.RunRefreshWithGlobalProgressAsync,
                        gameId => _windowService.OpenManageAchievementsView(gameId, ManageAchievementsTab.Overview),
                        _cacheManager as Services.Friends.IFriendCacheManager,
                        _friendsOverviewDataCoordinator,
                        _achievementHotkeyTargetResolver.ResolveRunningGame);

                    // A friend-consuming theme is a plugin-lifetime consumer: it keeps the
                    // friends snapshot alive when the last friends view closes.
                    _friendsOverviewDataCoordinator?.SetExternalConsumerProbe(
                        () => _themeIntegrationService?.HasFriendThemeConsumers == true);
                    if (_friendsOverviewDataCoordinator != null)
                    {
                        _friendsOverviewDataCoordinator.SnapshotReleased += FriendsOverviewDataCoordinator_SnapshotReleased;
                        _eventSubscriptions.Add(() =>
                            _friendsOverviewDataCoordinator.SnapshotReleased -= FriendsOverviewDataCoordinator_SnapshotReleased);
                    }

                    SubscribeDatabaseEventHandlers();

                    AddSettingsSupport(new AddSettingsSupportArgs
                    {
                        SourceName = "PlayniteAchievements",
                        SettingsRoot = "Settings"
                    });

                    AddCustomElementSupport(new AddCustomElementSupportArgs
                    {
                        ElementList = _themeControlRegistry.GetSupportedElementNames(),
                        SourceName = "PlayniteAchievements"
                    });
                }

                // Initialize top panel item for popout window
                _topPanelItem = new PlayniteAchievementsTopPanelItem(
                    OpenOverviewWindow);

                _logger.Info("PlayniteAchievementsPlugin initialized.");
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            _logger.Info($"GetSettings called, firstRunSettings={firstRunSettings}");
            return _settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunView)
        {
            try
            {
                _logger.Info($"GetSettingsView called, firstRunView={firstRunView}");
                var control = new SettingsControl(
                    _settingsViewModel,
                    _logger,
                    this,
                    _providerRegistry,
                    (owner, currentValue) => _windowService?.PickColor(owner, currentValue));
                _logger.Info("GetSettingsView succeeded");
                return control;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GetSettingsView failed");
                throw;
            }
        }

        // === Overview ===

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                Type = SiderbarItemType.View,
                Icon = GetOverviewIcon(),
                Opened = () =>
                {
                    return new OverviewHostControl(
                        () => new OverviewControl(PlayniteApi, _logger, _refreshService, _cacheManager, PersistSettingsForUi, _achievementOverridesService, _achievementDataService, _libraryProjectionService, _gameCustomDataStore, _refreshCoordinator, _settingsViewModel.Settings, OverviewLaunchContext.Sidebar, _friendsOverviewDataCoordinator),
                        _logger,
                        PlayniteApi,
                        _refreshService,
                        this);
                }
            };

        }

        private FrameworkElement GetOverviewIcon()
        {
            return BrandIconFactory.CreateTrophyIcon(18);
        }

        // === Top Panel ===

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            if (_settingsViewModel?.Settings?.Persisted?.ShowTopMenuBarButton == true)
            {
                yield return _topPanelItem;
            }
        }

        /// <summary>
        /// Resolves the started process id for a specific running game, or for the most recently
        /// started still-running game when <paramref name="gameId"/> is null or empty.
        /// </summary>
        private int? GetProcessIdForGame(Guid? gameId)
        {
            if (gameId.HasValue && gameId.Value != Guid.Empty)
            {
                // The tracker's learned pid (the process owning the game's foreground window)
                // beats the started pid, which is a dead bootstrapper for launcher-wrapped games.
                var learnedPid = _windowTracker?.TryGetProcessId(gameId.Value);
                if (learnedPid.HasValue)
                {
                    return learnedPid;
                }

                lock (_runningGamesLock)
                {
                    return _startedProcessIds.TryGetValue(gameId.Value, out var pid) ? pid : null;
                }
            }

            lock (_runningGamesLock)
            {
                return _runningGameOrder.Count > 0 &&
                       _startedProcessIds.TryGetValue(_runningGameOrder[0], out var newestPid)
                    ? newestPid
                    : null;
            }
        }

        private void TrackStartedGame(Game game, int? processId)
        {
            if (game == null)
            {
                return;
            }

            lock (_runningGamesLock)
            {
                _runningGameOrder.Remove(game.Id);
                _runningGameOrder.Insert(0, game.Id);
                _startedProcessIds[game.Id] = processId;
            }
        }

        private void UntrackStoppedGame(Game game)
        {
            if (game == null)
            {
                return;
            }

            lock (_runningGamesLock)
            {
                _runningGameOrder.Remove(game.Id);
                _startedProcessIds.Remove(game.Id);
            }
        }

        private bool AnyGameRunning()
        {
            lock (_runningGamesLock)
            {
                return _runningGameOrder.Count > 0;
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            try
            {
                TrackStartedGame(args?.Game, args?.StartedProcessId);
                _windowTracker?.OnGameStarted(args?.Game, args?.StartedProcessId);
                _libraryProjectionService?.SetGameSessionActive(true);
                _achievementHotkeyTargetResolver?.NotifyGameStarted(args?.Game);
                _inGamePoller?.Start(args?.Game);
                _unlockRecordings?.OnGameStarted(args?.Game);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to track started game for achievement hotkeys or in-game polling.");
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            try
            {
                UntrackStoppedGame(args?.Game);
                if (args?.Game != null)
                {
                    _windowTracker?.OnGameStopped(args.Game.Id);
                }

                _libraryProjectionService?.SetGameSessionActive(AnyGameRunning());
                if (args?.Game != null)
                {
                    _toastNotifications?.ClearPending(args.Game.Id);
                    _unlockRecordings?.OnGameStopped(args.Game, ResolveRecordingHandoffGame());
                }
                else
                {
                    _toastNotifications?.ClearPending();
                    _unlockRecordings?.OnGameStopped(null);
                }

                _achievementHotkeyTargetResolver?.NotifyGameStopped(args?.Game);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to track stopped game for achievement hotkeys.");
            }

            _ = StopPollingAndRefreshStoppedGameAsync(args?.Game);
        }

        /// <summary>
        /// The recording handoff target when the capture-owning game stops: the still-running
        /// game the user last had in the foreground, else the most recently started still-running
        /// game (per the tracked start order).
        /// </summary>
        private Game ResolveRecordingHandoffGame()
        {
            List<Guid> order;
            lock (_runningGamesLock)
            {
                order = _runningGameOrder.ToList();
            }

            var stableForeground = _windowTracker?.StableForegroundGameId;
            if (stableForeground.HasValue && order.Contains(stableForeground.Value))
            {
                var foregroundGame = PlayniteApi?.Database?.Games?.Get(stableForeground.Value);
                if (foregroundGame != null)
                {
                    return foregroundGame;
                }
            }

            foreach (var gameId in order)
            {
                var game = PlayniteApi?.Database?.Games?.Get(gameId);
                if (game != null)
                {
                    return game;
                }
            }

            return null;
        }

        private async Task StopPollingAndRefreshStoppedGameAsync(Game game)
        {
            if (game == null)
            {
                return;
            }

            try
            {
                _inGamePoller?.Stop(game);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to stop in-game poller for {game.Name}.");
            }

            // The game-close refresh runs a Single request, which bypasses user exclusions so a
            // manual single-game refresh can force an excluded game. Automatic refreshes must
            // honor the exclusion, so gate this path explicitly.
            var excludedGameIds = GameCustomDataLookup.GetExcludedRefreshGameIds(
                _settingsViewModel?.Settings?.Persisted,
                _gameCustomDataStore);
            if (excludedGameIds?.Contains(game.Id) == true)
            {
                _logger.Info($"Game stopped: {game.Name}; excluded from refreshes, skipping refresh.");
                // No refresh delta will arrive to rebuild the projection after the session's
                // suppressed warms; schedule it explicitly (no-op while other games still run).
                _libraryProjectionService?.Warm();
                return;
            }

            if (!AnyProviderCapable(game))
            {
                _logger.Info($"Game stopped: {game.Name}; no enabled provider is capable, skipping refresh.");
                // No refresh delta will arrive to rebuild the projection after the session's
                // suppressed warms; schedule it explicitly (no-op while other games still run).
                _libraryProjectionService?.Warm();
                return;
            }

            // With other games still running the poller may have a tick in flight; wait for it
            // (bounded) because RefreshRuntime rejects concurrent runs rather than queueing them —
            // a blind execute would silently drop this game's final refresh.
            for (var waited = 0; waited < 60 && _refreshService?.IsRebuilding == true; waited += 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            _logger.Info($"Game stopped: {game.Name}. Triggering refresh.");
            await _refreshCoordinator.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Single,
                SingleGameId = game.Id
            }).ConfigureAwait(false);
        }

        private bool AnyProviderCapable(Game game)
        {
            var providers = Providers;
            if (providers == null)
            {
                return false;
            }

            foreach (var provider in providers)
            {
                try
                {
                    if (provider?.IsCapable(game) == true)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Provider capability check failed for {provider?.ProviderKey}.");
                }
            }

            return false;
        }

        // === Lifecycle ===

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            using (PerfScope.StartStartup(_logger, "OnApplicationStarted", thresholdMs: 50))
            {
                _applicationStarted = true;

                // Warm the overview/start-page projection now that the game library is loaded, so
                // resolved game presentation (cover, icon, playtime, last played, metadata) reflects
                // Playnite's populated database rather than the blank values an early startup warm
                // would bake in.
                _libraryProjectionService?.Warm();
                // The friends overview snapshot is intentionally NOT warmed here: it is built
                // on demand by the first consumer (friends view or a theme friend binding) and
                // released when the last consumer detaches, so it only occupies memory while
                // something displays it.

                var dispatcher = PlayniteApi?.MainView?.UIDispatcher
                    ?? System.Windows.Application.Current?.Dispatcher;

                if (dispatcher != null)
                {
                    try
                    {
                        var selectedGames = PlayniteApi?.MainView?.SelectedGames?
                            .Where(g => g != null)
                            .Take(2)
                            .ToList();

                        if (selectedGames?.Count == 1)
                        {
                            var game = selectedGames[0];
                            _logger.Debug($"Requesting initial theme data for selected game: {game.Name}");
                            _settingsViewModel.Settings.SetSelectedGame(game);
                            _themeIntegrationService?.RequestUpdate(game.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Failed to populate initial theme data.");
                    }
                }
                else
                {
                    _logger.Warn("Could not obtain UI dispatcher; theme integration updates disabled");
                }

                try
                {
                    EnsureAchievementResourcesLoaded();
                    new AchievementToastTemplateResolver(PlayniteApi, _logger)
                        .LogActiveThemeOverrideDiagnostics("Startup");
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to log achievement toast theme override diagnostics.");
                }

                _achievementHotkeyService?.Start();

                // Re-localize un-customized default tag names to the current Playnite
                // language; needs the database, which is only open from this point on.
                try
                {
                    if (_tagSyncService?.RelocalizeDefaultTagNames() == true)
                    {
                        PersistSettingsForUi();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to re-localize default tag names.");
                }

                // Auto-migrate themes that have been updated since the last migration.
                _themeAutoMigrationService?.ScheduleAutoMigration();

                RestartBackgroundUpdater();
            }
        }

        private void PersistedSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(PersistedSettings.EnablePeriodicUpdates) ||
                e.PropertyName == nameof(PersistedSettings.PeriodicUpdateHours) ||
                e.PropertyName == nameof(PersistedSettings.EnableFriendsPeriodicUpdates) ||
                e.PropertyName == nameof(PersistedSettings.FriendsPeriodicUpdateHours) ||
                e.PropertyName == nameof(PersistedSettings.EnableFriendsFeatures))
            {
                RestartBackgroundUpdater();
            }

            if (e.PropertyName == nameof(PersistedSettings.EnableInGamePolling) ||
                e.PropertyName == nameof(PersistedSettings.InGamePollIntervalSeconds) ||
                e.PropertyName == nameof(PersistedSettings.InGamePollRefreshFriends) ||
                e.PropertyName == nameof(PersistedSettings.InGameFriendRefreshMultiplier) ||
                e.PropertyName == nameof(PersistedSettings.InGameFriendBatchSize))
            {
                RestartInGamePollerForRunningGames();
            }

            if (e.PropertyName == nameof(PersistedSettings.UseUniformRarityBadges) ||
                e.PropertyName == nameof(PersistedSettings.RarityColors))
            {
                RarityAppearanceHelper.ApplyBadgeApplicationResources(
                    _settingsViewModel?.Settings?.Persisted);
            }

            if (e.PropertyName == nameof(PersistedSettings.RoundRarityPercentages))
            {
                AchievementRarityResolver.RoundDisplayPercentages =
                    _settingsViewModel?.Settings?.Persisted?.RoundRarityPercentages ?? false;
            }

            if (e.PropertyName == nameof(PersistedSettings.GlobalLanguage))
            {
                FormattingCulture.Refresh();
            }

            if (e.PropertyName == nameof(PersistedSettings.EnableAchievementHotkeys) ||
                e.PropertyName == nameof(PersistedSettings.EnableGlobalAchievementHotkeys) ||
                e.PropertyName == nameof(PersistedSettings.ViewAchievementsHotkey) ||
                e.PropertyName == nameof(PersistedSettings.ManageAchievementsHotkey) ||
                e.PropertyName == nameof(PersistedSettings.OverviewHotkey))
            {
                _achievementHotkeyService?.RefreshConfiguration();
            }

            if (ShouldInvalidateFriendDataForSetting(e.PropertyName))
            {
                InvalidateFriendDataCoordinators();
            }

            InvalidateStartPageData();
            _tagSyncService?.HandlePersistedSettingsPropertyChanged(e);
        }

        private void FriendCacheManager_FriendCacheInvalidated(object sender, FriendCacheInvalidatedEventArgs e)
        {
            InvalidateFriendDataCoordinators(e);
        }

        // The derived coordinators cache slices whose Projection references the released
        // snapshot's projection; invalidating them lets the full friend row set become
        // collectable. Their next builds fall back to the cheap scoped DB loads.
        private void FriendsOverviewDataCoordinator_SnapshotReleased(object sender, EventArgs e)
        {
            _friendGameAchievementsDataCoordinator?.Invalidate();
            _friendsRecentUnlocksDataCoordinator?.Invalidate();
        }

        // Settings-driven callers pass no args (projection-affecting settings need a full
        // rebuild); cache-driven callers pass the change scope through so the overview
        // coordinator can patch instead of reloading everything.
        private void InvalidateFriendDataCoordinators(FriendCacheInvalidatedEventArgs args = null)
        {
            _friendsOverviewDataCoordinator?.Invalidate(args);
            _friendGameAchievementsDataCoordinator?.Invalidate();
            _friendsRecentUnlocksDataCoordinator?.Invalidate();
        }

        private static bool ShouldInvalidateFriendDataForSetting(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.ShowFriendSpoilers) ||
                   propertyName == nameof(PersistedSettings.Friends) ||
                   propertyName == nameof(PersistedSettings.FriendMergeGroups) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenIcon) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenTitle) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenDescription) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenSuffix) ||
                   propertyName == nameof(PersistedSettings.ShowLockedIcon) ||
                   propertyName == nameof(PersistedSettings.UseSeparateLockedIconsWhenAvailable) ||
                   propertyName == nameof(PersistedSettings.SeparateLockedIconEnabledGameIds);
        }

        private void RestartBackgroundUpdater()
        {
            try
            {
                _backgroundUpdates?.Stop();
                if (_applicationStarted)
                {
                    _backgroundUpdates?.Start();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to restart background updater.");
            }
        }

        private void RestartInGamePollerForRunningGames()
        {
            try
            {
                var running = _inGamePoller?.RunningGames?.ToList() ?? new List<Game>();
                if (running.Count == 0)
                {
                    running = PlayniteApi?.Database?.Games?
                        .Where(game => game?.IsRunning == true)
                        .OrderByDescending(game => game.LastActivity)
                        .ToList() ?? new List<Game>();
                }

                _inGamePoller?.StopAll();
                if (_applicationStarted)
                {
                    foreach (var game in running)
                    {
                        _inGamePoller?.Start(game);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to restart in-game achievement poller.");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            _logger.Info("OnApplicationStopped called.");
            _applicationStarted = false;
            // Stop startup init if still running
            try
            {
                _eventSubscriptions.DisposeAll();

            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error during application shutdown cleanup.");
            }

            _backgroundUpdates.Stop();
            try { _inGamePoller?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose inGamePoller"); }
            try { _toastNotifications?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose toastNotifications"); }
            try { _unlockRecordings?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose unlockRecordings"); }
            try { _windowTracker?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose windowTracker"); }

            try { _achievementHotkeyService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose achievementHotkeyService"); }
            try { _windowService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose windowService"); }
            try { _libraryProjectionService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose libraryProjectionService"); }
            try { _imageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose imageService"); }
            try { _diskImageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose diskImageService"); }
            try { _manualSourceRegistry?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose manualSourceRegistry"); }
            try { _fullscreenControllerNavigationService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose fullscreenControllerNavigationService"); }
            try { _fullscreenWindowService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose fullscreenWindowService"); }
            try { _themeIntegrationService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose themeIntegrationService"); }
            try { _friendsRecentUnlocksDataCoordinator?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose friendsRecentUnlocksDataCoordinator"); }
            try { _friendGameAchievementsDataCoordinator?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose friendGameAchievementsDataCoordinator"); }
            try { _friendsOverviewDataCoordinator?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose friendsOverviewDataCoordinator"); }
            DisposeStartPageViews();

            // Shutdown logging system
            try { PluginLogger.Shutdown(); } catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Failed to shutdown logger: {ex}"); }
        }

        // === Theme Integration ===

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            try
            {
                if (args.NewValue?.Count == 1)
                {
                    var game = args.NewValue[0];
                    if (game == null)
                    {
                        _themeIntegrationService?.RequestUpdate(null);
                        _themeIntegrationService?.NotifySelectionChanged(null);
                        _themeIntegrationService?.ClearSingleGameThemeProperties();
                        _settingsViewModel.Settings.SetSelectedGame(null);
                        return;
                    }

                    _themeIntegrationService?.NotifySelectionChanged(game.Id);
                    _settingsViewModel.Settings.SetSelectedGame(game);
                    _themeIntegrationService?.RequestUpdate(game.Id);
                }
                else
                {
                    // Clear theme data when no game or multiple games selected
                    _themeIntegrationService?.RequestUpdate(null);
                    _themeIntegrationService?.NotifySelectionChanged(null);
                    _themeIntegrationService?.ClearSingleGameThemeProperties();
                    _settingsViewModel.Settings.SetSelectedGame(null);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in OnGameSelected");
            }
        }

        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            try
            {
                if (_fullscreenControllerNavigationService?.TryHandleControllerButtonStateChanged(args) == true)
                {
                    return;
                }

                base.OnControllerButtonStateChanged(args);

                if (args?.Button == ControllerInput.B && args.State == ControllerInputState.Pressed)
                {
                    _fullscreenWindowService?.HandleControllerBackPressed();
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to handle controller button state change for fullscreen overlay.");
            }
        }

        internal void RequestThemeUpdate(Game gameContext)
        {
            _themeIntegrationService?.RequestUpdate(gameContext?.Id);
        }

        private void SubscribeDatabaseEventHandlers()
        {
            // Listen for game database changes to auto-refresh new entries and clean up removed games.
            PlayniteApi?.Database?.Games?.ItemCollectionChanged += Games_ItemCollectionChanged;
            _eventSubscriptions.Add(() => PlayniteApi?.Database?.Games?.ItemCollectionChanged -= Games_ItemCollectionChanged);

            // Invalidate the cached library projection when a game's Playnite-owned fields change
            // (playtime, last played, cover, icon, metadata) so the overview/start page rebuild
            // against fresh values instead of serving a stale cached snapshot.
            PlayniteApi?.Database?.Games?.ItemUpdated += Games_ItemUpdated;
            _eventSubscriptions.Add(() => PlayniteApi?.Database?.Games?.ItemUpdated -= Games_ItemUpdated);
        }

        private void SubscribePluginEventHandlers()
        {
            _refreshService.GameRefreshed += OnAchievementGameRefreshed;
            _eventSubscriptions.Add(() => _refreshService.GameRefreshed -= OnAchievementGameRefreshed);

            try
            {
                if (_gameCustomDataStore != null)
                {
                    _gameCustomDataStore.CustomDataChanged += GameCustomDataStore_CustomDataChanged;
                    _eventSubscriptions.Add(() =>
                    {
                        _gameCustomDataStore.CustomDataChanged -= GameCustomDataStore_CustomDataChanged;
                        lock (_customDataChangeSync)
                        {
                            _customDataChangeTimer?.Dispose();
                            _customDataChangeTimer = null;
                            _pendingCustomDataChangeIds.Clear();
                        }
                    });
                }

                var persisted = _settingsViewModel?.Settings?.Persisted;
                if (persisted != null)
                {
                    persisted.PropertyChanged += PersistedSettings_PropertyChanged;
                    _eventSubscriptions.Add(() => persisted.PropertyChanged -= PersistedSettings_PropertyChanged);
                }

                _eventSubscriptions.Add(() => _tagSyncService?.DetachTaggingSettingsSubscription());
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to subscribe to persisted settings changes.");
            }
        }

        // Coalesces bursts of custom-data changes (e.g. checkbox toggles in the Manage
        // Achievements window) so tag sync, theme rebuilds, and start-page invalidation
        // run once per burst instead of once per edit. Cache invalidation subscribers
        // stay on the synchronous CustomDataChanged event; only these side effects are
        // deferred.
        private static readonly TimeSpan CustomDataChangeCoalesceDelay = TimeSpan.FromMilliseconds(400);
        private readonly object _customDataChangeSync = new object();
        private readonly HashSet<Guid> _pendingCustomDataChangeIds = new HashSet<Guid>();
        private Timer _customDataChangeTimer;

        private void GameCustomDataStore_CustomDataChanged(object sender, GameCustomDataChangedEventArgs e)
        {
            if (e == null || e.PlayniteGameId == Guid.Empty)
            {
                return;
            }

            lock (_customDataChangeSync)
            {
                _pendingCustomDataChangeIds.Add(e.PlayniteGameId);
                if (_customDataChangeTimer == null)
                {
                    _customDataChangeTimer = new Timer(
                        _ => FlushPendingCustomDataChanges(),
                        null,
                        CustomDataChangeCoalesceDelay,
                        Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _customDataChangeTimer.Change(CustomDataChangeCoalesceDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void FlushPendingCustomDataChanges()
        {
            List<Guid> gameIds;
            lock (_customDataChangeSync)
            {
                gameIds = _pendingCustomDataChangeIds.ToList();
                _pendingCustomDataChangeIds.Clear();
            }

            foreach (var gameId in gameIds)
            {
                HandleCustomDataChanged(gameId);
            }
        }

        private void HandleCustomDataChanged(Guid gameId)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (_tagSyncService != null && persisted?.TaggingSettings?.EnableTagging == true)
            {
                QueueTagSync(gameId);
            }

            try
            {
                _themeIntegrationService?.NotifyCustomDataChanged(gameId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to refresh theme state after custom-data change for gameId={gameId}.");
            }

            InvalidateStartPageData();
        }

        private void QueueTagSync(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            var tagSyncService = _tagSyncService;
            if (tagSyncService == null)
            {
                return;
            }

            // Accumulate ids and let a single drainer sync them in batches. Ids that
            // arrive while a batch is running (e.g. per-game refresh events during a
            // large scan) are picked up by the next batch, so the Playnite database
            // sees a few batched writes instead of one write per game.
            lock (_tagSyncGate)
            {
                _pendingTagSyncIds.Add(gameId);
                if (_tagSyncDrainRunning)
                {
                    return;
                }

                _tagSyncDrainRunning = true;
            }

            _ = Task.Run(() => DrainPendingTagSyncs(tagSyncService));
        }

        private void DrainPendingTagSyncs(TagSyncService tagSyncService)
        {
            while (true)
            {
                List<Guid> batch;
                lock (_tagSyncGate)
                {
                    if (_pendingTagSyncIds.Count == 0)
                    {
                        _tagSyncDrainRunning = false;
                        return;
                    }

                    batch = _pendingTagSyncIds.ToList();
                    _pendingTagSyncIds.Clear();
                }

                try
                {
                    tagSyncService.SyncTagsForGames(batch);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed queued tag sync for {batch.Count} game(s).");
                }
            }
        }

        private void OnAchievementGameRefreshed(Guid gameId)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (_tagSyncService != null && persisted?.TaggingSettings?.EnableTagging == true)
            {
                // Queued off-thread into the batched tag-sync drainer so the Playnite DB
                // write (and its ItemUpdated fan-out) does not run inside the provider's
                // per-game refresh loop.
                QueueTagSync(gameId);
            }

            InvalidateStartPageData();
        }

        private void HandleRefreshAuthNotifications(RebuildPayload payload)
        {
            if (payload == null)
                return;

            if (payload.FailedProviderKeys?.Count > 0)
            {
                _notifications?.ShowProviderAuthFailed(payload.FailedProviderKeys);
            }
            else if (!payload.AuthRequired)
            {
                _notifications?.ClearAllProviderAuthNotifications();
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            EnsureAchievementResourcesLoaded();
            return _themeControlRegistry.TryCreate(args.Name, out var control) ? control : null;
        }

        // === Game selection wiring ===

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            _logger.Info("Games_ItemCollectionChanged triggered.");

            // Games added/removed change what the overview and start page project; drop the cached
            // library projection so the next open rebuilds against the current library.
            _libraryProjectionService?.Invalidate();

            if (e == null)
            {
                return;
            }

            var addedItems = e.AddedItems;
            if (addedItems != null)
            {
                var addedGameIds = new List<Guid>();
                foreach (var game in addedItems)
                {
                    if (game == null)
                    {
                        continue;
                    }

                    addedGameIds.Add(game.Id);
                }

                if (addedGameIds.Count > 0)
                {
                    _ = TriggerNewGamesRefreshAsync(addedGameIds);
                }
            }

            var removedItems = e.RemovedItems;
            if (removedItems != null)
            {
                foreach (var game in removedItems)
                {
                    if (game == null)
                    {
                        continue;
                    }

                    _ = TriggerRemovedGameCleanupAsync(game);
                }
            }
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            // A game's Playnite-owned fields (playtime, last played, cover, icon, metadata) changed;
            // invalidate so the cached overview/start-page projection is rebuilt with fresh values.
            // Invalidate() coalesces bursts (e.g. library scans) via its warm debounce.
            _libraryProjectionService?.Invalidate();
        }

        private Task TriggerNewGamesRefreshAsync(List<Guid> gameIds)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var validGameIds = gameIds?
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList() ?? new List<Guid>();

                    // The GameIds path bypasses user exclusions so a manual multi-select menu
                    // refresh can force excluded games; this auto-refresh of newly added games is
                    // not user-initiated, so drop excluded games before requesting the refresh.
                    var excludedGameIds = GameCustomDataLookup.GetExcludedRefreshGameIds(
                        _settingsViewModel?.Settings?.Persisted,
                        _gameCustomDataStore);
                    if (excludedGameIds?.Count > 0)
                    {
                        validGameIds = validGameIds
                            .Where(id => !excludedGameIds.Contains(id))
                            .ToList();
                    }

                    if (validGameIds.Count == 0)
                    {
                        return;
                    }

                    _logger.Info($"Detected {validGameIds.Count} new game(s); starting batched refresh.");
                    await _refreshCoordinator.ExecuteAsync(new RefreshRequest { GameIds = validGameIds }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed batched auto-refresh for newly added games.");
                }
            });
        }

        private Task TriggerRemovedGameCleanupAsync(Game game)
        {
            return Task.Run(() =>
            {
                try
                {
                    _logger.Info($"Detected removed game '{game?.Name}' ({game?.GameId}); removing cached achievements and icons.");
                    _cacheManager.RemoveGameCache(game.Id);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed cleanup for removed game '{game?.Name}' ({game?.GameId}).");
                }
            });
        }
    }
}





