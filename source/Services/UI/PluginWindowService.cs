using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Common;
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
        private readonly RefreshEntryPoint _refreshCoordinator;
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
            RefreshEntryPoint refreshCoordinator,
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
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
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

        public async Task RunRefreshWithGlobalProgressAsync(
            RefreshRequest request,
            string errorLogMessage,
            bool validateAuthentication,
            Action<bool> onCompleted = null)
        {
            request ??= new RefreshRequest();

            try
            {
                _logger?.Info($"RunRefreshWithGlobalProgressAsync: Starting mode={request.Mode}, singleGameId={request.SingleGameId}, explicitGameCount={request.GameIds?.Count ?? 0}, validateAuthentication={validateAuthentication}");
                await InvokeOnUiThreadAsync(() => RunRefreshWithGlobalProgressCore(request, errorLogMessage, validateAuthentication, onCompleted)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, errorLogMessage ?? ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"));
                SafeInvokeRefreshCompleted(onCompleted, false);
            }
        }

        private void RunRefreshWithGlobalProgressCore(
            RefreshRequest request,
            string errorLogMessage,
            bool validateAuthentication,
            Action<bool> onCompleted)
        {
            var initialText = validateAuthentication
                ? ResourceProvider.GetString("LOCPlayAch_Auth_Checking")
                : ResourceProvider.GetString("LOCPlayAch_Status_Starting");
            if (string.IsNullOrWhiteSpace(initialText))
            {
                initialText = ResourceProvider.GetString("LOCPlayAch_Status_Starting");
            }

            var progressOptions = new GlobalProgressOptions(initialText, true)
            {
                Cancelable = true,
                IsIndeterminate = validateAuthentication
            };

            _api.Dialogs.ActivateGlobalProgress(async progress =>
            {
                UpdateGlobalProgress(
                    progress,
                    text: initialText,
                    current: 0,
                    max: 100,
                    isIndeterminate: validateAuthentication);

                if (validateAuthentication)
                {
                    var providers = await _refreshService
                        .GetAuthenticatedProvidersOrShowDialogAsync(progress.CancelToken)
                        .ConfigureAwait(false);
                    if (providers == null || providers.Count == 0)
                    {
                        _logger?.Info("RunRefreshWithGlobalProgressAsync: Authentication preflight found no authenticated providers.");
                        SafeInvokeRefreshCompleted(onCompleted, false);
                        return;
                    }

                    _logger?.Info($"RunRefreshWithGlobalProgressAsync: Authentication preflight completed with {providers.Count} provider(s).");
                    UpdateGlobalProgress(
                        progress,
                        text: ResourceProvider.GetString("LOCPlayAch_Status_Starting"),
                        current: 0,
                        max: 100,
                        isIndeterminate: false);
                }
                else
                {
                    UpdateGlobalProgress(progress, current: 0, max: 100, isIndeterminate: false);
                }

                EventHandler<ProgressReport> progressHandler = null;
                progressHandler = (sender, report) =>
                {
                    if (report == null)
                    {
                        return;
                    }

                    try
                    {
                        var percent = report.PercentComplete;
                        if (percent <= 0 || double.IsNaN(percent))
                        {
                            if (report.TotalSteps > 0)
                            {
                                percent = (report.CurrentStep * 100.0) / report.TotalSteps;
                            }
                            else
                            {
                                percent = 0;
                            }
                        }
                        UpdateGlobalProgress(
                            progress,
                            text: report.Message,
                            current: Math.Max(0, Math.Min(100, percent)));
                    }
                    catch
                    {
                    }
                };

                _refreshService.RebuildProgress += progressHandler;

                var success = false;
                try
                {
                    await Task.Run(() => _refreshCoordinator.ExecuteAsync(
                        request,
                        new RefreshExecutionPolicy
                        {
                            ValidateAuthentication = false,
                            SwallowExceptions = false,
                            ErrorLogMessage = errorLogMessage,
                            ExternalCancellationToken = progress.CancelToken
                        }), progress.CancelToken).ConfigureAwait(false);
                    success = true;
                    UpdateGlobalProgress(
                        progress,
                        text: ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete"),
                        current: 100);
                }
                catch (OperationCanceledException)
                {
                    _refreshService.CancelCurrentRebuild();
                    UpdateGlobalProgress(progress, text: ResourceProvider.GetString("LOCPlayAch_Status_Canceled"));
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, errorLogMessage ?? ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"));
                    UpdateGlobalProgress(progress, text: ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"));
                }
                finally
                {
                    _refreshService.RebuildProgress -= progressHandler;
                    SafeInvokeRefreshCompleted(onCompleted, success);
                }
            }, progressOptions);
        }

        private void UpdateGlobalProgress(
            GlobalProgressActionArgs progress,
            string text = null,
            double? current = null,
            double? max = null,
            bool? isIndeterminate = null)
        {
            if (progress == null)
            {
                return;
            }

            Action update = () =>
            {
                if (max.HasValue)
                {
                    progress.ProgressMaxValue = max.Value;
                }

                if (current.HasValue)
                {
                    progress.CurrentProgressValue = current.Value;
                }

                if (isIndeterminate.HasValue)
                {
                    progress.IsIndeterminate = isIndeterminate.Value;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    progress.Text = text;
                }
            };

            if (progress.MainDispatcher != null)
            {
                progress.MainDispatcher.InvokeIfNeeded(update);
            }
            else
            {
                update();
            }
        }

        private void SafeInvokeRefreshCompleted(Action<bool> onCompleted, bool success)
        {
            if (onCompleted == null)
            {
                return;
            }

            try
            {
                onCompleted(success);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Refresh completion callback failed.");
            }
        }

        private Task InvokeOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("UI dispatcher is not available.");
            }

            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
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

                // Some themes don't define the shared base styles our controls expect.
                // Ensure plugin resource dictionaries are loaded before constructing the view.
                _ensureAchievementResourcesLoaded?.Invoke();

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

        public void OpenDynamicThemeCommandTestView(Guid? gameId = null)
        {
            try
            {
                Game game = null;
                if (gameId.HasValue)
                {
                    game = _api?.Database?.Games?.Get(gameId.Value);
                    if (game == null)
                    {
                        _api?.Dialogs?.ShowErrorMessage(
                            ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame"),
                            ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                        return;
                    }
                }

                var view = new Views.ParityTests.DynamicThemeCommandTestView(game);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1200,
                    Height = 860
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(
                    gameId.HasValue ? "Dynamic Theme Command Tester" : "Dynamic Theme Command Tester (All Games)",
                    view,
                    windowOptions
                );

                window.MinWidth = 900;
                window.MinHeight = 640;

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
                _logger.Error(ex, $"Failed to open dynamic theme command test view for gameId={(gameId.HasValue ? gameId.Value.ToString() : "<all>")}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open dynamic command test view: {ex.Message}",
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

                    // Defensive fallbacks for themes missing these keys: templates depend on them.
                    if (app.TryFindResource("BaseTextBlockStyle") == null)
                    {
                        var fallbackBaseTextBlockStyle = new Style(typeof(TextBlock));
                        fallbackBaseTextBlockStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
                        app.Resources["BaseTextBlockStyle"] = fallbackBaseTextBlockStyle;
                    }

                    if (app.TryFindResource("SearchTextBoxStyle") == null)
                    {
                        var fallbackSearchTextBoxStyle = new Style(typeof(TextBox));
                        fallbackSearchTextBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
                        app.Resources["SearchTextBoxStyle"] = fallbackSearchTextBoxStyle;
                    }
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
