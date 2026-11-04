using System;
using System.Collections.Generic;
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
        // PluginLogger, not LogManager: the latter writes to playnite.log, which rotates per session,
        // so the GIF playback-path line this class emits was landing in a file that is gone by the time
        // anyone looks for it. The plugin's own log is what a user sends.
        private static readonly ILogger Logger =
            Services.Logging.PluginLogger.GetLogger(nameof(AsyncImage));

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

        /// <summary>
        /// When true, the Image decodes at the file's native resolution and its MaxWidth/MaxHeight
        /// are set to the loaded bitmap's pixel size, one pixel per DIP, so it can shrink to fit
        /// its container but never grows past its own size. Bound to the
        /// PlayAch.Image.CapAtOwnSize resource by the resizable grid columns.
        /// </summary>
        public static readonly DependencyProperty CapAtOwnSizeProperty = DependencyProperty.RegisterAttached(
            "CapAtOwnSize",
            typeof(bool),
            typeof(AsyncImage),
            new PropertyMetadata(false, OnCapAtOwnSizeChanged));

        public static void SetCapAtOwnSize(DependencyObject element, bool value) => element.SetValue(CapAtOwnSizeProperty, value);
        public static bool GetCapAtOwnSize(DependencyObject element) => (bool)element.GetValue(CapAtOwnSizeProperty);

        // Whether UpdateOwnSizeCap wrote the current MaxWidth/MaxHeight, so clearing the cap
        // never removes limits set by XAML.
        private static readonly DependencyProperty OwnSizeCapAppliedProperty = DependencyProperty.RegisterAttached(
            "OwnSizeCapApplied",
            typeof(bool),
            typeof(AsyncImage),
            new PropertyMetadata(false));

        // When true (default), retained-frame WebP animations phase-lock to the process-wide epoch
        // so recreated elements resume mid-cycle. GIFs intentionally ignore this value and start at
        // frame one; the settings background preview preserves continuity by sharing one persistent
        // Source.
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

        private static readonly DependencyProperty FrameClockProperty = DependencyProperty.RegisterAttached(
            "FrameClock", typeof(AnimationClock), typeof(AsyncImage), new PropertyMetadata(null));

        private static readonly DependencyProperty AnimationsPausedProperty = DependencyProperty.RegisterAttached(
            "AnimationsPaused", typeof(bool), typeof(AsyncImage), new PropertyMetadata(false));

        /// <summary>
        /// Pauses native GIF/WebM playback and WebP frame clocks in the visual subtree for a
        /// notification slide. Even a predecoded frame swap invalidates the whole-card cache.
        /// Keeping the clock lets WebP resume without cloning its retained bitmaps. UI thread only.
        /// </summary>
        public static void PauseGifAnimationsUnder(DependencyObject root)
        {
            ForEachImage(root, image =>
            {
                if ((bool)image.GetValue(AnimationsPausedProperty)) return;
                image.SetValue(AnimationsPausedProperty, true);
                GetNativeGifAnimation(image)?.Pause();
                (image.GetValue(FrameClockProperty) as AnimationClock)?.Controller?.Pause();
            });
        }

        /// <summary>Resumes the decoders <see cref="PauseGifAnimationsUnder"/> paused.</summary>
        public static void ResumeGifAnimationsUnder(DependencyObject root)
        {
            ForEachImage(root, image =>
            {
                if (!(bool)image.GetValue(AnimationsPausedProperty)) return;
                image.ClearValue(AnimationsPausedProperty);
                GetNativeGifAnimation(image)?.Resume();
                (image.GetValue(FrameClockProperty) as AnimationClock)?.Controller?.Resume();
            });
        }

        /// <summary>
        /// Shows a still in place of each image under <paramref name="root"/> that
        /// <paramref name="stillFor"/> returns one for, keyed by the image's local file path,
        /// until the returned scope is disposed; images it returns null for are left alone. For a
        /// synchronous render such as a screenshot: both sides run inside one dispatcher callback,
        /// so the screen never shows the swap. The change goes through SetCurrentValue so the
        /// image's own value and bindings keep ownership, and a WebP frame clock is detached for
        /// the span (an animation would override the still) and reattached where it left off.
        /// UI thread only.
        /// </summary>
        internal static IDisposable HoldStills(DependencyObject root, Func<string, ImageSource> stillFor)
        {
            var restores = new List<Action>();
            if (stillFor == null)
            {
                return new RestoreScope(restores);
            }

            ForEachImage(root, target =>
            {
                if (!(GetUri(target) is string uri) || !(target is IAnimatable animatable))
                {
                    return;
                }

                var path = AnimatedImageHelper.NormalizeSourceUri(uri);
                var still = string.IsNullOrWhiteSpace(path) ? null : stillFor(path);
                if (still == null)
                {
                    return;
                }

                var property = target is System.Windows.Controls.Image
                    ? System.Windows.Controls.Image.SourceProperty
                    : ImageBrush.ImageSourceProperty;
                var clock = target.GetValue(FrameClockProperty) as AnimationClock;
                if (clock != null)
                {
                    animatable.ApplyAnimationClock(property, null);
                }

                var previous = target.GetValue(property);
                target.SetCurrentValue(property, still);
                restores.Add(() =>
                {
                    target.SetCurrentValue(property, previous);
                    if (clock != null)
                    {
                        animatable.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
                    }
                });
            });

            return new RestoreScope(restores);
        }

        private sealed class RestoreScope : IDisposable
        {
            private List<Action> _restores;

            internal RestoreScope(List<Action> restores)
            {
                _restores = restores;
            }

            public void Dispose()
            {
                var restores = _restores;
                _restores = null;
                if (restores == null)
                {
                    return;
                }

                for (var i = restores.Count - 1; i >= 0; i--)
                {
                    restores[i]();
                }
            }
        }

        private static void ForEachImage(
            DependencyObject root, Action<DependencyObject> action)
        {
            if (root == null)
            {
                return;
            }

            if (root is System.Windows.Controls.Image || root is ImageBrush)
            {
                action(root);
            }

            if (!(root is Visual)) return;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                ForEachImage(VisualTreeHelper.GetChild(root, i), action);
            }
        }

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
            if (!(sender is DependencyObject d))
            {
                return;
            }

            // OnUnloaded leaves the GIF attached (clearing it would flash on a visibility toggle) and
            // only takes it out of playback, so a reload just counts it back in.
            if (GetNativeGifAnimation(d) is NativeGifAnimation reloaded)
            {
                reloaded.SetOffscreen(!(d is FrameworkElement reloadedElement) || !reloadedElement.IsVisible);
                return;
            }

            _ = StartLoadAsync(d);
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
                // A recycled/reparented image can leave the paused subtree before Resume walks
                // it. Its next attachment must not inherit that old slide's pause.
                d.ClearValue(AnimationsPausedProperty);
                // Cancel any pending load but don't clear the source.
                // The image is cached by ImageService, so clearing causes
                // unnecessary visual flash during visibility toggles
                // without freeing any memory.
                CancelExisting(d);
            }
        }

        private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is FrameworkElement fe))
            {
                return;
            }

            // A hidden GIF keeps its frame and position but stops counting toward playback, so a
            // collapsed panel or an inactive tab costs no decoding.
            GetNativeGifAnimation(fe)?.SetOffscreen(!fe.IsLoaded || !fe.IsVisible);

            if (!fe.IsLoaded)
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

            // The element (or its window) was hidden. Cancel a pending async load so a late static
            // frame cannot overwrite the animation, but leave any attached animation in place: it
            // continues from its frame when the element reappears instead of restarting.
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
                var applyGray = GetGray(d) || AnimatedImageHelper.HasGrayPrefix(uriString);
                var image = d as System.Windows.Controls.Image;

                // A GIF already on disk plays straight from its file: the player shows its own
                // first frame, so no static still is decoded or cached for it. The still is only
                // loaded if playback fails (OnNativeGifFailed).
                if (image != null &&
                    await TryStartNativeGifAsync(image, uriString, fallback: null, applyGray, cts.Token))
                {
                    LogGifPath(uriString, "native");
                    return;
                }

                // Resume on the UI thread: StartLoadAsync is only entered from dispatcher
                // contexts, and the whole tail below (ApplySource, animation start, finally
                // bookkeeping) touches thread-affine DependencyObjects.
                BitmapSource bmp = await service.GetAsync(uriString, decode, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                // A remote GIF reaches disk through the load above, so it can play natively now.
                // Do not publish the still first: keep it blank until the live bitmap is attached,
                // retaining bmp only as the corrupt/unsupported fallback.
                if (image != null)
                {
                    if (await TryStartNativeGifAsync(image, uriString, bmp,
                        applyGray,
                        cts.Token))
                    {
                        LogGifPath(uriString, "native");
                        return;
                    }

                    LogGifPath(uriString, "frames");
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
                !Services.Images.ImageFormats.UsesNativePlayer(localPath) ||
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
                if ((bool)image.GetValue(AnimationsPausedProperty)) animation.Pause();
                animation.Start();
                return true;
            }
            catch (Exception ex)
            {
                if (animation != null)
                {
                    animation.Failed -= OnNativeGifFailed;
                    animation.Dispose();
                }

                // A cancelled load was superseded by a newer one that owns the image now. Player
                // creation can outlast the newer load (a WebM opens its decoders first), so writing
                // the fallback here would blank the source the newer load already attached.
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Without a still in hand the caller loads one itself, so an unreadable GIF just
                // takes the normal image path.
                if (fallback == null && !(ex is OperationCanceledException))
                {
                    if (ReferenceEquals(GetNativeGifAnimation(image), animation))
                    {
                        SetNativeGifAnimation(image, null);
                        SetActiveAnimationSource(image, null);
                    }

                    Logger?.Debug(ex, $"Native GIF playback unavailable for '{uriString}'.");
                    return false;
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

            if (image.Source == null)
            {
                _ = ApplyStaticFallbackAsync(image, animation.SourceIdentity);
            }
        }

        /// <summary>
        /// Loads the still for a GIF whose playback failed after it started without one. Applied
        /// only if the element still shows nothing for the same source.
        /// </summary>
        private static async Task ApplyStaticFallbackAsync(
            System.Windows.Controls.Image image, string uriString)
        {
            var service = PlayniteAchievementsPlugin.Instance?.ImageService;
            if (service == null || string.IsNullOrWhiteSpace(uriString))
            {
                return;
            }

            var identity = GetLastEffectiveSourceIdentity(image);
            try
            {
                var bmp = await service.GetAsync(uriString, GetLastRequestedDecodePixel(image), CancellationToken.None);
                if (bmp != null &&
                    image.Source == null &&
                    GetNativeGifAnimation(image) == null &&
                    Equals(identity, GetLastEffectiveSourceIdentity(image)))
                {
                    ApplySource(image, bmp);
                }
            }
            catch (Exception ex)
            {
                Logger?.Debug(ex, $"GIF still fallback failed for '{uriString}'.");
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
            UpdateOwnSizeCap(image);
            image?.RaiseEvent(new RoutedEventArgs(SourceReadyEvent, image));
        }

        private static void OnCapAtOwnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            UpdateOwnSizeCap(d as System.Windows.Controls.Image);

            // Reload through the Uri path so the decode moves between the capped size and native
            // resolution. A native GIF already plays at native size and ignores the reload.
            OnUriChanged(d, e);
        }

        private static void UpdateOwnSizeCap(System.Windows.Controls.Image image)
        {
            if (image == null)
            {
                return;
            }

            if (GetCapAtOwnSize(image) && image.Source is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                image.MaxWidth = bitmap.PixelWidth;
                image.MaxHeight = bitmap.PixelHeight;
                image.SetValue(OwnSizeCapAppliedProperty, true);
                return;
            }

            if ((bool)image.GetValue(OwnSizeCapAppliedProperty))
            {
                image.ClearValue(FrameworkElement.MaxWidthProperty);
                image.ClearValue(FrameworkElement.MaxHeightProperty);
                image.ClearValue(OwnSizeCapAppliedProperty);
            }
        }

        // GIF-candidate URIs whose chosen playback path has already been reported, so the line is
        // one per source per session rather than one per element per reload.
        private static readonly HashSet<string> LoggedGifPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reports which playback path a GIF took. The two behave differently for anyone reusing
        /// the Image's Source: "native" mutates one bitmap in place, so an ImageBrush holding that
        /// object animates with it, while "frames" replaces Source per frame, leaving such a brush
        /// on whichever frame it captured. The settings background mockup depends on the former.
        /// </summary>
        private static void LogGifPath(string uriString, string path)
        {
            try
            {
                var normalized = AnimatedImageHelper.NormalizeSourceUri(uriString);
                if (string.IsNullOrWhiteSpace(normalized) ||
                    !Services.Images.ImageFormats.UsesNativePlayer(normalized) ||
                    !LoggedGifPaths.Add(normalized + "|" + path))
                {
                    return;
                }

                Logger?.Info(
                    $"[Image] '{normalized}' plays via the {path} path " +
                    $"(rooted={System.IO.Path.IsPathRooted(normalized)}, " +
                    $"exists={System.IO.File.Exists(normalized)}).");
            }
            catch
            {
            }
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
            if (explicitDecode < 0 || GetCapAtOwnSize(d))
            {
                // Negative opt-out, or an image capped at its own size: decode at native
                // resolution (no DecodePixelWidth downscale and no size-inferred reloads).
                // Passed through negative so MemoryImageService can distinguish it from "unset".
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
            var clock = animation.CreateClock();
            target.SetValue(FrameClockProperty, clock);
            if ((bool)target.GetValue(AnimationsPausedProperty)) clock.Controller?.Pause();
            if (target is System.Windows.Controls.Image image)
            {
                image.Source = firstFrame;
                image.ApplyAnimationClock(System.Windows.Controls.Image.SourceProperty, clock, HandoffBehavior.SnapshotAndReplace);
                UpdateOwnSizeCap(image);
                return;
            }

            if (target is System.Windows.Media.ImageBrush brush)
            {
                brush.ImageSource = firstFrame;
                brush.ApplyAnimationClock(System.Windows.Media.ImageBrush.ImageSourceProperty, clock, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private static void StopAnimation(DependencyObject target)
        {
            SetActiveAnimationSource(target, null);
            target.ClearValue(FrameClockProperty);

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
