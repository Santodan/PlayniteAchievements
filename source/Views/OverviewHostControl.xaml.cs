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
    /// Hosts the overview in Playnite's sidebar view, or the first-time landing page when
    /// appropriate. The plugin keeps one host for the session, and the overview stays built
    /// while another view is shown.
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

            // The host lives for the session, so the static event roots nothing extra.
            PlayniteAchievementsPlugin.SettingsSaved += Plugin_SettingsSaved;
            Loaded += OverviewHostControl_Loaded;
            Unloaded += OverviewHostControl_Unloaded;
        }

        private void OverviewHostControl_Loaded(object sender, RoutedEventArgs e)
        {
            _logger.Info("OverviewHostControl_Loaded called");
            // The kept overview is shown again and reads what changed while it was hidden, unless
            // the landing page applies now (the cache was cleared meanwhile).
            if (_overview != null && !ShowLandingPage(_plugin.Settings))
            {
                _overview.Activate();
                return;
            }

            RecreateContent();
        }

        // Hidden, the overview stays built with the rows it read and starts no reads; Activate on
        // the next visit reads again only after a change. The landing page is rebuilt each visit.
        private void OverviewHostControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _logger.Debug("OverviewHostControl_Unloaded called");
            _overview?.Deactivate();
            if (_landingPage != null)
            {
                _landingPage.Dispose();
                _landingPage = null;
                PART_Content.Content = null;
            }

            // What the hidden overview holds, once the view that replaced it has settled.
            PlayniteAchievementsPlugin.Instance?.ScheduleRetentionDiagnostics("overview.hidden", delaySeconds: 8);
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

                if (ShowLandingPage(settings))
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

        // Show landing page if:
        // 1. Haven't seen the theme migration page yet (!seenThemeMigration)
        // 2. Haven't seen landing page before (!firstTimeCompleted)
        // 3. No data in achievements_cache (NO MATTER WHAT)
        private bool ShowLandingPage(PlayniteAchievementsSettings settings)
        {
            var firstTimeCompleted = settings?.Persisted?.FirstTimeSetupCompleted ?? true;
            var seenThemeMigration = settings?.Persisted?.SeenThemeMigration ?? false;
            var dataService = _plugin?.AchievementDataService;
            var hasCachedData = dataService?.HasCachedGameData() == true;
            _logger.Info($"Overview opening: FirstTimeSetupCompleted={firstTimeCompleted}, SeenThemeMigration={seenThemeMigration}, HasCachedData={hasCachedData}, HasSteamAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<SteamSettings>().SteamUserId)}, HasEpicAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<EpicSettings>().AccountId)}, HasRaAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<RetroAchievementsSettings>().RaUsername)}");
            return settings != null && (!seenThemeMigration || !firstTimeCompleted || !hasCachedData);
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
