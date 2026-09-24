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
using PlayniteAchievements.Services.Notifications;
using PlayniteAchievements.Services.Showcase;
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
            "Steam", "Epic", "GOG", "BattleNet", "EA", "Ubisoft", "GameJolt", "Riot", "PSN", "Xbox", "GooglePlay", "Apple", "FFXIV", "RetroAchievements", "RPCS3", "ShadPS4", "Xenia", "Manual", "Exophase", "Hoyoverse"
        };

        private static readonly string[] ProviderRefreshOrder =
        {
            "Manual", "FFXIV", "Exophase", "Steam", "Epic", "GOG", "BattleNet", "EA", "GameJolt", "Riot", "GW2", "Hoyoverse", "RPCS3", "ShadPS4", "PSN", "Xenia", "Xbox", "RetroAchievements"
        };

        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly RefreshRuntime _refreshService;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementMarkerToggle _achievementMarkerToggle;
        private readonly AchievementDataService _achievementDataService;
        private readonly LibraryProjectionService _libraryProjectionService;
        private readonly ICacheManager _cacheManager;
        private readonly IFriendCacheManager _friendCacheManager;
        private readonly FriendsOverviewDataCoordinator _friendsOverviewDataCoordinator;
        private readonly FriendGameAchievementsDataCoordinator _friendGameAchievementsDataCoordinator;
        private readonly MemoryImageService _imageService;
        private readonly DiskImageService _diskImageService;
        private readonly RayTrackService _rayTrackService;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly NotificationImageStore _notificationImageStore;
        private readonly FallbackIconStore _fallbackIconStore;
        private NotificationStylePortableStore _notificationStylePortableStore;
        private NotificationStylePresetStore _notificationStylePresetStore;
        private readonly NotificationPublisher _notifications;
        private readonly ProviderRegistry _providerRegistry;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly Services.CustomProviders.CustomProviderStore _customProviderStore;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly SubscriptionCollection _eventSubscriptions = new SubscriptionCollection();

        /// <summary>Last seen answer to "does any widget draw from the Unlock Next pool?".</summary>
        private bool _unlockNextPoolRequired;

        private readonly BackgroundUpdater _backgroundUpdates;
        private readonly InGameAchievementMonitor _inGameMonitor;
        private readonly ActiveGameWindowTracker _windowTracker;
        private readonly Services.Sound.UnlockSoundService _unlockSounds;
        private readonly ToastNotificationService _toastNotifications;
        private readonly Services.Recording.UnlockRecordingService _unlockRecordings;
        private readonly Services.Captures.CaptureLibraryService _captureLibraryService;

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

        /// <summary>
        /// Games whose change can only have moved the Customized tag, so they are reconciled
        /// without the achievement load a full evaluation does.
        /// </summary>
        private readonly HashSet<Guid> _pendingCustomizationTagSyncIds = new HashSet<Guid>();
        private bool _tagSyncDrainRunning;
        private TagSyncService _tagSyncService;
        private AutoCapstoneMaintainer _autoCapstoneMaintainer;

        public override Guid Id { get; } =
            Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");

        public PlayniteAchievementsSettings Settings => _settingsViewModel.Settings;
        public ProviderRegistry ProviderRegistry => _providerRegistry;

        /// <summary>
        /// True while a settings window holds a pending edit snapshot. Editors that write straight
        /// to the live persisted tree suppress their own save while this is true, leaving the
        /// settings window's OK/Cancel to decide.
        /// </summary>
        public bool IsSettingsEditSessionActive => _settingsViewModel?.IsEditSessionActive ?? false;

        /// <summary>The unlock sound service, for the settings page's per-tier table and Test buttons.</summary>
        internal Services.Sound.UnlockSoundService UnlockSounds => _unlockSounds;
        public GameCustomDataStore GameCustomDataStore => _gameCustomDataStore;
        public Services.CustomProviders.CustomProviderStore CustomProviderStore => _customProviderStore;
        public IReadOnlyList<IDataProvider> Providers => _refreshService?.Providers;
        public RefreshRuntime RefreshRuntime => _refreshService;
        public AchievementOverridesService AchievementOverridesService => _achievementOverridesService;
        public AchievementMarkerToggle AchievementMarkerToggle => _achievementMarkerToggle;
        public AchievementDataService AchievementDataService => _achievementDataService;
        public MemoryImageService ImageService => _imageService;
        public DiskImageService DiskImageService => _diskImageService;
        public RayTrackService RayTrackService => _rayTrackService;
        internal Services.Captures.CaptureLibraryService CaptureLibraryService => _captureLibraryService;
        public ManagedCustomIconService ManagedCustomIconService => _managedCustomIconService;
        public ICacheManager CacheManager => _cacheManager;
        public NotificationImageStore NotificationImageStore => _notificationImageStore;
        public FallbackIconStore FallbackIconStore => _fallbackIconStore;
        public NotificationStylePortableStore NotificationStylePortableStore =>
            _notificationStylePortableStore ?? (_notificationStylePortableStore =
                new NotificationStylePortableStore(_notificationImageStore, _logger));
        public NotificationStylePresetStore NotificationStylePresetStore =>
            _notificationStylePresetStore ?? (_notificationStylePresetStore =
                new NotificationStylePresetStore(NotificationStylePortableStore, GetPluginUserDataPath()));
        public ThemeIntegrationService ThemeIntegrationService => _themeIntegrationService;
        public ThemeIntegrationService ThemeUpdateService => _themeIntegrationService;
        public TagSyncService TagSyncService => _tagSyncService;
        internal RefreshEntryPoint RefreshEntryPoint => _refreshCoordinator;
        internal Services.Friends.IFriendCacheManager FriendCacheManager => _friendCacheManager;
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

            // Runs before the dispatcher marshal so the store write stays off the UI thread, and
            // before the subscriber check so it happens whether or not anything is listening.
            ClearGoalForUnlock(args);

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

        /// <summary>
        /// A goal is something still to be earned, so unlocking it retires the goal. Skips the
        /// synthetic notifications (previews, test fires, the game-complete banner) and friend
        /// unlocks, none of which represent the user earning that achievement.
        /// </summary>
        private static void ClearGoalForUnlock(AchievementUnlockedEventArgs args)
        {
            if (args.IsPreview ||
                args.IsTestFire ||
                args.IsFriendUnlock ||
                args.IsGameCompleted ||
                args.PlayniteGameId == Guid.Empty ||
                string.IsNullOrWhiteSpace(args.ApiName))
            {
                return;
            }

            try
            {
                Instance?.AchievementOverridesService?.PruneUnlockedGoals(
                    args.PlayniteGameId,
                    new[] { args.ApiName });
            }
            catch (Exception ex)
            {
                Instance?._logger?.Error(ex, $"Failed clearing goal for unlocked achievement '{args.ApiName}'.");
            }
        }

        private void TryWarmCustomDataCache()
        {
            if (_gameCustomDataStore == null)
            {
                return;
            }

            try
            {
                // Before the warm, so the warm caches the upgraded rows rather than rows this is
                // about to replace. Reads already normalize every record to the current schema on
                // the way out; this writes that result back, which is what stops the migration
                // backup arming on every launch for a game the user has never edited.
                using (PerfScope.StartStartup(_logger, "PluginCtor.CustomDataSchemaUpgrade", thresholdMs: 50))
                {
                    _gameCustomDataStore.UpgradeStoredRecordsToCurrentSchema();
                }

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
            var installDirectory = GetPluginInstallDirectory();
            return string.IsNullOrEmpty(installDirectory)
                ? null
                : Path.Combine(installDirectory, "Localization");
        }

        /// <summary>
        /// The extension's install directory (where the plugin dll, the bundled sounds and the sound
        /// host exe live), resolved from the assembly location. Null when it cannot be resolved.
        /// </summary>
        private string GetPluginInstallDirectory()
        {
            try
            {
                var installDirectory = Path.GetDirectoryName(typeof(PlayniteAchievementsPlugin).Assembly.Location);
                return string.IsNullOrEmpty(installDirectory) ? null : installDirectory;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to resolve plugin install directory.");
                return null;
            }
        }

        private void OnSettingsSavedForUnlockSounds(object sender, EventArgs e)
        {
            try
            {
                _unlockSounds?.ApplySettings();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Applying unlock sound settings failed.");
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
                SingleGameId = playniteGameId,
                SurfaceUserNotices = true
            }) ?? Task.CompletedTask;
        }

        // Invoked by AchievementHotkeyService on F5. Refreshes the plugin view shown in the
        // active/topmost window (the single-game View Achievements window, or the Overview as a
        // standalone window or open sidebar view) regardless of which element holds focus.
        // Returns true when a plugin view handled it, so the service can suppress Playnite's own
        // F5 library update.
        private bool TryRefreshActivePluginView()
        {
            // Invoked synchronously from the hotkey input filter, which must never see a throw.
            // The window/visual-tree walk below can fault while the tree is partially built during
            // startup; fail closed (key not handled) and log rather than escaping into the pipeline.
            try
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
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to refresh active plugin view from hotkey.");
                return false;
            }
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
            // Invoked synchronously from the hotkey input filter, which must never see a throw.
            // The window/visual-tree walk below can fault while the tree is partially built during
            // startup; fail closed (key not handled) and log rather than escaping into the pipeline.
            try
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
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to flip category mode from hotkey.");
                return false;
            }
        }

        public PlayniteAchievementsPlugin(IPlayniteAPI api) : base(api)
        {
            // Initialize logging system first
            var pluginUserDataPath = GetPluginUserDataPath();
            PluginLogger.Initialize(pluginUserDataPath);
            // Before the first scope below, so a traced session covers startup too.
            PerfScope.ConfigureTracing(pluginUserDataPath);
            _logger = PluginLogger.GetLogger(nameof(PlayniteAchievementsPlugin));
            _themeControlRegistry = new ThemeControlRegistry();
            _resourceService = new AchievementResourceService(_logger);

            using (PerfScope.StartStartup(_logger, "PluginCtor.Total", thresholdMs: 50))
            {
                Properties = _pluginProperties;

                Instance = this;
                _logger.Info("PlayniteAchievementsPlugin initializing...");

                // Stamps which build produced this log. Diagnostic packages are rebuilt far more
                // often than the version changes, so several materially different builds share
                // one file name -- and a capture was read against the wrong one because the only
                // way to tell them apart was which tags happened to be missing. The assembly's
                // own timestamp distinguishes builds packed minutes apart.
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    var built = System.IO.File.GetLastWriteTimeUtc(assembly.Location);
                    _logger.Info(
                        $"[Build] version={assembly.GetName().Version} " +
                        $"builtUtc={built:yyyy-MM-dd HH:mm:ss} " +
                        $"tracing={Common.PerfScope.PerfTracingEnabled}");
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not stamp the build into the log.");
                }

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
                _manualSourceRegistry = new ManualSourceRegistry(_logger, settings, PlayniteApi, pluginUserDataPath);

                // User-defined custom providers resolve through static hooks so the registry and
                // the icon converter stay free of the store type (both are linked into the tests).
                _customProviderStore = new Services.CustomProviders.CustomProviderStore(pluginUserDataPath, _logger);
                ProviderRegistry.CustomProviderResolver = ResolveCustomProviderVisuals;
                Views.Converters.ProviderIconConverter.CustomGeometryResolver = id => _customProviderStore?.GetGeometry(id);
                Views.Converters.ProviderIconConverter.CustomGeometryVersionResolver = id => _customProviderStore?.GetVersion(id) ?? 0;

                // Create provider registry
                _providerRegistry = new ProviderRegistry(settings, ProviderDisplayOrder, _logger, _manualSourceRegistry);
                _providerRegistry.SyncFromSettings(settings.Persisted);
                settings.Persisted?.MigrateLegacyProviderFriends();
                _gameCustomDataStore = _settingsViewModel.GameCustomDataStore;
                _gameCustomDataStore.AttachRuntimeSettings(settings);
                _gameCustomDataStore.AttachCustomProviderCatalog(
                    id => _customProviderStore.TryGet(id, out var definition) ? definition : null,
                    definition => _customProviderStore.ImportIfMissing(definition));
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
                    CategoryArtChainResolver.OverrideDisplayPathResolver =
                        (storedValue, gameId, displayMode) => _managedCustomIconService
                            .ResolveCategoryArtDisplayPath(storedValue, gameId, displayMode);
                    _notificationImageStore = new NotificationImageStore(_diskImageService, _logger);
                    _fallbackIconStore = new FallbackIconStore(_diskImageService, _logger);
                    // Read through Settings.Persisted on every call: the settings dialog mutates the
                    // live instance and CancelEdit replaces it wholesale.
                    AchievementIconResolver.LockedFallbackPathAccessor =
                        () => Settings?.Persisted?.LockedFallbackIconPath;
                    AchievementIconResolver.HiddenFallbackPathAccessor =
                        () => Settings?.Persisted?.HiddenFallbackIconPath;
                    _imageService = new MemoryImageService(_logger, _diskImageService);
                    _rayTrackService = new RayTrackService(_logger, _imageService);
                    _gameCustomDataStore.AttachManagedCustomIconService(_managedCustomIconService);
                    _gameCustomDataStore.AttachNotificationImageStore(_notificationImageStore);

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
                    if (_friendCacheManager != null)
                    {
                        _friendCacheManager.FriendCacheInvalidated += FriendCacheManager_FriendCacheInvalidated;
                        _eventSubscriptions.Add(() => _friendCacheManager.FriendCacheInvalidated -= FriendCacheManager_FriendCacheInvalidated);
                    }

                    _cacheManager.CacheInvalidated += (_, args) =>
                    {
                        // Scoped invalidations arrive in bursts -- one per custom-data edit --
                        // and each start-page invalidation makes every live widget re-pull a
                        // full library snapshot. Collapse the burst through the same coalescer
                        // the CustomDataChanged path already uses. A full invalidation is a
                        // bulk event, not a burst, so it still lands immediately.
                        if (args?.IsFull == false)
                        {
                            ScheduleStartPageInvalidate();
                        }
                        else
                        {
                            InvalidateStartPageData();
                        }

                        // Deliberately unconditional: scoped invalidations also come from the
                        // refresh pipeline's end-of-run raise, which includes friend-mode runs,
                        // and this is two cheap Invalidate() calls.
                        InvalidateFriendDataCoordinators();
                        ScheduleRetentionDiagnostics();
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
                    _achievementMarkerToggle = new AchievementMarkerToggle(
                        _achievementOverridesService,
                        () => _settingsViewModel?.Settings?.Persisted,
                        () => _gameCustomDataStore,
                        gameId => _cacheManager?.LoadGameData(gameId.ToString()));
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
                        isRefreshActive: () => _refreshService?.IsRebuilding == true,
                        currentUserIdentityLoader: () => _friendCacheManager?.LoadCurrentUserIdentities(),
                        hasActiveSnapshotPublisher: () => _startPageDataCoordinator?.HasActivePublisher == true);
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

                    // The auto capstone stands for the achievements a refresh just rewrote, so it
                    // is brought back into step here: what it derives only changes when provider
                    // data does.
                    _autoCapstoneMaintainer = new AutoCapstoneMaintainer(
                        _gameCustomDataStore,
                        _achievementOverridesService,
                        gameId => _achievementDataService?.GetGameAchievementData(gameId),
                        NotifyAchievementUnlocked,
                        _logger);
                    _refreshCoordinator.RefreshCompleted += gameIds => _autoCapstoneMaintainer?.Maintain(gameIds);
                    _windowTracker = new ActiveGameWindowTracker(_logger);
                    var soundThemeResolver = new AchievementToastTemplateResolver(PlayniteApi, _logger);
                    var pluginInstallDirectory = GetPluginInstallDirectory();
                    _unlockSounds = new Services.Sound.UnlockSoundService(
                        settings,
                        new UnlockSoundResolver(
                            () => settings?.Persisted?.UnlockSounds,
                            () => soundThemeResolver.ResolveActiveThemeDirectories(Application.Current?.Resources),
                            UnlockSoundResolver.GetBundledSoundsDirectory(pluginInstallDirectory),
                            _logger,
                            () => settings?.Persisted?.AllowThemeUnlockSounds ?? true,
                            mode => soundThemeResolver.ResolveThemeDirectoriesForMode(
                                Application.Current?.Resources,
                                mode),
                            () => soundThemeResolver.ActiveThemeModeName),
                        pluginInstallDirectory,
                        _logger);
                    SettingsSaved += OnSettingsSavedForUnlockSounds;
                    // Bound a sound by the card it belongs to. Read through a delegate because the
                    // toast service is constructed below this one, and because the effective
                    // duration can come from a theme override rather than the setting.
                    _unlockSounds.MaxPlaybackSeconds =
                        () => _toastNotifications?.GetEffectiveToastDurationSecondsSafe();
                    _toastNotifications = new ToastNotificationService(
                        PlayniteApi,
                        settings,
                        _logger,
                        () => _resourceService.EnsureAchievementResourcesLoaded(_settingsViewModel.Settings),
                        GetProcessIdForGame,
                        _windowTracker,
                        _gameCustomDataStore,
                        // Late-bound: the recording service is constructed just below, but the
                        // toast service only ever invokes these from an unlock handler, long after
                        // the field is assigned.
                        e => _unlockRecordings?.WouldRequestClip(e) ?? false,
                        (e, capHeight) => _unlockRecordings?.TryCaptureAnchorFrame(e, capHeight),
                        _unlockSounds);
                    _unlockRecordings = new Services.Recording.UnlockRecordingService(
                        PlayniteApi,
                        settings,
                        _logger,
                        pluginUserDataPath,
                        GetProcessIdForGame,
                        _toastNotifications,
                        key => Services.UI.ProviderNotificationPolicy.Resolve(settings?.Persisted, key).Recordings,
                        _windowTracker,
                        // Fails open while the provider registry is still being built: refusing to
                        // refresh a game we cannot classify is free, but refusing to capture one
                        // costs a clip that cannot be recovered afterwards.
                        game => Providers == null || AnyProviderCapable(game),
                        // The sound host's pid, so the recorder excludes its process from clip
                        // captures, and its measured onsets, so composited chimes land where the
                        // live ones were heard.
                        () => _unlockSounds?.HostProcessId,
                        id => _unlockSounds?.TryGetAudibleOnsetUtc(id));
                    _captureLibraryService = new Services.Captures.CaptureLibraryService(
                        () => _settingsViewModel?.Settings?.Persisted,
                        _logger);
                    Services.Captures.AchievementCapturePathResolver.CaptureLibraryAccessor =
                        () => _captureLibraryService;
                    _inGameMonitor = new InGameAchievementMonitor(
                        PlayniteApi,
                        settings,
                        _logger,
                        _cacheManager,
                        _refreshService,
                        (request, policy) => _refreshCoordinator.ExecuteAsync(request, policy),
                        NotifyAchievementUnlocked);
                    _backgroundUpdates = new BackgroundUpdater(_refreshCoordinator, _refreshService, _cacheManager, settings, _logger, _notifications, null);

                    // Create tag sync service
                    _tagSyncService = new TagSyncService(
                        PlayniteApi,
                        _logger,
                        settings,
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
                        _friendGameAchievementsDataCoordinator,
                        // Deliberately the field, not GetStartPageDataCoordinator(): publishing
                        // is an optimization for widget hosts that already exist. Creating the
                        // coordinator here would stand up a process-lifetime service holding a
                        // full-library snapshot for a user who has no start page at all.
                        () => _startPageDataCoordinator);

                    _achievementHotkeyTargetResolver = new AchievementHotkeyTargetResolver(PlayniteApi, _logger);
                    _achievementHotkeyService = new AchievementHotkeyService(
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        _achievementHotkeyTargetResolver,
                        _logger,
                        gameId => _windowService.ToggleViewAchievementsWindowFromHotkey(gameId),
                        gameId => _windowService.ToggleManageAchievementsViewFromHotkey(gameId),
                        ToggleOverviewWindowFromHotkey,
                        OpenSettingsViewFromHotkey,
                        TryFlipCategoryModeInActiveView,
                        TryRefreshActivePluginView,
                        runningGameId => _inGameMonitor?.FireTestNotification(runningGameId));

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
                        _achievementHotkeyTargetResolver.ResolveRunningGame,
                        ToggleAchievementCapstoneFromTheme,
                        target => _achievementMarkerToggle.ToggleGoal(target),
                        _captureLibraryService);

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
                // Pre-build the system font-family list off the UI thread so the first open of the
                // notification appearance tab doesn't block on the (slow) font enumeration.
                System.Threading.Tasks.Task.Run(
                    () => ViewModels.Settings.NotificationAppearanceEditorViewModel.PrewarmFontOptions());
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
                        () => new OverviewControl(PlayniteApi, _logger, _refreshService, _cacheManager, PersistSettingsForUi, _achievementOverridesService, _achievementDataService, _libraryProjectionService, _gameCustomDataStore, _refreshCoordinator, _settingsViewModel.Settings, OverviewLaunchContext.Sidebar, _friendsOverviewDataCoordinator, () => _startPageDataCoordinator),
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
                _inGameMonitor?.Start(args?.Game);
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
                // Detach exact-path watchers and cancel queued progress reads before clearing
                // session toasts or handing recording ownership to another running game.
                _inGameMonitor?.Stop(args?.Game);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to stop in-game monitor for {args?.Game?.Name}.");
            }

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

            _ = RefreshStoppedGameAsync(args?.Game);
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

        private async Task RefreshStoppedGameAsync(Game game)
        {
            if (game == null)
            {
                return;
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

            // With other games still running the monitor may have a refresh in flight; wait for it
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

        /// <summary>
        /// Logs the plugin build and host context once at startup, so a user-submitted log can be
        /// tied to a specific release without having to ask.
        /// </summary>
        private void LogStartupBanner()
        {
            try
            {
                _logger.Info(
                    $"[Startup] Playnite Achievements {ReadManifestVersion() ?? "<unknown>"}; " +
                    $"playnite={PlayniteApi?.ApplicationInfo?.ApplicationVersion?.ToString() ?? "<unknown>"}, " +
                    $"mode={PlayniteApi?.ApplicationInfo?.Mode.ToString() ?? "<unknown>"}, " +
                    $"portable={PlayniteApi?.ApplicationInfo?.IsPortable.ToString() ?? "<unknown>"}.");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[Startup] Could not log the startup banner.");
            }
        }

        /// <summary>
        /// Reads <c>Version</c> from the manifest shipped next to the plugin assembly. The assembly
        /// itself carries no meaningful version, so extension.yaml is the only source that matches
        /// the number users see in Playnite's add-on list.
        /// </summary>
        private static string ReadManifestVersion()
        {
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return null;
            }

            var manifestPath = Path.Combine(assemblyDirectory, "extension.yaml");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed.Substring("Version:".Length).Trim().Trim('"', '\'');
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }

            return null;
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            using (PerfScope.StartStartup(_logger, "OnApplicationStarted", thresholdMs: 50))
            {
                _applicationStarted = true;

                // Measures the symptom rather than a suspected cause: every other timing here is
                // a scope around code someone already suspected, and a reported freeze was
                // repeatedly not inside one.
                Common.UiStallWatchdog.Start(_logger);

                LogStartupBanner();

                // Launch and preload the sound host off the UI thread so the first unlock plays with
                // no device-open or decode cost; a settings save re-applies the same step.
                System.Threading.Tasks.Task.Run(() => OnSettingsSavedForUnlockSounds(this, EventArgs.Empty));

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

                // Normalize un-customized notification header texts back to null so they
                // follow the current Playnite language.
                try
                {
                    var headerTextService = new NotificationHeaderTextService(
                        GetPluginLocalizationDirectory(),
                        _logger);
                    if (headerTextService.RelocalizeDefaultHeaderTexts(_settingsViewModel?.Settings?.Persisted))
                    {
                        PersistSettingsForUi();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to re-localize notification header texts.");
                }

                _notificationImageStore?.PruneOrphans(
                    _settingsViewModel?.Settings?.Persisted,
                    _gameCustomDataStore?.LoadAll());

                _fallbackIconStore?.PruneOrphans(_settingsViewModel?.Settings?.Persisted);

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
                ReconfigureInGameMonitor();
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

        // Runs when CancelEdit replaces the whole PersistedSettings instance. Every
        // per-property side effect above may have been reverted in one step without a
        // property change firing, so re-derive all of them against the new instance.
        private void OnPersistedSettingsInstanceChanged()
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;

            RestartBackgroundUpdater();
            ReconfigureInGameMonitor();
            RarityAppearanceHelper.ApplyBadgeApplicationResources(persisted);
            AchievementRarityResolver.RoundDisplayPercentages = persisted?.RoundRarityPercentages ?? false;
            FormattingCulture.Refresh();
            _achievementHotkeyService?.RefreshConfiguration();
            InvalidateFriendDataCoordinators();
            InvalidateStartPageData();
            _tagSyncService?.InitializeAndSubscribeTaggingSettings();
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
        }

        // Settings-driven callers pass no args (projection-affecting settings need a full
        // rebuild); cache-driven callers pass the change scope through so the overview
        // coordinator can patch instead of reloading everything.
        private void InvalidateFriendDataCoordinators(FriendCacheInvalidatedEventArgs args = null)
        {
            _friendsOverviewDataCoordinator?.Invalidate(args);
            _friendGameAchievementsDataCoordinator?.Invalidate();
        }

        private static bool ShouldInvalidateFriendDataForSetting(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.ShowFriendSpoilers) ||
                   propertyName == nameof(PersistedSettings.FriendNameDisplayMode) ||
                   propertyName == nameof(PersistedSettings.Friends) ||
                   propertyName == nameof(PersistedSettings.FriendMergeGroups) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenIcon) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenTitle) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenDescription) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenSuffix) ||
                   propertyName == nameof(PersistedSettings.ShowLockedIcon) ||
                   propertyName == nameof(PersistedSettings.ShowLockedTitle) ||
                   propertyName == nameof(PersistedSettings.ShowLockedDescription) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenTrophy) ||
                   propertyName == nameof(PersistedSettings.ShowHiddenPoints) ||
                   propertyName == nameof(PersistedSettings.ShowLockedTrophy) ||
                   propertyName == nameof(PersistedSettings.ShowLockedPoints) ||
                   propertyName == nameof(PersistedSettings.UseSeparateLockedIconsWhenAvailable) ||
                   propertyName == nameof(PersistedSettings.SeparateLockedIconEnabledGameIds) ||
                   propertyName == nameof(PersistedSettings.LockedFallbackIconPath) ||
                   propertyName == nameof(PersistedSettings.HiddenFallbackIconPath);
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

        private void ReconfigureInGameMonitor()
        {
            try
            {
                var running = _inGameMonitor?.RunningGames?.ToList() ?? new List<Game>();
                if (running.Count == 0)
                {
                    running = PlayniteApi?.Database?.Games?
                        .Where(game => game?.IsRunning == true)
                        .OrderByDescending(game => game.LastActivity)
                        .ToList() ?? new List<Game>();
                }

                _inGameMonitor?.Reconfigure();
                if (_applicationStarted)
                {
                    foreach (var game in running)
                    {
                        _inGameMonitor?.Start(game);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reconfigure in-game achievement monitor.");
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
            try { _inGameMonitor?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose inGameMonitor"); }
            try { _toastNotifications?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose toastNotifications"); }
            try { _unlockRecordings?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose unlockRecordings"); }
            // After the recordings: a session in flight still reads the host's pid until then.
            SettingsSaved -= OnSettingsSavedForUnlockSounds;
            try { _unlockSounds?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose unlockSounds"); }
            try { _captureLibraryService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose captureLibraryService"); }
            try { _windowTracker?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose windowTracker"); }

            try { _achievementHotkeyService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose achievementHotkeyService"); }
            try { _windowService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose windowService"); }
            try { _libraryProjectionService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose libraryProjectionService"); }
            try { _rayTrackService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose rayTrackService"); }
            try { _imageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose imageService"); }
            try { _diskImageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose diskImageService"); }
            try { _manualSourceRegistry?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose manualSourceRegistry"); }
            try { _fullscreenControllerNavigationService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose fullscreenControllerNavigationService"); }
            try { _fullscreenWindowService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose fullscreenWindowService"); }
            try { _themeIntegrationService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose themeIntegrationService"); }
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
            // A widget switching to the Unlock Next source needs locked achievements the cached
            // projection never hydrated, so that one option edit has to drop the cache. Widget
            // options live in a nested string bag and raise no settings PropertyChanged, which is
            // why this listens to the showcase's own change event instead.
            ShowcaseConfigurationEvents.Changed += OnShowcaseConfigurationChanged;
            _eventSubscriptions.Add(() => ShowcaseConfigurationEvents.Changed -= OnShowcaseConfigurationChanged);

            _refreshService.GameRefreshed += OnAchievementGameRefreshed;
            _eventSubscriptions.Add(() => _refreshService.GameRefreshed -= OnAchievementGameRefreshed);
            if (_customProviderStore != null)
            {
                _customProviderStore.Changed += CustomProviderStore_Changed;
                _eventSubscriptions.Add(() => _customProviderStore.Changed -= CustomProviderStore_Changed);
            }
            _inGameMonitor.ProgressApplied += OnAchievementGameRefreshed;
            _eventSubscriptions.Add(() => _inGameMonitor.ProgressApplied -= OnAchievementGameRefreshed);

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

                // Subscribes through the settings wrapper: CancelEdit replaces the whole
                // PersistedSettings instance, and a direct subscription would be left on
                // the orphan, silently stopping every settings-driven side effect below
                // for the rest of the session.
                var settings = _settingsViewModel?.Settings;
                if (settings != null)
                {
                    var subscription = new PersistedSettingsSubscription(
                        settings,
                        PersistedSettings_PropertyChanged,
                        OnPersistedSettingsInstanceChanged);
                    _eventSubscriptions.Add(() => subscription.Dispose());
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

        // Value is whether any change coalesced into this burst could move a library rollup. A
        // burst that cannot - category order, art and membership, goal reordering - still has to
        // repaint the game's own theme surface, but nothing library-wide reads it, so the tag
        // sync, the start page and the whole-library theme lists are left alone.
        private readonly Dictionary<Guid, bool> _pendingCustomDataChangeIds = new Dictionary<Guid, bool>();
        private Timer _customDataChangeTimer;

        private void GameCustomDataStore_CustomDataChanged(object sender, GameCustomDataChangedEventArgs e)
        {
            if (e == null || e.PlayniteGameId == Guid.Empty)
            {
                return;
            }

            lock (_customDataChangeSync)
            {
                _pendingCustomDataChangeIds.TryGetValue(e.PlayniteGameId, out var pendingAffectsSummaryData);
                _pendingCustomDataChangeIds[e.PlayniteGameId] = pendingAffectsSummaryData || e.AffectsSummaryData;
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
            List<KeyValuePair<Guid, bool>> pending;
            lock (_customDataChangeSync)
            {
                pending = _pendingCustomDataChangeIds.ToList();
                _pendingCustomDataChangeIds.Clear();
            }

            foreach (var change in pending)
            {
                HandleCustomDataChanged(change.Key, change.Value);
            }
        }

        private CustomProviderVisuals ResolveCustomProviderVisuals(string customProviderId)
        {
            return _customProviderStore != null && _customProviderStore.TryGet(customProviderId, out var definition)
                ? new CustomProviderVisuals(
                    definition.Name,
                    definition.ColorHex,
                    hasIcon: !string.IsNullOrWhiteSpace(definition.IconPathData))
                : null;
        }

        // A definition edit changes how every assigned game resolves its provider name, icon and
        // color. The converter's tinted-icon cache is cleared first, then each assigned game
        // re-projects through the same CustomDataChanged path an assignment change uses. A
        // deletion clears the assignments instead, which raises the same event through the write.
        private void CustomProviderStore_Changed(object sender, Services.CustomProviders.CustomProviderChangedEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Id) || _gameCustomDataStore == null)
            {
                return;
            }

            try
            {
                Views.Converters.ProviderIconConverter.Invalidate("GeoCustom:" + e.Id + "|");

                var affectedGameIds = _gameCustomDataStore.LoadAll()
                    .Where(data => data != null &&
                                   data.PlayniteGameId != Guid.Empty &&
                                   string.Equals(data.CustomProviderId, e.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(data => data.PlayniteGameId)
                    .ToList();

                foreach (var gameId in affectedGameIds)
                {
                    if (e.Deleted)
                    {
                        _gameCustomDataStore.Update(gameId, data => data.CustomProviderId = null);
                    }
                    else
                    {
                        _gameCustomDataStore.NotifyChanged(gameId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed propagating custom provider change for id={e.Id}.");
            }
        }

        private void HandleCustomDataChanged(Guid gameId, bool affectsSummaryData)
        {
            // Runs on a pool thread 400ms after the last edit in a burst. Uninstrumented until
            // now, which made everything it fans out into invisible in a log.
            using (var scope = Common.PerfScope.Start(_logger, "Plugin.HandleCustomDataChanged", thresholdMs: 10))
            {
                scope?.SetContext("affectsSummary=" + affectsSummaryData);
                HandleCustomDataChangedCore(gameId, affectsSummaryData);
            }
        }

        private void HandleCustomDataChangedCore(Guid gameId, bool affectsSummaryData)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (_tagSyncService != null && persisted?.TaggingSettings?.EnableTagging == true)
            {
                // Any custom-data change at all, not just a summary-affecting one: completion
                // only moves with the summary data, but the Customized tag reports whether the
                // game carries customization of any kind, which a rename or a note moves while
                // leaving every count alone. Those get the narrow sync, which skips the
                // achievement load a full evaluation needs.
                using (Common.PerfScope.Start(_logger, "Plugin.CustomDataChanged.QueueTagSync", thresholdMs: 10))
                {
                    QueueTagSync(gameId, fullEvaluation: affectsSummaryData);
                }
            }

            try
            {
                // The game's own theme surface still repaints - a category edit is visible there -
                // but the whole-library theme lists are rebuilt only when something they read moved.
                using (var scope = Common.PerfScope.Start(_logger, "Plugin.CustomDataChanged.ThemeNotify", thresholdMs: 10))
                {
                    scope?.SetContext("refreshLibraryState=" + affectsSummaryData);
                    _themeIntegrationService?.NotifyCustomDataChanged(gameId, refreshLibraryState: affectsSummaryData);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to refresh theme state after custom-data change for gameId={gameId}.");
            }

            if (affectsSummaryData)
            {
                InvalidateStartPageData();
            }
        }

        // A capstone write is a SQLite save, and a theme button click arrives on the UI thread, so
        // this hands off rather than blocking. The write raises CustomDataChanged, which is what
        // rebuilds the theme's achievement lists and repaints the toggled row.
        private void ToggleAchievementCapstoneFromTheme(AchievementMarkerTarget target)
        {
            _ = ToggleAchievementCapstoneFromThemeAsync(target);
        }

        private async Task ToggleAchievementCapstoneFromThemeAsync(AchievementMarkerTarget target)
        {
            try
            {
                var result = await _achievementMarkerToggle.ToggleCapstoneAsync(target);
                if (result.Attempted && !result.Success)
                {
                    _logger?.Error(
                        $"Theme capstone toggle failed for gameId={target.GameId}, apiName='{target.ApiName}': {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Theme capstone toggle failed for gameId={target.GameId}.");
            }
        }

        /// <param name="fullEvaluation">
        /// False when the change can only have moved the Customized tag, which is reconciled
        /// without loading the game's achievement data. A refresh defaults to a full evaluation.
        /// </param>
        private void QueueTagSync(Guid gameId, bool fullEvaluation = true)
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
                if (fullEvaluation)
                {
                    // A full sync covers the customization tag too, so it supersedes a narrow one
                    // already queued for the same game.
                    _pendingTagSyncIds.Add(gameId);
                    _pendingCustomizationTagSyncIds.Remove(gameId);
                }
                else if (!_pendingTagSyncIds.Contains(gameId))
                {
                    _pendingCustomizationTagSyncIds.Add(gameId);
                }

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
                List<Guid> customizationBatch;
                lock (_tagSyncGate)
                {
                    if (_pendingTagSyncIds.Count == 0 && _pendingCustomizationTagSyncIds.Count == 0)
                    {
                        _tagSyncDrainRunning = false;
                        return;
                    }

                    batch = _pendingTagSyncIds.ToList();
                    _pendingTagSyncIds.Clear();
                    customizationBatch = _pendingCustomizationTagSyncIds.ToList();
                    _pendingCustomizationTagSyncIds.Clear();
                }

                try
                {
                    // These write to the Playnite database, which makes Playnite re-render its
                    // own library view and fire ItemUpdated back at this plugin.
                    if (batch.Count > 0)
                    {
                        using (var scope = Common.PerfScope.Start(_logger, "TagSync.SyncTags", thresholdMs: 10))
                        {
                            scope?.SetContext("games=" + batch.Count);
                            tagSyncService.SyncTagsForGames(batch);
                        }
                    }

                    if (customizationBatch.Count > 0)
                    {
                        using (var scope = Common.PerfScope.Start(_logger, "TagSync.SyncCustomizationTags", thresholdMs: 10))
                        {
                            scope?.SetContext("games=" + customizationBatch.Count);
                            tagSyncService.SyncCustomizationTagsForGames(customizationBatch);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(
                        ex,
                        $"Failed queued tag sync for {batch.Count + customizationBatch.Count} game(s).");
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

            // Fires per saved game during a bulk refresh (and per in-game unlock via the
            // monitor). Coalesced: the end-of-run scoped CacheInvalidated invalidates once
            // regardless, and mid-run start-page freshness comes from the overview's
            // published snapshots when one is open.
            ScheduleStartPageInvalidate();
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
                // Only the providers this refresh actually spoke to; clearing every known provider
                // would drop a warning raised by a refresh of some other provider's game.
                _notifications?.ClearProviderAuthNotifications(payload.ExecutedProviderKeys);
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            EnsureAchievementResourcesLoaded();
            return _themeControlRegistry.TryCreate(args.Name, out var control) ? control : null;
        }

        /// <summary>
        /// Drops the cached library projection when the showcase starts needing the Unlock Next
        /// candidate pool, so the next build hydrates it. Only the false-to-true flip matters; a
        /// widget dropping the source leaves a harmless pool behind until the next rebuild.
        /// </summary>
        private void OnShowcaseConfigurationChanged(object sender, EventArgs e)
        {
            var required = ShowcaseWidgetOptions.RequiresUnlockNextPool(Settings?.Persisted?.Showcase);
            if (!required || _unlockNextPoolRequired)
            {
                _unlockNextPoolRequired = required;
                return;
            }

            _unlockNextPoolRequired = true;
            _libraryProjectionService?.Invalidate();
            ScheduleStartPageInvalidate();
        }

        // === Game selection wiring ===

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            _logger.Info("Games_ItemCollectionChanged triggered.");

            // Games added/removed change what the overview and start page project; drop the cached
            // library projection so the next open rebuilds against the current library.
            _libraryProjectionService?.Invalidate();
            ScheduleStartPageInvalidate();

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
            // invalidate so the cached overview/start-page projection picks up fresh values.
            //
            // Per game, so the rebuild waits for the library to go quiet. Playnite raises this for
            // playtime ticks and for the plugin's own tag-sync writes, and the previous full
            // invalidation warmed immediately every time -- a whole-library rebuild that holds the
            // store's read connection for hundreds of milliseconds, behind which UI-thread reads
            // queue. The cache is still dropped either way, so an on-demand consumer never sees
            // stale values; only the precompute waits.
            _libraryProjectionService?.InvalidateForGame();
            ScheduleStartPageInvalidate();
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





