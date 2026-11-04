using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Lets the settings toast preview sit on a game, as the notification will in game: when shown,
    /// a game image fills the preview frame behind the card, and a card with a blurred background
    /// is handed a blurred copy of the part behind it through the same
    /// <see cref="AchievementToastViewModel.SetLiveBackdrop"/> the in-game capture feeds, blurred by
    /// the same <see cref="BoxBlur"/> at the same reach, so its template draws it with its tint,
    /// border and glow and both sliders read as they will in game. When hidden, the frame shows its
    /// plain surface and a blurred card its tint over its opaque surface.
    ///
    /// The preview cannot capture what is behind it: anything it captured would contain the card.
    /// UI thread only.
    /// </summary>
    internal sealed class ToastPreviewBackdrop
    {
        // The blurred copy is read back at this fraction of the frame's pixels and stretched by
        // the card's brush, as the live capture's mip level is.
        private const int BlurDownscale = 4;

        private readonly Border _frame;
        private readonly Func<string> _resolveImagePath;
        private readonly Dictionary<string, BitmapSource> _decoded = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        private BitmapSource _fallback;

        // The frame's current backdrop and what it was cut from, so a rebuild that changes neither
        // the image nor the frame size (every style edit) reuses it.
        private BitmapSource _backdrop;
        private BitmapSource _backdropSource;

        /// <param name="frame">The bordered frame around the toast preview.</param>
        /// <param name="resolveImagePath">The image to sit the card on, or null for a generated one.</param>
        public ToastPreviewBackdrop(Border frame, Func<string> resolveImagePath)
        {
            _frame = frame;
            _resolveImagePath = resolveImagePath;
        }

        /// <summary>
        /// Shows or hides the game behind <paramref name="vm"/>'s card (the first item of
        /// <paramref name="surface"/>). Showing fills the frame, and the card's blurred background
        /// when it has one, once the card is laid out.
        /// </summary>
        public void Apply(ItemsControl surface, AchievementToastViewModel vm, bool show)
        {
            if (_frame == null || vm == null)
            {
                return;
            }

            if (!show)
            {
                Hide(vm);
                return;
            }

            // The frame's size and the card's place in it are laid-out quantities.
            _frame.Dispatcher.BeginInvoke(
                new Action(() => ApplyLaidOut(surface, vm)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void Hide(AchievementToastViewModel vm)
        {
            if (_backdrop != null)
            {
                _frame.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.Surface");
                _backdrop = null;
                _backdropSource = null;
            }

            vm.SetLiveBackdrop(null, new Rect(0, 0, 1, 1));
        }

        private void ApplyLaidOut(ItemsControl surface, AchievementToastViewModel vm)
        {
            try
            {
                if (!TryMeasureCard(surface, out var card, out var pxPerDip))
                {
                    return;
                }

                var frameWidth = Math.Max(1, (int)Math.Round(_frame.ActualWidth * pxPerDip));
                var frameHeight = Math.Max(1, (int)Math.Round(_frame.ActualHeight * pxPerDip));
                var source = LoadSource();
                if (_backdrop == null || !ReferenceEquals(_backdropSource, source) ||
                    _backdrop.PixelWidth != frameWidth || _backdrop.PixelHeight != frameHeight)
                {
                    _backdrop = CropToFill(source, frameWidth, frameHeight);
                    _backdropSource = source;
                    if (_backdrop == null)
                    {
                        return;
                    }

                    var brush = new ImageBrush(_backdrop) { Stretch = Stretch.Fill };
                    brush.Freeze();
                    _frame.Background = brush;
                }

                if (vm.HasToastBlur)
                {
                    var viewbox = new Rect(
                        card.X / _frame.ActualWidth, card.Y / _frame.ActualHeight,
                        card.Width / _frame.ActualWidth, card.Height / _frame.ActualHeight);
                    vm.SetLiveBackdrop(BlurredCopy(_backdrop, vm.BlurReachPhys), viewbox);
                }
            }
            catch
            {
                // The frame keeps its previous backdrop; a blurred card keeps its tint over its
                // opaque surface.
            }
        }

        /// <summary>
        /// The card's rect (its template root, without the glow room) in the frame's DIPs, and the
        /// frame's device pixels per DIP.
        /// </summary>
        private bool TryMeasureCard(ItemsControl surface, out Rect card, out double pxPerDip)
        {
            card = Rect.Empty;
            pxPerDip = 1;
            if (surface == null || surface.Items.Count == 0 || _frame.ActualWidth <= 0 || _frame.ActualHeight <= 0 ||
                !(surface.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement container) ||
                VisualTreeHelper.GetChildrenCount(container) == 0 ||
                !(VisualTreeHelper.GetChild(container, 0) is FrameworkElement root) ||
                root.ActualWidth <= 0 || !_frame.IsAncestorOf(root))
            {
                return false;
            }

            card = root.TransformToAncestor(_frame).TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            pxPerDip = PresentationSource.FromVisual(_frame)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            return true;
        }

        private BitmapSource LoadSource()
        {
            var path = _resolveImagePath?.Invoke();
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (_decoded.TryGetValue(path, out var cached))
                {
                    return cached;
                }

                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 1280;
                    image.EndInit();
                    image.Freeze();
                    _decoded[path] = image;
                    return image;
                }
                catch
                {
                    _decoded[path] = null;
                }
            }

            return _fallback ?? (_fallback = GenerateFallback());
        }

        /// <summary>
        /// A colorful stand-in with enough structure for the blur to read, for a library with no
        /// background art.
        /// </summary>
        private static BitmapSource GenerateFallback()
        {
            const int width = 960;
            const int height = 540;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1B, 0x26, 0x3B), Color.FromRgb(0x3A, 0x1C, 0x47), 30), null,
                    new Rect(0, 0, width, height));
                var spots = new[]
                {
                    (Color.FromRgb(0xF4, 0xA2, 0x61), 220.0, 160.0, 150.0),
                    (Color.FromRgb(0x2A, 0x9D, 0x8F), 640.0, 330.0, 190.0),
                    (Color.FromRgb(0xE7, 0x6F, 0x51), 820.0, 120.0, 110.0),
                    (Color.FromRgb(0x48, 0x95, 0xEF), 360.0, 430.0, 130.0)
                };
                foreach (var (color, x, y, radius) in spots)
                {
                    var glow = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
                    dc.DrawEllipse(glow, null, new Point(x, y), radius, radius);
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The centered part of <paramref name="source"/> that fills a frame of this size, at that size.</summary>
        private static BitmapSource CropToFill(BitmapSource source, int width, int height)
        {
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return null;
            }

            var scale = Math.Max((double)width / source.PixelWidth, (double)height / source.PixelHeight);
            var cropWidth = Math.Min(source.PixelWidth, Math.Max(1, (int)Math.Round(width / scale)));
            var cropHeight = Math.Min(source.PixelHeight, Math.Max(1, (int)Math.Round(height / scale)));
            var cropped = new CroppedBitmap(
                source,
                new Int32Rect((source.PixelWidth - cropWidth) / 2, (source.PixelHeight - cropHeight) / 2, cropWidth, cropHeight));
            var sized = new TransformedBitmap(cropped, new ScaleTransform((double)width / cropWidth, (double)height / cropHeight));
            var converted = new FormatConvertedBitmap(sized, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }

        /// <summary>
        /// <paramref name="backdrop"/> shrunk by <see cref="BlurDownscale"/> and blurred by
        /// <see cref="BoxBlur"/> at <paramref name="reachPhys"/> device pixels, as the live capture
        /// blurs its shrunk region.
        /// </summary>
        private static BitmapSource BlurredCopy(BitmapSource backdrop, double reachPhys)
        {
            var small = new FormatConvertedBitmap(
                new TransformedBitmap(backdrop, new ScaleTransform(1.0 / BlurDownscale, 1.0 / BlurDownscale)),
                PixelFormats.Bgra32, null, 0);
            var width = small.PixelWidth;
            var height = small.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            small.CopyPixels(pixels, stride, 0);

            var reach = reachPhys / BlurDownscale;
            if (reach >= 1)
            {
                BoxBlur.Apply(pixels, 0, width, height, stride, 4, BoxBlur.RadiusFor(reach));
            }

            var blurred = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            blurred.Freeze();
            return blurred;
        }
    }
}
