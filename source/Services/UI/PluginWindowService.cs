using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.ManageAchievements;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Centralizes plugin-owned window orchestration to keep the plugin entrypoint thin.
    /// </summary>
    internal class PluginWindowService : IDisposable
    {
        private const string ViewAchievementsWindowPlacementKey = "SingleGameAchievements";
        private const string ViewFriendsAchievementsWindowPlacementKey = "SingleGameFriendsAchievements";
        private const string ManageAchievementsWindowPlacementKey = "ManageAchievements";
        private const string OverviewWindowPlacementKey = "Overview";
        private const string ColorPickerWindowPlacementKey = "ColorPicker";
        private const int ShowWindowRestore = 9;

        private enum AchievementWindowKind
        {
            ViewAchievements,
            ViewFriendsAchievements,
            ManageAchievements
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly RefreshRuntime _refreshService;
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly ICacheManager _cacheManager;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly LibraryProjectionService _libraryProjectionService;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly FriendsOverviewDataCoordinator _friendsOverviewDataCoordinator;
        private readonly FriendGameAchievementsDataCoordinator _friendGameAchievementsDataCoordinator;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly Action _ensureAchievementResourcesLoaded;
        private readonly FullscreenControllerNavigationService _fullscreenControllerNavigationService;
        private readonly PluginWindowSoftCloseCoordinator _softCloseCoordinator;
        private readonly System.Collections.Generic.Dictionary<Tuple<AchievementWindowKind, Guid>, Window> _achievementWindows =
            new System.Collections.Generic.Dictionary<Tuple<AchievementWindowKind, Guid>, Window>();
        private Window _overviewWindow;

        public PluginWindowService(
            IPlayniteAPI api,
            ILogger logger,
            RefreshRuntime refreshRuntime,
            RefreshEntryPoint refreshCoordinator,
            ICacheManager cacheManager,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            LibraryProjectionService libraryProjectionService,
            GameCustomDataStore gameCustomDataStore,
            PlayniteAchievementsSettings settings,
            ManualSourceRegistry manualSourceRegistry,
            Action ensureAchievementResourcesLoaded,
            FullscreenControllerNavigationService fullscreenControllerNavigationService,
            FriendsOverviewDataCoordinator friendsOverviewDataCoordinator = null,
            FriendGameAchievementsDataCoordinator friendGameAchievementsDataCoordinator = null)
        {
            _api = api;
            _logger = logger;
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementOverridesService = achievementOverridesService;
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _libraryProjectionService = libraryProjectionService;
            _gameCustomDataStore = gameCustomDataStore;
            _friendsOverviewDataCoordinator = friendsOverviewDataCoordinator;
            _friendGameAchievementsDataCoordinator = friendGameAchievementsDataCoordinator;
            _settings = settings;
            _manualSourceRegistry = manualSourceRegistry ?? throw new ArgumentNullException(nameof(manualSourceRegistry));
            _ensureAchievementResourcesLoaded = ensureAchievementResourcesLoaded;
            _fullscreenControllerNavigationService = fullscreenControllerNavigationService;
            _softCloseCoordinator = new PluginWindowSoftCloseCoordinator(_logger);
        }

        public void Dispose()
        {
            try
            {
                _softCloseCoordinator?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to dispose plugin window soft-close coordinator.");
            }
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
            PrepareForegroundActivation(window);

            if (isFullscreen)
            {
                window.Show();
                BringWindowToForeground(window);
            }
            else
            {
                window.ShowDialog();
            }
        }

        private void PrepareForegroundActivation(Window window)
        {
            if (window == null)
            {
                return;
            }

            window.ShowActivated = true;

            RoutedEventHandler loadedHandler = null;
            loadedHandler = (s, e) =>
            {
                window.Loaded -= loadedHandler;
                QueueBringWindowToForeground(window);
            };

            EventHandler contentRenderedHandler = null;
            contentRenderedHandler = (s, e) =>
            {
                window.ContentRendered -= contentRenderedHandler;
                QueueBringWindowToForeground(window);
            };

            window.Loaded += loadedHandler;
            window.ContentRendered += contentRenderedHandler;
        }

        public string PickColor(Window owner, string currentValue)
        {
            _ensureAchievementResourcesLoaded?.Invoke();

            var view = new AlphaColorPickerDialog();
            view.SetInitialColor(currentValue);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_ColorPicker_WindowTitle"),
                view,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 440,
                    Height = 465
                });

            window.MinWidth = 420;
            window.MinHeight = 440;

            try
            {
                window.Owner = owner ?? _api?.Dialogs?.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to set owner for color picker window.");
            }

            AttachWindowPlacement(window, ColorPickerWindowPlacementKey, isFullscreen: false);

            view.RequestClose += (_, __) =>
            {
                window.DialogResult = view.DialogResult;
            };

            PrepareForegroundActivation(window);
            return window.ShowDialog() == true ? view.SelectedColorText : null;
        }

        private static void QueueBringWindowToForeground(Window window)
        {
            if (window == null)
            {
                return;
            }

            window.Dispatcher.BeginInvoke(
                new Action(() => BringWindowToForeground(window)),
                DispatcherPriority.ApplicationIdle);
        }

        private void AttachWindowPlacement(Window window, string key, bool isFullscreen)
        {
            if (isFullscreen || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            WindowPlacementPersistenceService.Attach(
                window,
                _settings?.Persisted,
                _persistSettingsForUi,
                key,
                _logger);
        }

        private Window CreateManagedPopoutWindow(
            string title,
            UserControl view,
            WindowOptions windowOptions,
            bool isFullscreen,
            string placementKey = null,
            Action<Window> configureWindow = null,
            Action closed = null,
            IFullscreenControllerNavigable fullscreenController = null,
            bool enableSoftClose = true)
        {
            _ensureAchievementResourcesLoaded?.Invoke();

            var window = PlayniteUiProvider.CreateExtensionWindow(
                title,
                view,
                windowOptions,
                isFullscreen);

            configureWindow?.Invoke(window);
            AttachWindowPlacement(window, placementKey, isFullscreen);
            EnsureOwner(window);

            if (closed != null)
            {
                window.Closed += (s, e) =>
                {
                    try
                    {
                        closed();
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Plugin window close handler failed.");
                    }
                };
            }

            if (enableSoftClose)
            {
                AttachSoftClose(window, isFullscreen);
            }

            if (isFullscreen && fullscreenController != null)
            {
                _fullscreenControllerNavigationService?.RegisterWindow(window, fullscreenController);
            }

            return window;
        }

        private Window EnsureOwner(Window window)
        {
            if (window == null)
            {
                return null;
            }

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = _api?.Dialogs?.GetCurrentAppWindow();
                }

                return window.Owner;
            }
            catch
            {
                return null;
            }
        }

        private void AttachSoftClose(Window window, bool isFullscreen)
        {
            if (window == null || isFullscreen)
            {
                return;
            }

            _softCloseCoordinator.Register(window, EnsureOwner(window));
        }

        public void ShowRefreshProgressControlAndRun(Func<Task> refreshTask, Action<Guid> openViewAchievementsWindow, Guid? singleGameRefreshId = null)
        {
            ShowRefreshProgressControl(singleGameRefreshId, refreshTask, openViewAchievementsWindow, validateCanStart: false);
        }

        public void ShowRefreshProgressControl(
            Guid? singleGameRefreshId,
            Func<Task> refreshTask,
            Action<Guid> openViewAchievementsWindow,
            bool validateCanStart)
        {
            try
            {
                InvokeOnUiThread(() => ShowRefreshProgressControlCore(
                    singleGameRefreshId,
                    refreshTask,
                    openViewAchievementsWindow));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to show refresh progress window");
            }
        }

        private void ShowRefreshProgressControlCore(
            Guid? singleGameRefreshId,
            Func<Task> refreshTask,
            Action<Guid> openViewAchievementsWindow)
        {
            var isFullscreen = DetectFullscreenMode();

            var progressWindow = new RefreshProgressControl(
                _refreshService,
                _logger,
                singleGameRefreshId,
                openViewAchievementsWindow);

            var windowOptions = new WindowOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                ShowCloseButton = true,
                CanBeResizable = false,
                Width = 400,
                Height = 280
            };

            var window = CreateManagedPopoutWindow(
                progressWindow.WindowTitle,
                progressWindow,
                windowOptions,
                isFullscreen);

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
                RefreshAuthContext authContext = null;
                UpdateGlobalProgress(
                    progress,
                    text: initialText,
                    current: 0,
                    max: 100,
                    isIndeterminate: validateAuthentication);

                if (validateAuthentication)
                {
                    authContext = await _refreshService
                        .GetRefreshAuthContextOrShowDialogAsync(request, progress.CancelToken)
                        .ConfigureAwait(false);
                    if (authContext == null || !authContext.HasAuthenticatedProviders)
                    {
                        _logger?.Info("RunRefreshWithGlobalProgressAsync: Authentication preflight found no authenticated providers.");
                        SafeInvokeRefreshCompleted(onCompleted, false);
                        return;
                    }

                    _logger?.Info($"RunRefreshWithGlobalProgressAsync: Authentication preflight completed with {authContext.AuthenticatedProviders.Count} provider(s).");
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
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Failed to update global progress from rebuild progress report.");
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
                            AuthContext = authContext,
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

        public void ToggleViewAchievementsWindowFromHotkey(Guid gameId)
        {
            try
            {
                InvokeOnUiThread(() => ToggleAchievementWindow(
                    AchievementWindowKind.ViewAchievements,
                    gameId,
                    () => OpenViewAchievementsWindowCore(gameId)));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to toggle View Achievements window for gameId={gameId}");
            }
        }

        public void ToggleManageAchievementsViewFromHotkey(Guid gameId)
        {
            try
            {
                InvokeOnUiThread(() => ToggleAchievementWindow(
                    AchievementWindowKind.ManageAchievements,
                    gameId,
                    () => OpenManageAchievementsViewCore(gameId, ManageAchievementsTab.Overview)));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to toggle Manage Achievements window for gameId={gameId}");
            }
        }

        public void ToggleOverviewWindowFromHotkey()
        {
            try
            {
                InvokeOnUiThread(() =>
                {
                    if (TryActivateOverviewWindow(closeIfActive: true))
                    {
                        return;
                    }

                    OpenOverviewWindowCore();
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to toggle Achievements Overview window");
            }
        }

        public void OpenOverviewWindow()
        {
            try
            {
                InvokeOnUiThread(() =>
                {
                    if (TryActivateOverviewWindow(closeIfActive: false))
                    {
                        return;
                    }

                    OpenOverviewWindowCore();
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open Achievements Overview window");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open achievements overview: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
        }

        private bool TryActivateOverviewWindow(bool closeIfActive)
        {
            var window = FindOpenOverviewWindow();
            if (window == null)
            {
                return false;
            }

            if (closeIfActive && window.IsActive)
            {
                window.Close();
                return true;
            }

            ActivateTrackedWindow(window);
            return true;
        }

        private Window FindOpenOverviewWindow()
        {
            if (_overviewWindow?.IsVisible == true)
            {
                return _overviewWindow;
            }

            var application = Application.Current;
            return application?.Windows?
                .OfType<Window>()
                .Where(window => window != null && window.IsVisible && !ReferenceEquals(window, application.MainWindow))
                .FirstOrDefault(IsOverviewWindow);
        }

        private static bool IsOverviewWindow(Window window)
        {
            return ContainsOverviewControl(window?.Content);
        }

        private static bool ContainsOverviewControl(object content)
        {
            if (content == null)
            {
                return false;
            }

            if (content is OverviewControl)
            {
                return true;
            }

            if (content is FullscreenOverlayContainer overlay)
            {
                return ContainsOverviewControl(overlay.HostedContent);
            }

            if (content is ContentControl contentControl)
            {
                return ContainsOverviewControl(contentControl.Content);
            }

            return false;
        }

        private void TrackOverviewWindow(Window window)
        {
            if (window == null)
            {
                return;
            }

            _overviewWindow = window;
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_overviewWindow, window))
                {
                    _overviewWindow = null;
                }
            };
        }

        private void ToggleAchievementWindow(AchievementWindowKind kind, Guid gameId, Action openWindow)
        {
            if (gameId == Guid.Empty || openWindow == null)
            {
                return;
            }

            if (TryGetTrackedWindow(kind, gameId, out var existingWindow))
            {
                if (existingWindow.IsActive)
                {
                    existingWindow.Close();
                    return;
                }

                ActivateTrackedWindow(existingWindow);
                return;
            }

            CloseTrackedWindows(kind);
            openWindow();
        }

        private bool TryActivateTrackedWindow(AchievementWindowKind kind, Guid gameId)
        {
            if (!TryGetTrackedWindow(kind, gameId, out var window))
            {
                return false;
            }

            ActivateTrackedWindow(window);
            return true;
        }

        private bool TryActivateManageAchievementsWindow(
            Guid gameId,
            ManageAchievementsTab tab,
            bool selectManageCategoriesSubTab = false)
        {
            if (!TryGetTrackedWindow(AchievementWindowKind.ManageAchievements, gameId, out var window))
            {
                return false;
            }

            if (TryGetWindowContent<ManageAchievementsControl>(window, out var control))
            {
                control.SelectTab(tab, selectManageCategoriesSubTab);
            }

            ActivateTrackedWindow(window);
            return true;
        }

        private static bool TryGetWindowContent<T>(Window window, out T content)
            where T : class
        {
            content = null;
            return TryGetWindowContent(window?.Content, out content);
        }

        private static bool TryGetWindowContent<T>(object candidate, out T content)
            where T : class
        {
            content = null;
            if (candidate == null)
            {
                return false;
            }

            if (candidate is T typed)
            {
                content = typed;
                return true;
            }

            if (candidate is FullscreenOverlayContainer overlay)
            {
                return TryGetWindowContent(overlay.HostedContent, out content);
            }

            if (candidate is ContentControl contentControl)
            {
                return TryGetWindowContent(contentControl.Content, out content);
            }

            return false;
        }

        private bool TryGetTrackedWindow(AchievementWindowKind kind, Guid gameId, out Window window)
        {
            window = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var key = Tuple.Create(kind, gameId);
            if (!_achievementWindows.TryGetValue(key, out var tracked) || tracked == null)
            {
                _achievementWindows.Remove(key);
                return false;
            }

            window = tracked;
            return true;
        }

        private void TrackAchievementWindow(AchievementWindowKind kind, Guid gameId, Window window)
        {
            if (gameId == Guid.Empty || window == null)
            {
                return;
            }

            var key = Tuple.Create(kind, gameId);
            _achievementWindows[key] = window;
            window.Closed += (s, e) =>
            {
                if (_achievementWindows.TryGetValue(key, out var tracked) &&
                    ReferenceEquals(tracked, window))
                {
                    _achievementWindows.Remove(key);
                }
            };
        }

        private void CloseTrackedWindows(AchievementWindowKind kind)
        {
            var windows = _achievementWindows
                .Where(pair => pair.Key.Item1 == kind)
                .Select(pair => pair.Value)
                .Where(window => window != null)
                .Distinct()
                .ToList();

            foreach (var window in windows)
            {
                try
                {
                    window.Close();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to close tracked achievement window.");
                }
            }
        }

        private void ActivateTrackedWindow(Window window)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                if (!window.IsVisible)
                {
                    window.Show();
                }

                BringWindowToForeground(window);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to activate tracked achievement window.");
            }
        }

        private static void BringWindowToForeground(Window window)
        {
            if (window == null || !window.IsVisible)
            {
                return;
            }

            try
            {
                var wasMinimized = window.WindowState == WindowState.Minimized;
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                var helper = new WindowInteropHelper(window);
                var hwnd = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();
                if (hwnd == IntPtr.Zero)
                {
                    window.Activate();
                    window.Focus();
                    return;
                }

                if (wasMinimized)
                {
                    ShowWindowNative(hwnd, ShowWindowRestore);
                }

                var foregroundWindow = GetForegroundWindow();
                var currentThread = GetCurrentThreadId();
                var targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                var foregroundThread = foregroundWindow != IntPtr.Zero
                    ? GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero)
                    : 0;

                var attachedCurrent = false;
                var attachedForeground = false;

                try
                {
                    if (targetThread != 0 && targetThread != currentThread)
                    {
                        attachedCurrent = AttachThreadInput(currentThread, targetThread, true);
                    }

                    if (targetThread != 0 &&
                        foregroundThread != 0 &&
                        foregroundThread != targetThread)
                    {
                        attachedForeground = AttachThreadInput(foregroundThread, targetThread, true);
                    }

                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    SetActiveWindow(hwnd);
                }
                finally
                {
                    if (attachedForeground)
                    {
                        AttachThreadInput(foregroundThread, targetThread, false);
                    }

                    if (attachedCurrent)
                    {
                        AttachThreadInput(currentThread, targetThread, false);
                    }
                }

                var wasTopmost = window.Topmost;
                window.Topmost = true;
                window.Topmost = wasTopmost;
                window.Activate();
                window.Focus();
            }
            catch
            {
                try
                {
                    window.Activate();
                    window.Focus();
                }
                catch
                {
                    // Static best-effort focus fallback; no logger available here.
                }
            }
        }

        private void OpenOverviewWindowCore()
        {
            try
            {
                var isFullscreen = DetectFullscreenMode();

                var view = new OverviewControl(
                    _api,
                    _logger,
                    _refreshService,
                    _cacheManager,
                    _persistSettingsForUi,
                    _achievementOverridesService,
                    _achievementDataService,
                    _libraryProjectionService,
                    _gameCustomDataStore,
                    _refreshCoordinator,
                    _settings,
                    OverviewLaunchContext.Popout,
                    _friendsOverviewDataCoordinator);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1280,
                    Height = 800
                };

                var window = CreateManagedPopoutWindow(
                    ResourceProvider.GetString("LOCPlayAch_Menu_OpenOverview") ??
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    view,
                    windowOptions,
                    isFullscreen,
                    OverviewWindowPlacementKey,
                    closed: () =>
                    {
                        view.Deactivate();
                        view.Dispose();
                    },
                    fullscreenController: view);

                window.Loaded += (s, e) => view.Activate();
                TrackOverviewWindow(window);

                ShowWindow(window, isFullscreen);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open Achievements Overview window");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open achievements overview: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
        }

        public void OpenViewAchievementsWindow(Guid gameId, string focusAchievementId = null)
        {
            try
            {
                InvokeOnUiThread(() => OpenViewAchievementsWindowCore(gameId, focusAchievementId));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open View Achievements window for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open View Achievements: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        private void OpenViewAchievementsWindowCore(Guid gameId, string focusAchievementId = null)
        {
            if (TryActivateTrackedWindow(AchievementWindowKind.ViewAchievements, gameId))
            {
                if (!string.IsNullOrWhiteSpace(focusAchievementId) &&
                    TryGetTrackedWindow(AchievementWindowKind.ViewAchievements, gameId, out var existingWindow) &&
                    TryGetWindowContent<ViewAchievementsControl>(existingWindow, out var existingView))
                {
                    existingView.FocusAchievement(focusAchievementId);
                }

                return;
            }

            CloseTrackedWindows(AchievementWindowKind.ViewAchievements);

            try
            {
                var isFullscreen = DetectFullscreenMode();

                var view = new ViewAchievementsControl(
                    gameId,
                    _refreshService,
                    _achievementDataService,
                    _api,
                    _logger,
                    _settings,
                    _achievementOverridesService,
                    _cacheManager);

                if (!string.IsNullOrWhiteSpace(focusAchievementId))
                {
                    view.FocusAchievement(focusAchievementId);
                }

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 800,
                    Height = 700
                };

                var window = CreateManagedPopoutWindow(
                    view.WindowTitle,
                    view,
                    windowOptions,
                    isFullscreen,
                    ViewAchievementsWindowPlacementKey,
                    configureWindow: createdWindow =>
                    {
                        createdWindow.MinWidth = 450;
                        createdWindow.MinHeight = 500;
                    },
                    closed: view.Cleanup,
                    fullscreenController: view);

                TrackAchievementWindow(AchievementWindowKind.ViewAchievements, gameId, window);

                ShowWindow(window, isFullscreen);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open View Achievements window for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open View Achievements: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        public void OpenViewFriendsAchievementsWindow(Guid gameId)
        {
            try
            {
                InvokeOnUiThread(() => OpenViewFriendsAchievementsWindowCore(gameId));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open View Friends Achievements window for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open View Friends Achievements: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        private void OpenViewFriendsAchievementsWindowCore(Guid gameId)
        {
            if (TryActivateTrackedWindow(AchievementWindowKind.ViewFriendsAchievements, gameId))
            {
                return;
            }

            CloseTrackedWindows(AchievementWindowKind.ViewFriendsAchievements);

            try
            {
                var isFullscreen = DetectFullscreenMode();
                var view = new ViewFriendsAchievementsControl(
                    gameId,
                    _friendGameAchievementsDataCoordinator,
                    _refreshService,
                    _refreshCoordinator,
                    _api,
                    _logger,
                    _settings,
                    _achievementOverridesService,
                    _cacheManager);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 980,
                    Height = 700
                };

                var window = CreateManagedPopoutWindow(
                    view.WindowTitle,
                    view,
                    windowOptions,
                    isFullscreen,
                    ViewFriendsAchievementsWindowPlacementKey,
                    configureWindow: createdWindow =>
                    {
                        createdWindow.MinWidth = 720;
                        createdWindow.MinHeight = 500;
                    },
                    closed: view.Cleanup,
                    fullscreenController: view);

                TrackAchievementWindow(AchievementWindowKind.ViewFriendsAchievements, gameId, window);
                ShowWindow(window, isFullscreen);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open View Friends Achievements window for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open View Friends Achievements: {ex.Message}",
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
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to set owner for test view window.");
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
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to set owner for test view window.");
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

        public void OpenManageAchievementsView(
            Guid gameId,
            ManageAchievementsTab initialTab,
            bool selectManageCategoriesSubTab = false)
        {
            try
            {
                InvokeOnUiThread(() => OpenManageAchievementsViewCore(
                    gameId,
                    initialTab,
                    selectManageCategoriesSubTab));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open Manage Achievements view for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open manage achievements view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        private void OpenManageAchievementsViewCore(
            Guid gameId,
            ManageAchievementsTab initialTab,
            bool selectManageCategoriesSubTab = false)
        {
            if (TryActivateManageAchievementsWindow(
                gameId,
                initialTab,
                selectManageCategoriesSubTab))
            {
                return;
            }

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

                var view = new ManageAchievementsControl(
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
                    _manualSourceRegistry,
                    selectManageCategoriesSubTab);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1080,
                    Height = 760
                };

                var window = CreateManagedPopoutWindow(
                    view.WindowTitle,
                    view,
                    windowOptions,
                    isFullscreen,
                    ManageAchievementsWindowPlacementKey,
                    configureWindow: createdWindow =>
                    {
                        createdWindow.MinWidth = 860;
                        createdWindow.MinHeight = 620;
                    },
                    closed: view.Cleanup,
                    fullscreenController: view);

                TrackAchievementWindow(AchievementWindowKind.ManageAchievements, gameId, window);

                ShowWindow(window, isFullscreen);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open Manage Achievements view for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open manage achievements view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        public void OpenCapstoneView(Guid gameId)
        {
            OpenManageAchievementsView(gameId, ManageAchievementsTab.Capstones);
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

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", EntryPoint = "ShowWindow")]
        private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);
    }
}
