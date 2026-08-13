using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Attached behavior to lazy-load images only when a control is realized.
    /// Supports Image and ImageBrush targets.
    /// </summary>
    public static class AsyncImage
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

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

        // When true (default), retained-frame animations (WebP and the legacy ImageBrush GIF
        // path) phase-lock to the process-wide epoch so recreated elements resume mid-cycle.
        // Native GIF Image controls intentionally ignore this value and start at frame one; the
        // settings background preview preserves continuity by sharing one persistent Source.
        public static readonly DependencyProperty PhaseLockProperty = DependencyProperty.RegisterAttached(
            "PhaseLock",
            typeof(bool),
            typeof(AsyncImage),
            new PropertyMetadata(true));

        public static void SetPhaseLock(DependencyObject element, bool value) => element.SetValue(PhaseLockProperty, value);
        public static bool GetPhaseLock(DependencyObject element) => (bool)element.GetValue(PhaseLockProperty);

        /// <summary>
        /// Raised when a new source object has finished loading and is ready to be shared with
        /// another visual. This is deliberately separate from Image.Source change notifications:
        /// WPF reports every WriteableBitmap frame invalidation as a Source sub-property change.
        /// </summary>
        public static readonly RoutedEvent SourceReadyEvent = EventManager.RegisterRoutedEvent(
            "SourceReady",
            RoutingStrategy.Direct,
            typeof(RoutedEventHandler),
            typeof(AsyncImage));

        public static void AddSourceReadyHandler(DependencyObject element, RoutedEventHandler handler)
        {
            if (element is UIElement uiElement)
            {
                uiElement.AddHandler(SourceReadyEvent, handler);
            }
        }

        public static void RemoveSourceReadyHandler(DependencyObject element, RoutedEventHandler handler)
        {
            if (element is UIElement uiElement)
            {
                uiElement.RemoveHandler(SourceReadyEvent, handler);
            }
        }

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

        private static readonly DependencyProperty LastEffectiveSourceIdentityProperty = DependencyProperty.RegisterAttached(
            "LastEffectiveSourceIdentity",
            typeof(object),
            typeof(AsyncImage),
            new PropertyMetadata(null));

        private static object GetLastEffectiveSourceIdentity(DependencyObject element) =>
            element.GetValue(LastEffectiveSourceIdentityProperty);

        private static void SetLastEffectiveSourceIdentity(DependencyObject element, object value) =>
            element.SetValue(LastEffectiveSourceIdentityProperty, value);

        private static readonly DependencyProperty ActiveAnimationSourceProperty = DependencyProperty.RegisterAttached(
            "ActiveAnimationSource",
            typeof(string),
            typeof(AsyncImage),
            new PropertyMetadata(null));

        private static string GetActiveAnimationSource(DependencyObject element) =>
            element?.GetValue(ActiveAnimationSourceProperty) as string;

        private static void SetActiveAnimationSource(DependencyObject element, string value) =>
            element?.SetValue(ActiveAnimationSourceProperty, value);

        private static readonly DependencyProperty NativeGifAnimationProperty = DependencyProperty.RegisterAttached(
            "NativeGifAnimation",
            typeof(NativeGifAnimation),
            typeof(AsyncImage),
            new PropertyMetadata(null));

        private static NativeGifAnimation GetNativeGifAnimation(DependencyObject element) =>
            element?.GetValue(NativeGifAnimationProperty) as NativeGifAnimation;

        private static void SetNativeGifAnimation(DependencyObject element, NativeGifAnimation value) =>
            element?.SetValue(NativeGifAnimationProperty, value);

        private static void OnUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d == null)
            {
                return;
            }

            var previousIdentity = GetLastEffectiveSourceIdentity(d);
            var nextIdentity = GetEffectiveSourceIdentity(d);
            var sourceIdentityChanged = !Equals(previousIdentity, nextIdentity);

            // A mutable ImageSource stored in this attached property reports every frame dirty as
            // a dependency-property sub-change. Its identity has not changed, so leave the shared
            // source attached and let WPF redraw it instead of reapplying it on every GIF frame.
            if (!sourceIdentityChanged && GetUri(d) is ImageSource)
            {
                return;
            }

            // Native GIFs render at their own dimensions, so DecodePixel changes do not require a
            // reload. PhaseLock has no change callback; a running native GIF likewise ignores it.
            // Gray participates in the effective identity and therefore still restarts correctly.
            if (!sourceIdentityChanged && GetNativeGifAnimation(d) != null)
            {
                return;
            }

            CancelExisting(d);
            SetLastRequestedDecodePixel(d, 0);
            SetLastEffectiveSourceIdentity(d, nextIdentity);

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

            // If the current value is already an ImageSource, apply it directly.
            if (GetUri(d) is ImageSource imageSource)
            {
                ApplySource(d, imageSource);
                return;
            }

            if (nextIdentity == null)
            {
                ApplySource(d, null);
                return;
            }

            if (sourceIdentityChanged)
            {
                // The logical source changed (for example a recycled row bound to a different icon),
                // so clear the old visual immediately instead of leaving stale artwork on screen.
                ApplySource(d, null);
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

            var normalizedUri = AnimatedImageHelper.NormalizeSourceUri(uri);
            if (!string.IsNullOrWhiteSpace(normalizedUri) &&
                Services.Images.ImageFormats.IsAnimatedFile(normalizedUri))
            {
                // Animation frames use the decode size resolved by the initial load. Rebuilding a
                // running animation for every inferred-size increase causes visible flicker; the
                // surfaces where animation resolution matters (such as toast backgrounds) set an
                // explicit DecodePixel.
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
                // An animation kept alive across the hide is still attached to the element,
                // so re-loading would needlessly tear it down (and the async rebuild can race a
                // subsequent hide, leaving a static frame). Only (re)start when nothing is running.
                if (GetActiveAnimationSource(fe) != null)
                {
                    return;
                }

                _ = StartLoadAsync(fe);
                return;
            }

            // The element (or its window) was hidden — e.g. the toast's focus-hiding loop toggling
            // window visibility while a game is foreground. Cancel a pending async load so a late
            // static frame cannot overwrite the animation, but leave any running animation in
            // place: its timeline keeps advancing and resumes rendering when the element reappears,
            // instead of restarting from scratch on every focus flip.
            CancelPendingLoad(fe);
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

        // Cancels only a pending async load (leaving any running animation untouched), used
        // when an element is merely hidden rather than having its logical source change. Keeping
        // the animation attached lets it resume on re-show without an async rebuild.
        private static void CancelPendingLoad(DependencyObject d)
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
                SetLastEffectiveSourceIdentity(d, null);
                ApplySource(d, null);
                return;
            }

            CancelExisting(d);

            if (GetGray(d) && !uriString.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
            {
                uriString = GrayPrefix + uriString;
            }

            // OnUriChanged clears the visual when the logical source changes.
            // For same-source reloads (visibility/decode changes), keep the current
            // image visible until the refreshed bitmap is ready to avoid flash.

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

                // Resume on the UI thread: StartLoadAsync is only entered from dispatcher
                // contexts, and the whole tail below (ApplySource, animation start, finally
                // bookkeeping) touches thread-affine DependencyObjects.
                BitmapSource bmp = await service.GetAsync(uriString, decode, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                // XamlAnimatedGif clears Image.Source while it initializes. Do not first publish
                // the static frame for a GIF and create a visible static -> blank -> live flash;
                // keep it blank until the live bitmap is attached, retaining bmp only as the
                // corrupt/unsupported fallback.
                var applyGray = GetGray(d) || AnimatedImageHelper.HasGrayPrefix(uriString);
                if (d is System.Windows.Controls.Image image &&
                    await TryStartNativeGifAsync(
                        image,
                        uriString,
                        bmp,
                        applyGray,
                        cts.Token))
                {
                    return;
                }

                ApplySource(d, bmp);

                // Start animation after the first static frame is available. Await setup so this
                // CTS remains cancellable until the native decoder or retained-frame fallback has
                // actually been attached; a recycled control cannot receive an obsolete source.
                await StartAnimationAsync(d, uriString, decode, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                // Keep blank on failure.
                Logger?.Debug(ex, $"AsyncImage load failed for '{uriString}'.");
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

        private static async Task StartAnimationAsync(
            DependencyObject d,
            string uriString,
            int decodePixel,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(uriString))
            {
                return;
            }

            try
            {
                // Read on the UI thread so the background decode below never touches the target.
                var applyGray = GetGray(d) || AnimatedImageHelper.HasGrayPrefix(uriString);

                // Fast path: with the frames already cached (e.g. a settings mockup
                // rebuilt during a slider drag), building the animation is cheap — attach it
                // synchronously, in the same dispatcher pass as the static bitmap, so the
                // element never renders an out-of-phase frame.
                if (TryApplyCachedAnimation(d, uriString, applyGray, decodePixel))
                {
                    return;
                }

                // Cache miss: decode off the UI thread. Only the frozen frames cross back; the
                // animation is always built at apply time so its phase-locked BeginTime never has
                // to be stamped onto an already-frozen instance.
                var decoded = await Task.Run(
                    () => !cancellationToken.IsCancellationRequested &&
                          AnimatedImageHelper.TryEnsureCachedFrames(uriString, applyGray, decodePixel),
                    cancellationToken);

                if (!decoded || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    TryApplyCachedAnimation(d, uriString, applyGray, decodePixel);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger?.Debug(ex, $"Animation setup failed for '{uriString}'.");
            }
        }

        private static async Task<bool> TryStartNativeGifAsync(
            System.Windows.Controls.Image image,
            string uriString,
            ImageSource fallback,
            bool applyGray,
            CancellationToken cancellationToken)
        {
            var localPath = AnimatedImageHelper.NormalizeSourceUri(uriString);
            if (string.IsNullOrWhiteSpace(localPath) ||
                !Services.Images.ImageFormats.IsGifExtension(
                    Services.Images.ImageFormats.GetExtension(localPath)) ||
                !System.IO.Path.IsPathRooted(localPath) ||
                !System.IO.File.Exists(localPath))
            {
                return false;
            }

            NativeGifAnimation animation = null;
            try
            {
                animation = await NativeGifAnimation.CreateAsync(
                    image,
                    uriString,
                    localPath,
                    fallback,
                    applyGray,
                    cancellationToken,
                    ex => Logger?.Debug(ex, $"Native GIF playback failed for '{uriString}'."),
                    () => RaiseSourceReady(image));

                cancellationToken.ThrowIfCancellationRequested();
                animation.Failed += OnNativeGifFailed;
                SetNativeGifAnimation(image, animation);
                SetActiveAnimationSource(image, localPath);
                animation.Start();
                return true;
            }
            catch
            {
                if (animation != null)
                {
                    animation.Failed -= OnNativeGifFailed;
                    animation.Dispose();
                }

                image.Source = fallback;
                RaiseSourceReady(image);
                throw;
            }
        }

        private static void OnNativeGifFailed(object sender, EventArgs e)
        {
            if (!(sender is NativeGifAnimation animation))
            {
                return;
            }

            var image = animation.Target;
            animation.Failed -= OnNativeGifFailed;
            if (ReferenceEquals(GetNativeGifAnimation(image), animation))
            {
                SetNativeGifAnimation(image, null);
                SetActiveAnimationSource(image, null);
            }
        }

        /// <summary>
        /// Builds the animation over the cached frames and attaches it. Returns false when the
        /// source is not decoded yet, leaving the target untouched. UI thread only: the phase-lock
        /// flag is read off the target here, at attach time, so a late-set PhaseLock still takes
        /// effect.
        /// </summary>
        private static bool TryApplyCachedAnimation(
            DependencyObject target,
            string uriString,
            bool applyGray,
            int decodePixel)
        {
            if (!AnimatedImageHelper.TryCreateAnimationFromCache(
                    uriString,
                    applyGray,
                    decodePixel,
                    GetPhaseLock(target),
                    out var normalizedSource,
                    out var firstFrame,
                    out var animation))
            {
                return false;
            }

            ApplyAnimatedFrames(target, normalizedSource, firstFrame, animation);
            return true;
        }

        private static void ApplySource(DependencyObject d, ImageSource source)
        {
            if (d is System.Windows.Controls.Image img)
            {
                StopAnimation(d);
                img.Source = source;
                RaiseSourceReady(img);
                return;
            }

            if (d is System.Windows.Media.ImageBrush brush)
            {
                StopAnimation(d);
                brush.ImageSource = source;
                return;
            }
        }

        private static void RaiseSourceReady(System.Windows.Controls.Image image)
        {
            image?.RaiseEvent(new RoutedEventArgs(SourceReadyEvent, image));
        }

        private static object GetEffectiveSourceIdentity(DependencyObject d)
        {
            var uri = GetUri(d);
            if (uri is ImageSource imageSource)
            {
                return imageSource;
            }

            if (!(uri is string uriString))
            {
                return null;
            }

            return NormalizeEffectiveUriIdentity(uriString, GetGray(d));
        }

        private static string NormalizeEffectiveUriIdentity(string uri, bool applyGray)
        {
            var normalized = (uri ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (applyGray && !normalized.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = GrayPrefix + normalized;
            }

            return normalized;
        }

        private static int ResolveDecodePixel(DependencyObject d)
        {
            var explicitDecode = GetDecodePixel(d);
            if (explicitDecode < 0)
            {
                // Negative opt-out: decode at native resolution (no DecodePixelWidth
                // downscale and no size-inferred reloads). Passed through negative so
                // MemoryImageService can distinguish it from "unset".
                return -1;
            }

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
            SetActiveAnimationSource(target, normalizedSource);

            // The animation arrives already stamped with its phase-locked BeginTime and frozen
            // (see AnimatedImageHelper.TryCreateAnimationFromCache), so it is attached as-is. Never
            // clone it to adjust BeginTime here: Freezable.Clone on a frozen animation deep-copies
            // every key frame's bitmap through CachedBitmap.CloneCore, which reallocates every
            // decoded frame per attach and exhausts memory on long animations.
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
            SetActiveAnimationSource(target, null);

            if (target is System.Windows.Controls.Image image)
            {
                var nativeGif = GetNativeGifAnimation(image);
                if (nativeGif != null)
                {
                    SetNativeGifAnimation(image, null);
                    nativeGif.Failed -= OnNativeGifFailed;
                    nativeGif.Dispose();
                }

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
