using Playnite.SDK;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Decodes animated images into frozen frames and builds the WPF animation that plays them.
    /// Handles GIF and WebP, which differ in how much work they need: WIC returns a GIF's raw
    /// sub-frames, which must be composited against a running canvas, but returns a WebP's frames
    /// already composited to full canvas.
    /// </summary>
    internal static class AnimatedImageHelper
    {
        private const string GrayPrefix = "gray:";
        private const string CacheBustPrefix = "cachebust|";
        private const string PreviewHttpPrefix = "previewhttp:";
        private const int MaxAnimationFrames = 600;
        private const int MaxFramePixelArea = 2048 * 2048;
        private const int MaxCachedAnimations = 64;

        // Every retained frame is a full-canvas snapshot, so retained bytes scale with
        // area x frame count. MaxFramePixelArea only bounds the area, which a wide-but-short
        // animation passes while still costing hundreds of megabytes across a few hundred frames.
        // This budget bounds the product: 32 Mpx is a 128 MB ceiling per animation. Animated
        // sources are reduced to their requested display width before this budget is applied, so
        // the ceiling removes temporal frames only after spatial resolution has been made useful.
        private const long MaxRetainedPixels = 32L * 1024 * 1024;

        // Aggregate ceiling across every cached animation (~128 MB of BGRA). The entry count alone
        // let a handful of large animations dominate the process.
        private const long MaxCachedBytes = 128L * 1024 * 1024;

        private const int BytesPerCompositedPixel = 4;

        // Below this an animation is not worth retaining; the caller falls back to the static
        // image path instead. It is also what sends a single-frame GIF or a still WebP down the
        // ordinary image path.
        private const int MinAnimationFrames = 2;

        // Applied when a frame declares no usable duration of its own.
        private const int DefaultFrameDelayMilliseconds = 100;

        private static readonly ILogger Logger = LogManager.GetLogger();

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, (List<BitmapSource> Frames, List<int> Delays)> FrameCache =
            new Dictionary<string, (List<BitmapSource> Frames, List<int> Delays)>(StringComparer.OrdinalIgnoreCase);

        // Wall-clock epoch used to phase-lock every animation instance: a recreated element
        // (e.g. the settings mockup rebuilding during a slider drag) resumes the animation mid-cycle
        // instead of restarting it from the first frame.
        private static readonly System.Diagnostics.Stopwatch AnimationEpoch =
            System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Decodes and caches an animation's frames so a later
        /// <see cref="TryCreateAnimationFromCache"/> call succeeds. Safe to run on a background
        /// thread: the cached frames are frozen bitmaps and no animation is built here, so no
        /// thread-affine <see cref="Freezable"/> crosses back to the UI thread.
        /// </summary>
        public static bool TryEnsureCachedFrames(string uri, bool applyGray, int decodePixel)
        {
            return TryResolveFrames(uri, applyGray, decodePixel, decodeIfMissing: true, out _, out _);
        }

        /// <summary>
        /// Builds a ready-to-begin animation over already-cached frames; fails when the source has
        /// not been decoded yet (see <see cref="TryEnsureCachedFrames"/>). Never decodes, so it is
        /// cheap enough to run synchronously on the UI thread: the frames are shared with the
        /// cache and only the lightweight key frames are new. A recreated element can therefore
        /// attach its animation in the same layout pass and never flash a static frame.
        /// </summary>
        /// <remarks>
        /// <paramref name="phaseLock"/> is resolved here rather than by the caller because the
        /// animation must be frozen before WPF receives it, and cloning a frozen animation to
        /// stamp BeginTime afterwards deep-copies every frame bitmap
        /// (<c>CachedBitmap.CloneCore</c>), which is an out-of-memory risk on long animations.
        /// </remarks>
        public static bool TryCreateAnimationFromCache(
            string uri,
            bool applyGray,
            int decodePixel,
            bool phaseLock,
            out string normalizedSource,
            out ImageSource firstFrame,
            out ObjectAnimationUsingKeyFrames animation)
        {
            firstFrame = null;
            animation = null;

            if (!TryResolveFrames(
                    uri,
                    applyGray,
                    decodePixel,
                    decodeIfMissing: false,
                    out normalizedSource,
                    out var cached))
            {
                return false;
            }

            try
            {
                var keyFrames = new ObjectAnimationUsingKeyFrames
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };

                var current = TimeSpan.Zero;
                var frameCount = Math.Min(cached.Frames.Count, cached.Delays.Count);
                for (var i = 0; i < frameCount; i++)
                {
                    var frame = cached.Frames[i];
                    if (frame == null)
                    {
                        continue;
                    }

                    keyFrames.KeyFrames.Add(new DiscreteObjectKeyFrame(frame, KeyTime.FromTimeSpan(current)));
                    current = current.Add(TimeSpan.FromMilliseconds(cached.Delays[i]));
                }

                if (keyFrames.KeyFrames.Count == 0)
                {
                    return false;
                }

                // One iteration = the full frame sequence.
                keyFrames.Duration = new Duration(current);

                // Stamped immediately before freezing, i.e. at the moment the caller is about to
                // begin the animation, so no creation-to-begin delay leaks in as a per-instance
                // phase error.
                keyFrames.BeginTime = phaseLock ? PhaseLockBeginTime(keyFrames.Duration) : TimeSpan.Zero;

                if (keyFrames.CanFreeze)
                {
                    keyFrames.Freeze();
                }

                // Phase-locked surfaces show the frame at the current phase until the animation
                // takes over. Independent surfaces such as live toasts start at frame zero.
                var phaseMilliseconds = phaseLock && current.TotalMilliseconds > 0
                    ? AnimationEpoch.ElapsedMilliseconds % current.TotalMilliseconds
                    : 0.0;
                firstFrame = FrameAtPhase(cached, phaseMilliseconds);
                animation = keyFrames;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves a source string to its cached frames, optionally decoding when the cache
        /// misses. Shared by the background decode pass and the UI-thread build pass.
        /// </summary>
        private static bool TryResolveFrames(
            string uri,
            bool applyGray,
            int decodePixel,
            bool decodeIfMissing,
            out string normalizedSource,
            out (List<BitmapSource> Frames, List<int> Delays) frames)
        {
            normalizedSource = NormalizeSourceUri(uri);
            frames = default((List<BitmapSource> Frames, List<int> Delays));

            // Some preview paths encode grayscale intent directly in the source string (gray:...)
            // instead of the AsyncImage.Gray attached property.
            applyGray = applyGray || HasGrayPrefix(uri);

            if (string.IsNullOrWhiteSpace(normalizedSource) ||
                !ImageFormats.IsAnimationCandidate(normalizedSource) ||
                !Path.IsPathRooted(normalizedSource) ||
                !File.Exists(normalizedSource))
            {
                return false;
            }

            try
            {
                var cacheKey = GetFrameCacheKey(normalizedSource, applyGray, decodePixel);
                var cached = TryGetCachedAnimation(cacheKey);
                if (cached == null)
                {
                    if (!decodeIfMissing)
                    {
                        return false;
                    }

                    cached = Decode(normalizedSource, applyGray, decodePixel);
                    if (cached == null)
                    {
                        return false;
                    }

                    SetCachedAnimation(cacheKey, cached.Value);
                }

                if (cached.Value.Frames.Count == 0)
                {
                    return false;
                }

                frames = cached.Value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Decodes one source into frozen frames and their delays, dispatching on format. Returns
        /// null when the source is a still image, exceeds the retention budget, or cannot be
        /// decoded on this machine.
        /// </summary>
        private static (List<BitmapSource> Frames, List<int> Delays)? Decode(
            string normalizedSource,
            bool applyGray,
            int decodePixel)
        {
            // IgnoreImageCache bypasses WPF's URI-keyed decode cache: the managed image slots reuse
            // fixed file names, so an overwritten animation at the same path must decode fresh bytes.
            const BitmapCreateOptions createOptions =
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache;
            var sourceUri = new Uri(normalizedSource, UriKind.Absolute);

            if (ImageFormats.IsWebpExtension(ImageFormats.GetExtension(normalizedSource)))
            {
                // Checked up front so a machine without the codec costs a cheap boolean per image
                // rather than a thrown decoder exception.
                if (!WebpCodecProbe.IsSupported)
                {
                    return null;
                }

                var webpDecoder = BitmapDecoder.Create(sourceUri, createOptions, BitmapCacheOption.OnLoad);
                if (webpDecoder?.Frames == null || webpDecoder.Frames.Count < MinAnimationFrames)
                {
                    return null;
                }

                var sourceDelays = BuildWebpFrameDelays(normalizedSource, webpDecoder.Frames.Count);
                var webpFrames = BuildFullCanvasFrames(
                    webpDecoder,
                    applyGray,
                    decodePixel,
                    sourceDelays,
                    out var webpDelays);
                if (webpFrames.Count == 0)
                {
                    return null;
                }

                return (webpFrames, webpDelays);
            }

            var decoder = new GifBitmapDecoder(sourceUri, createOptions, BitmapCacheOption.OnLoad);
            if (decoder.Frames == null || decoder.Frames.Count == 0)
            {
                return null;
            }

            var sourceGifDelays = BuildFrameDelays(decoder, decoder.Frames.Count);
            var composited = BuildCompositedGifFrames(
                decoder,
                applyGray,
                decodePixel,
                sourceGifDelays,
                out var delays);
            if (composited.Count == 0)
            {
                return null;
            }

            if (delays.Count != composited.Count)
            {
                return null;
            }

            return (composited, delays);
        }

        /// <summary>
        /// Takes decoder frames as they are, for formats WIC already composites to full canvas.
        /// </summary>
        /// <remarks>
        /// Running these through the GIF canvas walk would blend each frame over its predecessor a
        /// second time, so the two paths must stay separate.
        /// </remarks>
        private static List<BitmapSource> BuildFullCanvasFrames(
            BitmapDecoder decoder,
            bool applyGray,
            int decodePixel,
            IList<int> sourceDelays,
            out List<int> retainedDelays)
        {
            var result = new List<BitmapSource>();
            var first = decoder.Frames[0];
            ResolveAnimationDimensions(
                first.PixelWidth,
                first.PixelHeight,
                decodePixel,
                decoder.Frames.Count,
                out var targetWidth,
                out var targetHeight);
            var retainedFrameCount = ResolveFrameBudget(
                targetWidth,
                targetHeight,
                decoder.Frames.Count);
            BuildFrameRetentionPlan(
                decoder.Frames.Count,
                retainedFrameCount,
                sourceDelays,
                out var retainedIndices,
                out var plannedDelays);
            retainedDelays = new List<int>(retainedIndices.Count);

            for (var retainedIndex = 0; retainedIndex < retainedIndices.Count; retainedIndex++)
            {
                BitmapSource frame = decoder.Frames[retainedIndices[retainedIndex]];
                if (frame == null)
                {
                    continue;
                }

                frame = ResizeAndDetachFrame(frame, targetWidth, targetHeight);
                if (applyGray)
                {
                    frame = ConvertToGrayscale(frame);
                }

                if (frame.CanFreeze)
                {
                    frame.Freeze();
                }

                result.Add(frame);
                retainedDelays.Add(plannedDelays[retainedIndex]);
            }

            return result;
        }

        /// <summary>
        /// Removes cached frames for every source whose key contains the given segment
        /// (case-insensitive). Called through the image service's eviction chokepoint so an
        /// overwritten or cleared animation at a fixed managed path never re-serves the old frames.
        /// </summary>
        internal static void EvictBySegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return;
            }

            lock (CacheSync)
            {
                List<string> keysToEvict = null;
                foreach (var key in FrameCache.Keys)
                {
                    if (key.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        (keysToEvict ?? (keysToEvict = new List<string>())).Add(key);
                    }
                }

                if (keysToEvict == null)
                {
                    return;
                }

                foreach (var key in keysToEvict)
                {
                    FrameCache.Remove(key);
                }
            }
        }

        public static string NormalizeSourceUri(string uri)
        {
            var normalized = (uri ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            normalized = StripCacheBustPrefix(normalized);
            while (!string.IsNullOrWhiteSpace(normalized) &&
                   normalized.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(GrayPrefix.Length);
            }

            if (normalized.StartsWith(PreviewHttpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(PreviewHttpPrefix.Length);
            }

            if ((normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                ImageFormats.IsAnimationCandidate(normalized))
            {
                try
                {
                    var disk = PlayniteAchievementsPlugin.Instance?.DiskImageService;
                    var cachePath = disk?.GetIconCachePathFromUri(normalized, decodeSize: 0, gameId: null);
                    if (!string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath))
                    {
                        normalized = cachePath;
                    }
                }
                catch
                {
                }
            }

            return normalized;
        }

        public static bool HasGrayPrefix(string uri)
        {
            var normalized = (uri ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            normalized = StripCacheBustPrefix(normalized);

            return normalized.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string StripCacheBustPrefix(string value)
        {
            var normalized = value;
            while (!string.IsNullOrWhiteSpace(normalized) &&
                   normalized.StartsWith(CacheBustPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var firstSeparator = normalized.IndexOf('|');
                if (firstSeparator < 0)
                {
                    break;
                }

                var secondSeparator = normalized.IndexOf('|', firstSeparator + 1);
                if (secondSeparator < 0 || secondSeparator + 1 >= normalized.Length)
                {
                    break;
                }

                normalized = normalized.Substring(secondSeparator + 1);
            }

            return normalized;
        }

        private static int GetGifFrameDelayMilliseconds(BitmapFrame frame)
        {
            try
            {
                if (frame?.Metadata is BitmapMetadata metadata)
                {
                    var delay = ReadMetadataInt(metadata, "/grctlext/Delay");
                    if (delay > 0)
                    {
                        return delay * 10;
                    }
                }
            }
            catch
            {
            }

            return DefaultFrameDelayMilliseconds;
        }

        private static List<int> BuildFrameDelays(GifBitmapDecoder decoder, int frameCount)
        {
            var delays = new List<int>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var delayMilliseconds = i < decoder.Frames.Count
                    ? GetGifFrameDelayMilliseconds(decoder.Frames[i])
                    : DefaultFrameDelayMilliseconds;
                if (delayMilliseconds < 20)
                {
                    delayMilliseconds = DefaultFrameDelayMilliseconds;
                }

                delays.Add(delayMilliseconds);
            }

            return delays;
        }

        /// <summary>
        /// Per-frame delays for a WebP, read from its ANMF chunks. Falls back to the default for
        /// any frame the container does not account for, so the delay list always matches the
        /// frame list.
        /// </summary>
        private static List<int> BuildWebpFrameDelays(string normalizedSource, int frameCount)
        {
            var hasDurations = WebpAnimationInfo.TryReadFrameDurations(normalizedSource, out var durations);
            var delays = new List<int>(frameCount);

            for (var i = 0; i < frameCount; i++)
            {
                delays.Add(hasDurations && i < durations.Count
                    ? durations[i]
                    : DefaultFrameDelayMilliseconds);
            }

            return delays;
        }

        /// <summary>
        /// Selects evenly spaced frames from the complete source and assigns each selected frame
        /// the time covered by its bucket. The sum of the retained delays therefore stays equal
        /// to the source duration even when the pixel budget permits only a small number of
        /// frames. This is especially important for large toast backgrounds: retaining only the
        /// leading frames made that short prefix loop repeatedly and look much faster than the
        /// original GIF.
        /// </summary>
        internal static void BuildFrameRetentionPlan(
            int availableFrames,
            int retainedFrames,
            IList<int> sourceDelays,
            out List<int> retainedIndices,
            out List<int> retainedDelays)
        {
            retainedIndices = new List<int>();
            retainedDelays = new List<int>();

            var sourceCount = Math.Min(availableFrames, sourceDelays?.Count ?? 0);
            var retainedCount = Math.Min(sourceCount, Math.Max(0, retainedFrames));
            if (sourceCount <= 0 || retainedCount <= 0)
            {
                return;
            }

            retainedIndices.Capacity = retainedCount;
            retainedDelays.Capacity = retainedCount;

            for (var bucket = 0; bucket < retainedCount; bucket++)
            {
                var start = (int)((long)bucket * sourceCount / retainedCount);
                var end = (int)((long)(bucket + 1) * sourceCount / retainedCount);
                long bucketDelay = 0;
                for (var sourceIndex = start; sourceIndex < end; sourceIndex++)
                {
                    bucketDelay += Math.Max(1, sourceDelays[sourceIndex]);
                }

                retainedIndices.Add(start);
                retainedDelays.Add((int)Math.Min(int.MaxValue, bucketDelay));
            }
        }

        /// <summary>
        /// The negative BeginTime that aligns an animation with the shared epoch when begun
        /// right now. Call immediately before BeginAnimation so no creation-to-begin delay
        /// leaks into the phase.
        /// </summary>
        internal static TimeSpan PhaseLockBeginTime(Duration iterationDuration)
        {
            var totalMilliseconds = iterationDuration.HasTimeSpan
                ? iterationDuration.TimeSpan.TotalMilliseconds
                : 0.0;
            return totalMilliseconds <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(-(AnimationEpoch.ElapsedMilliseconds % totalMilliseconds));
        }

        private static BitmapSource FrameAtPhase(
            (List<BitmapSource> Frames, List<int> Delays) cached,
            double phaseMilliseconds)
        {
            var elapsed = 0.0;
            for (var i = 0; i < cached.Frames.Count && i < cached.Delays.Count; i++)
            {
                elapsed += cached.Delays[i];
                if (phaseMilliseconds < elapsed)
                {
                    return cached.Frames[i];
                }
            }

            return cached.Frames[0];
        }

        private static string GetFrameCacheKey(string normalizedSource, bool applyGray, int decodePixel)
        {
            var sizeKey = Math.Max(0, decodePixel);
            return sizeKey + "|" + (applyGray ? "gray|" : string.Empty) + normalizedSource;
        }

        private static (List<BitmapSource> Frames, List<int> Delays)? TryGetCachedAnimation(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return null;
            }

            lock (CacheSync)
            {
                if (!FrameCache.TryGetValue(cacheKey, out var cached))
                {
                    return null;
                }
                return cached;
            }
        }

        private static void SetCachedAnimation(string cacheKey, (List<BitmapSource> Frames, List<int> Delays) cached)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || cached.Frames == null || cached.Delays == null)
            {
                return;
            }

            lock (CacheSync)
            {
                FrameCache[cacheKey] = cached;
                TrimCache(cacheKey);
            }
        }

        /// <summary>
        /// Evicts entries until the cache is inside both the entry-count and the retained-bytes
        /// budget, never evicting <paramref name="keepKey"/> (the entry just added). Caller holds
        /// <see cref="CacheSync"/>.
        /// </summary>
        private static void TrimCache(string keepKey)
        {
            var retainedBytes = 0L;
            foreach (var entry in FrameCache.Values)
            {
                retainedBytes += EstimateRetainedBytes(entry);
            }

            if (FrameCache.Count <= MaxCachedAnimations && retainedBytes <= MaxCachedBytes)
            {
                return;
            }

            // Any eviction order is acceptable for a cache; evicting one entry at a time (rather
            // than clearing wholesale, as this previously did) keeps unrelated animations warm.
            foreach (var key in new List<string>(FrameCache.Keys))
            {
                if (FrameCache.Count <= MaxCachedAnimations && retainedBytes <= MaxCachedBytes)
                {
                    return;
                }

                if (string.Equals(key, keepKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (FrameCache.TryGetValue(key, out var evicted))
                {
                    retainedBytes -= EstimateRetainedBytes(evicted);
                    FrameCache.Remove(key);
                }
            }
        }

        /// <summary>
        /// Bytes held by one cache entry. Every retained frame is a full-canvas snapshot of the
        /// same size, whichever format it came from, so the first frame's dimensions describe them
        /// all.
        /// </summary>
        private static long EstimateRetainedBytes((List<BitmapSource> Frames, List<int> Delays) cached)
        {
            var first = cached.Frames != null && cached.Frames.Count > 0 ? cached.Frames[0] : null;
            return first == null
                ? 0L
                : (long)first.PixelWidth * first.PixelHeight * BytesPerCompositedPixel * cached.Frames.Count;
        }

        internal static void ResolveAnimationDimensions(
            int sourceWidth,
            int sourceHeight,
            int decodePixel,
            int availableFrames,
            out int targetWidth,
            out int targetHeight)
        {
            targetWidth = Math.Max(1, sourceWidth);
            targetHeight = Math.Max(1, sourceHeight);

            var desiredFrames = Math.Min(Math.Max(0, availableFrames), MaxAnimationFrames);
            if (desiredFrames <= 0)
            {
                return;
            }

            var maxFrameArea = Math.Max(1L, MaxRetainedPixels / desiredFrames);
            var targetArea = (long)targetWidth * targetHeight;
            if (targetArea <= maxFrameArea)
            {
                // The complete animation already fits. Keep its native pixels even if the control
                // normally requests a smaller still-image decode; icon animations are commonly
                // modest enough that downscaling them buys nothing material.
                return;
            }

            // The native animation is too large to retain. Start with the surface's requested
            // display width, which is usually enough to bring wide toast backgrounds under the
            // ceiling without sacrificing a single temporal frame.
            if (sourceWidth > 0 && sourceHeight > 0 && decodePixel > 0 && sourceWidth > decodePixel)
            {
                targetWidth = decodePixel;
                targetHeight = Math.Max(
                    1,
                    (int)Math.Round(sourceHeight * (decodePixel / (double)sourceWidth)));
                targetArea = (long)targetWidth * targetHeight;
                if (targetArea <= maxFrameArea)
                {
                    return;
                }
            }

            // Even the requested display size is too expensive. Prefer temporal fidelity over
            // surplus pixels and reduce both dimensions only as far as the full frame sequence
            // requires.
            var scale = Math.Sqrt(maxFrameArea / (double)targetArea);
            targetWidth = Math.Max(1, (int)Math.Floor(targetWidth * scale));
            targetHeight = Math.Max(1, (int)Math.Floor(targetHeight * scale));

            // Floating-point rounding can leave the product a few pixels over the exact ceiling.
            while ((long)targetWidth * targetHeight > maxFrameArea)
            {
                if (targetWidth >= targetHeight && targetWidth > 1)
                {
                    targetWidth--;
                }
                else if (targetHeight > 1)
                {
                    targetHeight--;
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Produces a standalone frame at the useful display resolution. The detached pixel copy
        /// is important: caching a TransformedBitmap directly would also retain its full-size
        /// source and defeat the animation memory budget.
        /// </summary>
        private static BitmapSource ResizeAndDetachFrame(BitmapSource source, int targetWidth, int targetHeight)
        {
            if (source == null)
            {
                return null;
            }

            if (source.PixelWidth == targetWidth && source.PixelHeight == targetHeight)
            {
                return source;
            }

            BitmapSource resized = new TransformedBitmap(
                source,
                new ScaleTransform(
                    targetWidth / (double)source.PixelWidth,
                    targetHeight / (double)source.PixelHeight));
            if (resized.Format != PixelFormats.Bgra32)
            {
                resized = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
            }

            var stride = targetWidth * BytesPerCompositedPixel;
            var pixels = new byte[stride * targetHeight];
            resized.CopyPixels(pixels, stride, 0);
            return BitmapSource.Create(
                targetWidth,
                targetHeight,
                source.DpiX,
                source.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
        }

        /// <summary>
        /// How many frames of a source this size are worth retaining, or 0 when it should not
        /// animate at all. Shared by both decode paths so the memory ceiling does not depend on
        /// which format is being read.
        /// </summary>
        private static int ResolveFrameBudget(int width, int height, int availableFrames)
        {
            if (width <= 0 || height <= 0 || availableFrames <= 0)
            {
                return 0;
            }

            var frameArea = (long)width * height;
            if (frameArea > MaxFramePixelArea)
            {
                Logger?.Info(
                    $"[Animation] Skipping animation for a {width}x{height} source: the frame area exceeds the {MaxFramePixelArea} pixel limit.");
                return 0;
            }

            // area x frames, not area alone: a modest frame multiplied by hundreds of frames is
            // what actually exhausts memory.
            var budgetFrames = (int)Math.Min(int.MaxValue, MaxRetainedPixels / frameArea);
            var frameCount = Math.Min(availableFrames, Math.Min(MaxAnimationFrames, budgetFrames));
            if (frameCount < MinAnimationFrames)
            {
                Logger?.Info(
                    $"[Animation] Skipping animation for a {width}x{height} source with {availableFrames} frames: " +
                    $"the retained-pixel budget allows only {frameCount} frame(s).");
                return 0;
            }

            if (frameCount < availableFrames)
            {
                Logger?.Info(
                    $"[Animation] Sampling a {width}x{height} animation at {frameCount} of {availableFrames} frames " +
                    $"(~{frameArea * frameCount * BytesPerCompositedPixel / (1024 * 1024)} MB retained).");
            }

            return frameCount;
        }

        private static List<BitmapSource> BuildCompositedGifFrames(
            GifBitmapDecoder decoder,
            bool applyGray,
            int decodePixel,
            IList<int> sourceDelays,
            out List<int> retainedDelays)
        {
            var result = new List<BitmapSource>();
            retainedDelays = new List<int>();
            if (decoder?.Frames == null || decoder.Frames.Count == 0)
            {
                return result;
            }

            var sourceWidth = decoder.Frames[0].PixelWidth;
            var sourceHeight = decoder.Frames[0].PixelHeight;
            ResolveAnimationDimensions(
                sourceWidth,
                sourceHeight,
                decodePixel,
                decoder.Frames.Count,
                out var width,
                out var height);
            var retainedFrameCount = ResolveFrameBudget(width, height, decoder.Frames.Count);
            BuildFrameRetentionPlan(
                decoder.Frames.Count,
                retainedFrameCount,
                sourceDelays,
                out var retainedIndices,
                out var plannedDelays);
            if (retainedIndices.Count == 0)
            {
                return result;
            }

            var stride = width * 4;
            var canvas = new byte[stride * height];

            int prevLeft = 0;
            int prevTop = 0;
            int prevWidth = 0;
            int prevHeight = 0;
            int prevDisposal = 0;
            byte[] previousCanvasBackup = null;

            var nextRetained = 0;
            for (var i = 0; i < decoder.Frames.Count; i++)
            {
                ApplyPreviousDisposal(canvas, stride, prevDisposal, prevLeft, prevTop, prevWidth, prevHeight, previousCanvasBackup);
                previousCanvasBackup = null;

                var frame = decoder.Frames[i];
                if (frame == null)
                {
                    continue;
                }

                GetGifFrameGeometry(
                    frame,
                    sourceWidth,
                    sourceHeight,
                    out var sourceLeft,
                    out var sourceTop,
                    out var sourceFrameWidth,
                    out var sourceFrameHeight);
                ScaleGifFrameGeometry(
                    sourceLeft,
                    sourceTop,
                    sourceFrameWidth,
                    sourceFrameHeight,
                    sourceWidth,
                    sourceHeight,
                    width,
                    height,
                    out var left,
                    out var top,
                    out var frameWidth,
                    out var frameHeight);
                var disposal = 0;
                if (frame.Metadata is BitmapMetadata frameMetadata)
                {
                    disposal = ReadMetadataInt(frameMetadata, "/grctlext/Disposal");
                }

                if (disposal == 3)
                {
                    previousCanvasBackup = (byte[])canvas.Clone();
                }

                var framePixels = CopyFramePixels(frame, frameWidth, frameHeight);
                AlphaBlendFrame(canvas, stride, width, height, framePixels, frameWidth, frameHeight, left, top);

                if (nextRetained < retainedIndices.Count && i == retainedIndices[nextRetained])
                {
                    var snapshot = BitmapSource.Create(
                        width,
                        height,
                        96,
                        96,
                        PixelFormats.Bgra32,
                        null,
                        canvas,
                        stride);
                    if (applyGray)
                    {
                        snapshot = ConvertToGrayscale(snapshot);
                    }

                    if (snapshot.CanFreeze)
                    {
                        snapshot.Freeze();
                    }

                    result.Add(snapshot);
                    retainedDelays.Add(plannedDelays[nextRetained]);
                    nextRetained++;
                }

                prevLeft = left;
                prevTop = top;
                prevWidth = frameWidth;
                prevHeight = frameHeight;
                prevDisposal = disposal;
            }

            return result;
        }

        private static void ScaleGifFrameGeometry(
            int sourceLeft,
            int sourceTop,
            int sourceFrameWidth,
            int sourceFrameHeight,
            int sourceCanvasWidth,
            int sourceCanvasHeight,
            int targetCanvasWidth,
            int targetCanvasHeight,
            out int left,
            out int top,
            out int width,
            out int height)
        {
            left = (int)((long)sourceLeft * targetCanvasWidth / sourceCanvasWidth);
            top = (int)((long)sourceTop * targetCanvasHeight / sourceCanvasHeight);

            var sourceRight = Math.Min(sourceCanvasWidth, sourceLeft + sourceFrameWidth);
            var sourceBottom = Math.Min(sourceCanvasHeight, sourceTop + sourceFrameHeight);
            var right = (int)Math.Ceiling(sourceRight * (targetCanvasWidth / (double)sourceCanvasWidth));
            var bottom = (int)Math.Ceiling(sourceBottom * (targetCanvasHeight / (double)sourceCanvasHeight));

            left = Math.Max(0, Math.Min(targetCanvasWidth - 1, left));
            top = Math.Max(0, Math.Min(targetCanvasHeight - 1, top));
            width = Math.Max(1, Math.Min(targetCanvasWidth - left, right - left));
            height = Math.Max(1, Math.Min(targetCanvasHeight - top, bottom - top));
        }

        private static void ApplyPreviousDisposal(byte[] canvas, int stride, int disposal, int left, int top, int width, int height, byte[] backup)
        {
            if (canvas == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (disposal == 2)
            {
                for (var y = 0; y < height; y++)
                {
                    var canvasRow = (top + y) * stride + (left * 4);
                    var length = width * 4;
                    if (canvasRow < 0 || canvasRow + length > canvas.Length)
                    {
                        continue;
                    }

                    Array.Clear(canvas, canvasRow, length);
                }
            }
            else if (disposal == 3 && backup != null && backup.Length == canvas.Length)
            {
                Buffer.BlockCopy(backup, 0, canvas, 0, canvas.Length);
            }
        }

        private static byte[] CopyFramePixels(BitmapSource frame, int width, int height)
        {
            var source = ResizeAndDetachFrame(frame, width, height);
            if (source.Format != PixelFormats.Bgra32)
            {
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        private static void AlphaBlendFrame(
            byte[] canvas,
            int canvasStride,
            int canvasWidth,
            int canvasHeight,
            byte[] framePixels,
            int frameWidth,
            int frameHeight,
            int left,
            int top)
        {
            if (framePixels == null)
            {
                return;
            }

            var frameStride = frameWidth * 4;
            for (var y = 0; y < frameHeight; y++)
            {
                var canvasY = top + y;
                if (canvasY < 0 || canvasY >= canvasHeight)
                {
                    continue;
                }

                for (var x = 0; x < frameWidth; x++)
                {
                    var canvasX = left + x;
                    if (canvasX < 0 || canvasX >= canvasWidth)
                    {
                        continue;
                    }

                    var srcIndex = y * frameStride + x * 4;
                    var dstIndex = canvasY * canvasStride + canvasX * 4;

                    var srcB = framePixels[srcIndex + 0];
                    var srcG = framePixels[srcIndex + 1];
                    var srcR = framePixels[srcIndex + 2];
                    var srcA = framePixels[srcIndex + 3];

                    if (srcA == 255)
                    {
                        canvas[dstIndex + 0] = srcB;
                        canvas[dstIndex + 1] = srcG;
                        canvas[dstIndex + 2] = srcR;
                        canvas[dstIndex + 3] = srcA;
                        continue;
                    }

                    if (srcA == 0)
                    {
                        continue;
                    }

                    var dstB = canvas[dstIndex + 0];
                    var dstG = canvas[dstIndex + 1];
                    var dstR = canvas[dstIndex + 2];
                    var dstA = canvas[dstIndex + 3];

                    var invA = 255 - srcA;
                    canvas[dstIndex + 0] = (byte)((srcB * srcA + dstB * invA) / 255);
                    canvas[dstIndex + 1] = (byte)((srcG * srcA + dstG * invA) / 255);
                    canvas[dstIndex + 2] = (byte)((srcR * srcA + dstR * invA) / 255);
                    canvas[dstIndex + 3] = (byte)Math.Min(255, srcA + (dstA * invA) / 255);
                }
            }
        }

        private static void GetGifFrameGeometry(BitmapFrame frame, int canvasWidth, int canvasHeight, out int left, out int top, out int width, out int height)
        {
            left = 0;
            top = 0;
            width = Math.Max(1, Math.Min(canvasWidth, frame.PixelWidth));
            height = Math.Max(1, Math.Min(canvasHeight, frame.PixelHeight));

            try
            {
                if (frame?.Metadata is BitmapMetadata metadata)
                {
                    left = Math.Max(0, ReadMetadataInt(metadata, "/imgdesc/Left"));
                    top = Math.Max(0, ReadMetadataInt(metadata, "/imgdesc/Top"));

                    var w = ReadMetadataInt(metadata, "/imgdesc/Width");
                    var h = ReadMetadataInt(metadata, "/imgdesc/Height");
                    if (w > 0)
                    {
                        width = Math.Min(canvasWidth, w);
                    }

                    if (h > 0)
                    {
                        height = Math.Min(canvasHeight, h);
                    }
                }
            }
            catch
            {
            }

            if (left + width > canvasWidth)
            {
                width = Math.Max(1, canvasWidth - left);
            }

            if (top + height > canvasHeight)
            {
                height = Math.Max(1, canvasHeight - top);
            }
        }

        private static int ReadMetadataInt(BitmapMetadata metadata, string query)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(query) || !metadata.ContainsQuery(query))
            {
                return 0;
            }

            var value = metadata.GetQuery(query);
            switch (value)
            {
                case byte b:
                    return b;
                case ushort s:
                    return s;
                case uint i:
                    return (int)i;
                case int j:
                    return j;
                default:
                    return 0;
            }
        }

        private static BitmapSource ConvertToGrayscale(BitmapSource source)
        {
            if (source == null)
            {
                return null;
            }

            try
            {
                var bgraSource = source;
                if (bgraSource.Format != PixelFormats.Bgra32)
                {
                    bgraSource = new FormatConvertedBitmap(bgraSource, PixelFormats.Bgra32, null, 0);
                }

                int width = bgraSource.PixelWidth;
                int height = bgraSource.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                bgraSource.CopyPixels(pixels, stride, 0);

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i + 0];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte gray = (byte)Math.Min(255, (int)(0.114 * b + 0.587 * g + 0.299 * r));
                    pixels[i + 0] = gray;
                    pixels[i + 1] = gray;
                    pixels[i + 2] = gray;
                }

                var grayImage = BitmapSource.Create(
                    width,
                    height,
                    bgraSource.DpiX,
                    bgraSource.DpiY,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);

                return grayImage;
            }
            catch
            {
                return source;
            }
        }
    }
}
