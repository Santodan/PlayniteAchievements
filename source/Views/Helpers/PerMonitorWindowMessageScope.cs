using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
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
    /// Uses comctl32 <c>SetWindowSubclass</c>, which chains with WPF's own <c>HwndSubclass</c>, and
    /// detaches on <c>WM_NCDESTROY</c>. The subclass callback is a single static delegate so it can
    /// never be collected while a window still routes through it.
    /// </summary>
    internal static class PerMonitorWindowMessageScope
    {
        private const uint WmNcDestroy = 0x0082;
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

        /// <summary>
        /// Installs the subclass on <paramref name="window"/>. The window must already have an HWND
        /// and the call must be made on the window's thread. Returns false when the window has no
        /// handle or the subclass could not be installed; the window then behaves as before.
        /// </summary>
        public static bool Attach(Window window)
        {
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
                return DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }
        }
    }
}
