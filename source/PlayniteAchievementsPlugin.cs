using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
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
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.RPCS3;
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
using PlayniteAchievements.Services.ThemeIntegration;
using PlayniteAchievements.Services.ThemeMigration;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Shell;
using System.Windows.Threading;
using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;

namespace PlayniteAchievements
{
    public class PlayniteAchievementsPlugin : GenericPlugin
    {
        private readonly ILogger _logger;


        // Set Properties before constructor runs
        private static readonly GenericPluginProperties _pluginProperties = new GenericPluginProperties
        {
            HasSettings = true
        };

        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly AchievementService _achievementService;
        private readonly MemoryImageService _imageService;
        private readonly DiskImageService _diskImageService;
        private readonly NotificationPublisher _notifications;
        private readonly SteamSessionManager _steamSessionManager;
        private readonly GogSessionManager _gogSessionManager;
        private readonly EpicSessionManager _epicSessionManager;
        private readonly PsnSessionManager _psnSessionManager;
        private readonly XboxSessionManager _xboxSessionManager;
        private readonly ProviderRegistry _providerRegistry;
        private readonly ManualAchievementsProvider _manualProvider;
        private readonly LegacyManualLinkImporter _legacyManualLinkImporter;

        private readonly BackgroundUpdater _backgroundUpdates;
        private readonly RefreshCoordinator _refreshCoordinator;
        private bool _applicationStarted;

        // Theme integration
        private readonly ThemeIntegrationUpdateService _themeUpdateService;
        private readonly FullscreenWindowService _fullscreenWindowService;
        private readonly ThemeIntegrationService _themeIntegrationService;

        // Control factories for theme integration
        private static readonly Dictionary<string, Func<Control>> LegacyControlFactories = new Dictionary<string, Func<Control>>(StringComparer.OrdinalIgnoreCase)
        {
            { "PluginButton", () => new Views.ThemeIntegration.Legacy.PluginButtonControl() },
            { "PluginProgressBar", () => new Views.ThemeIntegration.Legacy.PluginProgressBarControl() },
            { "PluginCompactList", () => new Views.ThemeIntegration.Legacy.PluginCompactListControl() },
            { "PluginCompactLocked", () => new Views.ThemeIntegration.Legacy.PluginCompactLockedControl() },
            { "PluginCompactUnlocked", () => new Views.ThemeIntegration.Legacy.PluginCompactUnlockedControl() },
            { "PluginChart", () => new Views.ThemeIntegration.Legacy.PluginChartControl() },
            { "PluginUserStats", () => new Views.ThemeIntegration.Legacy.PluginUserStatsControl() },
            { "PluginList", () => new Views.ThemeIntegration.Legacy.PluginListControl() },
            { "PluginViewItem", () => new Views.ThemeIntegration.Legacy.PluginViewItemControl() }
        };

        private static readonly Dictionary<string, Func<Control>> DesktopControlFactories = new Dictionary<string, Func<Control>>(StringComparer.OrdinalIgnoreCase)
        {
            { "AchievementButton", () => new Views.ThemeIntegration.Desktop.AchievementButtonControl() },
            { "AchievementProgressBar", () => new Views.ThemeIntegration.Desktop.AchievementProgressBarControl() },
            { "AchievementCompactList", () => new Views.ThemeIntegration.Desktop.AchievementCompactListControl() },
            { "AchievementChart", () => new Views.ThemeIntegration.Desktop.AchievementChartControl() },
            { "AchievementStats", () => new Views.ThemeIntegration.Desktop.AchievementStatsControl() },
            { "AchievementList", () => new Views.ThemeIntegration.Desktop.AchievementListControl() }
        };

        public override Guid Id { get; } =
            Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");

        public PlayniteAchievementsSettings Settings => _settingsViewModel.Settings;
        public ProviderRegistry ProviderRegistry => _providerRegistry;
        public AchievementService AchievementService => _achievementService;
        public MemoryImageService ImageService => _imageService;
        public ThemeIntegrationService ThemeIntegrationService => _themeIntegrationService;
        public ThemeIntegrationUpdateService ThemeUpdateService => _themeUpdateService;
        public SteamSessionManager SteamSessionManager => _steamSessionManager;
        public EpicSessionManager EpicSessionManager => _epicSessionManager;
        internal RefreshCoordinator RefreshCoordinator => _refreshCoordinator;
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

