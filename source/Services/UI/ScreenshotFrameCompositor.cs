using System;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Renders the theme screenshot-frame DataTemplate over a captured screenshot, producing the
    /// "framed" unlock-screenshot variant. The frame is laid out on a virtual canvas 1080 DIPs
    /// tall (width = aspect-scaled) and rendered at the screenshot's exact pixel size, so frame
    /// chrome keeps the same proportions at any resolution. Compose runs on the UI thread only
    /// (template instantiation and view-model brushes are dispatcher-affine); the returned bitmap
    /// is frozen so encoding can move to a worker thread.
    /// </summary>
    internal sealed class ScreenshotFrameCompositor
    {
        public const double ReferenceHeightDips = 1080.0;

        private readonly ILogger _logger;

        public ScreenshotFrameCompositor(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Virtual-canvas dimensions for a screenshot: the canvas is always 1080 DIPs tall and the
        /// render DPI is scaled so the canvas maps exactly onto the pixel dimensions.
        /// </summary>
        internal static (double CanvasWidth, double CanvasHeight, double Scale) ComputeCanvas(int pixelWidth, int pixelHeight)
        {
            var scale = pixelHeight / ReferenceHeightDips;
            return (pixelWidth / scale, ReferenceHeightDips, scale);
        }

        /// <summary>
        /// Composites the frame template (with the toast view model as DataContext) onto the
        /// screenshot. Returns a frozen bitmap sized exactly like the input, or null on failure.
        /// </summary>
        public BitmapSource ComposeFramed(BitmapSource screenshot, DataTemplate frameTemplate, object viewModel)
        {
            if (screenshot == null || frameTemplate == null || viewModel == null)
            {
                return null;
            }

            try
            {
                var pixelWidth = screenshot.PixelWidth;
                var pixelHeight = screenshot.PixelHeight;
                if (pixelWidth <= 0 || pixelHeight <= 0)
                {
                    return null;
                }

                var (canvasWidth, canvasHeight, scale) = ComputeCanvas(pixelWidth, pixelHeight);
                var canvasSize = new Size(canvasWidth, canvasHeight);

                var host = new ContentControl
                {
                    Content = viewModel,
                    ContentTemplate = frameTemplate,
                };
                host.Measure(canvasSize);
                host.Arrange(new Rect(canvasSize));
                host.UpdateLayout();

                var target = new RenderTargetBitmap(
                    pixelWidth,
                    pixelHeight,
                    96 * scale,
                    96 * scale,
                    PixelFormats.Pbgra32);

                var background = new DrawingVisual();
                using (var context = background.RenderOpen())
                {
                    context.DrawImage(screenshot, new Rect(0, 0, canvasWidth, canvasHeight));
                }

                // RenderTargetBitmap accumulates renders, so the screenshot goes down first and
                // the frame visual is drawn on top.
                target.Render(background);
                target.Render(host);
                target.Freeze();
                return target;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Screenshot frame compositing failed.");
                return null;
            }
        }

        /// <summary>
        /// Converts a captured GDI bitmap to a frozen WPF bitmap via LockBits (avoids the GDI
        /// handle leak of CreateBitmapSourceFromHBitmap). Thread-agnostic.
        /// </summary>
        public static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
        {
            if (bitmap == null)
            {
                return null;
            }

            var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var source = BitmapSource.Create(
                    bitmap.Width,
                    bitmap.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    data.Scan0,
                    data.Stride * bitmap.Height,
                    data.Stride);
                source.Freeze();
                return source;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
    }
}
