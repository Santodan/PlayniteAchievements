using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services;
using Playnite.SDK;

namespace PlayniteAchievements.Views
{
    /// <summary>
    /// Lightweight host that returns immediately from the sidebar Opened callback and defers
    /// creation of the heavy SidebarControl until after Playnite has a chance to paint.
    /// Also handles showing the first-time landing page when appropriate.
    /// </summary>
    public partial class SidebarHostControl : UserControl
    {
        private readonly Func<UserControl> _createView;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly RefreshRuntime _refreshService;
        private readonly PlayniteAchievementsPlugin _plugin;

        private SidebarControl _sidebar;
        private FirstTimeLandingPage _landingPage;
        private bool _createScheduled;

        public SidebarHostControl(
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

            Loaded += SidebarHostControl_Loaded;
            Unloaded += SidebarHostControl_Unloaded;

            // Subscribe to settings saved event to refresh provider status
            PlayniteAchievementsPlugin.SettingsSaved += Plugin_SettingsSaved;
        }

        private void SidebarHostControl_Loaded(object sender, RoutedEventArgs e)
        {
            _logger.Info("SidebarHostControl_Loaded called");
            // Always recreate content when loaded (handles sidebar reopen)
            RecreateContent();
            _sidebar?.Activate();
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

        private void SidebarHostControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _sidebar?.Deactivate();
                _sidebar?.Dispose();
                _landingPage?.Dispose();
            }
            catch
            {
                // no-op
            }
            finally
            {
                _sidebar = null;
                _landingPage = null;
                _createScheduled = false;
                // Clear content to allow recreation on next load
                PART_Content.Content = null;

                // Unsubscribe from settings saved event
                PlayniteAchievementsPlugin.SettingsSaved -= Plugin_SettingsSaved;
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

            // Refresh sidebar if visible
            if (_sidebar != null)
            {
                _sidebar.RefreshView();
            }
        }

        private void RecreateContent()
        {
            _logger.Info("RecreateContent called - clearing existing content");

            // Dispose existing content
            _sidebar?.Dispose();
            _landingPage?.Dispose();
            _sidebar = null;
            _landingPage = null;
            PART_Content.Content = null;

            // Reset the flag to allow recreation
            _createScheduled = false;

            // Now recreate content
            EnsureContentCreated();
        }

        private void EnsureContentCreated()
        {
            if (_createScheduled)
            {
                _logger.Info("EnsureContentCreate: already scheduled, skipping");
                return;
            }

            _createScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded)
                {
                    _createScheduled = false;
                    _logger.Info("EnsureContentCreate: not loaded, canceling");
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
                    _logger.Info($"Sidebar opening: FirstTimeSetupCompleted={firstTimeCompleted}, SeenThemeMigration={seenThemeMigration}, HasCachedData={hasCachedData}, HasSteamAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<SteamSettings>().SteamUserId)}, HasEpicAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<EpicSettings>().AccountId)}, HasRaAuth={!string.IsNullOrEmpty(ProviderRegistry.Settings<RetroAchievementsSettings>().RaUsername)}");

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
                        _logger.Info("Creating sidebar directly");
                        CreateSidebar();
                    }

                    PART_Loading.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to create sidebar content.");
                    PART_Loading.Visibility = Visibility.Visible;
                }
                finally
                {
                    _createScheduled = false;
                }
            }), DispatcherPriority.Background);
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

        private void CreateSidebar()
        {
            _logger.Info("Creating sidebar control.");

            var control = _createView() as SidebarControl;
            if (control == null)
            {
                throw new InvalidOperationException("SidebarHostControl factory did not return SidebarControl.");
            }

            _sidebar = control;
            PART_Content.Content = _sidebar;
            _sidebar.Activate();
        }

        private void LandingPage_SetupComplete(object sender, EventArgs e)
        {
            _logger.Info("First-time setup complete, transitioning to sidebar.");

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _landingPage?.Dispose();
                    _landingPage = null;

                    CreateSidebar();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to transition from landing page to sidebar.");
                }
            }), DispatcherPriority.Background);
        }
    }
}