            using (PerfScope.StartStartup(_logger, "PluginCtor.Total", thresholdMs: 50))
            {
                Properties = _pluginProperties;

                Instance = this;
                _logger.Info("PlayniteAchievementsPlugin initializing...");

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

                // Configure rarity thresholds from settings
                RarityHelper.Configure(
                    _settingsViewModel.Settings.Persisted.UltraRareThreshold,
                    _settingsViewModel.Settings.Persisted.RareThreshold,
                    _settingsViewModel.Settings.Persisted.UncommonThreshold);

                var settings = _settingsViewModel.Settings;
                var pluginUserDataPath = GetPluginUserDataPath();

                using (PerfScope.StartStartup(_logger, "PluginCtor.SessionManagers", thresholdMs: 50))
                {
                    // Create shared Steam session manager for use by provider and settings UI
                    _steamSessionManager = new SteamSessionManager(PlayniteApi, _logger, settings);
                    _gogSessionManager = new GogSessionManager(PlayniteApi, _logger, settings);
                    _epicSessionManager = new EpicSessionManager(PlayniteApi, _logger, settings);
                    _psnSessionManager = new PsnSessionManager(PlayniteApi, _logger, settings.Persisted);
                    _xboxSessionManager = new XboxSessionManager(PlayniteApi, _logger, settings.Persisted);
                }

                List<IDataProvider> providers;
                using (PerfScope.StartStartup(_logger, "PluginCtor.ProviderCreation", thresholdMs: 50))
                {
                    _manualProvider = new ManualAchievementsProvider(_logger, settings, pluginUserDataPath);
                    providers = new List<IDataProvider>
                    {
                        _manualProvider,  // Manual provider first - explicit user overrides take priority
                        new SteamDataProvider(
                            _logger,
                            settings,
                            PlayniteApi,
                            _steamSessionManager,
                            pluginUserDataPath),
                        new GogDataProvider(
                            _logger,
                            settings,
                            PlayniteApi,
                            pluginUserDataPath,
                            _gogSessionManager),
                        new EpicDataProvider(
                            _logger,
                            settings,
                            PlayniteApi,
                            _epicSessionManager),
                        new PsnDataProvider(
                            _logger,
                            settings,
                            _psnSessionManager),
                        new XboxDataProvider(
                            _logger,
                            settings,
                            _xboxSessionManager),
                        new RetroAchievementsDataProvider(
                            _logger,
                            settings,
                            PlayniteApi,
                            pluginUserDataPath),
                        new ShadPS4DataProvider(
                            _logger,
                            settings,
                            PlayniteApi),
                        new Rpcs3DataProvider(
                            _logger,
                            settings,
                            PlayniteApi)
                    };
                }

                using (PerfScope.StartStartup(_logger, "PluginCtor.AchievementServiceCreation", thresholdMs: 50))
                {
                    _diskImageService = new DiskImageService(_logger, pluginUserDataPath);
                    _imageService = new MemoryImageService(_logger, _diskImageService);

                    // Create provider registry and sync from persisted settings
                    _providerRegistry = new ProviderRegistry();
                    _providerRegistry.SyncFromSettings(settings.Persisted);

                    _achievementService = new AchievementService(api, settings, _logger, this, providers, _diskImageService, _providerRegistry);
                    _notifications = new NotificationPublisher(api, settings, _logger);
                    _refreshCoordinator = new RefreshCoordinator(_achievementService, _logger, ShowRefreshProgressControlAndRun);
                    _backgroundUpdates = new BackgroundUpdater(_refreshCoordinator, _achievementService, settings, _logger, _notifications, null);
                    _legacyManualLinkImporter = new LegacyManualLinkImporter(
                        () => _settingsViewModel?.Settings?.Persisted,
                        gameId => PlayniteApi?.Database?.Games?.Get(gameId) != null,
                        gameId => _achievementService.GetRawGameAchievementData(gameId) != null,
                        _logger);

                    try
                    {
                        if (settings?.Persisted != null)
                        {
                            settings.Persisted.PropertyChanged += PersistedSettings_PropertyChanged;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Failed to subscribe to persisted settings changes.");
                    }
                }

                using (PerfScope.StartStartup(_logger, "PluginCtor.ThemeServicesWiring", thresholdMs: 50))
                {
                    // Create theme integration services
                    // Note: We need to create _themeIntegrationService before _themeUpdateService,
                    // but _themeIntegrationService needs a callback to _themeUpdateService.
                    // We resolve this by using a local variable for the callback.
                    ThemeIntegrationUpdateService themeUpdateService = null;
                    Action<Guid?> requestUpdate = (id) => themeUpdateService?.RequestUpdate(id);

                    _fullscreenWindowService = new FullscreenWindowService(
                        PlayniteApi,
                        _settingsViewModel.Settings,
                        requestUpdate);

                    _themeIntegrationService = new ThemeIntegrationService(
                        PlayniteApi,
                        _achievementService,
                        _refreshCoordinator,
                        _settingsViewModel.Settings,
                        _fullscreenWindowService,
                        requestUpdate,
                        _logger);

                    themeUpdateService = new ThemeIntegrationUpdateService(
                        _themeIntegrationService,
                        _achievementService,
                        _settingsViewModel.Settings,
                        _logger,
                        PlayniteApi?.MainView?.UIDispatcher ?? System.Windows.Application.Current.Dispatcher);
                    _themeUpdateService = themeUpdateService;

                    // Listen for new games entering the database to auto-refresh .
                    PlayniteApi?.Database?.Games?.ItemCollectionChanged += Games_ItemCollectionChanged;

                    // Theme integration - settings support
                    // SettingsRoot = "Settings" tells Playnite to access the Settings property on the ViewModel
                    // Theme bindings like {PluginSettings Plugin=PlayniteAchievements, Path=HasAchievements}
                    // will resolve to ViewModel.Settings.HasAchievements
                    AddSettingsSupport(new AddSettingsSupportArgs
                    {
                        SourceName = "PlayniteAchievements",
                        SettingsRoot = "Settings"
                    });

                    // Custom elements integration
                    AddCustomElementSupport(new AddCustomElementSupportArgs
                    {
                        ElementList = new List<string>
                        {
                            // SuccessStory-compatible controls (legacy naming; properties are also exposed via native keys)
                            "PluginButton",
                            "PluginProgressBar",
                            "PluginCompactList",
                            "PluginCompactLocked",
                            "PluginCompactUnlocked",
                            "PluginChart",
                            "PluginUserStats",
                            "PluginList",
                            "PluginViewItem",

                            // Native PlayniteAchievements controls (always available)
                            "AchievementButton",
                            "AchievementProgressBar",
                            "AchievementCompactList",
                            "AchievementChart",
                            "AchievementStats",
                            "AchievementList"
                        },
                        SourceName = "PlayniteAchievements"
                    });
                }

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
                var control = new SettingsControl(_settingsViewModel, _logger, this, _steamSessionManager, _gogSessionManager, _epicSessionManager, _psnSessionManager, _xboxSessionManager);
                _logger.Info("GetSettingsView succeeded");
                return control;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GetSettingsView failed");
                throw;
            }
        }

