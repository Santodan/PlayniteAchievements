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
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.ReleaseMonitoring;
using PlayniteAchievements.Services.ThemeIntegration;
using PlayniteAchievements.Services.Local;
using PlayniteAchievements.Services.ThemeMigration;
using PlayniteAchievements.Services.Tagging;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Providers.Local;
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
        private static readonly string[] ThemeSourceAliases =
        {
            "PlayniteAchievementsSantodan",
            "PlayniteAchievements",
            "SuccessStory",
            "SSHelper"
        };

        private readonly ILogger _logger;


        // Set Properties before constructor runs
        private static readonly GenericPluginProperties _pluginProperties = new GenericPluginProperties
        {
            HasSettings = true
        };

        private static readonly string[] ProviderDisplayOrder =
        {
            "Steam", "Epic", "GOG", "BattleNet", "GooglePlay", "Apple", "EA", "PSN", "Xbox", "RetroAchievements", "ShadPS4", "Xenia", "RPCS3", "Local", "Manual", "Exophase",
        };

        private static readonly string[] ProviderRefreshOrder =
        {
            "Manual", "Exophase", "Steam", "Epic", "GOG", "PSN", "Xbox", "Xenia", "RPCS3", "ShadPS4", "RetroAchievements", "Local",
        };

        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly RefreshRuntime _refreshService;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly ICacheManager _cacheManager;
        private readonly MemoryImageService _imageService;
        private readonly DiskImageService _diskImageService;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly NotificationPublisher _notifications;
        private readonly ProviderRegistry _providerRegistry;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly ManualSourceRegistry _manualSourceRegistry;
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
        private readonly ForkReleaseMonitor _forkReleaseMonitor;
        private readonly ActiveGameAchievementMonitor _activeGameAchievementMonitor;
        private readonly Services.Exophase.ExophaseGameAchievementMonitor _exophaseGameAchievementMonitor;

        // Tagging
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

        /// <summary>
        /// Raises the SettingsSaved event to notify listeners that settings have changed.
        /// </summary>
        public static void NotifySettingsSaved() => SettingsSaved?.Invoke(null, EventArgs.Empty);

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

        private void LoadLocalization()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var locDir = Path.Combine(pluginDir, "Localization");

                // Always load LocSource.xaml as the English base/fallback.
                var locSourceFile = Path.Combine(locDir, "LocSource.xaml");
                if (File.Exists(locSourceFile))
                {
                    MergeDictionary(locSourceFile);
                }
                else
                {
                    _logger?.Warn($"Localization source file not found: {locSourceFile}");
                }

                // Load the user's language file if it differs from LocSource.
                var language = PlayniteApi?.ApplicationSettings?.Language;
                if (!string.IsNullOrEmpty(language) && language != "LocSource")
                {
                    var langFile = Path.Combine(locDir, $"{language}.xaml");
                    if (File.Exists(langFile))
                    {
                        MergeDictionary(langFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load localization");
            }
        }

        private void MergeDictionary(string filePath)
        {
            var dict = new ResourceDictionary { Source = new Uri(filePath, UriKind.Absolute) };
            Application.Current.Resources.MergedDictionaries.Add(dict);
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
                LoadLocalization();
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
                _manualSourceRegistry = new ManualSourceRegistry(_logger, settings, PlayniteApi, pluginUserDataPath);

                // Create provider registry
                _providerRegistry = new ProviderRegistry(settings, ProviderDisplayOrder, _logger, _manualSourceRegistry);
                _providerRegistry.SyncFromSettings(settings.Persisted);
                _gameCustomDataStore = _settingsViewModel.GameCustomDataStore;
                _gameCustomDataStore.AttachRuntimeSettings(settings);
                TryWarmCustomDataCache();

                List<IDataProvider> providers;
                using (PerfScope.StartStartup(_logger, "PluginCtor.ProviderCreation", thresholdMs: 50))
                {
                    providers = _providerRegistry.CreateProviders(settings, PlayniteApi, pluginUserDataPath);

                    /*// === ADD LOCAL PROVIDER MANUALLY ===
                    var localProvider = new Providers.Local.LocalSavesProvider(PlayniteApi, _logger, settings.Persisted);
                    providers.Add(localProvider);
                    _providerRegistry.RegisterProvider(localProvider); */  // This line may or may not be needed — try without first
                }

                // Phase 3: Wire core services, refresh pipeline, and tagging.
                using (PerfScope.StartStartup(_logger, "PluginCtor.RefreshServiceCreation", thresholdMs: 50))
                {
                    _diskImageService = new DiskImageService(_logger, pluginUserDataPath);
                    _managedCustomIconService = new ManagedCustomIconService(_diskImageService, _logger);
                    _imageService = new MemoryImageService(_logger, _diskImageService);
                    _gameCustomDataStore.AttachManagedCustomIconService(_managedCustomIconService);

                    _refreshService = new RefreshRuntime(api, settings, _logger, this, providers, _diskImageService, _managedCustomIconService, _providerRegistry, ProviderRefreshOrder, onRefreshCompleted: payload => HandleRefreshAuthNotifications(payload));
                    _cacheManager = _refreshService.Cache;
                    _cacheManager.CacheInvalidated += (_, __) =>
                    {
                        try { _imageService?.Clear(); } catch { }
                    };
                    _achievementOverridesService = new AchievementOverridesService(
                        _gameCustomDataStore,
                        _cacheManager,
                        _settingsViewModel.Settings,
                        _logger,
                        _ => SavePluginSettings(_settingsViewModel.Settings),
                        force => _cacheManager.NotifyCacheInvalidated(),
                        gameIds =>
                        {
                            if (gameIds == null || gameIds.Count == 0)
                            {
                                return;
                            }

                            foreach (var gameId in gameIds)
                            {
                                _themeIntegrationService?.RequestUpdate(gameId);
                            }
                        });
                    _achievementDataService = new AchievementDataService(
                        _cacheManager,
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        _logger,
                        _gameCustomDataStore);
                    _gameCustomDataStore.AttachAchievementDataService(_achievementDataService);
                    _notifications = new NotificationPublisher(api, settings, _logger);
                    _forkReleaseMonitor = new ForkReleaseMonitor(
                        api,
                        settings,
                        _notifications,
                        () => SavePluginSettings(_settingsViewModel.Settings),
                        _logger);
                    _refreshCoordinator = new RefreshEntryPoint(
                        _refreshService,
                        _logger,
                        runWithProgressWindow: ShowRefreshProgressControlAndRun);
                    var localAchievementScreenshotService = new LocalAchievementScreenshotService(_logger);
                    _activeGameAchievementMonitor = new ActiveGameAchievementMonitor(
                        _cacheManager,
                        _providerRegistry,
                        _notifications,
                        localAchievementScreenshotService,
                        IsRealtimeNotificationDisabledForGame,
                        _logger);
                    _exophaseGameAchievementMonitor = new Services.Exophase.ExophaseGameAchievementMonitor(
                        _cacheManager,
                        _providerRegistry,
                        _notifications,
                        IsRealtimeNotificationDisabledForGame,
                        _logger);
                    _backgroundUpdates = new BackgroundUpdater(_refreshCoordinator, _refreshService, _cacheManager, settings, _logger, _notifications, null);

                    // Create tag sync service
                    _tagSyncService = new TagSyncService(
                        PlayniteApi,
                        _logger,
                        settings.Persisted);
                    _tagSyncService.InitializeAndSubscribeTaggingSettings();

                    _windowService = new PluginWindowService(
                        PlayniteApi,
                        _logger,
                        _refreshService,
                        _refreshCoordinator,
                        _cacheManager,
                        PersistSettingsForUi,
                        _achievementOverridesService,
                        _achievementDataService,
                        _settingsViewModel.Settings,
                        _manualSourceRegistry,
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
                        _logger,
                        _windowService.RunRefreshWithGlobalProgressAsync);

                    SubscribeDatabaseEventHandlers();

                    RegisterThemeSourceAliases();
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

        private void RegisterThemeSourceAliases()
        {
            var registeredAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var supportedElements = _themeControlRegistry.GetSupportedElementNames();

            foreach (var sourceName in ThemeSourceAliases)
            {
                if (string.IsNullOrWhiteSpace(sourceName) || !registeredAliases.Add(sourceName))
                {
                    continue;
                }

                AddSettingsSupport(new AddSettingsSupportArgs
                {
                    SourceName = sourceName,
                    SettingsRoot = "Settings"
                });

                AddCustomElementSupport(new AddCustomElementSupportArgs
                {
                    ElementList = supportedElements,
                    SourceName = sourceName
                });
            }
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
            _activeGameAchievementMonitor?.Stop();
            _exophaseGameAchievementMonitor?.Stop();
            _ = _activeGameAchievementMonitor?.TryDetectMissedUnlocksAfterStopAsync(args.Game);
            _logger.Info($"Game stopped: {args.Game.Name}. Triggering refresh.");
            _ = _refreshCoordinator.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Single,
                SingleGameId = args.Game.Id
            });
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (args?.Game == null)
            {
                return;
            }

            _activeGameAchievementMonitor?.Start(args.Game);
            _exophaseGameAchievementMonitor?.Start(args.Game);
        }

        // === Lifecycle ===

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            using (PerfScope.StartStartup(_logger, "OnApplicationStarted", thresholdMs: 50))
            {
                _applicationStarted = true;

                try
                {
                    ScrubInvalidLocalImportedGameMedia();
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed to scrub invalid media from local imported games during startup.");
                }

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

                _ = _forkReleaseMonitor?.CheckForForkReleaseAsync();

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

        private void ScrubInvalidLocalImportedGameMedia()
        {
            var games = PlayniteApi?.Database?.Games;
            if (games == null)
            {
                return;
            }

            var updatedGames = 0;
            foreach (var game in games)
            {
                if (game == null || game.Id == Guid.Empty)
                {
                    continue;
                }

                if (!LocalSavesProvider.TryGetAppIdOverride(game.Id, out _) &&
                    !LocalSavesProvider.TryGetFolderOverride(game.Id, out _))
                {
                    continue;
                }

                var changed = false;

                var sanitizedIcon = SanitizeLocalImportedGameImageField(game, game.Icon, "Icon", ref changed);
                if (!string.Equals(game.Icon, sanitizedIcon, StringComparison.Ordinal))
                {
                    game.Icon = sanitizedIcon;
                }

                var sanitizedCover = SanitizeLocalImportedGameImageField(game, game.CoverImage, "CoverImage", ref changed);
                if (!string.Equals(game.CoverImage, sanitizedCover, StringComparison.Ordinal))
                {
                    game.CoverImage = sanitizedCover;
                }

                var sanitizedBackground = SanitizeLocalImportedGameImageField(game, game.BackgroundImage, "BackgroundImage", ref changed);
                if (!string.Equals(game.BackgroundImage, sanitizedBackground, StringComparison.Ordinal))
                {
                    game.BackgroundImage = sanitizedBackground;
                }

                if (!changed)
                {
                    continue;
                }

                PlayniteApi.Database.Games.Update(game);
                updatedGames++;
            }

            if (updatedGames > 0)
            {
                _logger?.Info($"Scrubbed invalid media references from {updatedGames} local imported game(s).");
            }
        }

        private string SanitizeLocalImportedGameImageField(Game game, string imageId, string fieldName, ref bool changed)
        {
            if (string.IsNullOrWhiteSpace(imageId))
            {
                if (imageId != null)
                {
                    changed = true;
                }

                return null;
            }

            var trimmedImageId = imageId.Trim();
            var resolvedPath = PlayniteApi?.Database?.GetFullFilePath(trimmedImageId)?.Trim();
            if (string.IsNullOrWhiteSpace(resolvedPath) ||
                resolvedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                resolvedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                resolvedPath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(resolvedPath))
            {
                changed = true;
                _logger?.Warn($"Clearing invalid {fieldName} for locally imported game '{game?.Name}' ({game?.Id}). Raw='{trimmedImageId}', Resolved='{resolvedPath}'.");
                return null;
            }

            return trimmedImageId;
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
            _activeGameAchievementMonitor?.Dispose();
            _exophaseGameAchievementMonitor?.Dispose();

            try { _imageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose imageService"); }
            try { _diskImageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose diskImageService"); }
            try { _manualSourceRegistry?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose manualSourceRegistry"); }
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
            // Listen for game database changes to auto-refresh new entries and clean up removed games.
            PlayniteApi?.Database?.Games?.ItemCollectionChanged += Games_ItemCollectionChanged;
            _eventSubscriptions.Add(() => PlayniteApi?.Database?.Games?.ItemCollectionChanged -= Games_ItemCollectionChanged);
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
                    _eventSubscriptions.Add(() => _gameCustomDataStore.CustomDataChanged -= GameCustomDataStore_CustomDataChanged);
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

        private void GameCustomDataStore_CustomDataChanged(object sender, GameCustomDataChangedEventArgs e)
        {
            if (e?.PlayniteGameId == Guid.Empty)
            {
                return;
            }

            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (_tagSyncService != null && persisted?.TaggingSettings?.EnableTagging == true)
            {
                _tagSyncService.SyncTagsForGames(new List<Guid> { e.PlayniteGameId });
            }

            try
            {
                _themeIntegrationService?.NotifyCustomDataChanged(e.PlayniteGameId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to refresh theme state after custom-data change for gameId={e.PlayniteGameId}.");
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





