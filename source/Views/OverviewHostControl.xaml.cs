using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;

namespace PlayniteAchievements.Views
{
    /// <summary>
    /// Hosts the overview in Playnite's sidebar view, creating the OverviewControl on each
    /// Loaded, or the first-time landing page when appropriate.
    /// </summary>
    public partial class OverviewHostControl : UserControl
    {
        private readonly Func<UserControl> _createView;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly RefreshRuntime _refreshService;
        private readonly PlayniteAchievementsPlugin _plugin;

        private OverviewControl _overview;
        private FirstTimeLandingPage _landingPage;
        private bool _settingsSavedHooked;

        public OverviewHostControl(
            Func<UserControl> createView,
            ILogger logger,
            IPlayniteAPI api,
            RefreshRuntime refreshRuntime,
            PlayniteAchievementsPlugin plugin)
        {
            _createView = createView ?? throw new ArgumentNullException(nameof(createView));
            _logger = logger;
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            InitializeComponent();
            FormattingCulture.Apply(this);

            Loaded += OverviewHostControl_Loaded;
            Unloaded += OverviewHostControl_Unloaded;
        }

        private void OverviewHostControl_Loaded(object sender, RoutedEventArgs e)
        {
            _logger.Info("OverviewHostControl_Loaded called");
            // Subscribe to the static settings-saved event here rather than in the constructor so
            // the subscription tracks the visual-tree lifecycle. WPF does not guarantee Unloaded
            // fires; hooking in the constructor and unhooking only in Unloaded would permanently
            // root this host (and its overview tree) via the static event whenever Unloaded is
            // skipped. The guard makes a second Loaded without an intervening Unloaded idempotent.
            if (!_settingsSavedHooked)
            {
                PlayniteAchievementsPlugin.SettingsSaved += Plugin_SettingsSaved;
                _settingsSavedHooked = true;
            }

            // Always recreate content when loaded (handles overview reopen)
            RecreateContent();
            _overview?.Activate();
        }

        public void RefreshContent()
        {
            _logger.Info("RefreshContent called - forcing content refresh");
            RecreateContent();
        }

        /// <summary>
        /// Refreshes the provider status on the landing page if it's currently visible.
        /// Called when settings are saved to update authentication status display.
        /// </summary>
        public void RefreshProviderStatus()
        {
            if (_landingPage != null)
            {
                _logger.Info("RefreshProviderStatus called - updating landing page provider status");
                _landingPage.RefreshProviderStatuses();
            }
        }

        private void OverviewHostControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _logger.Debug("OverviewHostControl_Unloaded called");
            using var perf = PerfScope.Start(
                _logger,
                "OverviewHost.Unloaded",
                thresholdMs: 30,
                context: $"overview={_overview != null} landing={_landingPage != null}");
            try
            {
                _overview?.Deactivate();
                _overview?.Dispose();
                _landingPage?.Dispose();
            }
            catch
            {
                // no-op
            }
            finally
            {
                _overview = null;
                _landingPage = null;
                // Clear content to allow recreation on next load
                PART_Content.Content = null;

                // Unsubscribe from settings saved event
                if (_settingsSavedHooked)
                {
                    PlayniteAchievementsPlugin.SettingsSaved -= Plugin_SettingsSaved;
                    _settingsSavedHooked = false;
                }
            }
        }

        private void Plugin_SettingsSaved(object sender, EventArgs e)
        {
            _logger.Info("Settings saved - refreshing views");

            // Refresh landing page provider status if visible
            if (_landingPage != null)
            {
                _landingPage.RefreshProviderStatuses();
            }

            // Refresh overview if visible
            if (_overview != null)
            {
                _overview.RefreshView();
            }
        }

        private void RecreateContent()
        {
            _logger.Info("RecreateContent called - clearing existing content");

            // Dispose existing content
            using (PerfScope.Start(_logger, "OverviewHost.DisposePrevious", thresholdMs: 30))
            {
                _overview?.Dispose();
                _landingPage?.Dispose();
            }

            _overview = null;
            _landingPage = null;
            PART_Content.Content = null;
            CreateContent();
        }

        private void CreateContent()
        {
            if (!IsLoaded)
            {
                _logger.Info("CreateContent: not loaded, skipping");
                return;
            }

            try
            {
                // Use the shared settings object from the plugin (same instance used by providers)
                // This ensures settings changes in landing page are immediately visible to providers
                var settings = _plugin.Settings;
                if (settings != null)
                {
                    // Ensure plugin reference is set for ISettings methods (SavePluginSettings)
                    settings._plugin = _plugin;
                }

                var firstTimeCompleted = settings?.Persisted?.FirstTimeSetupCompleted ?? true;
                var seenThemeMigration = settings?.Persisted?.SeenThemeMigration ?? false;
                var dataService = _plugin?.AchievementDataService;
                var hasCachedData = dataService?.HasCachedGameData() == true;
                _logger.Info($"Overview opening: FirstTimeSetupCompleted={firstTimeCompleted}, SeenThemeMigration={seenThemeMigration}, HasCachedData={hasCachedData}, HasSteamAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<SteamSettings>().SteamUserId)}, HasEpicAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<EpicSettings>().AccountId)}, HasRaAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<RetroAchievementsSettings>().RaUsername)}");

                // Show landing page if:
                // 1. Haven't seen the theme migration page yet (!seenThemeMigration)
                // 2. Haven't seen landing page before (!firstTimeCompleted)
                // 3. No data in achievements_cache (NO MATTER WHAT)
                bool showLandingPage = !seenThemeMigration || !firstTimeCompleted || !hasCachedData;

                if (settings != null && showLandingPage)
                {
                    _logger.Info("Creating landing page");
                    CreateLandingPage(settings);
                }
                else
                {
                    _logger.Info("Creating overview directly");
                    CreateOverview();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to create overview content.");
            }
        }

        private void CreateLandingPage(PlayniteAchievementsSettings settings)
        {
            _logger.Info("Showing first-time landing page.");

            // Mark that the user has seen the theme migration landing page
            if (!settings.Persisted.SeenThemeMigration)
            {
                settings.Persisted.SeenThemeMigration = true;
                _plugin.SavePluginSettings(settings);
                _logger.Info("Set SeenThemeMigration to true");
            }

            _landingPage = new FirstTimeLandingPage(
                _api,
                _logger,
                _refreshService,
                _plugin.RefreshEntryPoint,
                settings,
                _plugin,
                _plugin.ProviderRegistry);

            _landingPage.SetupComplete += LandingPage_SetupComplete;

            PART_Content.Content = _landingPage;
        }

        private void CreateOverview()
        {
            _logger.Info("Creating overview control.");

            OverviewControl control;
            using (PerfScope.Start(_logger, "OverviewHost.CreateView", thresholdMs: 30))
            {
                control = _createView() as OverviewControl;
            }

            if (control == null)
            {
                throw new InvalidOperationException("OverviewHostControl factory did not return OverviewControl.");
            }

            _overview = control;
            PART_Content.Content = _overview;
            using (PerfScope.Start(_logger, "OverviewHost.Activate", thresholdMs: 30))
            {
                _overview.Activate();
            }
        }

        private void LandingPage_SetupComplete(object sender, EventArgs e)
        {
            _logger.Info("First-time setup complete, transitioning to overview.");

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _landingPage?.Dispose();
                    _landingPage = null;

                    CreateOverview();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to transition from landing page to overview.");
                }
            }), DispatcherPriority.Background);
        }
    }
}
