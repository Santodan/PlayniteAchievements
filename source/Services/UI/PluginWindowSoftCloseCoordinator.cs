using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class PluginWindowSoftCloseCoordinator : IDisposable
    {
        private const int WhMouseLl = 14;
        private const int WmLButtonDown = 0x0201;
        private const uint GwOwner = 4;
        private const uint GaRoot = 2;

        private readonly ILogger _logger;
        private readonly Dictionary<Window, Registration> _registrations =
            new Dictionary<Window, Registration>();

        private LowLevelMouseProc _hookProc;
        private IntPtr _hookHandle = IntPtr.Zero;
        private long _registrationSequence;
        private bool _disposed;

        public PluginWindowSoftCloseCoordinator(ILogger logger)
        {
            _logger = logger;
        }

        public void Register(Window window, Func<Window> ownerResolver)
        {
            if (_disposed || window == null || ownerResolver == null)
            {
                return;
            }

            // Handles and the owner are resolved live at click time: Register runs before the
            // window is shown, a handle cached now can go stale if WPF recreates the HWND, and
            // the owner may not be resolvable yet at all (hotkey-opened windows can register
            // while no Playnite window is current).
            Unregister(window);
            _registrations[window] = new Registration(window, ownerResolver, ++_registrationSequence);
            window.Closed += Window_Closed;
            EnsureHook();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var window in _registrations.Keys.ToList())
            {
                window.Closed -= Window_Closed;
            }

            _registrations.Clear();
            UninstallHook();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Unregister(sender as Window);
        }

        private void Unregister(Window window)
        {
            if (window == null)
            {
                return;
            }

            if (!_registrations.Remove(window))
            {
                return;
            }

            window.Closed -= Window_Closed;
            if (_registrations.Count == 0)
            {
                UninstallHook();
            }
        }

        private void EnsureHook()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                return;
            }

            _hookProc = HandleMouse;
            _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(null), 0);
            if (_hookHandle != IntPtr.Zero)
            {
                return;
            }

            _logger?.Debug(
                $"Failed to install plugin window soft-close hook. Win32Error={Marshal.GetLastWin32Error()}");
            _hookProc = null;
        }

        private void UninstallHook()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            _hookProc = null;
        }

        private IntPtr HandleMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // Only the button-down that lands on the owner is intercepted; button-up events
                // always pass through so a drag that starts inside the popout and ends over the
                // owner still releases mouse capture normally.
                if (nCode >= 0 &&
                    wParam == new IntPtr(WmLButtonDown) &&
                    TryGetSoftCloseTarget(lParam, out var target))
                {
                    if (!target.IsClosing)
                    {
                        target.IsClosing = true;
                        var window = target.Window;
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
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to handle owner click for plugin window.");
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private bool TryGetSoftCloseTarget(IntPtr lParam, out Registration target)
        {
            target = null;
            if (_registrations.Count == 0)
            {
                return false;
            }

            // GetCursorPos reports the cursor in this process's coordinate space, the same space
            // GetWindowRect and WindowFromPoint operate in. The hook struct's pt is in physical
            // screen coordinates, which can disagree with that space under DPI virtualization;
            // it is used for logging only.
            if (!GetCursorPos(out var cursor))
            {
                return false;
            }

            var rootUnderCursor = GetAncestor(WindowFromPoint(cursor), GaRoot);
            if (rootUnderCursor == IntPtr.Zero)
            {
                return false;
            }

            var targetPopout = IntPtr.Zero;
            var targetOwner = IntPtr.Zero;
            foreach (var registration in _registrations.Values)
            {
                if (registration.Window?.IsVisible != true)
                {
                    continue;
                }

                var popoutHandle = GetLiveHandle(registration.Window);
                if (popoutHandle == IntPtr.Zero ||
                    IsPointInsidePopupOrOwnedWindow(popoutHandle, cursor))
                {
                    continue;
                }

                // Close only when the click demonstrably lands on the owner window itself.
                // Anything else (the popout, its popups, other applications, unresolvable
                // owners) leaves the popout open.
                var ownerHandle = GetLiveHandle(registration.ResolveOwner());
                if (ownerHandle == IntPtr.Zero || rootUnderCursor != ownerHandle)
                {
                    continue;
                }

                // With stacked popouts owned by the same window, close only the most recently
                // registered one — the top modal.
                if (target == null || registration.Sequence > target.Sequence)
                {
                    target = registration;
                    targetPopout = popoutHandle;
                    targetOwner = ownerHandle;
                }
            }

            if (target == null)
            {
                return false;
            }

            LogSoftClose(target, lParam, cursor, rootUnderCursor, targetPopout, targetOwner);
            return true;
        }

        private void LogSoftClose(
            Registration target,
            IntPtr lParam,
            NativePoint cursor,
            IntPtr rootUnderCursor,
            IntPtr popoutHandle,
            IntPtr ownerHandle)
        {
            if (target.IsClosing)
            {
                return;
            }

            try
            {
                var hookPoint = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt;
                GetWindowRect(popoutHandle, out var popoutRect);
                GetWindowRect(ownerHandle, out var ownerRect);
                _logger?.Debug(
                    $"Soft-closing plugin window '{target.Window?.Title}': " +
                    $"hookPt=({hookPoint.X},{hookPoint.Y}) cursor=({cursor.X},{cursor.Y}) " +
                    $"root=0x{rootUnderCursor.ToInt64():X} " +
                    $"popout=0x{popoutHandle.ToInt64():X} rect=({popoutRect.Left},{popoutRect.Top},{popoutRect.Right},{popoutRect.Bottom}) " +
                    $"owner=0x{ownerHandle.ToInt64():X} rect=({ownerRect.Left},{ownerRect.Top},{ownerRect.Right},{ownerRect.Bottom})");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to log soft-close diagnostics.");
            }
        }

        private static IntPtr GetLiveHandle(Window window)
        {
            if (window == null)
            {
                return IntPtr.Zero;
            }

            try
            {
                // Read the current handle only; never EnsureHandle() here, which would force a
                // handle on a window that may be unshown or mid-teardown at click time.
                return new WindowInteropHelper(window).Handle;
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

        private static bool IsPointInsidePopupOrOwnedWindow(IntPtr windowHandle, NativePoint point)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            if (IsPointInsideWindow(windowHandle, point))
            {
                return true;
            }

            // WPF Popup content (ComboBox dropdowns, context menus, tooltips) and child-hosted
            // content (HwndHost/airspace) render in separate HWNDs that can extend beyond the
            // plugin window's rectangle. Resolve the top-level window under the cursor, then walk
            // its owner chain, recognizing the plugin window as "inside".
            var current = GetAncestor(WindowFromPoint(point), GaRoot);
            var guard = 0;
            while (current != IntPtr.Zero && guard++ < 32)
            {
                if (current == windowHandle)
                {
                    return true;
                }

                current = GetWindow(current, GwOwner);
            }

            return false;
        }

        private sealed class Registration
        {
            private readonly Func<Window> _ownerResolver;

            public Registration(Window window, Func<Window> ownerResolver, long sequence)
            {
                Window = window;
                _ownerResolver = ownerResolver;
                Sequence = sequence;
            }

            public Window Window { get; }

            public long Sequence { get; }

            public bool IsClosing { get; set; }

            public Window ResolveOwner()
            {
                try
                {
                    return _ownerResolver();
                }
                catch
                {
                    return null;
                }
            }
        }

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

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
