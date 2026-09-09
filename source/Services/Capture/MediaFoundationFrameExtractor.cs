using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Playnite.SDK;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Decodes one still frame out of a recording-buffer segment: the sample covering
    /// <c>offsetSeconds</c> into the file. Segments are a few seconds long and begin on a
    /// keyframe, so a sequential read-and-discard reaches any frame quickly — no seek, matching
    /// the repo's other SourceReader uses. Returns a top-down 32bppRgb bitmap, or null on any
    /// failure; callers fall back to a live screen capture, so a decode failure can never cost
    /// the screenshot. An offset past the last sample yields the last decodable frame rather
    /// than null, keeping a boundary-of-segment anchor on the nearest real footage.
    /// </summary>
    internal static class MediaFoundationFrameExtractor
    {
        private const long OneSecond100ns = 10_000_000L;

        [HandleProcessCorruptedStateExceptions, System.Security.SecurityCritical]
        public static Bitmap ExtractFrame(string segmentPath, double offsetSeconds, ILogger logger)
        {
            if (string.IsNullOrEmpty(segmentPath))
            {
                return null;
            }

            try
            {
                using (MediaFoundationRuntime.Acquire())
                using (var readerAttributes = new MediaAttributes(1))
                {
                    // Advanced video processing lets the reader chain the H.264 decoder plus a
                    // color converter so it can hand us RGB32 directly.
                    readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
                    using (var reader = new SourceReader(segmentPath, readerAttributes))
                    {
                        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                        reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);

                        using (var request = new MediaType())
                        {
                            request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                            request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                            reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                        }

                        int frameW, frameH, stride;
                        using (var decodedType = reader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream))
                        {
                            var size = decodedType.Get(MediaTypeAttributeKeys.FrameSize);
                            frameW = (int)(size >> 32);
                            frameH = (int)(size & 0xffffffff);
                            stride = ReadStride(decodedType, frameW);
                        }

                        if (frameW <= 0 || frameH <= 0)
                        {
                            return null;
                        }

                        var targetTicks = (long)(Math.Max(0, offsetSeconds) * OneSecond100ns);
                        Sample taken = null;
                        try
                        {
                            while (true)
                            {
                                var sample = reader.ReadSample(
                                    (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlags.None,
                                    out _, out var flags, out _);
                                if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                                {
                                    sample?.Dispose();
                                    break;
                                }

                                taken?.Dispose();
                                taken = sample;
                                if (sample.SampleTime + Math.Max(0, sample.SampleDuration) > targetTicks)
                                {
                                    break;
                                }
                            }

                            return taken == null ? null : ToBitmap(taken, frameW, frameH, stride);
                        }
                        finally
                        {
                            taken?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[Recording] Frame extraction failed for '{segmentPath}'.");
                return null;
            }
        }

        /// <summary>
        /// Copies a decoded RGB32 sample into a top-down GDI bitmap. Rgb (not Argb): the decoded
        /// buffer's alpha channel is undefined, and on an Rgb bitmap it reads as opaque, so saved
        /// PNGs are never transparent. A negative stride means bottom-up rows and is normalized
        /// here.
        /// </summary>
        private static Bitmap ToBitmap(Sample sample, int frameW, int frameH, int stride)
        {
            var absStride = Math.Abs(stride);
            var bottomUp = stride < 0;
            var pixels = new byte[absStride * frameH];
            using (var buffer = sample.ConvertToContiguousBuffer())
            {
                var ptr = buffer.Lock(out _, out var currentLength);
                try
                {
                    Marshal.Copy(ptr, pixels, 0, Math.Min(currentLength, pixels.Length));
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            Bitmap bitmap = null;
            try
            {
                bitmap = new Bitmap(frameW, frameH, PixelFormat.Format32bppRgb);
                var data = bitmap.LockBits(
                    new Rectangle(0, 0, frameW, frameH), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    var rowBytes = frameW * 4;
                    for (var row = 0; row < frameH; row++)
                    {
                        var sourceRow = bottomUp ? frameH - 1 - row : row;
                        Marshal.Copy(
                            pixels, sourceRow * absStride,
                            IntPtr.Add(data.Scan0, row * data.Stride), rowBytes);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return bitmap;
            }
            catch
            {
                bitmap?.Dispose();
                throw;
            }
        }

        private static int ReadStride(MediaType type, int frameW)
        {
            try
            {
                return type.Get(MediaTypeAttributeKeys.DefaultStride);
            }
            catch
            {
                return frameW * 4;
            }
        }
    }
}
