using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Playnite.SDK;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Re-encodes an already-exported unlock clip with one achievement's toast overlay track
    /// composited in: the base clip's video decodes to RGB32 through a SourceReader (advanced
    /// video processing inserts the H.264 decoder and color converter), frames inside the toast
    /// interval get the track's card blended in on the CPU at its recorded client-relative
    /// position (translated to the synthetic single-toast corner), and everything re-encodes
    /// through a SinkWriter H.264 stream (hardware MFT where present). Audio passes through as
    /// native AAC, stream-copied. Samples before <c>trimLeadSeconds</c> (the base clip's keyframe
    /// lead) are dropped and the rest re-stamped, so the output starts exactly at the clip
    /// window. Any failure returns false — the caller keeps the toastless base clip, so a
    /// re-encode failure can never lose a clip.
    /// </summary>
    internal sealed class MediaFoundationOverlayReencoder
    {
        private const long OneSecond100ns = 10_000_000L;

        // Backpressure cap on the sink writer's input queue. Decoding runs much faster than the
        // H.264 encoder drains, and uncompressed RGB32 frames are huge (~14 MB at 1440p) — an
        // unthrottled write loop balloons the queue by gigabytes of native memory and the whole
        // export dies with E_OUTOFMEMORY. ~96 MB keeps a handful of frames in flight, plenty to
        // keep the encoder busy.
        private const int MaxQueuedVideoBytes = 96 * 1024 * 1024;
        private const int QueuePollSleepMs = 10;
        private const int QueuePollMaxIterations = 1000; // give up pacing after ~10s and proceed

        private readonly ILogger _logger;
        private bool _statisticsUnavailable;

        public MediaFoundationOverlayReencoder(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Writes the composited clip to <paramref name="outputPath"/>. Times are in the base
        /// clip's own timeline: the toast blits over
        /// [<paramref name="toastStartSeconds"/>, +<paramref name="toastMaxSeconds"/>], bounded
        /// by the track's own duration and the video's end, and the output ends at
        /// <paramref name="endSeconds"/> (typically shortly after the recorded fade, so the next
        /// wave's unlock sound never lands in the clip's audio tail).
        /// </summary>
        [HandleProcessCorruptedStateExceptions, System.Security.SecurityCritical]
        public bool Export(
            string baseClipPath, ToastOverlayTrack track,
            double toastStartSeconds, double toastMaxSeconds, double trimLeadSeconds,
            double endSeconds, string outputPath)
        {
            if (string.IsNullOrEmpty(baseClipPath) || track == null ||
                track.Samples.Count == 0 || string.IsNullOrEmpty(outputPath))
            {
                return false;
            }

            MediaManager.Startup();
            try
            {
                using (var readerAttributes = new MediaAttributes(1))
                {
                    // Advanced video processing lets the reader chain the H.264 decoder plus a
                    // color converter so it can hand us RGB32 directly.
                    readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
                    using (var videoReader = new SourceReader(baseClipPath, readerAttributes))
                    {
                        videoReader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                        videoReader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);

                        int frameW, frameH, fps;
                        using (var rgbRequest = new MediaType())
                        {
                            rgbRequest.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                            rgbRequest.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                            videoReader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, rgbRequest);
                        }

                        int stride;
                        MediaType decodedType = videoReader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream);
                        using (decodedType)
                        {
                            var size = decodedType.Get(MediaTypeAttributeKeys.FrameSize);
                            frameW = (int)(size >> 32);
                            frameH = (int)(size & 0xffffffff);
                            fps = ReadFps(decodedType);
                            stride = ReadStride(decodedType, frameW);

                            // The sink must agree with our row-order interpretation. When the
                            // decoder's type omits MF_MT_DEFAULT_STRIDE, MF's convention for RGB
                            // is bottom-up — the encoder's converter would vertically flip the
                            // whole clip even though the video processor hands us top-down rows.
                            // Declaring the stride we actually assume removes the ambiguity.
                            decodedType.Set(MediaTypeAttributeKeys.DefaultStride, stride);

                            SinkWriter sink = null;
                            try
                            {
                                using (var sinkAttributes = new MediaAttributes(1))
                                {
                                    sinkAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
                                    sink = MediaFactory.CreateSinkWriterFromURL(outputPath, null, sinkAttributes);
                                }

                                var videoStream = AddVideoStream(sink, decodedType, frameW, frameH, fps);
                                var audioStream = TryAddAudioPassthrough(sink, baseClipPath, out var audioReader);
                                using (audioReader)
                                {
                                    sink.BeginWriting();
                                    WriteComposited(
                                        sink, videoStream, videoReader, audioStream, audioReader,
                                        track, toastStartSeconds, toastMaxSeconds, trimLeadSeconds,
                                        endSeconds, frameW, frameH, stride);
                                    sink.Finalize();
                                }

                                return true;
                            }
                            finally
                            {
                                sink?.Dispose();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Recording] Toast overlay re-encode failed; the toastless clip is kept.");
                return false;
            }
            finally
            {
                try
                {
                    MediaManager.Shutdown();
                }
                catch
                {
                    // Startup/Shutdown are refcounted per process; ignore an unbalanced shutdown.
                }
            }
        }

        private static int AddVideoStream(SinkWriter sink, MediaType decodedType, int frameW, int frameH, int fps)
        {
            using (var outputType = new MediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outputType.Set(
                    MediaTypeAttributeKeys.AvgBitrate,
                    MediaFoundationH264Encoder.ComputeBitrate(frameW, frameH, fps));
                outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, fps);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(frameW, frameH));
                outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                sink.AddStream(outputType, out var streamIndex);

                // The reader's own decoded type as input guarantees subtype/size/stride agreement;
                // the sink inserts the RGB32 -> encoder color converter.
                sink.SetInputMediaType(streamIndex, decodedType, null);
                return streamIndex;
            }
        }

        /// <summary>
        /// Adds an audio stream configured for native AAC passthrough (stream copy) when the base
        /// clip has one; returns -1 (and a null reader) for video-only clips.
        /// </summary>
        private int TryAddAudioPassthrough(SinkWriter sink, string baseClipPath, out SourceReader audioReader)
        {
            audioReader = null;
            try
            {
                var reader = new SourceReader(baseClipPath);
                try
                {
                    reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection((int)SourceReaderIndex.FirstAudioStream, true);
                    using (var nativeType = reader.GetNativeMediaType((int)SourceReaderIndex.FirstAudioStream, 0))
                    {
                        sink.AddStream(nativeType, out var streamIndex);
                        sink.SetInputMediaType(streamIndex, nativeType, null);
                        audioReader = reader;
                        return streamIndex;
                    }
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Base clip has no usable audio stream; re-encoding video only.");
                return -1;
            }
        }

        /// <summary>
        /// Decodes, composites, re-stamps, and writes both streams interleaved by output time
        /// (a multi-stream SinkWriter blocks a stream that runs too far ahead of the other).
        /// </summary>
        private void WriteComposited(
            SinkWriter sink, int videoStream, SourceReader videoReader,
            int audioStream, SourceReader audioReader,
            ToastOverlayTrack track,
            double toastStartSeconds, double toastMaxSeconds, double trimLeadSeconds,
            double endSeconds, int frameW, int frameH, int stride)
        {
            var trimLead = ToTicks(trimLeadSeconds);
            var toastStart = ToTicks(toastStartSeconds);
            var toastEnd = toastStart + ToTicks(Math.Min(Math.Max(0, toastMaxSeconds), track.DurationSeconds));
            // Output-timeline end cut (base timeline minus the lead): both streams stop here.
            var endLimit = ToTicks(endSeconds) - trimLead;

            var absStride = Math.Abs(stride);
            var bottomUp = stride < 0;
            var frameBuffer = new byte[absStride * frameH];
            byte[] inflated = null;
            var inflatedIndex = -1;

            var pendingAudio = audioStream >= 0 ? ReadNextAudio(audioReader, trimLead) : null;
            while (true)
            {
                var sample = videoReader.ReadSample(
                    (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlags.None,
                    out _, out var flags, out _);
                if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                {
                    sample?.Dispose();
                    break;
                }

                var time = sample.SampleTime;
                if (time < trimLead)
                {
                    sample.Dispose();
                    continue;
                }

                if (time - trimLead > endLimit)
                {
                    sample.Dispose();
                    break;
                }

                // Drain audio up to this video timestamp so both streams advance together.
                while (pendingAudio != null && pendingAudio.SampleTime <= time - trimLead)
                {
                    WriteAndDispose(sink, audioStream, pendingAudio);
                    pendingAudio = ReadNextAudio(audioReader, trimLead);
                }

                Sample outSample = null;
                try
                {
                    if (time >= toastStart && time <= toastEnd)
                    {
                        var sampleIndex = track.FindSampleIndexAtOrBefore((time - toastStart) / (double)OneSecond100ns);
                        if (sampleIndex >= 0 &&
                            TryGetOverlay(track, sampleIndex, ref inflated, ref inflatedIndex, out var overlayFrame))
                        {
                            var trackSample = track.Samples[sampleIndex];
                            var destRect = OverlayBlitMath.ScaleRect(
                                trackSample.RelX + track.OffsetX, trackSample.RelY + track.OffsetY,
                                overlayFrame.Width, overlayFrame.Height,
                                trackSample.ClientW, trackSample.ClientH, frameW, frameH);
                            outSample = ComposeSample(
                                sample, frameBuffer, absStride, bottomUp, frameW, frameH,
                                inflated, overlayFrame.Width, overlayFrame.Height, destRect);
                        }
                    }

                    if (outSample == null)
                    {
                        // Outside the toast interval (or no overlay): re-stamp and pass through.
                        sample.SampleTime = time - trimLead;
                        outSample = sample;
                        sample = null;
                    }
                    else
                    {
                        outSample.SampleTime = time - trimLead;
                    }

                    sink.WriteSample(videoStream, outSample);
                }
                finally
                {
                    sample?.Dispose();
                    outSample?.Dispose();
                }

                WaitForEncoderQueue(sink, videoStream);
            }

            // Trailing audio after the last video sample, up to the end cut.
            while (pendingAudio != null && pendingAudio.SampleTime <= endLimit)
            {
                WriteAndDispose(sink, audioStream, pendingAudio);
                pendingAudio = ReadNextAudio(audioReader, trimLead);
            }

            pendingAudio?.Dispose();
        }

        /// <summary>
        /// Blocks until the sink writer's queued input drops under the byte cap, pacing the
        /// decode loop to the encoder. Statistics failures disable pacing for the run (the export
        /// then just risks the old memory profile rather than failing outright).
        /// </summary>
        private void WaitForEncoderQueue(SinkWriter sink, int videoStream)
        {
            if (_statisticsUnavailable)
            {
                return;
            }

            try
            {
                for (var i = 0; i < QueuePollMaxIterations; i++)
                {
                    sink.GetStatistics(videoStream, out var stats);
                    if (stats.DwByteCountQueued < MaxQueuedVideoBytes)
                    {
                        return;
                    }

                    Thread.Sleep(QueuePollSleepMs);
                }
            }
            catch (Exception ex)
            {
                _statisticsUnavailable = true;
                _logger?.Debug(ex, "[Recording] Sink writer statistics unavailable; re-encode runs unpaced.");
            }
        }

        /// <summary>
        /// Blends the overlay into a copy of the decoded frame and wraps it in a fresh sample —
        /// ConvertToContiguousBuffer may hand back a detached copy, so mutating in place is not
        /// reliable. Timestamps are copied by the caller.
        /// </summary>
        private static Sample ComposeSample(
            Sample source, byte[] frameBuffer, int absStride, bool bottomUp, int frameW, int frameH,
            byte[] overlay, int overlayW, int overlayH, System.Drawing.Rectangle destRect)
        {
            using (var buffer = source.ConvertToContiguousBuffer())
            {
                var ptr = buffer.Lock(out _, out var currentLength);
                try
                {
                    var length = Math.Min(currentLength, frameBuffer.Length);
                    Marshal.Copy(ptr, frameBuffer, 0, length);
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            // A negative stride means bottom-up rows: normalize to top-down, blit, restore.
            if (bottomUp)
            {
                FlipRows(frameBuffer, absStride, frameH);
            }

            OverlayBlitMath.BlendOnto(frameBuffer, frameW, frameH, absStride, overlay, overlayW, overlayH, destRect);

            if (bottomUp)
            {
                FlipRows(frameBuffer, absStride, frameH);
            }

            var outBuffer = MediaFactory.CreateMemoryBuffer(frameBuffer.Length);
            try
            {
                var outPtr = outBuffer.Lock(out _, out _);
                try
                {
                    Marshal.Copy(frameBuffer, 0, outPtr, frameBuffer.Length);
                }
                finally
                {
                    outBuffer.Unlock();
                }

                outBuffer.CurrentLength = frameBuffer.Length;

                var outSample = MediaFactory.CreateSample();
                outSample.AddBuffer(outBuffer);
                outSample.SampleDuration = Math.Max(0, source.SampleDuration);
                return outSample;
            }
            finally
            {
                outBuffer.Dispose();
            }
        }

        /// <summary>
        /// The track frame for a sample, inflating lazily and caching the last inflation (60 fps
        /// video against a ~30 fps track reuses every other frame). A frame whose compression
        /// failed (null payload) falls back to the cached previous frame so the card holds
        /// instead of flickering out.
        /// </summary>
        private static bool TryGetOverlay(
            ToastOverlayTrack track, int sampleIndex,
            ref byte[] inflated, ref int inflatedIndex, out ToastOverlayTrack.Frame frame)
        {
            frame = null;
            var frameIndex = track.Samples[sampleIndex].FrameIndex;
            if (frameIndex < 0 || frameIndex >= track.Frames.Count)
            {
                frameIndex = inflatedIndex;
            }

            if (frameIndex < 0)
            {
                return false;
            }

            var candidate = track.Frames[frameIndex];
            if (candidate.Deflated == null)
            {
                if (inflatedIndex < 0)
                {
                    return false;
                }

                frame = track.Frames[inflatedIndex];
                return inflated != null;
            }

            if (frameIndex != inflatedIndex)
            {
                inflated = candidate.ToRaw();
                inflatedIndex = frameIndex;
            }

            frame = candidate;
            return inflated != null;
        }

        private static Sample ReadNextAudio(SourceReader audioReader, long trimLead)
        {
            while (true)
            {
                var sample = audioReader.ReadSample(
                    (int)SourceReaderIndex.FirstAudioStream, SourceReaderControlFlags.None,
                    out _, out var flags, out _);
                if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                {
                    sample?.Dispose();
                    return null;
                }

                if (sample.SampleTime < trimLead)
                {
                    sample.Dispose();
                    continue;
                }

                sample.SampleTime -= trimLead;
                return sample;
            }
        }

        private static void WriteAndDispose(SinkWriter sink, int streamIndex, Sample sample)
        {
            try
            {
                sink.WriteSample(streamIndex, sample);
            }
            finally
            {
                sample.Dispose();
            }
        }

        private static void FlipRows(byte[] buffer, int stride, int height)
        {
            var temp = new byte[stride];
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(buffer, top * stride, temp, 0, stride);
                Buffer.BlockCopy(buffer, bottom * stride, buffer, top * stride, stride);
                Buffer.BlockCopy(temp, 0, buffer, bottom * stride, stride);
            }
        }

        private static int ReadFps(MediaType type)
        {
            try
            {
                var packed = type.Get(MediaTypeAttributeKeys.FrameRate);
                var numerator = (int)(packed >> 32);
                var denominator = (int)(packed & 0xffffffff);
                if (numerator > 0 && denominator > 0)
                {
                    return Math.Max(1, (int)Math.Round(numerator / (double)denominator));
                }
            }
            catch
            {
                // fall through to the default
            }

            return 60;
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

        private static long Pack(int high, int low)
        {
            return ((long)high << 32) | (uint)low;
        }

        private static long ToTicks(double seconds)
        {
            return (long)(Math.Max(0, seconds) * OneSecond100ns);
        }
    }
}
