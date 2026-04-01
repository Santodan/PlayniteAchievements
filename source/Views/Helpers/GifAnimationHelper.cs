using System;
using System.Collections.Generic;
using System.IO;
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

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, (List<BitmapSource> Frames, List<int> Delays)> FrameCache =
            new Dictionary<string, (List<BitmapSource> Frames, List<int> Delays)>(StringComparer.OrdinalIgnoreCase);

        public static bool TryCreateAnimation(string uri, bool applyGray, out string normalizedSource, out ImageSource firstFrame, out ObjectAnimationUsingKeyFrames animation)
        {
            normalizedSource = NormalizeGifSourceUri(uri);
            firstFrame = null;
            animation = null;

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
                    var decoder = new GifBitmapDecoder(new Uri(normalizedSource, UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames == null || decoder.Frames.Count == 0)
                    {
                        return false;
                    }

                    var frames = BuildCompositedGifFrames(decoder, applyGray);
                    if (frames.Count == 0)
                    {
                        return false;
                    }

                    var delays = BuildFrameDelays(decoder, frames.Count);
                    if (delays.Count != frames.Count)
                    {
                        return false;
                    }

                    cached = (frames, delays);
                    SetCachedAnimation(cacheKey, cached.Value);
                }

                if (cached.Value.Frames.Count == 0)
                {
                    return false;
                }

                var keyFrames = new ObjectAnimationUsingKeyFrames
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };

                var current = TimeSpan.Zero;
                for (var i = 0; i < cached.Value.Frames.Count; i++)
                {
                    var frame = cached.Value.Frames[i];
                    if (frame == null)
                    {
                        continue;
                    }

                    var delayMilliseconds = cached.Value.Delays[i];

                    keyFrames.KeyFrames.Add(new DiscreteObjectKeyFrame(frame, KeyTime.FromTimeSpan(current)));
                    current = current.Add(TimeSpan.FromMilliseconds(delayMilliseconds));
                }

                if (keyFrames.KeyFrames.Count == 0)
                {
                    return false;
                }

                if (keyFrames.CanFreeze)
                {
                    keyFrames.Freeze();
                }

                firstFrame = cached.Value.Frames[0];
                animation = keyFrames;
                return true;
            }
            catch
            {
                return false;
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
                if (FrameCache.Count >= MaxCachedGifAnimations && !FrameCache.ContainsKey(cacheKey))
                {
                    FrameCache.Clear();
                }

                FrameCache[cacheKey] = cached;
            }
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

            if ((long)width * height > MaxGifPixelArea)
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

            var frameCount = Math.Min(decoder.Frames.Count, MaxCompositedGifFrames);
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
