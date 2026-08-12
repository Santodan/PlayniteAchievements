using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// A window's rectangles, all in one coordinate space (physical pixels). Any of them may be
    /// <see cref="Rectangle.Empty"/> when the underlying call failed.
    /// </summary>
    internal readonly struct WindowRects
    {
        public WindowRects(Rectangle clientArea, Rectangle frameBounds, Rectangle outerRect)
        {
            ClientArea = clientArea;
            FrameBounds = frameBounds;
            OuterRect = outerRect;
        }

        /// <summary>The client area (content, no chrome), positioned in screen coordinates.</summary>
        public Rectangle ClientArea { get; }

        /// <summary>
        /// The DWM extended frame bounds: the visible window, excluding the invisible resize
        /// border and shadow that <see cref="OuterRect"/> includes.
        /// </summary>
        public Rectangle FrameBounds { get; }

        /// <summary>The outer window rect, invisible resize border included.</summary>
        public Rectangle OuterRect { get; }

        public bool IsEmpty => ClientArea.IsEmpty && FrameBounds.IsEmpty && OuterRect.IsEmpty;

        /// <summary>
        /// The best single rect to treat as "the window" when capturing it whole: the client area
        /// so chrome is excluded, else the visible frame, else the outer rect. Empty when none
        /// resolved. A borderless or fullscreen window's client area is the whole window, so all
        /// three coincide.
        /// </summary>
        public Rectangle PreferredCaptureArea
        {
            get
            {
                if (!ClientArea.IsEmpty)
                {
                    return ClientArea;
                }

                return !FrameBounds.IsEmpty ? FrameBounds : OuterRect;
            }
        }

        public override string ToString()
        {
            return $"client={ClientArea} frame={FrameBounds} window={OuterRect}";
        }
    }

    /// <summary>
    /// The one place window rectangles are measured. Every rect is read inside a single
    /// Per-Monitor-V2 scope, which is what makes them comparable: the host process is
    /// system-DPI-aware, so on a monitor whose effective DPI differs from the system DPI an
    /// unscoped <c>GetClientRect</c> comes back virtualized while <c>DwmGetWindowAttribute</c>
    /// stays physical, and arithmetic mixing the two is wrong in a way that is invisible on a
    /// 100%-scaled single-monitor machine.
    ///
    /// Callers that had their own copy of these reads each had to remember that on their own, and
    /// one of them not remembering is what mis-cropped recorded clips while screenshots of the same
    /// window looked right. Measuring in one place means the rule is applied once.
    ///
    /// Nested inside an existing scope this is a no-op, so callers may wrap larger regions.
    /// </summary>
    internal static class WindowRectangles
    {
        /// <summary>
        /// Measures the window. Individual rects come back empty when their call fails or reports
        /// nothing positive; the whole result is empty for a handle that resolves nothing.
        /// </summary>
        public static WindowRects Measure(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return default;
            }

            try
            {
                using (DpiAwarenessScope.PerMonitorV2())
                {
                    return new WindowRects(MeasureClient(hwnd), MeasureFrame(hwnd), MeasureOuter(hwnd));
                }
            }
            catch
            {
                return default;
            }
        }

        private static Rectangle MeasureClient(IntPtr hwnd)
        {
            if (!GetClientRect(hwnd, out var client))
            {
                return Rectangle.Empty;
            }

            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            var origin = new POINT { X = client.Left, Y = client.Top };
            return width > 0 && height > 0 && ClientToScreen(hwnd, ref origin)
                ? new Rectangle(origin.X, origin.Y, width, height)
                : Rectangle.Empty;
        }

        private static Rectangle MeasureFrame(IntPtr hwnd)
        {
            try
            {
                return DwmGetWindowAttribute(
                    hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf(typeof(RECT))) == 0
                    ? ToRectangle(frame)
                    : Rectangle.Empty;
            }
            catch
            {
                // DWM unavailable.
                return Rectangle.Empty;
            }
        }

        private static Rectangle MeasureOuter(IntPtr hwnd)
        {
            return GetWindowRect(hwnd, out var window) ? ToRectangle(window) : Rectangle.Empty;
        }

        private static Rectangle ToRectangle(RECT rect)
        {
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            return width > 0 && height > 0
                ? new Rectangle(rect.Left, rect.Top, width, height)
                : Rectangle.Empty;
        }

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
