using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Centralizes plugin-owned window orchestration to keep the plugin entrypoint thin.
    /// </summary>
    internal class PluginWindowService
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly Action _ensureAchievementResourcesLoaded;

        public PluginWindowService(
            IPlayniteAPI api,
            ILogger logger,
            RefreshRuntime refreshRuntime,
            ICacheManager cacheManager,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            PlayniteAchievementsSettings settings,
            ManualSourceRegistry manualSourceRegistry,
            Action ensureAchievementResourcesLoaded)
        {
            _api = api;
            _logger = logger;
            _refreshService = refreshRuntime;
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementOverridesService = achievementOverridesService;
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _settings = settings;
            _manualSourceRegistry = manualSourceRegistry ?? throw new ArgumentNullException(nameof(manualSourceRegistry));
            _ensureAchievementResourcesLoaded = ensureAchievementResourcesLoaded;
        }

        private bool DetectFullscreenMode()
        {
            try
            {
                return _api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to check fullscreen mode");
                return false;
            }
        }

        private void ShowWindow(Window window, bool isFullscreen)
        {
            if (isFullscreen)
            {
                window.Show();
                try
                {
                    window.Topmost = true;
                    window.Activate();
                    window.Topmost = false;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to activate window in fullscreen");
                }
            }
            else
            {
                window.ShowDialog();
            }
        }

        public void ShowRefreshProgressControlAndRun(Func<Task> refreshTask, Action<Guid> openSingleGameAchievementsView, Guid? singleGameRefreshId = null)
        {
            ShowRefreshProgressControl(singleGameRefreshId, refreshTask, openSingleGameAchievementsView, validateCanStart: false);
        }

        public void ShowRefreshProgressControl(
            Guid? singleGameRefreshId,
            Func<Task> refreshTask,
            Action<Guid> openSingleGameAchievementsView,
            bool validateCanStart)
        {
            try
            {
                InvokeOnUiThread(() => ShowRefreshProgressControlCore(
                    singleGameRefreshId,
                    refreshTask,
                    openSingleGameAchievementsView));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to show refresh progress window");
            }
        }

        private void ShowRefreshProgressControlCore(
            Guid? singleGameRefreshId,
            Func<Task> refreshTask,
            Action<Guid> openSingleGameAchievementsView)
        {
            var isFullscreen = DetectFullscreenMode();

            var progressWindow = new RefreshProgressControl(
                _refreshService,
                _logger,
                singleGameRefreshId,
                openSingleGameAchievementsView);

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
                windowOptions,
                isFullscreen
            );

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = _api?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch
            {
            }

            progressWindow.RequestClose += (s, ev) => window.Close();

            window.Closed += (s, ev) =>
            {
                if (_refreshService.IsRebuilding)
                {
                    _logger?.Info("Progress window closed while refresh running - cancelling refresh.");
                    _refreshService.CancelCurrentRebuild();
                }
            };

            if (refreshTask != null)
            {
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

            ShowWindow(window, isFullscreen);
        }

        private void InvokeOnUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("UI dispatcher is not available.");
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action, DispatcherPriority.Normal);
            }
        }

        public void OpenSingleGameAchievementsView(Guid gameId)
        {
            try
            {
                var isFullscreen = DetectFullscreenMode();

                var view = new SingleGameControl(
                    gameId,
                    _refreshService,
                    _achievementDataService,
                    _api,
                    _logger,
                    _settings);

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
                    windowOptions,
                    isFullscreen
                );

                window.MinWidth = 450;
                window.MinHeight = 500;
                try
                {
                    if (window.Owner == null)
                    {
                        window.Owner = _api?.Dialogs?.GetCurrentAppWindow();
                    }
                }
                catch
                {
                }

                window.Closed += (s, ev) => view.Cleanup();

                ShowWindow(window, isFullscreen);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open per-game achievements view for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open achievements view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        public void OpenModernParityTestView(Guid gameId)
        {
            try
            {
                var game = _api?.Database?.Games?.Get(gameId);
                if (game == null)
                {
                    _api?.Dialogs?.ShowErrorMessage(
                        ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                    return;
                }

                var view = new Views.ParityTests.ModernParityTestView(game);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 900,
                    Height = 700
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(
                    "Modern Theme Controls Test",
                    view,
                    windowOptions
                );

                try
                {
                    if (window.Owner == null)
                    {
                        window.Owner = _api?.Dialogs?.GetCurrentAppWindow();
                    }
                }
                catch
                {
                }

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open modern parity test view for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open test view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        public void OpenGameOptionsView(Guid gameId, GameOptionsTab initialTab)
        {
            try
            {
                var isFullscreen = DetectFullscreenMode();

                var game = _api?.Database?.Games?.Get(gameId);
                if (game == null)
                {
                    _api?.Dialogs?.ShowErrorMessage(
                        ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                    return;
                }

                _ensureAchievementResourcesLoaded?.Invoke();

                var view = new GameOptionsControl(
                    gameId,
                    initialTab,
                    _refreshService,
                    _cacheManager,
                    _persistSettingsForUi,
                    _achievementOverridesService,
                    _achievementDataService,
                    _api,
                    _logger,
                    _settings,
                    _manualSourceRegistry);

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
                    windowOptions,
                    isFullscreen);

                window.MinWidth = 860;
                window.MinHeight = 620;
                try
                {
                    if (window.Owner == null)
                    {
                        window.Owner = _api?.Dialogs?.GetCurrentAppWindow();
                    }
                }
                catch
                {
                }

                window.Closed += (s, e) => view.Cleanup();

                ShowWindow(window, isFullscreen);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open Game Options view for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open game options view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        public void OpenCapstoneView(Guid gameId)
        {
            OpenGameOptionsView(gameId, GameOptionsTab.Capstones);
        }

        public void OpenParityTestView(Guid gameId, bool modern)
        {
            try
            {
                var game = _api?.Database?.Games?.Get(gameId);
                if (game == null)
                {
                    _api?.Dialogs?.ShowErrorMessage(
                        ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                    return;
                }

                UserControl view;
                string title;

                if (modern)
                {
                    view = new Views.ParityTests.ModernParityTestView(game);
                    title = "PlayniteAchievements UI Parity Test (Modern)";
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
                _logger.Error(ex, $"Failed to open parity test view for gameId={gameId}, modern={modern}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open parity test view: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
        }

        public void EnsureAchievementResourcesLoaded()
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
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/RarityBadges.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/TrophyBadges.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/AchievementTemplates.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Providers/ProviderIcons.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/MigrationStyles.xaml");
                }

                if (app.Dispatcher.CheckAccess())
                {
                    LoadResources();
                }
                else
                {
                    app.Dispatcher.Invoke(LoadResources, DispatcherPriority.Normal);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to load achievement resources at application level.");
            }
        }

        private static void EnsureMergedDictionaryLoaded(ResourceDictionary resources, string relativeUri)
        {
            if (resources == null || string.IsNullOrWhiteSpace(relativeUri))
            {
                return;
            }

            var targetUri = new Uri(relativeUri, UriKind.Relative);
            foreach (var dictionary in resources.MergedDictionaries)
            {
                if (dictionary?.Source == null)
                {
                    continue;
                }

                if (Uri.Compare(dictionary.Source, targetUri, UriComponents.SerializationInfoString, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return;
                }
            }

            resources.MergedDictionaries.Add(new ResourceDictionary { Source = targetUri });
        }
    }
}
