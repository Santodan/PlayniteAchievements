using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// A content-free window that sits directly under a DWM-composed toast window and asks DWM to
    /// blur what is behind it, so a translucent card shows a live blur of the game or desktop.
    ///
    /// The blur lives on its own window because DWM blurs a window's whole region, and shaping the
    /// toast window's region to its cards would also clip their glow. This window has the toast
    /// window's rect and a region made of the cards' rounded rects, so the blur is card-shaped while
    /// the toast keeps its full surface.
    ///
    /// All members are wrapped so they can never throw into the toast pipeline.
    /// </summary>
    internal sealed class ToastBackdropWindow : IDisposable
    {
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsExTopmost = 0x00000008;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;
        private const int WcaAccentPolicy = 19;
        private const int AccentEnableBlurBehind = 3;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwcpDoNotRound = 1;
        private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
        private const int RgnOr = 2;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpHideWindow = 0x0080;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left, Right, Top, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        private HwndSource _source;
        private bool _hasRegion;
        private bool _visible;

        private ToastBackdropWindow(HwndSource source)
        {
            _source = source;
        }

        /// <summary>
        /// Creates the hidden backdrop window, or returns null when the window cannot be created or
        /// this Windows build refuses the blur accent; the card then shows its tint alone.
        /// </summary>
        public static ToastBackdropWindow TryCreate()
        {
            HwndSource source = null;
            try
            {
                var parameters = new HwndSourceParameters("PlayniteAchievementsToastBackdrop")
                {
                    Width = 1,
                    Height = 1,
                    WindowStyle = WsPopup,
                    ExtendedWindowStyle = WsExTopmost | WsExTransparent | WsExToolWindow | WsExNoActivate,
                    UsesPerPixelOpacity = false
                };
                source = new HwndSource(parameters);
                source.AddHook(WndProc);
                if (source.CompositionTarget != null)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }

                var hwnd = source.Handle;

                // A transparent client area lets the blur show; the frame extension gives WPF's
                // transparent clear color real alpha, as on the toast window itself.
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                var corner = DwmwcpDoNotRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
                var border = DwmwaColorNone;
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));

                if (!ApplyBlurAccent(hwnd))
                {
                    source.Dispose();
                    return null;
                }

                return new ToastBackdropWindow(source);
            }
            catch
            {
                try
                {
                    source?.Dispose();
                }
                catch
                {
                }

                return null;
            }
        }

        private static bool ApplyBlurAccent(IntPtr hwnd)
        {
            var accent = new AccentPolicy { AccentState = AccentEnableBlurBehind };
            var size = Marshal.SizeOf(accent);
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        // Clicks never land here: the toast window above covers every card, and the region
        // excludes everything else. Reporting transparent keeps it that way during transitions.
        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmNcHitTest)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Shapes the blur to <paramref name="cards"/> (each card's root element, whose bounds are
        /// the visible card) with corners of <paramref name="cornerRadiusDip"/> in each card's own
        /// units, then places this window under <paramref name="toastWindow"/> and shows it.
        /// </summary>
        public void ShowUnder(Window toastWindow, IReadOnlyList<FrameworkElement> cards, double cornerRadiusDip)
        {
            try
            {
                if (_source == null || toastWindow == null || cards == null)
                {
                    return;
                }

                _hasRegion = ApplyRegion(toastWindow, cards, cornerRadiusDip);
                if (!_hasRegion)
                {
                    Hide();
                    return;
                }

                _visible = true;
                Follow(toastWindow);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Re-shapes the blur after the cards resize, keeping the current visibility.
        /// </summary>
        public void Reshape(Window toastWindow, IReadOnlyList<FrameworkElement> cards, double cornerRadiusDip)
        {
            try
            {
                if (_source == null || toastWindow == null || cards == null)
                {
                    return;
                }

                _hasRegion = ApplyRegion(toastWindow, cards, cornerRadiusDip);
                if (!_hasRegion)
                {
                    Hide();
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Matches the toast window's rect and keeps this window directly beneath it. Called after
        /// every toast window move while shown.
        /// </summary>
        public void Follow(Window toastWindow)
        {
            try
            {
                if (_source == null || !_visible || !_hasRegion)
                {
                    return;
                }

                var toastHwnd = new WindowInteropHelper(toastWindow).Handle;
                if (toastHwnd == IntPtr.Zero || !GetWindowRect(toastHwnd, out var rect))
                {
                    return;
                }

                // Inserting after the toast window puts this one immediately below it; both are
                // topmost, so it stays above the game.
                SetWindowPos(
                    _source.Handle, toastHwnd,
                    rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                    SwpNoActivate | SwpShowWindow);
            }
            catch
            {
            }
        }

        public void Hide()
        {
            try
            {
                _visible = false;
                if (_source != null)
                {
                    SetWindowPos(_source.Handle, IntPtr.Zero, 0, 0, 0, 0,
                        SwpNoActivate | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpHideWindow);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                var source = _source;
                _source = null;
                source?.Dispose();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Builds the union of the cards' rounded rects in window-relative device pixels (the toast
        /// window's coordinates, which this window shares) and hands it to the window. Returns false
        /// when no card has a usable size.
        /// </summary>
        private bool ApplyRegion(Window toastWindow, IReadOnlyList<FrameworkElement> cards, double cornerRadiusDip)
        {
            var presentation = PresentationSource.FromVisual(toastWindow);
            var toDevice = presentation?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var windowBounds = new Rect(0, 0, toastWindow.ActualWidth, toastWindow.ActualHeight);

            var combined = IntPtr.Zero;
            try
            {
                foreach (var card in cards)
                {
                    if (card == null || card.ActualWidth <= 0 || card.ActualHeight <= 0 ||
                        !toastWindow.IsAncestorOf(card))
                    {
                        continue;
                    }

                    var bounds = card.TransformToAncestor(toastWindow)
                        .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                    bounds.Intersect(windowBounds);
                    if (bounds.IsEmpty)
                    {
                        continue;
                    }

                    // The card's own scale (fit and DPI compensation) times the device scale turns its
                    // corner radius into device pixels.
                    var scale = bounds.Width / card.ActualWidth * toDevice.M11;
                    var diameter = (int)Math.Round(cornerRadiusDip * 2 * scale);
                    var topLeft = toDevice.Transform(bounds.TopLeft);
                    var bottomRight = toDevice.Transform(bounds.BottomRight);

                    // GDI region rects exclude their right and bottom edges, hence the +1.
                    var cardRegion = CreateRoundRectRgn(
                        (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
                        (int)Math.Round(bottomRight.X) + 1, (int)Math.Round(bottomRight.Y) + 1,
                        diameter, diameter);
                    if (cardRegion == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (combined == IntPtr.Zero)
                    {
                        combined = cardRegion;
                        continue;
                    }

                    CombineRgn(combined, combined, cardRegion, RgnOr);
                    DeleteObject(cardRegion);
                }

                if (combined == IntPtr.Zero)
                {
                    return false;
                }

                // The system owns the region once SetWindowRgn succeeds; only a failed call leaves it ours.
                if (SetWindowRgn(_source.Handle, combined, true) == 0)
                {
                    DeleteObject(combined);
                    combined = IntPtr.Zero;
                    return false;
                }

                combined = IntPtr.Zero;
                return true;
            }
            finally
            {
                if (combined != IntPtr.Zero)
                {
                    DeleteObject(combined);
                }
            }
        }
    }
}