        // === Menus ===

        private const string PluginGameMenuSection = "Playnite Achievements";
        private const string PluginMainMenuSection = "@Playnite Achievements";

        private bool IsRefreshInProgress()
        {
            return _achievementService?.IsRebuilding == true;
        }

        private IEnumerable<GameMenuItem> GetRefreshInProgressGameMenuHeader(Guid? singleGameRefreshId = null)
        {
            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshInProgress"),
                MenuSection = PluginGameMenuSection
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ViewRefreshProgress"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    ShowRefreshProgressControl(singleGameRefreshId);
                }
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Button_Cancel"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    _achievementService.CancelCurrentRebuild();
                }
            };

            yield return new GameMenuItem
            {
                Description = "-",
                MenuSection = PluginGameMenuSection
            };
        }

        private IEnumerable<MainMenuItem> GetRefreshInProgressMainMenuHeader()
        {
            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshInProgress"),
                MenuSection = PluginMainMenuSection
            };

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ViewRefreshProgress"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    ShowRefreshProgressControl();
                }
            };

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Button_Cancel"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    _achievementService.CancelCurrentRebuild();
                }
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args?.Games == null || args.Games.Count == 0)
            {
                yield break;
            }

            var refreshInProgress = IsRefreshInProgress();

            // Multiple games selected - offer "Refresh Selected"
            if (args.Games.Count > 1)
            {
                var selectedGames = args.Games.Where(g => g != null).ToList();
                if (selectedGames.Count == 0)
                {
                    yield break;
                }

                if (refreshInProgress)
                {
                    foreach (var item in GetRefreshInProgressGameMenuHeader())
                    {
                        yield return item;
                    }
                }
                else
                {
                    yield return new GameMenuItem
                    {
                        Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshSelected"),
                        MenuSection = PluginGameMenuSection,
                        Action = (a) =>
                        {
                            var selectedIds = selectedGames
                                .Select(g => g.Id)
                                .Where(id => id != Guid.Empty)
                                .Distinct()
                                .ToList();

                            _ = _refreshCoordinator.ExecuteAsync(
                                new RefreshRequest { GameIds = selectedIds },
                                RefreshExecutionPolicy.ProgressWindow());
                        }
                    };
                }

                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_ClearData"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        ClearSelectedGamesData(selectedGames);
                    }
                };

                // Add bulk exclude/include based on majority state
                var excludedIds = _settingsViewModel.Settings.Persisted.ExcludedGameIds;
                var excludedCount = selectedGames.Count(g => excludedIds.Contains(g.Id));
                var mostlyExcluded = excludedCount > selectedGames.Count / 2;

                yield return new GameMenuItem
                {
                    Description = mostlyExcluded
                        ? ResourceProvider.GetString("LOCPlayAch_Menu_IncludeGames")
                        : ResourceProvider.GetString("LOCPlayAch_Menu_ExcludeGames"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        if (!mostlyExcluded)
                        {
                            var result = PlayniteApi?.Dialogs?.ShowMessage(
                                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSelected"), selectedGames.Count),
                                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning) ?? MessageBoxResult.None;

                            if (result != MessageBoxResult.Yes)
                            {
                                return;
                            }
                        }

                        foreach (var g in selectedGames)
                        {
                            _achievementService.SetExcludedByUser(g.Id, !mostlyExcluded);
                        }
                    }
                };

                yield break;
            }

            // Single game selected
            var game = args.Games[0];
            if (game == null)
            {
                yield break;
            }

            if (refreshInProgress)
            {
                foreach (var item in GetRefreshInProgressGameMenuHeader(game.Id))
                {
                    yield return item;
                }
            }

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ViewAchievements"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    OpenSingleGameAchievementsView(game.Id);
                }
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_GameOptions"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    OpenGameOptionsView(game.Id);
                }
            };

            if (!refreshInProgress)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshGame"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest
                            {
                                Mode = RefreshModeType.Single,
                                SingleGameId = game.Id
                            },
                            RefreshExecutionPolicy.ProgressWindow(game.Id));
                    }
                };
            }

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ClearData"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    ClearSingleGameData(game);
                }
            };
        }

        private void ClearSingleGameData(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var result = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_ConfirmSingle"),game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _achievementService.RemoveGameCache(game.Id);
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_ConfirmSingle"), game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear cached data for game '{game.Name}' ({game.Id}).");
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ClearSelectedGamesData(IEnumerable<Game> selectedGames)
        {
            var targets = selectedGames?
                .Where(g => g != null && g.Id != Guid.Empty)
                .GroupBy(g => g.Id)
                .Select(g => g.First())
                .ToList() ?? new List<Game>();

            if (targets.Count == 0)
            {
                return;
            }

            var result = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_ConfirmSelected"), targets.Count),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var clearedCount = 0;
            foreach (var game in targets)
            {
                try
                {
                    _achievementService.RemoveGameCache(game.Id);
                    clearedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to clear cached data for game '{game.Name}' ({game.Id}).");
                }
            }

            PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_SuccessSelected"), clearedCount),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ShowRaGameIdDialog(Game game)
        {
            if (game == null)
            {
                return;
            }

            var hasExistingOverride = _settingsViewModel.Settings.Persisted.RaGameIdOverrides.TryGetValue(game.Id, out var existingId);
            var defaultText = hasExistingOverride ? existingId.ToString() : string.Empty;

            // Use custom text input dialog
            var inputDialog = new TextInputDialog(
                ResourceProvider.GetString("LOCPlayAch_Menu_RaGameId_DialogHint"),
                defaultText);

            var windowOptions = new WindowOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                ShowCloseButton = true,
                CanBeResizable = false,
                Width = 450,
                Height = 200
            };

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_Menu_RaGameId_DialogTitle"),
                inputDialog,
                windowOptions);

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = PlayniteApi?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch (Exception ex) { _logger?.Debug(ex, "Failed to set window owner"); }

            inputDialog.RequestClose += (s, ev) => window.Close();
            window.ShowDialog();

            if (inputDialog.DialogResult != true)
            {
                return;
            }

            var inputText = inputDialog.InputText?.Trim();
            if (string.IsNullOrWhiteSpace(inputText))
            {
                return;
            }

            if (!int.TryParse(inputText, out var newId) || newId <= 0)
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_RaGameId_InvalidId"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            TrySetRaGameIdOverride(game.Id, newId);
        }

        public bool TrySetRaGameIdOverride(Guid gameId, int newId)
        {
            var game = PlayniteApi?.Database?.Games?.Get(gameId);
            if (game == null || newId <= 0)
            {
                return false;
            }

            _settingsViewModel.Settings.Persisted.RaGameIdOverrides[game.Id] = newId;
            SavePluginSettings(_settingsViewModel.Settings);

            _logger?.Info($"Set RA game ID override for '{game.Name}' to {newId}");

            _ = _refreshCoordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = game.Id
                },
                RefreshExecutionPolicy.ProgressWindow(game.Id));

            return true;
        }

        private void ClearRaGameIdOverride(Game game)
        {
            if (game == null)
            {
                return;
            }

            if (!_settingsViewModel.Settings.Persisted.RaGameIdOverrides.ContainsKey(game.Id))
            {
                return;
            }

            var result = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_RaGameId_ClearConfirm"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            _settingsViewModel.Settings.Persisted.RaGameIdOverrides.Remove(game.Id);
            SavePluginSettings(_settingsViewModel.Settings);

            _logger?.Info($"Cleared RA game ID override for '{game.Name}'");

            PlayniteApi?.Dialogs?.ShowMessage(
                ResourceProvider.GetString("LOCPlayAch_Menu_RaGameId_Cleared"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows the RetroAchievements game ID override dialog for a game.
        /// </summary>
        public void ShowRaGameIdDialogForGame(Guid gameId)
        {
            var game = PlayniteApi?.Database?.Games?.Get(gameId);
            if (game != null)
            {
                ShowRaGameIdDialog(game);
            }
        }

        /// <summary>
        /// Clears the RetroAchievements game ID override for a game.
        /// </summary>
        public void ClearRaGameIdOverrideForGame(Guid gameId)
        {
            var game = PlayniteApi?.Database?.Games?.Get(gameId);
            if (game != null)
            {
                ClearRaGameIdOverride(game);
            }
        }

        /// <summary>
        /// Checks if a game is capable of using RetroAchievements.
        /// </summary>
        public bool IsRaCapable(Guid gameId)
        {
            var game = PlayniteApi?.Database?.Games?.Get(gameId);
            if (game == null) return false;

            var raProvider = _achievementService.GetProviders()
                .FirstOrDefault(p => p.ProviderKey == "RetroAchievements");

            return raProvider?.IsCapable(game) == true;
        }

        /// <summary>
        /// Checks if a game has a RetroAchievements game ID override.
        /// </summary>
        public bool HasRaGameIdOverride(Guid gameId)
        {
            return _settingsViewModel.Settings.Persisted.RaGameIdOverrides.ContainsKey(gameId);
        }

        /// <summary>
        /// Checks if a game is excluded by the user.
        /// </summary>
        public bool IsGameExcluded(Guid gameId)
        {
            return _settingsViewModel.Settings.Persisted.ExcludedGameIds.Contains(gameId);
        }

        /// <summary>
        /// Toggles the exclusion state of a game. Shows confirmation when excluding.
        /// </summary>
        public void ToggleGameExclusion(Guid gameId)
        {
            var isExcluded = IsGameExcluded(gameId);

            // Only show confirmation when excluding (not when including)
            if (!isExcluded)
            {
                var game = PlayniteApi?.Database?.Games?.Get(gameId);
                var gameName = game?.Name ?? ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame");

                var result = PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSingle"), gameName),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _achievementService.SetExcludedByUser(gameId, !isExcluded);
        }

        private bool UnlinkManualAchievements(Game game)
        {
            if (game == null)
            {
                return false;
            }

            var result = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_UnlinkAchievements_Confirm"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return false;
            }

            _settingsViewModel.Settings.Persisted.ManualAchievementLinks.Remove(game.Id);
            SavePluginSettings(_settingsViewModel.Settings);

            _logger?.Info($"Unlinked manual achievements for '{game.Name}'");

            PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_UnlinkAchievements_Success"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return true;
        }

        public bool UnlinkManualAchievementsForGame(Guid gameId)
        {
            var game = PlayniteApi?.Database?.Games?.Get(gameId);
            if (game == null)
            {
                return false;
            }

            return UnlinkManualAchievements(game);
        }

        internal LegacyManualImportResult ImportLegacyManualLinks(string folderPath)
        {
            var result = _legacyManualLinkImporter?.Import(folderPath) ?? new LegacyManualImportResult();

            if (result.Imported > 0 && !_settingsViewModel.Settings.Persisted.ManualEnabled)
            {
                _settingsViewModel.Settings.Persisted.ManualEnabled = true;
                result.ManualProviderAutoEnabled = true;
            }

            SavePluginSettings(_settingsViewModel.Settings);
            _providerRegistry?.SyncFromSettings(_settingsViewModel.Settings.Persisted);
            NotifySettingsSaved();

            return result;
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var refreshInProgress = IsRefreshInProgress();
            if (refreshInProgress)
            {
                foreach (var item in GetRefreshInProgressMainMenuHeader())
                {
                    yield return item;
                }
            }

            if (!refreshInProgress)
            {
                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Recent"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Recent },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Full"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Full },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Installed"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Installed },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Favorites"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Favorites },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Selected"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.LibrarySelected },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Missing"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Missing },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_MenuItem"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        if (!CustomRefreshControl.TryShowDialog(
                            PlayniteApi,
                            _achievementService,
                            _settingsViewModel.Settings,
                            _logger,
                            out var customOptions))
                        {
                            return;
                        }

                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest
                            {
                                Mode = RefreshModeType.Custom,
                                CustomOptions = customOptions
                            },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };
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
                        () => new SidebarControl(PlayniteApi, _logger, _achievementService, _refreshCoordinator, _settingsViewModel.Settings),
                        _logger,
                        PlayniteApi,
                        _achievementService,
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

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            _logger.Info($"Game stopped: {args.Game.Name}. Triggering achievement refresh.");
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

                try
                {
                    EnsureAchievementResourcesLoaded();
                }
                catch
                {
                    // ignore
                }

                // Defer authentication probing to avoid WebView disposal race condition during plugin initialization.
                // This ensures the UI dispatcher is fully ready and serializes WebView creation.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false); // Let Playnite fully initialize
                    try
                    {
                        await _gogSessionManager.PrimeAuthenticationStateAsync(default).ConfigureAwait(false);
                    }
                    catch { }
                    try
                    {
                        await _epicSessionManager.PrimeAuthenticationStateAsync(default).ConfigureAwait(false);
                    }
                    catch { }
                    try
                    {
                        await _psnSessionManager.PrimeAuthenticationStateAsync(default).ConfigureAwait(false);
                    }
                    catch { }
                    try
                    {
                        await _xboxSessionManager.PrimeAuthenticationStateAsync(default).ConfigureAwait(false);
                    }
                    catch { }
                });

                // Auto-migrate themes that have been updated since the last migration.
                AutoMigrateUpgradedThemesOnStartup();

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

        private void AutoMigrateUpgradedThemesOnStartup()
        {
            try
            {
                // Run migration on background thread to avoid blocking UI.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var themesDiscovery = new ThemeDiscoveryService(_logger, PlayniteApi);
                        var themesPath = themesDiscovery.GetDefaultThemesPath();
                        if (string.IsNullOrWhiteSpace(themesPath))
                        {
                            return;
                        }

                        // Seed cache with existing migrated themes if cache is empty
                        var persisted = _settingsViewModel?.Settings?.Persisted;
                        var wasCacheEmpty = persisted != null && (persisted.ThemeMigrationVersionCache == null || persisted.ThemeMigrationVersionCache.Count == 0);

                        // Discover themes once (without cache to get full list)
                        var themes = themesDiscovery.DiscoverThemes(themesPath, null);

                        if (wasCacheEmpty)
                        {
                            var seededThemes = themes.Where(t => t.HasBackup && !string.IsNullOrWhiteSpace(t.CurrentThemeVersion)).ToList();

                            if (seededThemes.Count > 0)
                            {
                                foreach (var theme in seededThemes)
                                {
                                    persisted.ThemeMigrationVersionCache[theme.Path] = new ThemeMigrationCacheEntry
                                    {
                                        ThemePath = theme.Path,
                                        ThemeName = theme.BestDisplayName,
                                        MigratedThemeVersion = theme.CurrentThemeVersion,
                                        MigratedAtUtc = DateTime.UtcNow
                                    };
                                }

                                SavePluginSettings(_settingsViewModel.Settings);
                                _logger.Info($"Seeded ThemeMigrationVersionCache with {seededThemes.Count} existing migrated themes.");
                            }
                        }

                        // Determine upgraded themes: those with cache entry whose version differs from current
                        var cache = _settingsViewModel?.Settings?.Persisted?.ThemeMigrationVersionCache;
                        var upgraded = themes
                            .Where(t => t != null && t.NeedsMigration && !string.IsNullOrWhiteSpace(t.CurrentThemeVersion))
                            .Where(t => cache != null && cache.TryGetValue(t.Path, out var cached) &&
                                        !string.IsNullOrWhiteSpace(cached.MigratedThemeVersion) &&
                                        !string.Equals(cached.MigratedThemeVersion, t.CurrentThemeVersion, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (upgraded.Count == 0)
                        {
                            return;
                        }

                        // Create migration service with save settings callback.
                        var migrationService = new ThemeMigrationService(
                            _logger,
                            _settingsViewModel?.Settings,
                            () => SavePluginSettings(_settingsViewModel.Settings));

                        var migratedThemes = new List<string>();

                        foreach (var theme in upgraded)
                        {
                            try
                            {
                                var result = await migrationService.MigrateThemeAsync(theme.Path);
                                if (result.Success)
                                {
                                    _logger.Info($"Auto-migrated upgraded theme: {theme.Name}");
                                    migratedThemes.Add(theme.BestDisplayName);
                                }
                                else
                                {
                                    _logger.Warn($"Failed to auto-migrate upgraded theme '{theme.Name}': {result.Message}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, $"Exception auto-migrating upgraded theme: {theme.Name}");
                            }
                        }

                        // Show notification for successfully migrated themes.
                        if (migratedThemes.Count > 0)
                        {
                            var dispatcher = PlayniteApi?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                            if (dispatcher != null)
                            {
                                _ = dispatcher.BeginInvoke(new Action(() =>
                                {
                                    foreach (var themeName in migratedThemes)
                                    {
                                        _notifications?.ShowThemeAutoMigrated(themeName);
                                    }
                                }));
                            }
                            else
                            {
                                foreach (var themeName in migratedThemes)
                                {
                                    _notifications?.ShowThemeAutoMigrated(themeName);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Failed startup theme auto-migration.");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to schedule startup theme auto-migration.");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            _logger.Info("OnApplicationStopped called.");
            _applicationStarted = false;
            // Stop startup init if still running
            try
            {


                PlayniteApi?.Database?.Games?.ItemCollectionChanged -= Games_ItemCollectionChanged;
                var persisted = _settingsViewModel?.Settings?.Persisted;
                if (persisted != null)
                {
                    persisted.PropertyChanged -= PersistedSettings_PropertyChanged;
                }

            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error during application shutdown cleanup.");
            }

            _backgroundUpdates.Stop();

            try { _imageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose imageService"); }
            try { _diskImageService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose diskImageService"); }
            try { _themeUpdateService?.Dispose(); } catch (Exception ex) { _logger?.Debug(ex, "Failed to dispose themeUpdateService"); }
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
                        _themeUpdateService.RequestUpdate(null);
                        _themeIntegrationService?.NotifySelectionChanged(null);
                        _settingsViewModel.Settings.SelectedGame = null;
                        return;
                    }

                    _themeUpdateService.RequestUpdate(game.Id);
                    _themeIntegrationService?.NotifySelectionChanged(game.Id);
                    _settingsViewModel.Settings.SelectedGame = game;
                }
                else
                {
                    // Clear theme data when no game or multiple games selected
                    _themeUpdateService.RequestUpdate(null);
                    _themeIntegrationService?.NotifySelectionChanged(null);
                    _settingsViewModel.Settings.SelectedGame = null;
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
            _themeUpdateService?.RequestUpdate(gameContext?.Id);
        }

        private void ShowRefreshProgressControlAndRun(Func<Task> refreshTask, Guid? singleGameRefreshId = null)
        {
            ShowRefreshProgressControl(singleGameRefreshId, refreshTask, validateCanStart: true);
        }

        private void ShowRefreshProgressControl(
            Guid? singleGameRefreshId = null,
            Func<Task> refreshTask = null,
            bool validateCanStart = false)
        {
            try
            {
                // Validate authentication before showing progress window
                if (validateCanStart && !_achievementService.ValidateCanStartRefresh())
                {
                    return;
                }

                var progressWindow = new RefreshProgressControl(
                    _achievementService,
                    _logger,
                    singleGameRefreshId,
                    OpenSingleGameAchievementsView);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 400,
                    Height = 280
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(
                    progressWindow.WindowTitle,
                    progressWindow,
                    windowOptions
                );

                try
                {
                    if (window.Owner == null)
                    {
                        window.Owner = PlayniteApi?.Dialogs?.GetCurrentAppWindow();
                    }
                }
                catch { }

                // Wire up close button from UserControl
                progressWindow.RequestClose += (s, ev) => window.Close();

                window.Closed += (s, ev) => { };

                var isFullscreen = false;
                try
                {
                    isFullscreen = PlayniteApi?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
                }
                catch (Exception ex) { _logger?.Debug(ex, "Failed to check fullscreen mode"); }

                if (refreshTask != null)
                {
                    // Start the refresh task after setting up window
                    Task.Run(async () =>
                    {
                        try
                        {
                            await refreshTask().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Refresh task failed");
                        }
                    });
                }

                if (isFullscreen)
                {
                    window.Show();
                    try
                    {
                        window.Topmost = true;
                        window.Activate();
                        window.Topmost = false;
                    }
                    catch (Exception ex) { _logger?.Debug(ex, "Failed to activate window in fullscreen"); }
                }
                else
                {
                    window.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to show refresh progress window");
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            // SuccessStory-compatible controls (legacy naming; properties are always populated).
            if (LegacyControlFactories.TryGetValue(args.Name, out var successStoryFactory))
            {
                return successStoryFactory();
            }

            // Desktop PlayniteAchievements controls (always available)
            if (DesktopControlFactories.TryGetValue(args.Name, out var desktopFactory))
            {
                return desktopFactory();
            }

            return null;
        }

        // === End Theme Integration ===

        /// <summary>
        /// Opens the per-game achievements view window for the specified game.
        /// Public for access from theme integration controls.
        /// </summary>
        public void OpenSingleGameAchievementsView(Guid gameId)
        {
            try
            {
                var view = new SingleGameControl(
                    gameId,
                    _achievementService,
                    PlayniteApi,
                    _logger,
                    _settingsViewModel.Settings);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 800,
                    Height = 700
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(
                    view.WindowTitle,
                    view,
                    windowOptions
                );

                window.MinWidth = 450;
                window.MinHeight = 500;
                try
                {
                    if (window.Owner == null)
                    {
                        window.Owner = PlayniteApi?.Dialogs?.GetCurrentAppWindow();
                    }
                }
                catch { }
                window.Closed += (s, ev) => view.Cleanup();

                var isFullscreen = false;
                try
                {
                    isFullscreen = PlayniteApi?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
                }
                catch (Exception ex) { _logger?.Debug(ex, "Failed to check fullscreen mode"); }

                // In fullscreen mode, modal dialogs can effectively lock the UI if they fail to surface above the theme.
                // Prefer a non-modal window.
                if (isFullscreen)
                {
                    window.Show();
                    try
                    {
                        // Force focus/foreground in fullscreen themes.
                        window.Topmost = true;
                        window.Activate();
                        window.Topmost = false;
                    }
                    catch { }
                }
                else
                {
                    window.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open per-game achievements view for gameId={gameId}");
                PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Failed to open achievements view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        public void OpenGameOptionsView(Guid gameId, GameOptionsTab initialTab = GameOptionsTab.Overview)
        {
            try
            {
                var game = PlayniteApi?.Database?.Games?.Get(gameId);
                if (game == null)
                {
                    PlayniteApi?.Dialogs?.ShowErrorMessage(
                        ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                    return;
                }

                EnsureAchievementResourcesLoaded();

                var view = new GameOptionsControl(
                    gameId,
                    initialTab,
                    _achievementService,
                    PlayniteApi,
                    _logger,
                    _settingsViewModel.Settings,
                    _manualProvider);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1080,
                    Height = 760
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(
                    view.WindowTitle,
                    view,
                    windowOptions);

                window.MinWidth = 860;
                window.MinHeight = 620;
                try
                {
                    if (window.Owner == null)
                    {
                        window.Owner = PlayniteApi?.Dialogs?.GetCurrentAppWindow();
                    }
                }
                catch { }

                window.Closed += (s, e) => view.Cleanup();

                var isFullscreen = false;
                try
                {
                    isFullscreen = PlayniteApi?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
                }
                catch (Exception ex) { _logger?.Debug(ex, "Failed to check fullscreen mode"); }

                if (isFullscreen)
                {
                    window.Show();
                    try
                    {
                        window.Topmost = true;
                        window.Activate();
                        window.Topmost = false;
                    }
                    catch (Exception ex) { _logger?.Debug(ex, "Failed to activate Game Options window in fullscreen"); }
                }
                else
                {
                    window.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open Game Options view for gameId={gameId}");
                PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Failed to open game options view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        public void OpenCapstoneView(Guid gameId)
        {
            OpenGameOptionsView(gameId, GameOptionsTab.Capstones);
        }

        private void EnsureAchievementResourcesLoaded()
        {
            try
            {
                var app = Application.Current;
                if (app == null)
                {
                    return;
                }

                void LoadResources()
                {
                    // Check if already loaded
                    if (app.Resources.Contains("BadgeShadow"))
                    {
                        return;
                    }

                    // Load rarity badges (geometries, fills, badge images)
                    var badgesUri = new Uri("/PlayniteAchievements;component/Resources/RarityBadges.xaml", UriKind.Relative);
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = badgesUri });

                    // Load trophy badges (trophy geometries, fills, badge images)
                    var trophyUri = new Uri("/PlayniteAchievements;component/Resources/TrophyBadges.xaml", UriKind.Relative);
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = trophyUri });

                    // Load achievement templates (datagrid styles, templates, converters)
                    var templatesUri = new Uri("/PlayniteAchievements;component/Resources/AchievementTemplates.xaml", UriKind.Relative);
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = templatesUri });

                    // Load provider icons (platform geometries)
                    var providerUri = new Uri("/PlayniteAchievements;component/Resources/ProviderIcons.xaml", UriKind.Relative);
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = providerUri });
                }

                if (app.Dispatcher.CheckAccess())
                {
                    LoadResources();
                }
                else
                {
                    app.Dispatcher.BeginInvoke((Action)LoadResources, DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to load achievement resources at application level.");
            }
        }

        private enum ParityTestMode
        {
            Native,
            Compatibility
        }

        private void OpenParityTestView(Guid gameId, ParityTestMode mode)
        {
            try
            {
                var game = PlayniteApi?.Database?.Games?.Get(gameId);
                if (game == null)
                {
                    PlayniteApi?.Dialogs?.ShowErrorMessage(
                        ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                    return;
                }

                UserControl view;
                string title;

                if (mode == ParityTestMode.Native)
                {
                    view = new Views.ParityTests.NativeParityTestView(game);
                    title = "PlayniteAchievements UI Parity Test (Native)";
                }
                else
                {
                    view = new Views.ParityTests.CompatibilityParityTestView(game);
                    title = "PlayniteAchievements UI Parity Test (Compatibility)";
                }

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 980,
                    Height = 780
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(title, view, windowOptions);
                window.MinWidth = 700;
                window.MinHeight = 500;

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open parity test view for gameId={gameId}, mode={mode}");
                PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Failed to open parity test view: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
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
                    _achievementService.RemoveGameCache(game.Id);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed cleanup for removed game '{game?.Name}' ({game?.GameId}).");
                }
            });
        }
    }
}


