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

            var windowHandle = GetWindowHandle(window);
            var ownerHandle = GetWindowHandle(owner);
            if (windowHandle == IntPtr.Zero || ownerHandle == IntPtr.Zero)
            {
                return;
            }

            Unregister(window);
            _registrations[window] = new Registration(window, ownerHandle, windowHandle);
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
                .FirstOrDefault(registration =>
                    registration.Window?.IsVisible == true &&
                    registration.Window.IsActive &&
                    IsPointInsideWindow(registration.OwnerHandle, mouse.pt) &&
                    !IsPointInsideWindow(registration.WindowHandle, mouse.pt));

            return target != null;
        }

        private static bool IsLeftButtonMessage(IntPtr message)
        {
            return message == new IntPtr(WmLButtonDown) ||
                   message == new IntPtr(WmLButtonUp);
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

        private sealed class Registration
        {
            public Registration(Window window, IntPtr ownerHandle, IntPtr windowHandle)
            {
                Window = window;
                OwnerHandle = ownerHandle;
                WindowHandle = windowHandle;
            }

            public Window Window { get; }

            public IntPtr OwnerHandle { get; }

            public IntPtr WindowHandle { get; }

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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
