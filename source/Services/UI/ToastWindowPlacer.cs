using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Positions the achievement toast window in raw physical (device) pixels via
    /// <c>SetWindowPos</c>, and resolves the game monitor's true effective DPI.
    ///
    /// Why physical pixels: to render crisply on a monitor whose scale differs from Playnite's
    /// (system) DPI, the toast HWND is made Per-Monitor-V2 aware (see <see cref="DpiAwarenessScope"/>)
    /// so Windows does not bitmap-rescale it. WPF (in a system-DPI-aware process without the
    /// per-monitor app.config switch) still reports window coordinates in the process's system-DPI
    /// space, so setting <c>Window.Left/Top</c> would land the per-monitor window at the wrong place
    /// on a differently-scaled monitor. Positioning the HWND directly in physical desktop coordinates
    /// sidesteps WPF's coordinate virtualization entirely.
    ///
    /// All members are wrapped so they can never throw into the toast pipeline.
    /// </summary>
    internal static class ToastWindowPlacer
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        /// <summary>Default toast card size (DIP) used when the real content size isn't measurable yet.</summary>
        public const double DefaultCardWidthDip = 438d;
        public const double DefaultCardHeightDip = 138d;

        /// <summary>The process/system device scale (main window's TransformToDevice.M11), or 1.0.</summary>
        public static double SystemScale()
        {
            try
            {
                var main = Application.Current?.MainWindow;
                var source = main != null ? PresentationSource.FromVisual(main) : null;
                if (source?.CompositionTarget != null)
                {
                    var m11 = source.CompositionTarget.TransformToDevice.M11;
                    if (m11 > 0)
                    {
                        return m11;
                    }
                }
            }
            catch
            {
                // Fall through to unity.
            }

            return 1.0;
        }

        /// <summary>
        /// The true effective scale (1.0 = 100%) of the monitor the given window is on. Read inside a
        /// Per-Monitor-V2 thread scope so <c>GetDpiForMonitor</c> returns the monitor's real DPI; in a
        /// system-aware context it would return the process (system) DPI instead. Returns 1.0 on
        /// failure so callers apply no compensation.
        /// </summary>
        public static double ResolveMonitorScale(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return 1.0;
            }

            try
            {
                using (DpiAwarenessScope.PerMonitorV2())
                {
                    var monitor = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
                    if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                    {
                        return dpiX / 96.0;
                    }
                }
            }
            catch
            {
                // Fall through to unity.
            }

            return 1.0;
        }

        /// <summary>
        /// The physical work area (excluding the taskbar) of the monitor the given window is on. Read
        /// inside a Per-Monitor-V2 thread scope so the rect comes back in true device pixels; in a
        /// system-aware context it would be virtualized. Used as the toast anchor when no game window
        /// is running, so a toast fired while Playnite sits on a secondary monitor lands there.
        /// </summary>
        public static bool TryGetMonitorWorkAreaPhysical(IntPtr windowHandle, out Rectangle workArea)
        {
            workArea = Rectangle.Empty;
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                using (DpiAwarenessScope.PerMonitorV2())
                {
                    var monitor = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
                    if (monitor == IntPtr.Zero)
                    {
                        return false;
                    }

                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        workArea = Rectangle.FromLTRB(
                            info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
                        return workArea.Width > 0 && workArea.Height > 0;
                    }
                }
            }
            catch
            {
                // Fall through to failure.
            }

            return false;
        }

        /// <summary>The HWND backing a window, or IntPtr.Zero if it has none yet.</summary>
        public static IntPtr Handle(Window window)
        {
            try
            {
                return window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// The device scale WPF actually renders the given window at (its own TransformToDevice.M11),
        /// or 1.0 if it has no presentation source yet.
        /// </summary>
        public static double RenderScale(Window window)
        {
            try
            {
                var target = window != null ? PresentationSource.FromVisual(window)?.CompositionTarget : null;
                if (target != null)
                {
                    var m11 = target.TransformToDevice.M11;
                    if (m11 > 0)
                    {
                        return m11;
                    }
                }
            }
            catch
            {
                // Fall through to unity.
            }

            return 1.0;
        }

        /// <summary>
        /// Computes the toast's target top-left in physical desktop pixels: the requested corner of
        /// the game's client rect (physical), inset by <paramref name="gapDip"/> scaled to the monitor.
        /// The toast's physical size is its WPF size times <paramref name="renderScale"/> (the content
        /// LayoutTransform already carries the DPI compensation, so the WPF size is monitor-correct).
        /// </summary>
        public static bool TryComputeCorner(
            Window window,
            Rectangle gameClientPhys,
            double renderScale,
            double monitorScale,
            bool alignRight,
            bool alignBottom,
            double gapDip,
            out int x,
            out int y)
        {
            x = 0;
            y = 0;

            if (window == null || gameClientPhys.Width <= 0 || gameClientPhys.Height <= 0 || renderScale <= 0)
            {
                return false;
            }

            var widthDip = window.ActualWidth > 0 ? window.ActualWidth : (window.Width > 0 ? window.Width : DefaultCardWidthDip);
            var heightDip = window.ActualHeight > 0 ? window.ActualHeight : (window.Height > 0 ? window.Height : DefaultCardHeightDip);
            if (double.IsNaN(widthDip) || widthDip <= 0)
            {
                widthDip = DefaultCardWidthDip;
            }

            if (double.IsNaN(heightDip) || heightDip <= 0)
            {
                heightDip = DefaultCardHeightDip;
            }

            var physW = (int)Math.Ceiling(widthDip * renderScale);
            var physH = (int)Math.Ceiling(heightDip * renderScale);
            var gap = (int)Math.Round(gapDip * (monitorScale > 0 ? monitorScale : 1.0));

            x = alignRight ? gameClientPhys.Right - physW - gap : gameClientPhys.Left + gap;
            y = alignBottom ? gameClientPhys.Bottom - physH - gap : gameClientPhys.Top + gap;
            return true;
        }

        /// <summary>Moves the window's HWND to a physical desktop position without resizing it.</summary>
        public static bool MovePhysical(Window window, int x, int y)
        {
            var hwnd = Handle(window);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Positions the toast at the requested corner of the game's client rect in physical pixels.
        /// Returns the placement point via <paramref name="x"/>/<paramref name="y"/> for diagnostics;
        /// false if it could not be computed or moved.
        /// </summary>
        public static bool PositionPhysical(
            Window window,
            Rectangle gameClientPhys,
            double renderScale,
            double monitorScale,
            bool alignRight,
            bool alignBottom,
            double gapDip,
            out int x,
            out int y)
        {
            if (!TryComputeCorner(window, gameClientPhys, renderScale, monitorScale, alignRight, alignBottom, gapDip, out x, out y))
            {
                return false;
            }

            return MovePhysical(window, x, y);
        }
    }
}
