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
    /// Gives the settings preview of a blurred-background toast something to sit on: a game image
    /// fills the preview frame, and the card is handed a blurred copy of the part behind it through
    /// the same <see cref="AchievementToastViewModel.SetLiveBackdrop"/> the in-game capture feeds,
    /// blurred by the same <see cref="BoxBlur"/> at the same reach. The card's own template then
    /// draws it with its tint, border and glow, so both sliders read as they will in game.
    ///
    /// The preview cannot capture what is behind it: anything it captured would contain the card.
    /// Off while the style has no blurred background, when the frame shows its plain surface.
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
        private bool _showingBackdrop;

        /// <param name="frame">The bordered frame around the toast preview.</param>
        /// <param name="resolveImagePath">The image to sit the card on, or null for a generated one.</param>
        public ToastPreviewBackdrop(Border frame, Func<string> resolveImagePath)
        {
            _frame = frame;
            _resolveImagePath = resolveImagePath;
        }

        /// <summary>
        /// Fills the frame and the card's blurred background for <paramref name="vm"/>, whose card
        /// is the first item of <paramref name="surface"/>, once it is laid out; restores the plain
        /// frame when the card has no blurred background.
        /// </summary>
        public void Apply(ItemsControl surface, AchievementToastViewModel vm)
        {
            if (_frame == null || vm == null)
            {
                return;
            }

            if (!vm.HasToastBlur)
            {
                RestorePlainFrame();
                return;
            }

            // The card's place in the frame is a laid-out quantity.
            _frame.Dispatcher.BeginInvoke(
                new Action(() => ApplyLaidOut(surface, vm)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplyLaidOut(ItemsControl surface, AchievementToastViewModel vm)
        {
            try
            {
                if (!vm.HasToastBlur || !TryMeasureCard(surface, out var card, out var pxPerDip))
                {
                    return;
                }

                var frameWidth = Math.Max(1, (int)Math.Round(_frame.ActualWidth * pxPerDip));
                var frameHeight = Math.Max(1, (int)Math.Round(_frame.ActualHeight * pxPerDip));
                var backdrop = CropToFill(LoadSource(), frameWidth, frameHeight);
                if (backdrop == null)
                {
                    return;
                }

                _showingBackdrop = true;
                _frame.Background = new ImageBrush(backdrop) { Stretch = Stretch.Fill };

                var blurred = BlurredCopy(backdrop, vm.BlurReachPhys);
                var viewbox = new Rect(
                    card.X / _frame.ActualWidth, card.Y / _frame.ActualHeight,
                    card.Width / _frame.ActualWidth, card.Height / _frame.ActualHeight);
                vm.SetLiveBackdrop(blurred, viewbox);
            }
            catch
            {
                // The preview keeps the card's tint over its opaque surface.
            }
        }

        private void RestorePlainFrame()
        {
            if (_showingBackdrop)
            {
                _frame.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.Surface");
                _showingBackdrop = false;
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
