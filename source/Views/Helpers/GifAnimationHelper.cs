using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.Helpers
{
    internal static class GifAnimationHelper
    {
        private const string GrayPrefix = "gray:";
        private const string CacheBustPrefix = "cachebust|";
        private const string PreviewHttpPrefix = "previewhttp:";
        private const int MaxCompositedGifFrames = 120;
        private const int MaxGifPixelArea = 2048 * 2048;
        private const int MaxCachedGifAnimations = 64;

        // Every composited frame is a full-canvas BGRA snapshot, so retained bytes scale with
        // area x frame count. MaxGifPixelArea only bounds the area, which a wide-but-short GIF
        // passes while still costing hundreds of megabytes across a few hundred frames. This
        // budget bounds the product: 16 Mpx is a 64 MB ceiling per animation, high enough that
        // normally sized notification art still reaches MaxCompositedGifFrames and only oversized
        // sources get trimmed.
        private const long MaxCompositedGifPixels = 16L * 1024 * 1024;

        // Aggregate ceiling across every cached animation (~128 MB of BGRA). The entry count alone
        // let a handful of large animations dominate the process.
        private const long MaxCachedGifBytes = 128L * 1024 * 1024;

        private const int BytesPerCompositedPixel = 4;

        // Below this an animation is not worth retaining; the caller falls back to the static
        // image path instead.
        private const int MinCompositedGifFrames = 2;

        private static readonly ILogger Logger = LogManager.GetLogger();

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, (List<BitmapSource> Frames, List<int> Delays)> FrameCache =
            new Dictionary<string, (List<BitmapSource> Frames, List<int> Delays)>(StringComparer.OrdinalIgnoreCase);

        // Wall-clock epoch used to phase-lock every animation instance: a recreated element
        // (e.g. the settings mockup rebuilding during a slider drag) resumes the GIF mid-cycle
        // instead of restarting it from the first frame.
        private static readonly System.Diagnostics.Stopwatch AnimationEpoch =
            System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Decodes and caches a GIF's composited frames so a later
        /// <see cref="TryCreateAnimationFromCache"/> call succeeds. Safe to run on a background
        /// thread: the cached frames are frozen bitmaps and no animation is built here, so no
        /// thread-affine <see cref="Freezable"/> crosses back to the UI thread.
        /// </summary>
        public static bool TryEnsureCachedFrames(string uri, bool applyGray)
        {
            return TryResolveFrames(uri, applyGray, decodeIfMissing: true, out _, out _);
        }

        /// <summary>
        /// Builds a ready-to-begin animation over already-cached frames; fails when the GIF has
        /// not been decoded yet (see <see cref="TryEnsureCachedFrames"/>). Never decodes, so it is
        /// cheap enough to run synchronously on the UI thread: the frames are shared with the
        /// cache and only the lightweight key frames are new. A recreated element can therefore
        /// attach its animation in the same layout pass and never flash a static frame.
        /// </summary>
        /// <remarks>
        /// <paramref name="phaseLock"/> is resolved here rather than by the caller because the
        /// animation must be frozen before WPF receives it, and cloning a frozen animation to
        /// stamp BeginTime afterwards deep-copies every frame bitmap
        /// (<c>CachedBitmap.CloneCore</c>), which is an out-of-memory risk on long GIFs.
        /// </remarks>
        public static bool TryCreateAnimationFromCache(
            string uri,
            bool applyGray,
            bool phaseLock,
            out string normalizedSource,
            out ImageSource firstFrame,
            out ObjectAnimationUsingKeyFrames animation)
        {
            firstFrame = null;
            animation = null;

            if (!TryResolveFrames(uri, applyGray, decodeIfMissing: false, out normalizedSource, out var cached))
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

                // The static frame shown until the animation takes over is the frame at the
                // current phase (not frame zero), so the handoff is seamless.
                var phaseMilliseconds = current.TotalMilliseconds > 0
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
        /// Resolves a source string to its cached composited frames, optionally decoding when the
        /// cache misses. Shared by the background decode pass and the UI-thread build pass.
        /// </summary>
        private static bool TryResolveFrames(
            string uri,
            bool applyGray,
            bool decodeIfMissing,
            out string normalizedSource,
            out (List<BitmapSource> Frames, List<int> Delays) frames)
        {
            normalizedSource = NormalizeGifSourceUri(uri);
            frames = default((List<BitmapSource> Frames, List<int> Delays));

            // Some preview paths encode grayscale intent directly in the source string (gray:...)
            // instead of the AsyncImage.Gray attached property.
            applyGray = applyGray || HasGrayPrefix(uri);

            if (string.IsNullOrWhiteSpace(normalizedSource) ||
                !normalizedSource.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathRooted(normalizedSource) ||
                !File.Exists(normalizedSource))
            {
                return false;
            }

            try
            {
                var cacheKey = GetFrameCacheKey(normalizedSource, applyGray);
                var cached = TryGetCachedAnimation(cacheKey);
                if (cached == null)
                {
                    if (!decodeIfMissing)
                    {
                        return false;
                    }

                    // IgnoreImageCache bypasses WPF's URI-keyed decode cache: the managed image
                    // slots reuse fixed file names, so an overwritten GIF at the same path must
                    // decode fresh bytes.
                    var decoder = new GifBitmapDecoder(
                        new Uri(normalizedSource, UriKind.Absolute),
                        BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache,
                        BitmapCacheOption.OnLoad);
                    if (decoder.Frames == null || decoder.Frames.Count == 0)
                    {
                        return false;
                    }

                    var composited = BuildCompositedGifFrames(decoder, applyGray);
                    if (composited.Count == 0)
                    {
                        return false;
                    }

                    var delays = BuildFrameDelays(decoder, composited.Count);
                    if (delays.Count != composited.Count)
                    {
                        return false;
                    }

                    cached = (composited, delays);
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
        /// Removes cached composited frames for every source whose key contains the given
        /// segment (case-insensitive). Called through the image service's eviction chokepoint
        /// so an overwritten or cleared GIF at a fixed managed path never re-serves the old
        /// animation.
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

        public static string NormalizeGifSourceUri(string uri)
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
                normalized.IndexOf(".gif", StringComparison.OrdinalIgnoreCase) >= 0)
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

            return 100;
        }

        private static List<int> BuildFrameDelays(GifBitmapDecoder decoder, int frameCount)
        {
            var delays = new List<int>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var delayMilliseconds = i < decoder.Frames.Count
                    ? GetGifFrameDelayMilliseconds(decoder.Frames[i])
                    : 100;
                if (delayMilliseconds < 20)
                {
                    delayMilliseconds = 100;
                }

                delays.Add(delayMilliseconds);
            }

            return delays;
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

        private static string GetFrameCacheKey(string normalizedSource, bool applyGray)
        {
            return applyGray
                ? "gray|" + normalizedSource
                : normalizedSource;
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

            if (FrameCache.Count <= MaxCachedGifAnimations && retainedBytes <= MaxCachedGifBytes)
            {
                return;
            }

            // Any eviction order is acceptable for a cache; evicting one entry at a time (rather
            // than clearing wholesale, as this previously did) keeps unrelated animations warm.
            foreach (var key in new List<string>(FrameCache.Keys))
            {
                if (FrameCache.Count <= MaxCachedGifAnimations && retainedBytes <= MaxCachedGifBytes)
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
        /// Bytes held by one cache entry. Every composited frame is a full-canvas BGRA snapshot of
        /// the same size, so the first frame's dimensions describe them all.
        /// </summary>
        private static long EstimateRetainedBytes((List<BitmapSource> Frames, List<int> Delays) cached)
        {
            var first = cached.Frames != null && cached.Frames.Count > 0 ? cached.Frames[0] : null;
            return first == null
                ? 0L
                : (long)first.PixelWidth * first.PixelHeight * BytesPerCompositedPixel * cached.Frames.Count;
        }

        private static List<BitmapSource> BuildCompositedGifFrames(GifBitmapDecoder decoder, bool applyGray)
        {
            var result = new List<BitmapSource>();
            if (decoder?.Frames == null || decoder.Frames.Count == 0)
            {
                return result;
            }

            var width = decoder.Frames[0].PixelWidth;
            var height = decoder.Frames[0].PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return result;
            }

            var frameArea = (long)width * height;
            if (frameArea > MaxGifPixelArea)
            {
                Logger?.Info(
                    $"[Gif] Skipping animation for a {width}x{height} source: the frame area exceeds the {MaxGifPixelArea} pixel limit.");
                return result;
            }

            // area x frames, not area alone: a modest frame multiplied by hundreds of frames is
            // what actually exhausts memory.
            var budgetFrames = (int)Math.Min(int.MaxValue, MaxCompositedGifPixels / frameArea);
            var frameCount = Math.Min(decoder.Frames.Count, Math.Min(MaxCompositedGifFrames, budgetFrames));
            if (frameCount < MinCompositedGifFrames)
            {
                Logger?.Info(
                    $"[Gif] Skipping animation for a {width}x{height} source with {decoder.Frames.Count} frames: " +
                    $"the composited-pixel budget allows only {frameCount} frame(s).");
                return result;
            }

            if (frameCount < decoder.Frames.Count)
            {
                Logger?.Info(
                    $"[Gif] Trimming a {width}x{height} animation to {frameCount} of {decoder.Frames.Count} frames " +
                    $"(~{frameArea * frameCount * BytesPerCompositedPixel / (1024 * 1024)} MB retained).");
            }

            var stride = width * 4;
            var canvas = new byte[stride * height];

            int prevLeft = 0;
            int prevTop = 0;
            int prevWidth = 0;
            int prevHeight = 0;
            int prevDisposal = 0;
            byte[] previousCanvasBackup = null;

            for (var i = 0; i < frameCount; i++)
            {
                ApplyPreviousDisposal(canvas, stride, prevDisposal, prevLeft, prevTop, prevWidth, prevHeight, previousCanvasBackup);
                previousCanvasBackup = null;

                var frame = decoder.Frames[i];
                if (frame == null)
                {
                    continue;
                }

                GetGifFrameGeometry(frame, width, height, out var left, out var top, out var frameWidth, out var frameHeight);
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

                prevLeft = left;
                prevTop = top;
                prevWidth = frameWidth;
                prevHeight = frameHeight;
                prevDisposal = disposal;
            }

            return result;
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
            var source = frame;
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
