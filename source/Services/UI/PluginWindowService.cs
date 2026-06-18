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
using PlayniteAchievements.Providers.Local;
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
        private const string ViewAchievementsWindowPlacementKey = "SingleGameAchievements";
        private const string ManageAchievementsWindowPlacementKey = "ManageAchievements";
        private const string OverviewWindowPlacementKey = "Overview";
        private const int WhMouseLl = 14;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int ShowWindowRestore = 9;

        private enum AchievementWindowKind
        {
            ViewAchievements,
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
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly Action _ensureAchievementResourcesLoaded;
        private readonly FullscreenControllerNavigationService _fullscreenControllerNavigationService;
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
            PlayniteAchievementsSettings settings,
            ManualSourceRegistry manualSourceRegistry,
            Action ensureAchievementResourcesLoaded,
            FullscreenControllerNavigationService fullscreenControllerNavigationService)
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
            _fullscreenControllerNavigationService = fullscreenControllerNavigationService;
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

        private void AttachOwnerClickClose(Window window, bool isFullscreen)
        {
            if (window == null || isFullscreen)
            {
                return;
            }

            Window owner;
            try
            {
                owner = window.Owner ?? _api?.Dialogs?.GetCurrentAppWindow();
            }
            catch
            {
                owner = null;
            }

            if (owner == null)
            {
                return;
            }

            var ownerHandle = GetWindowHandle(owner);
            var windowHandle = GetWindowHandle(window);
            if (ownerHandle == IntPtr.Zero || windowHandle == IntPtr.Zero)
            {
                return;
            }

            LowLevelMouseProc hookProc = null;
            var hookHandle = IntPtr.Zero;
            var isClosing = false;

            hookProc = (nCode, wParam, lParam) =>
            {
                try
                {
                    if (nCode >= 0 &&
                        IsLeftButtonMessage(wParam) &&
                        window.IsVisible &&
                        window.IsActive)
                    {
                        var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (IsPointInsideWindow(ownerHandle, mouse.pt) &&
                            !IsPointInsideWindow(windowHandle, mouse.pt))
                        {
                            if (!isClosing && wParam == new IntPtr(WmLButtonDown))
                            {
                                isClosing = true;
                                window.Dispatcher.BeginInvoke(
                                    new Action(() =>
                                    {
                                        if (window.IsVisible)
                                        {
                                            window.Close();
                                        }
                                    }),
                                    DispatcherPriority.Input);
                            }

                            return new IntPtr(1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to handle owner click for plugin window.");
                }

                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            };

            hookHandle = SetWindowsHookEx(WhMouseLl, hookProc, GetModuleHandle(null), 0);
            if (hookHandle == IntPtr.Zero)
            {
                _logger?.Debug(
                    $"Failed to install plugin window owner-click hook. Win32Error={Marshal.GetLastWin32Error()}");
                return;
            }

            window.Closed += (s, e) =>
            {
                if (hookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hookHandle);
                    hookHandle = IntPtr.Zero;
                }

                hookProc = null;
            };
        }

        private static bool IsLeftButtonMessage(IntPtr message)
        {
            return message == new IntPtr(WmLButtonDown) ||
                   message == new IntPtr(WmLButtonUp);
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
            if (isFullscreen)
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

        public void ToggleViewAchievementsWindow(Guid gameId)
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

        public void ToggleManageAchievementsView(Guid gameId)
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

        public void ToggleOverviewWindow()
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
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName") ?? "Playnite Achievements");
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

        private bool TryActivateManageAchievementsWindow(Guid gameId, ManageAchievementsTab tab)
        {
            if (!TryGetTrackedWindow(AchievementWindowKind.ManageAchievements, gameId, out var window))
            {
                return false;
            }

            if (window.Content is ManageAchievementsControl control)
            {
                control.SelectTab(tab);
            }

            ActivateTrackedWindow(window);
            return true;
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
                }
            }
        }

        private static IntPtr GetWindowHandle(Window window)
        {
            if (window == null)
            {
                return IntPtr.Zero;
            }

            try
            {
                var helper = new WindowInteropHelper(window);
                return helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static bool IsPointInsideWindow(IntPtr windowHandle, NativePoint point)
        {
            if (windowHandle == IntPtr.Zero ||
                !GetWindowRect(windowHandle, out var rect))
            {
                return false;
            }

            return point.X >= rect.Left &&
                   point.X < rect.Right &&
                   point.Y >= rect.Top &&
                   point.Y < rect.Bottom;
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
                    _refreshCoordinator,
                    _settings);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1280,
                    Height = 800
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(
                    ResourceProvider.GetString("LOCPlayAch_Menu_OpenOverview") ??
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName") ??
                    "Achievements Overview",
                    view,
                    windowOptions,
                    isFullscreen);

                AttachWindowPlacement(window, OverviewWindowPlacementKey, isFullscreen);
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

                window.Loaded += (s, e) => view.Activate();
                window.Closed += (s, e) =>
                {
                    view.Deactivate();
                    view.Dispose();
                };

                AttachOwnerClickClose(window, isFullscreen);
                TrackOverviewWindow(window);
                if (isFullscreen)
                {
                    _fullscreenControllerNavigationService?.RegisterWindow(window, view);
                }

                ShowWindow(window, isFullscreen);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open Achievements Overview window");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open achievements overview: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName") ?? "Playnite Achievements");
            }
        }

        public void OpenViewAchievementsWindow(Guid gameId)
        {
            try
            {
                InvokeOnUiThread(() => OpenViewAchievementsWindowCore(gameId));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open View Achievements window for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open View Achievements: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        private void OpenViewAchievementsWindowCore(Guid gameId)
        {
            if (TryActivateTrackedWindow(AchievementWindowKind.ViewAchievements, gameId))
            {
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
                AttachWindowPlacement(window, ViewAchievementsWindowPlacementKey, isFullscreen);
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
                AttachOwnerClickClose(window, isFullscreen);
                TrackAchievementWindow(AchievementWindowKind.ViewAchievements, gameId, window);
                if (isFullscreen)
                {
                    _fullscreenControllerNavigationService?.RegisterWindow(window, view);
                }

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

        public void OpenManageAchievementsView(Guid gameId, ManageAchievementsTab initialTab)
        {
            try
            {
                InvokeOnUiThread(() => OpenManageAchievementsViewCore(gameId, initialTab));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to open Manage Achievements view for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open manage achievements view: {ex.Message}",
                    "Playnite Achievements");
            }
        }

        private void OpenManageAchievementsViewCore(Guid gameId, ManageAchievementsTab initialTab)
        {
            if (TryActivateManageAchievementsWindow(gameId, initialTab))
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
                AttachWindowPlacement(window, ManageAchievementsWindowPlacementKey, isFullscreen);
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
                AttachOwnerClickClose(window, isFullscreen);
                TrackAchievementWindow(AchievementWindowKind.ManageAchievements, gameId, window);
                if (isFullscreen)
                {
                    _fullscreenControllerNavigationService?.RegisterWindow(window, view);
                }

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
        public void OpenLocalAchievementsEditorView(Guid gameId)
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

                var localProvider = _refreshService?.Providers?.OfType<LocalSavesProvider>().FirstOrDefault();
                var viewModel = new LocalAchievementEditorViewModel(
                    gameId,
                    _cacheManager,
                    localProvider,
                    _api,
                    _logger,
                    _settings);

                var view = new LocalAchievementEditorControl(viewModel);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1100,
                    Height = 760
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(
                    view.WindowTitle,
                    view,
                    windowOptions,
                    isFullscreen);

                window.MinWidth = 900;
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
                _logger.Error(ex, $"Failed to open local achievements editor for gameId={gameId}");
                _api?.Dialogs?.ShowErrorMessage(
                    $"Failed to open local achievements editor: {ex.Message}",
                    "Playnite Achievements");
            }
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
                    PercentRarityHelper.ApplyBadgeApplicationResources(
                        _settings?.Persisted?.UseUniformRarityBadges ?? false);
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

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public NativePoint pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
