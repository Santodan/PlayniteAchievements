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
        private const int WmLButtonUp = 0x0202;
        private const uint GwOwner = 4;
        private const uint GaRoot = 2;

        private readonly ILogger _logger;
        private readonly Dictionary<Window, Registration> _registrations =
            new Dictionary<Window, Registration>();

        private LowLevelMouseProc _hookProc;
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _disposed;

        public PluginWindowSoftCloseCoordinator(ILogger logger)
        {
            _logger = logger;
        }

        public void Register(Window window, Window owner)
        {
            if (_disposed || window == null || owner == null)
            {
                return;
            }

            // Handles are resolved live at click time (GetLiveHandle), not captured here:
            // Register runs before the window is shown, and a handle cached now can go stale
            // if WPF recreates the HWND or the owner window is recreated (desktop/fullscreen
            // switch). Storing the Window references keeps the hit test matched to reality.
            Unregister(window);
            _registrations[window] = new Registration(window, owner);
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
                if (nCode >= 0 &&
                    IsLeftButtonMessage(wParam) &&
                    TryGetSoftCloseTarget(lParam, out var target))
                {
                    if (!target.IsClosing && wParam == new IntPtr(WmLButtonDown))
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

            var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            target = _registrations.Values
                .FirstOrDefault(registration => IsSoftCloseTarget(registration, mouse.pt));

            return target != null;
        }

        private static bool IsSoftCloseTarget(Registration registration, NativePoint point)
        {
            if (registration.Window?.IsVisible != true || !registration.Window.IsActive)
            {
                return false;
            }

            var popoutHandle = GetLiveHandle(registration.Window);
            var ownerHandle = GetLiveHandle(registration.Owner);
            if (popoutHandle == IntPtr.Zero || ownerHandle == IntPtr.Zero)
            {
                return false;
            }

            return IsPointInsideWindow(ownerHandle, point) &&
                   !IsPointInsidePopupOrOwnedWindow(popoutHandle, point);
        }

        private static bool IsLeftButtonMessage(IntPtr message)
        {
            return message == new IntPtr(WmLButtonDown) ||
                   message == new IntPtr(WmLButtonUp);
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
            public Registration(Window window, Window owner)
            {
                Window = window;
                Owner = owner;
            }

            public Window Window { get; }

            public Window Owner { get; }

            public bool IsClosing { get; set; }
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
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
