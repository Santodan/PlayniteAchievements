using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Drives the live blurred background of a wave's blurred cards, from the slide-in to the end of
    /// the slide-out. Each rendered frame it measures where the cards are on screen (slide included),
    /// asks the capture for that region, writes the newest blurred frame into one shared bitmap and
    /// hands every card its part of it. The capture lags the card by a frame or two; the frame
    /// carries the screen rect it spans, so each card is placed against where it is now, and the
    /// requested region is padded so a moving card stays inside the frame it is shown from.
    ///
    /// In game it captures the game window, which never contains the toast. Without a game (the
    /// test fire) it captures the monitor and excludes the toast window from screen capture for its
    /// lifetime, so the blur never contains the card itself.
    /// </summary>
    internal sealed class ToastLiveBackdrop : IDisposable
    {
        private const uint WdaNone = 0;
        private const uint WdaExcludeFromCapture = 0x11;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private readonly Window _window;
        private readonly ItemsControl _surface;
        private readonly LiveRegionCapture _capture;
        private readonly double _reachPhys;
        private readonly IntPtr _excludedHwnd;
        private readonly List<KeyValuePair<AchievementToastViewModel, System.Drawing.Rectangle>> _cards =
            new List<KeyValuePair<AchievementToastViewModel, System.Drawing.Rectangle>>();

        private WriteableBitmap _bitmap;
        private System.Drawing.Rectangle _frameRect;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
        private int _framesApplied;
        private bool _disposed;

        private ToastLiveBackdrop(
            Window window, ItemsControl surface, LiveRegionCapture capture, double reachPhys, IntPtr excludedHwnd)
        {
            _window = window;
            _surface = surface;
            _capture = capture;
            _reachPhys = reachPhys;
            _excludedHwnd = excludedHwnd;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>
        /// Starts the live background for the blurred cards of <paramref name="surface"/>, capturing
        /// <paramref name="gameHwnd"/>, or the toast's monitor when it is zero. Null when no card is
        /// blurred or capture is unavailable, with <paramref name="skipped"/> saying why (null when
        /// no card asked for it); the cards then show their tint over the opaque surface. UI thread;
        /// needs the toast window's handle.
        /// </summary>
        public static ToastLiveBackdrop TryStart(Window window, ItemsControl surface, IntPtr gameHwnd, out string skipped)
        {
            skipped = null;
            try
            {
                var reach = 0d;
                var anyBlurred = false;
                foreach (var item in surface?.Items ?? (System.Collections.IEnumerable)Array.Empty<object>())
                {
                    if (item is AchievementToastViewModel vm && vm.HasToastBlur)
                    {
                        anyBlurred = true;
                        reach = Math.Max(reach, vm.BlurReachPhys);
                    }
                }

                if (!anyBlurred)
                {
                    return null;
                }

                var toastHwnd = window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
                if (toastHwnd == IntPtr.Zero)
                {
                    skipped = "no toast window handle";
                    return null;
                }

                var wholeMonitor = gameHwnd == IntPtr.Zero;
                if (wholeMonitor && !SetWindowDisplayAffinity(toastHwnd, WdaExcludeFromCapture))
                {
                    skipped = "the toast could not be excluded from the monitor capture (error " +
                              Marshal.GetLastWin32Error() + ")";
                    return null;
                }

                var capture = LiveRegionCapture.TryStart(wholeMonitor ? toastHwnd : gameHwnd, wholeMonitor, reach);
                if (capture == null)
                {
                    if (wholeMonitor)
                    {
                        SetWindowDisplayAffinity(toastHwnd, WdaNone);
                    }

                    skipped = "window capture is not supported";
                    return null;
                }

                return new ToastLiveBackdrop(window, surface, capture, reach, wholeMonitor ? toastHwnd : IntPtr.Zero);
            }
            catch (Exception ex)
            {
                skipped = ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// One line for the wave's diagnostics: the capture's state plus how many frames reached the
        /// cards. Read after <see cref="Dispose"/> for the whole wave.
        /// </summary>
        public string Describe()
        {
            return $"{_capture.Describe()} applied={_framesApplied} reach={_reachPhys:0.#}";
        }

        private void OnRendering(object sender, EventArgs e)
        {
            try
            {
                // WPF can raise Rendering more than once for the same frame.
                if (e is RenderingEventArgs args)
                {
                    if (args.RenderingTime == _lastRenderingTime)
                    {
                        return;
                    }

                    _lastRenderingTime = args.RenderingTime;
                }

                if (!MeasureCards(out var union))
                {
                    return;
                }

                // Cards travel up to about their own size per slide, a few frames of it before the
                // next capture lands; a quarter of the larger edge covers that at the capture rate.
                var motionPad = Math.Max(union.Width, union.Height) / 4;
                _capture.Request(LiveBackdropMath.RequestFor(union, _reachPhys, motionPad));

                var frame = _capture.TakeLatest();
                if (frame != null)
                {
                    WriteFrame(frame);
                    _framesApplied++;
                }

                if (_bitmap == null)
                {
                    return;
                }

                foreach (var card in _cards)
                {
                    var viewbox = LiveBackdropMath.Viewbox(card.Value, _frameRect);
                    if (!viewbox.IsEmpty)
                    {
                        card.Key.SetLiveBackdrop(_bitmap, new Rect(viewbox.X, viewbox.Y, viewbox.Width, viewbox.Height));
                    }
                }
            }
            catch
            {
                // A missed frame keeps the previous background.
            }
        }

        /// <summary>
        /// Each blurred card's on-screen rect in physical pixels (its template root, which excludes
        /// the glow room around it), and their union.
        /// </summary>
        private bool MeasureCards(out System.Drawing.Rectangle union)
        {
            union = System.Drawing.Rectangle.Empty;
            _cards.Clear();
            if (_window.ActualWidth <= 0 || _window.ActualHeight <= 0 ||
                !ToastWindowPlacer.TryGetPhysicalRect(_window, out var windowPhys))
            {
                return false;
            }

            var pxPerDipX = windowPhys.Width / _window.ActualWidth;
            var pxPerDipY = windowPhys.Height / _window.ActualHeight;
            for (var i = 0; i < _surface.Items.Count; i++)
            {
                if (!(_surface.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container) ||
                    !(container.Content is AchievementToastViewModel vm) || !vm.HasToastBlur ||
                    VisualTreeHelper.GetChildrenCount(container) == 0 ||
                    !(VisualTreeHelper.GetChild(container, 0) is FrameworkElement root) ||
                    root.ActualWidth <= 0 || root.ActualHeight <= 0 ||
                    !_window.IsAncestorOf(root))
                {
                    continue;
                }

                // Includes the slide host's translate and scale, so a sliding card is measured where
                // it is drawn.
                var bounds = root.TransformToAncestor(_window)
                    .TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                var screen = new System.Drawing.Rectangle(
                    windowPhys.X + (int)Math.Round(bounds.X * pxPerDipX),
                    windowPhys.Y + (int)Math.Round(bounds.Y * pxPerDipY),
                    Math.Max(1, (int)Math.Round(bounds.Width * pxPerDipX)),
                    Math.Max(1, (int)Math.Round(bounds.Height * pxPerDipY)));
                _cards.Add(new KeyValuePair<AchievementToastViewModel, System.Drawing.Rectangle>(vm, screen));
                union = union.IsEmpty ? screen : System.Drawing.Rectangle.Union(union, screen);
            }

            return _cards.Count > 0;
        }

        private void WriteFrame(LiveRegionCapture.Frame frame)
        {
            if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
            _frameRect = frame.ScreenRect;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CompositionTarget.Rendering -= OnRendering;
            _capture.Dispose();

            try
            {
                if (_excludedHwnd != IntPtr.Zero)
                {
                    SetWindowDisplayAffinity(_excludedHwnd, WdaNone);
                }

                foreach (var item in _surface.Items)
                {
                    (item as AchievementToastViewModel)?.SetLiveBackdrop(null, new Rect(0, 0, 1, 1));
                }
            }
            catch
            {
            }
        }
    }
}
