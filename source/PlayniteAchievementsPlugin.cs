using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.GOG;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.PSN;
using PlayniteAchievements.Providers.Xbox;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Logging;
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

        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly RefreshRuntime _refreshService;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly ICacheManager _cacheManager;
        private readonly MemoryImageService _imageService;
        private readonly DiskImageService _diskImageService;
        private readonly NotificationPublisher _notifications;
        private readonly ProviderRegistry _providerRegistry;
        private readonly ManualAchievementsProvider _manualProvider;
        private readonly SubscriptionCollection _eventSubscriptions = new SubscriptionCollection();

        private readonly BackgroundUpdater _backgroundUpdates;
        private readonly RefreshEntryPoint _refreshCoordinator;
        private bool _applicationStarted;

        // Top panel item
        private PlayniteAchievementsTopPanelItem _topPanelItem;

        // Theme integration
        private readonly FullscreenWindowService _fullscreenWindowService;
        private readonly ThemeIntegrationService _themeIntegrationService;
        private readonly ThemeControlRegistry _themeControlRegistry;
        private readonly PluginWindowService _windowService;
        private readonly ThemeAutoMigrationService _themeAutoMigrationService;

        // Tagging
        private TagSyncService _tagSyncService;

        public override Guid Id { get; } =
            Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");

        public PlayniteAchievementsSettings Settings => _settingsViewModel.Settings;
        public ProviderRegistry ProviderRegistry => _providerRegistry;
        public IReadOnlyList<IDataProvider> Providers => _refreshService?.Providers;
        public RefreshRuntime RefreshRuntime => _refreshService;
        public AchievementOverridesService AchievementOverridesService => _achievementOverridesService;
        public AchievementDataService AchievementDataService => _achievementDataService;
        public MemoryImageService ImageService => _imageService;
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

        /// <summary>
        /// Raises the SettingsSaved event to notify listeners that settings have changed.
        /// </summary>
        public static void NotifySettingsSaved() => SettingsSaved?.Invoke(null, EventArgs.Empty);

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
        public Task RequestSingleGameRefreshAsync(Guid playniteGameId)
        {
            return _refreshCoordinator?.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Single,
                SingleGameId = playniteGameId
            }) ?? Task.CompletedTask;
        }

        public PlayniteAchievementsPlugin(IPlayniteAPI api) : base(api)
        {
            // Initialize logging system first
            PluginLogger.Initialize(GetPluginUserDataPath());
            _logger = PluginLogger.GetLogger(nameof(PlayniteAchievementsPlugin));
            _themeControlRegistry = new ThemeControlRegistry();

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

                // Create provider registry
                _providerRegistry = new ProviderRegistry(settings, _logger);
                _providerRegistry.SyncFromSettings(settings.Persisted);

                List<IDataProvider> providers;
                using (PerfScope.StartStartup(_logger, "PluginCtor.ProviderCreation", thresholdMs: 50))
                {
                    providers = _providerRegistry.CreateProviders(settings, PlayniteApi, pluginUserDataPath);
                    _manualProvider = _providerRegistry.ManualProvider;
                }

                // Phase 3: Wire core services, refresh pipeline, and tagging.
                using (PerfScope.StartStartup(_logger, "PluginCtor.RefreshServiceCreation", thresholdMs: 50))
                {
                    _diskImageService = new DiskImageService(_logger, pluginUserDataPath);
                    _imageService = new MemoryImageService(_logger, _diskImageService);

                    _refreshService = new RefreshRuntime(api, settings, _logger, this, providers, _diskImageService, _providerRegistry);
                    _cacheManager = _refreshService.Cache;
                    _achievementOverridesService = new AchievementOverridesService(
                        settings,
                        _cacheManager,
                        _logger,
                        notifySettingsSaved => PersistSettingsForUi(),
                        force => _cacheManager.NotifyCacheInvalidated(),
                        gameIds => OnAchievementGameDataChanged(gameIds));
                    _achievementDataService = new AchievementDataService(_cacheManager, PlayniteApi, _settingsViewModel.Settings, _logger);
                    _notifications = new NotificationPublisher(api, settings, _logger);
                    _refreshCoordinator = new RefreshEntryPoint(
                        _refreshService,
                        _logger,
                        _providerRegistry,
                        runWithProgressWindow: ShowRefreshProgressControlAndRun);
                    _backgroundUpdates = new BackgroundUpdater(_refreshCoordinator, _refreshService, _cacheManager, settings, _logger, _notifications, null);

                    // Create tag sync service
                    _tagSyncService = new TagSyncService(
                        PlayniteApi,
                        _logger,
                        settings.Persisted,
                        _cacheManager);
                    _tagSyncService.InitializeAndSubscribeTaggingSettings();

                    _windowService = new PluginWindowService(
                        PlayniteApi,
                        _logger,
                        _refreshService,
                        _cacheManager,
                        PersistSettingsForUi,
                        _achievementOverridesService,
                        _achievementDataService,
                        _settingsViewModel.Settings,
                        _manualProvider,
                        EnsureAchievementResourcesLoaded);

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
                        _refreshCoordinator,
                        _settingsViewModel.Settings,
                        _fullscreenWindowService,
                        _logger);

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
                    PlayniteApi, _logger, _refreshService, _cacheManager, PersistSettingsForUi, _achievementOverridesService, _achievementDataService, _refreshCoordinator, _settingsViewModel.Settings);

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
                    _providerRegistry);
                _logger.Info("GetSettingsView succeeded");
                return control;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GetSettingsView failed");
                throw;
            }
        }

        // === Sidebar ===

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                Type = SiderbarItemType.View,
                Icon = GetSidebarIcon(),
                Opened = () =>
                {
                    return new SidebarHostControl(
                        () => new SidebarControl(PlayniteApi, _logger, _refreshService, _cacheManager, PersistSettingsForUi, _achievementOverridesService, _achievementDataService, _refreshCoordinator, _settingsViewModel.Settings),
                        _logger,
                        PlayniteApi,
                        _refreshService,
                        _cacheManager,
                        this);
                }
            };
        }

        private TextBlock GetSidebarIcon()
        {
            var tb = new TextBlock
            {
                Text = char.ConvertFromUtf32(0xedd7), // ico-font: trophy
                FontSize = 18
            };

            var font = ResourceProvider.GetResource("FontIcoFont") as FontFamily;
            tb.FontFamily = font ?? new FontFamily("Segoe UI Symbol");
            // tb.SetResourceReference(TextBlock.ForegroundProperty, "GlyphBrush");

            return tb;
        }

        // === Top Panel ===

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            if (_settingsViewModel?.Settings?.Persisted?.ShowTopMenuBarButton == true)
            {
                yield return _topPanelItem;
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            _logger.Info($"Game stopped: {args.Game.Name}. Triggering refresh.");
            _ = _refreshCoordinator.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Single,
                SingleGameId = args.Game.Id
            });
        }

        // === Lifecycle ===

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            using (PerfScope.StartStartup(_logger, "OnApplicationStarted", thresholdMs: 50))
            {
                _applicationStarted = true;

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
                            _logger.Debug($"Populating initial theme data for selected game: {game.Name}");
                            _settingsViewModel.Settings.SetSelectedGame(game);
                            _themeIntegrationService?.PopulateSingleGameDataSync(game.Id);
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
                }
                catch
                {
                    // ignore
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
                e.PropertyName == nameof(PersistedSettings.PeriodicUpdateHours))
            {
                RestartBackgroundUpdater();
            }
            _tagSyncService?.HandlePersistedSettingsPropertyChanged(e);
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

            try { _imageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose imageService"); }
            try { _diskImageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose diskImageService"); }
            try { _fullscreenWindowService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose fullscreenWindowService"); }
            try { _themeIntegrationService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose themeIntegrationService"); }

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

                    _settingsViewModel.Settings.SetSelectedGame(game);
                    _themeIntegrationService?.NotifySelectionChanged(game.Id);
                    // Populate cached single-game data immediately, then let the async pass reconcile if needed.
                    _themeIntegrationService?.PopulateSingleGameDataSync(game.Id);
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
            base.OnControllerButtonStateChanged(args);

            try
            {
                if (args?.Button == ControllerInput.B && args.State == ControllerInputState.Pressed)
                {
                    _fullscreenWindowService?.CloseOverlayWindowIfOpen();
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
            // Listen for game database changes to auto-refresh new entries and react to hide/unhide edits.
            PlayniteApi?.Database?.Games?.ItemCollectionChanged += Games_ItemCollectionChanged;
            PlayniteApi?.Database?.Games?.ItemUpdated += Games_ItemUpdated;

            _eventSubscriptions.Add(() => PlayniteApi?.Database?.Games?.ItemUpdated -= Games_ItemUpdated);
            _eventSubscriptions.Add(() => PlayniteApi?.Database?.Games?.ItemCollectionChanged -= Games_ItemCollectionChanged);
        }

        private void SubscribePluginEventHandlers()
        {
            _refreshService.GameRefreshed += OnAchievementGameRefreshed;
            _eventSubscriptions.Add(() => _refreshService.GameRefreshed -= OnAchievementGameRefreshed);

            try
            {
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

        private void OnAchievementGameRefreshed(Guid gameId)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (_tagSyncService != null && persisted?.TaggingSettings?.EnableTagging == true)
            {
                _tagSyncService.SyncTagsForGames(new List<Guid> { gameId });
            }
        }

        private void OnAchievementGameDataChanged(List<Guid> gameIds)
        {
            if (gameIds != null && gameIds.Count > 0)
            {
                _tagSyncService?.SyncTagsForGames(gameIds);
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            return _themeControlRegistry.TryCreate(args.Name, out var control) ? control : null;
        }

        // === Game selection wiring ===

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            _logger.Info("Games_ItemCollectionChanged triggered.");
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
            if (e?.UpdatedItems == null
                || _settingsViewModel?.Settings?.Persisted?.AutoExcludeHiddenGames != true)
            {
                return;
            }

            foreach (var update in e.UpdatedItems)
            {
                var oldGame = update?.OldData;
                var newGame = update?.NewData;
                if (oldGame == null || newGame == null || newGame.Id == Guid.Empty)
                {
                    continue;
                }

                if (oldGame.Hidden == newGame.Hidden)
                {
                    continue;
                }

                _achievementOverridesService.SetExcludedFromHiddenState(newGame.Id, newGame.Hidden);
            }
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





