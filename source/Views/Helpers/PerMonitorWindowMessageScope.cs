using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Subclasses a Per-Monitor-V2 window's procedure so every message it handles runs with the
    /// calling thread switched to Per-Monitor-V2 awareness for the duration of that message.
    ///
    /// The host process is system-DPI aware, so its UI thread is too. Win32 coordinate calls follow
    /// the calling thread's awareness, not the window's: on a monitor whose scale differs from the
    /// process's system DPI, <c>GetCursorPos</c> and <c>ScreenToClient</c> issued from a system-aware
    /// thread against a Per-Monitor-V2 window return values off by the system-to-monitor ratio.
    /// WPF's <c>MouseDevice.GetPosition</c> goes through exactly those two calls, and
    /// <c>ButtonBase.UpdateIsPressed</c> uses it while the mouse is captured between button-down and
    /// button-up, so a button in such a window sees the cursor outside itself and never raises
    /// <c>Click</c>. WPF's input pipeline runs synchronously inside the window procedure, so wrapping
    /// the procedure in a matching thread context makes those calls resolve in the window's own space.
    /// Popups and context menus opened from a handler are created inside the wrapped call and become
    /// Per-Monitor-V2 as well.
    ///
    /// Tooltips open from a dispatcher timer, outside any window message, so the subclass does not
    /// cover them: their popup HWND would be created system-aware (bitmap-scaled by Windows) and
    /// placed through virtualized <c>ClientToScreen</c>/<c>SetWindowPos</c>, landing at the wrong
    /// offset from the window. <c>ToolTipOpening</c> and <c>ContextMenuOpening</c> bubble to the
    /// window synchronously just before the popup HWND is created, so a handler on the window enters
    /// the Per-Monitor-V2 scope there and releases it when the current dispatcher operation
    /// completes (one-shot <c>Dispatcher.Hooks.OperationCompleted</c>). When the event arrives from
    /// inside a window message the thread is already Per-Monitor-V2 and the handler does nothing.
    ///
    /// WPF also re-hit-tests the mouse outside any window message: <c>MouseDevice.Synchronize</c>
    /// runs from a dispatcher operation after layout invalidates hit testing, reads the cursor
    /// through the same virtualized calls, and feeds a synthetic move at a position scaled by the
    /// system-to-monitor ratio. Alternating with real moves, that flips <c>IsMouseOver</c> between
    /// the element under the cursor and one nearer the screen origin, so hover blinks and clicks
    /// miss, worst on the right side of the window. A <c>PreProcessInput</c> handler cancels such
    /// a synchronize report for a subclassed window and re-issues it under Per-Monitor-V2.
    ///
    /// WindowChrome answers <c>WM_NCHITTEST</c> by converting the point with the window's
    /// <c>DpiScale</c>, which can disagree with the scale the window renders at, so the title-bar
    /// buttons resolve to <c>HTCAPTION</c> and a click drags the window. The subclass answers
    /// <c>HTCLIENT</c> first for any element marked hit-test visible in chrome, found through the
    /// render transform.
    ///
    /// Uses comctl32 <c>SetWindowSubclass</c>, which chains with WPF's own <c>HwndSubclass</c>, and
    /// detaches on <c>WM_NCDESTROY</c>. The subclass callback is a single static delegate so it can
    /// never be collected while a window still routes through it.
    /// </summary>
    internal static class PerMonitorWindowMessageScope
    {
        private const uint WmNcDestroy = 0x0082;
        private const uint WmNcHitTest = 0x0084;
        private static readonly IntPtr HtClient = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
        private static readonly UIntPtr SubclassId = new UIntPtr(0x50414D53); // 'PAMS'

        private delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        // Rooted for the lifetime of the process; the native side holds a raw function pointer.
        private static readonly SubclassProc Callback = HandleMessage;

        // A scope opened by a popup-opening event, released when the current dispatcher operation
        // completes. UI-thread only; at most one is pending at a time.
        private static IDisposable _pendingPopupScope;

        // Subclassed windows, so the synchronize handler only touches their input. UI-thread only.
        private static readonly HashSet<IntPtr> SubclassedWindows = new HashSet<IntPtr>();

        // InputReportEventArgs, InputReport and RawMouseInputReport are internal to PresentationCore.
        private static PropertyInfo _reportProperty;
        private static PropertyInfo _reportSourceProperty;
        private static FieldInfo _isSynchronizeField;
        private static bool _inputHandlerRegistered;
        private static bool _resynchronizing;
        private static bool _loggedResynchronize;
        private static bool _loggedChromeHitTest;
        private static ILogger _logger;

        /// <summary>
        /// Installs the subclass on <paramref name="window"/>. The window must already have an HWND
        /// and the call must be made on the window's thread. Returns false when the window has no
        /// handle or the subclass could not be installed; the window then behaves as before.
        /// </summary>
        public static bool Attach(Window window, ILogger logger = null)
        {
            if (logger != null)
            {
                _logger = logger;
            }

            if (window == null)
            {
                return false;
            }

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return false;
                }

                if (!SetWindowSubclass(hwnd, Callback, SubclassId, UIntPtr.Zero))
                {
                    return false;
                }

                window.AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(OnPopupOpening), handledEventsToo: true);
                window.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnPopupOpening), handledEventsToo: true);
                SubclassedWindows.Add(hwnd);
                EnsureInputHandler();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void OnPopupOpening(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_pendingPopupScope != null || DpiAwarenessScope.IsThreadPerMonitorV2())
                {
                    return;
                }

                var dispatcher = (sender as DispatcherObject)?.Dispatcher ?? Dispatcher.CurrentDispatcher;
                var scope = DpiAwarenessScope.PerMonitorV2();
                DispatcherHookEventHandler release = null;
                release = (_, __) =>
                {
                    dispatcher.Hooks.OperationCompleted -= release;
                    _pendingPopupScope = null;
                    scope.Dispose();
                };

                _pendingPopupScope = scope;
                dispatcher.Hooks.OperationCompleted += release;
            }
            catch
            {
                // Leave the popup to open in the thread's current context.
            }
        }

        private static void EnsureInputHandler()
        {
            if (_inputHandlerRegistered)
            {
                return;
            }

            _inputHandlerRegistered = true;
            try
            {
                var core = typeof(InputManager).Assembly;
                _reportProperty = core.GetType("System.Windows.Input.InputReportEventArgs")?
                    .GetProperty("Report", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _reportSourceProperty = core.GetType("System.Windows.Input.InputReport")?
                    .GetProperty("InputSource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _isSynchronizeField = core.GetType("System.Windows.Input.RawMouseInputReport")?
                    .GetField("_isSynchronize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (_reportProperty == null || _reportSourceProperty == null || _isSynchronizeField == null)
                {
                    _logger?.Warn("[Dpi] Mouse synchronize correction unavailable: PresentationCore input internals not found.");
                    return;
                }

                InputManager.Current.PreProcessInput += OnPreProcessInput;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Dpi] Mouse synchronize correction unavailable.");
            }
        }

        private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (_resynchronizing || SubclassedWindows.Count == 0)
            {
                return;
            }

            try
            {
                var input = e.StagingItem?.Input;
                if (input == null || input.Handled || !_reportProperty.DeclaringType.IsInstanceOfType(input))
                {
                    return;
                }

                var report = _reportProperty.GetValue(input);
                if (report == null ||
                    !_isSynchronizeField.DeclaringType.IsInstanceOfType(report) ||
                    !(_isSynchronizeField.GetValue(report) is bool isSynchronize) ||
                    !isSynchronize)
                {
                    return;
                }

                if (!(_reportSourceProperty.GetValue(report) is HwndSource source) ||
                    !SubclassedWindows.Contains(source.Handle) ||
                    DpiAwarenessScope.IsThreadPerMonitorV2())
                {
                    return;
                }

                // Computed from a system-aware thread against a Per-Monitor-V2 window: drop it and
                // take the position again in the window's own coordinate space.
                e.Cancel();
                var skewed = _loggedResynchronize ? default(Point) : Mouse.PrimaryDevice.GetPosition(source.RootVisual as IInputElement);
                _resynchronizing = true;
                try
                {
                    using (DpiAwarenessScope.PerMonitorV2())
                    {
                        if (!_loggedResynchronize)
                        {
                            _loggedResynchronize = true;
                            var corrected = Mouse.PrimaryDevice.GetPosition(source.RootVisual as IInputElement);
                            _logger?.Info(
                                $"[Dpi] Re-synchronized mouse in per-monitor window '{(source.RootVisual as Window)?.Title}': " +
                                $"systemAware=({skewed.X:0},{skewed.Y:0}), perMonitor=({corrected.X:0},{corrected.Y:0})");
                        }

                        Mouse.PrimaryDevice.Synchronize();
                    }
                }
                finally
                {
                    _resynchronizing = false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Dpi] Mouse synchronize correction failed.");
            }
        }

        private static IntPtr HandleMessage(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData)
        {
            if (uMsg == WmNcDestroy)
            {
                SubclassedWindows.Remove(hWnd);
                try
                {
                    RemoveWindowSubclass(hWnd, Callback, uIdSubclass);
                }
                catch
                {
                    // The window is being destroyed; nothing further routes through this subclass.
                }
            }

            using (DpiAwarenessScope.PerMonitorV2())
            {
                if (uMsg == WmNcHitTest && IsOverChromeElement(hWnd, lParam, out var window))
                {
                    var chromeResult = DefSubclassProc(hWnd, uMsg, wParam, lParam);
                    if (chromeResult != HtClient)
                    {
                        LogChromeHitTestCorrection(hWnd, window, chromeResult);
                    }

                    return HtClient;
                }

                return DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }
        }

        // WindowChrome converts the WM_NCHITTEST point with the window's DpiScale, which in this
        // system-aware process can disagree with the scale the window is actually rendered at, so the
        // point misses the title-bar buttons and resolves to HTCAPTION. Hit-test through the render
        // transform instead and claim the point for any element the chrome would pass through.
        private static bool IsOverChromeElement(IntPtr hWnd, IntPtr lParam, out Window window)
        {
            window = null;
            try
            {
                var source = HwndSource.FromHwnd(hWnd);
                window = source?.RootVisual as Window;
                if (window == null || source.CompositionTarget == null || !GetWindowRect(hWnd, out var rect))
                {
                    return false;
                }

                var packed = lParam.ToInt64();
                var devicePoint = new Point((short)(packed & 0xFFFF) - rect.Left, (short)((packed >> 16) & 0xFFFF) - rect.Top);
                var logicalPoint = source.CompositionTarget.TransformFromDevice.Transform(devicePoint);
                var element = window.InputHitTest(logicalPoint);
                return element != null && WindowChrome.GetIsHitTestVisibleInChrome(element);
            }
            catch
            {
                return false;
            }
        }

        private static void LogChromeHitTestCorrection(IntPtr hWnd, Window window, IntPtr chromeResult)
        {
            if (_loggedChromeHitTest)
            {
                return;
            }

            _loggedChromeHitTest = true;
            try
            {
                var renderScale = HwndSource.FromHwnd(hWnd)?.CompositionTarget?.TransformToDevice.M11 ?? 0;
                var dpiScale = VisualTreeHelper.GetDpi(window).DpiScaleX;
                _logger?.Info(
                    $"[Dpi] Corrected title-bar hit test in per-monitor window '{window.Title}': " +
                    $"chrome={chromeResult.ToInt64()}, renderScale={renderScale:0.###}, dpiScale={dpiScale:0.###}");
            }
            catch
            {
                // Diagnostic only.
            }
        }
    }
}
