using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Attached behavior to lazy-load images only when a control is realized.
    /// Supports Image and ImageBrush targets.
    /// </summary>
    public static class AsyncImage
    {
        private const string GrayPrefix = "gray:";
        private const int DefaultDecodePixel = 64;
        private const double DecodeOverscan = 1.25;
        private const double DecodeReloadThreshold = 1.2;

        public static readonly DependencyProperty UriProperty = DependencyProperty.RegisterAttached(
            "Uri",
            typeof(object),
            typeof(AsyncImage),
            new PropertyMetadata(null, OnUriChanged));

        public static void SetUri(DependencyObject element, object value) => element.SetValue(UriProperty, value);
        public static object GetUri(DependencyObject element) => element.GetValue(UriProperty);

        public static readonly DependencyProperty DecodePixelProperty = DependencyProperty.RegisterAttached(
            "DecodePixel",
            typeof(int),
            typeof(AsyncImage),
            new PropertyMetadata(0, OnUriChanged));

        public static void SetDecodePixel(DependencyObject element, int value) => element.SetValue(DecodePixelProperty, value);
        public static int GetDecodePixel(DependencyObject element) => (int)element.GetValue(DecodePixelProperty);

        public static readonly DependencyProperty GrayProperty = DependencyProperty.RegisterAttached(
            "Gray",
            typeof(bool),
            typeof(AsyncImage),
            new PropertyMetadata(false, OnUriChanged));

        public static void SetGray(DependencyObject element, bool value) => element.SetValue(GrayProperty, value);
        public static bool GetGray(DependencyObject element) => (bool)element.GetValue(GrayProperty);

        // Private attached state
        private static readonly DependencyProperty LoadCtsProperty = DependencyProperty.RegisterAttached(
            "LoadCts",
            typeof(CancellationTokenSource),
            typeof(AsyncImage),
            new PropertyMetadata(null));

        private static CancellationTokenSource GetLoadCts(DependencyObject element) =>
            (CancellationTokenSource)element.GetValue(LoadCtsProperty);

        private static void SetLoadCts(DependencyObject element, CancellationTokenSource value) =>
            element.SetValue(LoadCtsProperty, value);

        private static readonly DependencyProperty LastRequestedDecodePixelProperty = DependencyProperty.RegisterAttached(
            "LastRequestedDecodePixel",
            typeof(int),
            typeof(AsyncImage),
            new PropertyMetadata(0));

        private static int GetLastRequestedDecodePixel(DependencyObject element) =>
            (int)element.GetValue(LastRequestedDecodePixelProperty);

        private static void SetLastRequestedDecodePixel(DependencyObject element, int value) =>
            element.SetValue(LastRequestedDecodePixelProperty, value);

        private static readonly DependencyProperty ActiveAnimatedGifSourceProperty = DependencyProperty.RegisterAttached(
            "ActiveAnimatedGifSource",
            typeof(string),
            typeof(AsyncImage),
            new PropertyMetadata(null));

        private static string GetActiveAnimatedGifSource(DependencyObject element) =>
            element?.GetValue(ActiveAnimatedGifSourceProperty) as string;

        private static void SetActiveAnimatedGifSource(DependencyObject element, string value) =>
            element?.SetValue(ActiveAnimatedGifSourceProperty, value);

        private static void OnUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d == null)
            {
                return;
            }

            CancelExisting(d);

            if (d is FrameworkElement fe)
            {
                fe.Loaded -= OnLoaded;
                fe.Unloaded -= OnUnloaded;
                fe.SizeChanged -= OnSizeChanged;
                fe.IsVisibleChanged -= OnIsVisibleChanged;
                fe.Loaded += OnLoaded;
                fe.Unloaded += OnUnloaded;
                fe.SizeChanged += OnSizeChanged;
                fe.IsVisibleChanged += OnIsVisibleChanged;
            }

            // If the new value is already an ImageSource, apply it directly
            if (e.NewValue is ImageSource imageSource)
            {
                SetLastRequestedDecodePixel(d, 0);
                ApplySource(d, imageSource);
                return;
            }

            if (d is FrameworkElement loadedElement)
            {
                if (loadedElement.IsLoaded)
                {
                    _ = StartLoadAsync(d);
                }
            }
            else
            {
                // Freezables like ImageBrush have no Loaded/Unloaded; load immediately.
                _ = StartLoadAsync(d);
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is DependencyObject d)
            {
                _ = StartLoadAsync(d);
            }
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !fe.IsLoaded)
            {
                return;
            }

            if (!fe.IsVisible)
            {
                return;
            }

            if (!(GetUri(fe) is string uri) || string.IsNullOrWhiteSpace(uri))
            {
                return;
            }

            var normalizedGifUri = GifAnimationHelper.NormalizeGifSourceUri(uri);
            if (!string.IsNullOrWhiteSpace(normalizedGifUri) &&
                normalizedGifUri.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                // GIFs do not benefit from decode-pixel resize reloads and reloading causes visible animation flicker.
                return;
            }

            var desiredDecode = ResolveDecodePixel(fe);
            if (desiredDecode <= 0)
            {
                return;
            }

            var lastDecode = GetLastRequestedDecodePixel(fe);
            if (lastDecode > 0 && desiredDecode <= Math.Ceiling(lastDecode * DecodeReloadThreshold))
            {
                return;
            }

            _ = StartLoadAsync(fe);
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is DependencyObject d)
            {
                // Cancel any pending load but don't clear the source.
                // The image is cached by ImageService, so clearing causes
                // unnecessary visual flash during visibility toggles
                // without freeing any memory.
                CancelExisting(d);
            }
        }

        private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !fe.IsLoaded)
            {
                return;
            }

            if (fe.IsVisible)
            {
                _ = StartLoadAsync(fe);
                return;
            }

            CancelExisting(fe);
        }

        private static void CancelExisting(DependencyObject d)
        {
            try
            {
                StopAnimation(d);

                var existing = GetLoadCts(d);
                if (existing != null)
                {
                    existing.Cancel();
                    existing.Dispose();
                }
            }
            catch
            {
            }
            finally
            {
                SetLoadCts(d, null);
            }
        }

        private static async Task StartLoadAsync(DependencyObject d)
        {
            if (d is FrameworkElement fe && !fe.IsVisible)
            {
                return;
            }

            var uri = GetUri(d);

            // If already an ImageSource, apply directly (fallback path from converter)
            if (uri is ImageSource imageSource)
            {
                SetLastRequestedDecodePixel(d, 0);
                ApplySource(d, imageSource);
                return;
            }

            var uriString = uri as string;
            if (string.IsNullOrWhiteSpace(uriString))
            {
                SetLastRequestedDecodePixel(d, 0);
                ApplySource(d, null);
                return;
            }

            CancelExisting(d);

            if (GetGray(d) && !uriString.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
            {
                uriString = GrayPrefix + uriString;
            }

            // Don't clear existing source while loading - keep current image visible
            // until the new one is ready. This prevents flash during visibility toggles.

            var cts = new CancellationTokenSource();
            SetLoadCts(d, cts);

            try
            {
                var service = PlayniteAchievementsPlugin.Instance?.ImageService;
                if (service == null)
                {
                    return;
                }

                var decode = ResolveDecodePixel(d);
                SetLastRequestedDecodePixel(d, decode);

                BitmapSource bmp = await service.GetAsync(uriString, decode, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                // Apply on UI thread if needed.
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            ApplySource(d, bmp);
                        }
                    }));
                }
                else
                {
                    ApplySource(d, bmp);
                }

                // Start GIF animation asynchronously after the first static frame is already visible.
                _ = StartGifAnimationAsync(d, uriString, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch
            {
                // ignore; keep blank
            }
            finally
            {
                // Only clear if this CTS is still current
                var current = GetLoadCts(d);
                if (ReferenceEquals(current, cts))
                {
                    SetLoadCts(d, null);
                }
                try { cts.Dispose(); } catch { }
            }
        }

        private static async Task StartGifAnimationAsync(DependencyObject d, string uriString, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(uriString))
            {
                return;
            }

            try
            {
                var applyGray = GetGray(d);
                var created = await Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return (ok: false, normalized: (string)null, firstFrame: (ImageSource)null, animation: (ObjectAnimationUsingKeyFrames)null);
                    }

                    var ok = GifAnimationHelper.TryCreateAnimation(
                        uriString,
                        applyGray,
                        out var normalized,
                        out var firstFrame,
                        out var animation);
                    return (ok, normalized, firstFrame, animation);
                }, cancellationToken).ConfigureAwait(false);

                if (!created.ok || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            ApplyAnimatedFrames(d, created.normalized, created.firstFrame, created.animation);
                        }
                    }));
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    ApplyAnimatedFrames(d, created.normalized, created.firstFrame, created.animation);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private static void ApplySource(DependencyObject d, ImageSource source)
        {
            if (d is System.Windows.Controls.Image img)
            {
                StopAnimation(d);
                img.Source = source;
                return;
            }

            if (d is System.Windows.Media.ImageBrush brush)
            {
                StopAnimation(d);
                brush.ImageSource = source;
                return;
            }
        }

        private static int ResolveDecodePixel(DependencyObject d)
        {
            var explicitDecode = GetDecodePixel(d);
            if (!(d is FrameworkElement fe))
            {
                return explicitDecode > 0 ? explicitDecode : DefaultDecodePixel;
            }

            var inferredDecode = InferDecodePixel(fe);
            if (explicitDecode > 0 && inferredDecode > 0)
            {
                return Math.Max(explicitDecode, inferredDecode);
            }

            if (explicitDecode > 0)
            {
                return explicitDecode;
            }

            return inferredDecode > 0 ? inferredDecode : DefaultDecodePixel;
        }

        private static void ApplyAnimatedFrames(DependencyObject target, string normalizedSource, ImageSource firstFrame, ObjectAnimationUsingKeyFrames animation)
        {
            StopAnimation(target);
            SetActiveAnimatedGifSource(target, normalizedSource);

            if (target is System.Windows.Controls.Image image)
            {
                image.Source = firstFrame;
                image.BeginAnimation(System.Windows.Controls.Image.SourceProperty, animation, HandoffBehavior.SnapshotAndReplace);
                return;
            }

            if (target is System.Windows.Media.ImageBrush brush)
            {
                brush.ImageSource = firstFrame;
                brush.BeginAnimation(System.Windows.Media.ImageBrush.ImageSourceProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private static void StopAnimation(DependencyObject target)
        {
            SetActiveAnimatedGifSource(target, null);

            if (target is System.Windows.Controls.Image image)
            {
                image.BeginAnimation(System.Windows.Controls.Image.SourceProperty, null);
                return;
            }

            if (target is System.Windows.Media.ImageBrush brush)
            {
                brush.BeginAnimation(System.Windows.Media.ImageBrush.ImageSourceProperty, null);
            }
        }

        private static int InferDecodePixel(FrameworkElement fe)
        {
            var width = GetRealizedLength(fe.ActualWidth, fe.Width);
            var height = GetRealizedLength(fe.ActualHeight, fe.Height);
            var maxLength = Math.Max(width, height);
            if (maxLength <= 0)
            {
                return 0;
            }

            var dpiScale = 1.0;
            if (fe is Visual visual)
            {
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(visual);
                    dpiScale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
                }
                catch
                {
                }
            }

            return (int)Math.Ceiling(maxLength * dpiScale * DecodeOverscan);
        }

        private static double GetRealizedLength(double actual, double fallback)
        {
            if (!double.IsNaN(actual) && actual > 0)
            {
                return actual;
            }

            if (!double.IsNaN(fallback) && fallback > 0)
            {
                return fallback;
            }

            return 0;
        }
    }
}
