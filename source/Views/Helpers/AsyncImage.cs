using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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

        private static void OnUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d == null)
            {
                return;
            }

            CancelExisting(d);

            // If the new value is already an ImageSource, apply it directly
            if (e.NewValue is ImageSource imageSource)
            {
                ApplySource(d, imageSource);
                return;
            }

            if (d is FrameworkElement fe)
            {
                fe.Loaded -= OnLoaded;
                fe.Unloaded -= OnUnloaded;
                fe.Loaded += OnLoaded;
                fe.Unloaded += OnUnloaded;

                if (fe.IsLoaded)
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

        private static void CancelExisting(DependencyObject d)
        {
            try
            {
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
            var uri = GetUri(d);

            // If already an ImageSource, apply directly (fallback path from converter)
            if (uri is ImageSource imageSource)
            {
                ApplySource(d, imageSource);
                return;
            }

            var uriString = uri as string;
            if (string.IsNullOrWhiteSpace(uriString))
            {
                ApplySource(d, null);
                return;
            }

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

                var decode = GetDecodePixel(d);
                if (decode <= 0 && d is FrameworkElement fe)
                {
                    // Try to infer a reasonable decode size from the realized element.
                    var w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width;
                    var h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
                    var max = Math.Max(w, h);
                    decode = double.IsNaN(max) || max <= 0 ? 64 : (int)Math.Ceiling(max);
                }

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

        private static void ApplySource(DependencyObject d, ImageSource source)
        {
            if (d is System.Windows.Controls.Image img)
            {
                img.Source = source;
                return;
            }

            if (d is System.Windows.Media.ImageBrush brush)
            {
                brush.ImageSource = source;
                return;
            }
        }
    }
}
