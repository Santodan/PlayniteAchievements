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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

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
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint GW_HWNDPREV = 3;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOPMOST = 0x00000008;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;
        // Windows' baseline DPI: a monitor reporting this is at 100% scale.
        private const double StandardDpi = 96.0;

        /// <summary>
        /// Default toast window size (DIP) used when the real content size isn't measurable yet — only
        /// the pre-show placement pass, which runs before the window has been laid out. Width is the
        /// default card (<c>AchievementToastViewModel.DefaultToastCardWidth</c>, 410) plus the glow
        /// room reserved on each side (<c>ToastGlowMargin</c>, 16 without the border glow).
        /// </summary>
        public const double DefaultCardWidthDip = 442d;
        public const double DefaultCardHeightDip = 138d;

        /// <summary>
        /// How far (physical px) the toast HWND may land from the requested point before the placement
        /// is treated as a coordinate-space disagreement worth correcting.
        /// </summary>
        private const int PlacementTolerancePx = 2;

        /// <summary>
        /// What a physical placement actually did, so the caller can log the one case that matters:
        /// the toast did not end up where the corner math asked for it.
        /// </summary>
        internal struct PlacementOutcome
        {
            /// <summary>The window was moved (the <c>SetWindowPos</c> call succeeded).</summary>
            public bool Moved;

            /// <summary>The computed corner fell outside the anchor and was pulled back onto it.</summary>
            public bool Clamped;

            /// <summary>The requested physical top-left, after clamping.</summary>
            public int TargetX;
            public int TargetY;

            /// <summary>Where the HWND really landed (physical), or empty when it couldn't be read.</summary>
            public Rectangle Achieved;

            /// <summary>The HWND landed further than <see cref="PlacementTolerancePx"/> from the target.</summary>
            public bool Mismatched;
        }

        /// <summary>
        /// A learned constant offset between the coordinates handed to <c>SetWindowPos</c> and where
        /// the toast HWND actually lands, accumulated across a wave's settled placements and applied
        /// to every move of that wave. This is what rescues a coordinate-space disagreement between
        /// the anchor rect and the window's own DPI context — the case that leaves a toast entirely
        /// off-screen at a display scale we cannot reproduce. Only the settled stages measure, so the
        /// per-frame follow path never re-reads (and so never moves the window back and forth).
        /// </summary>
        internal struct PlacementCorrection
        {
            public int OffsetX;
            public int OffsetY;
        }

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
                        return dpiX / StandardDpi;
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
            return TryGetMonitorRectPhysical(windowHandle, true, out workArea);
        }

        /// <summary>
        /// The full physical bounds (taskbar area included) of the monitor the given window is on,
        /// read in the same Per-Monitor-V2 scope as <see cref="TryGetMonitorWorkAreaPhysical"/>.
        /// Callers that intersect a per-monitor window rect with its monitor must use this rather than
        /// <c>System.Windows.Forms.Screen.Bounds</c>: in this system-DPI-aware process Screen.Bounds is
        /// virtualized (and process-cached), so intersecting the two mixes coordinate spaces.
        /// </summary>
        public static bool TryGetMonitorBoundsPhysical(IntPtr windowHandle, out Rectangle bounds)
        {
            return TryGetMonitorRectPhysical(windowHandle, false, out bounds);
        }

        private static bool TryGetMonitorRectPhysical(IntPtr windowHandle, bool workArea, out Rectangle rect)
        {
            rect = Rectangle.Empty;
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
                        var source = workArea ? info.rcWork : info.rcMonitor;
                        rect = Rectangle.FromLTRB(source.Left, source.Top, source.Right, source.Bottom);
                        return rect.Width > 0 && rect.Height > 0;
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
        /// Delegates the corner math to <see cref="ComputeCorner"/>, then clamps the result into the
        /// anchor via <see cref="ClampToBounds"/>. The clamp covers the case where the toast is
        /// measured larger than the anchor it is placed in (an over-applied DPI compensation, or a
        /// fit-scale that could not measure): the right/bottom corners subtract that size from a far
        /// edge, so an oversized card lands past the opposite edge. It cannot catch a coordinate-space
        /// disagreement — the target is clamped in the same space the anchor was read in — which is
        /// what <see cref="PositionPhysical"/>'s measured correction is for.
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
            out int y,
            out bool clamped)
        {
            x = 0;
            y = 0;
            clamped = false;

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
            ComputeCorner(gameClientPhys, physW, physH, monitorScale, alignRight, alignBottom, gapDip, out x, out y);

            // A negative gap is deliberate: with the card's border glow on, the window hangs past the
            // anchor edge so the visible card body still sits a constant distance in. Allow exactly
            // that much overhang so clamping only ever rescues a genuinely off-screen result.
            var overhang = (int)Math.Round(Math.Max(0d, -gapDip) * (monitorScale > 0 ? monitorScale : 1.0));
            clamped = ClampToBounds(
                x, y, physW, physH, gameClientPhys, overhang, out var clampedX, out var clampedY);
            x = clampedX;
            y = clampedY;
            return true;
        }

        /// <summary>
        /// Pulls a computed top-left back inside <paramref name="boundsPhys"/> so the toast can never
        /// end up entirely off-screen, allowing <paramref name="allowedOverhang"/> physical pixels past
        /// either edge for the intentional glow overhang. Returns true when a coordinate was moved.
        /// </summary>
        public static bool ClampToBounds(
            int x,
            int y,
            int physW,
            int physH,
            Rectangle boundsPhys,
            int allowedOverhang,
            out int clampedX,
            out int clampedY)
        {
            clampedX = x;
            clampedY = y;
            if (boundsPhys.Width <= 0 || boundsPhys.Height <= 0)
            {
                return false;
            }

            var overhang = Math.Max(0, allowedOverhang);
            clampedX = ClampAxis(x, physW, boundsPhys.Left, boundsPhys.Right, overhang);
            clampedY = ClampAxis(y, physH, boundsPhys.Top, boundsPhys.Bottom, overhang);
            return clampedX != x || clampedY != y;
        }

        // Clamps one axis so a box of `size` starting at `value` stays within [min, max], allowed to
        // hang `overhang` past either end. A box wider than the span is pinned to the near edge rather
        // than pushed past the far one, so its leading edge stays visible.
        private static int ClampAxis(int value, int size, int min, int max, int overhang)
        {
            var lower = min - overhang;
            var upper = max - size + overhang;
            if (upper < lower)
            {
                return lower;
            }

            return value < lower ? lower : (value > upper ? upper : value);
        }

        /// <summary>
        /// Pure corner math, shared between live window placement and the per-item screenshot/clip
        /// composites: the top-left of a box of the given physical size placed at the requested
        /// corner of the client rect, inset by <paramref name="gapDip"/> scaled to the monitor.
        /// </summary>
        public static void ComputeCorner(
            Rectangle gameClientPhys,
            int physW,
            int physH,
            double monitorScale,
            bool alignRight,
            bool alignBottom,
            double gapDip,
            out int x,
            out int y)
        {
            var gap = (int)Math.Round(gapDip * (monitorScale > 0 ? monitorScale : 1.0));
            x = alignRight ? gameClientPhys.Right - physW - gap : gameClientPhys.Left + gap;
            y = alignBottom ? gameClientPhys.Bottom - physH - gap : gameClientPhys.Top + gap;
        }

        /// <summary>
        /// Moves the window's HWND to a physical desktop position without resizing it. The
        /// <c>SetWindowPos</c> call runs inside a Per-Monitor-V2 thread scope so the coordinates are
        /// interpreted as true device pixels; on a system-aware thread they would be virtualized to the
        /// system DPI and land wrong on a differently-scaled monitor. All anchor rects and the position
        /// math (<see cref="TryComputeCorner"/>) are in physical pixels, so this keeps one consistent
        /// coordinate space end to end.
        /// </summary>
        public static bool MovePhysical(Window window, int x, int y)
        {
            var hwnd = Handle(window);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                using (DpiAwarenessScope.PerMonitorV2())
                {
                    return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Inserts the toast directly above <paramref name="insertAfterHwnd"/> (the game window) in
        /// the z-order, without moving, resizing, or activating anything. The game is never raised,
        /// so an overlapping window keeps its place — the toast simply sits just above the game and
        /// is naturally occluded by anything above the game. No DPI scope needed (no coordinates).
        /// </summary>
        /// <summary>
        /// The toast window's on-screen rectangle in true physical (device) pixels — read inside a
        /// Per-Monitor-V2 scope so a system-DPI-aware process doesn't return system-virtualized
        /// coordinates on a differently-scaled monitor. Used to position the composited toast overlay
        /// against the (also physical-pixel) client-rect anchor.
        /// </summary>
        public static bool TryGetPhysicalRect(Window window, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            var hwnd = Handle(window);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                using (DpiAwarenessScope.PerMonitorV2())
                {
                    if (GetWindowRect(hwnd, out var r))
                    {
                        rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                        return rect.Width > 0 && rect.Height > 0;
                    }
                }
            }
            catch
            {
                // fall through
            }

            return false;
        }

        /// <summary>
        /// Places the toast directly above <paramref name="gameHwnd"/> in the z-order without moving,
        /// resizing, or activating anything — the game is never raised, overlapping windows keep their
        /// place, and the toast is occluded by anything above the game. SetWindowPos positions a
        /// window *after* (below) hWndInsertAfter, so to land the toast just ABOVE the game we insert
        /// it after the window currently above the game (skipping the toast itself). When nothing is
        /// above the game it goes to the top of the game's band — matching the game's topmost-ness,
        /// since a non-topmost window can never sit above a topmost (e.g. fullscreen) game window.
        /// </summary>
        public static bool SetZOrderAbove(Window window, IntPtr gameHwnd)
        {
            var toast = Handle(window);
            if (toast == IntPtr.Zero || gameHwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var above = GetWindow(gameHwnd, GW_HWNDPREV);
                while (above == toast && above != IntPtr.Zero)
                {
                    above = GetWindow(above, GW_HWNDPREV);
                }

                var insertAfter = above != IntPtr.Zero
                    ? above
                    : (IsTopmost(gameHwnd) ? HWND_TOPMOST : HWND_TOP);
                return SetWindowPos(toast, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTopmost(IntPtr hwnd)
        {
            try
            {
                return (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Positions the toast at the requested corner of the game's client rect in physical pixels,
        /// applying (and, on a measured pass, refining) the placement correction described by
        /// <see cref="PlacementCorrection"/>. <paramref name="measure"/> should be true only for a
        /// settled placement — the pre-show pass has no laid-out size and the per-frame follow must
        /// not re-measure. Reports what happened via <paramref name="outcome"/>; returns false if the
        /// corner could not be computed.
        /// </summary>
        public static bool PositionPhysical(
            Window window,
            Rectangle gameClientPhys,
            double renderScale,
            double monitorScale,
            bool alignRight,
            bool alignBottom,
            double gapDip,
            bool measure,
            ref PlacementCorrection correction,
            out PlacementOutcome outcome)
        {
            outcome = default(PlacementOutcome);
            if (!TryComputeCorner(
                window, gameClientPhys, renderScale, monitorScale, alignRight, alignBottom, gapDip,
                out var x, out var y, out var clamped))
            {
                return false;
            }

            outcome.TargetX = x;
            outcome.TargetY = y;
            outcome.Clamped = clamped;
            outcome.Moved = MovePhysical(window, x + correction.OffsetX, y + correction.OffsetY);
            if (!outcome.Moved || !measure || !TryGetPhysicalRect(window, out var actual))
            {
                return true;
            }

            // Settled placement: check where the window really landed. If it is not where we asked,
            // the coordinates we hand SetWindowPos and the space the anchor rect was read in disagree.
            // Fold the delta into the correction and re-issue — one move per measured pass, so a
            // later settled pass refines the offset rather than fighting it, and the unmeasured
            // per-frame path just reuses whatever has been learned.
            outcome.Achieved = actual;
            var dx = x - actual.Left;
            var dy = y - actual.Top;
            if (Math.Abs(dx) <= PlacementTolerancePx && Math.Abs(dy) <= PlacementTolerancePx)
            {
                return true;
            }

            outcome.Mismatched = true;
            correction.OffsetX += dx;
            correction.OffsetY += dy;
            if (MovePhysical(window, x + correction.OffsetX, y + correction.OffsetY) &&
                TryGetPhysicalRect(window, out var corrected))
            {
                outcome.Achieved = corrected;
            }

            return true;
        }
    }
}
