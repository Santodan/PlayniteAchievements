using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
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
        /// wave's unlock sound never lands in the clip's audio tail). When
        /// <paramref name="chimePcm"/> is provided (48 kHz stereo 16-bit), the audio decodes to
        /// PCM, the chime mixes in starting at <paramref name="chimeStartSeconds"/>, and the
        /// result re-encodes to AAC; otherwise the audio stream passes through untouched.
        /// </summary>
        /// <param name="configuredFps">
        /// The frame rate the base clip was captured at, used only when its media type does not declare
        /// one. Output cadence comes from the samples' own timestamps either way; this sets the declared
        /// rate, the bitrate and the keyframe spacing.
        /// </param>
        [HandleProcessCorruptedStateExceptions, System.Security.SecurityCritical]
        public bool Export(
            string baseClipPath, ToastOverlayTrack track,
            double toastStartSeconds, double toastMaxSeconds, double trimLeadSeconds,
            double endSeconds, byte[] chimePcm, double chimeStartSeconds, string outputPath,
            int configuredFps, RecordingQuality quality)
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
                            fps = ReadFps(decodedType, configuredFps);
                            stride = ReadStride(decodedType, frameW);

                            // The sink must agree with our row-order interpretation. When the
                            // decoder's type omits MF_MT_DEFAULT_STRIDE, MF's convention for RGB
                            // is bottom-up — the encoder's converter would vertically flip the
                            // whole clip even though the video processor hands us top-down rows.
                            // Declaring the stride we actually assume removes the ambiguity.
                            decodedType.Set(MediaTypeAttributeKeys.DefaultStride, stride);

                            // The decoder/converter hands back full-range RGB regardless of the base
                            // clip's own range, so say so: the sink's RGB -> encoder converter then
                            // compresses to the limited range the output type declares.
                            MediaFoundationColor.ApplyFullRangeRgbInput(decodedType);

                            SinkWriter sink = null;
                            try
                            {
                                using (var sinkAttributes = new MediaAttributes(1))
                                {
                                    sinkAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
                                    sink = MediaFactory.CreateSinkWriterFromURL(outputPath, null, sinkAttributes);
                                }

                                var videoStream = AddVideoStream(sink, decodedType, frameW, frameH, fps, quality);
                                var audioStream = TryAddAudio(
                                    sink, baseClipPath, decodeToPcm: chimePcm != null, out var audioReader);
                                using (audioReader)
                                {
                                    sink.BeginWriting();
                                    var timer = Stopwatch.StartNew();
                                    var counts = WriteComposited(
                                        sink, videoStream, videoReader, audioStream, audioReader,
                                        track, toastStartSeconds, toastMaxSeconds, trimLeadSeconds,
                                        endSeconds, audioStream >= 0 ? chimePcm : null, chimeStartSeconds,
                                        frameW, frameH, stride);
                                    sink.Finalize();
                                    LogPassCost(timer, counts, frameW, frameH);
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

        private static int AddVideoStream(SinkWriter sink, MediaType decodedType, int frameW, int frameH, int fps, RecordingQuality quality)
        {
            using (var outputType = new MediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outputType.Set(
                    MediaTypeAttributeKeys.AvgBitrate,
                    MediaFoundationH264Encoder.ComputeBitrate(frameW, frameH, fps, quality));
                outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, fps);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(frameW, frameH));
                outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                MediaFoundationColor.ApplyBt709LimitedOutput(outputType);
                sink.AddStream(outputType, out var streamIndex);

                // The reader's own decoded type as input guarantees subtype/size/stride agreement;
                // the sink inserts the RGB32 -> encoder color converter.
                sink.SetInputMediaType(streamIndex, decodedType, null);
                return streamIndex;
            }
        }

        // MF_E_INVALIDSTREAMNUMBER: what selecting the first audio stream returns on a video-only clip.
        private const uint MfInvalidStreamNumber = 0xC00D36B3;

        /// <summary>
        /// Adds an audio stream when the base clip has one; returns -1 (and a null reader) for
        /// video-only clips. Passthrough mode stream-copies the native AAC; PCM mode (chime mix)
        /// decodes to 48 kHz stereo 16-bit and re-encodes to AAC so samples can be modified.
        /// </summary>
        private int TryAddAudio(SinkWriter sink, string baseClipPath, bool decodeToPcm, out SourceReader audioReader)
        {
            audioReader = null;
            try
            {
                var reader = new SourceReader(baseClipPath);
                try
                {
                    reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection((int)SourceReaderIndex.FirstAudioStream, true);
                    int streamIndex;
                    using (var nativeType = reader.GetNativeMediaType((int)SourceReaderIndex.FirstAudioStream, 0))
                    {
                        if (decodeToPcm)
                        {
                            using (var pcmRequest = MediaFoundationClipExporter.CreatePcmType())
                            {
                                reader.SetCurrentMediaType((int)SourceReaderIndex.FirstAudioStream, pcmRequest);
                            }

                            using (var aacType = MediaFoundationClipExporter.CreateAacType())
                            {
                                sink.AddStream(aacType, out streamIndex);
                            }

                            using (var pcmType = MediaFoundationClipExporter.CreatePcmType())
                            {
                                sink.SetInputMediaType(streamIndex, pcmType, null);
                            }
                        }
                        else
                        {
                            sink.AddStream(nativeType, out streamIndex);
                            sink.SetInputMediaType(streamIndex, nativeType, null);
                        }
                    }

                    audioReader = reader;
                    return streamIndex;
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }
            }
            catch (SharpDX.SharpDXException ex) when ((uint)ex.HResult == MfInvalidStreamNumber)
            {
                // Expected whenever the session recorded no audio (loopback capture disabled or
                // unavailable): the base clip is video-only, so there is no first audio stream to
                // select. Not a failure, and not worth a stack trace once a clip per unlock.
                _logger?.Debug("[Recording] Base clip has no audio stream; re-encoding video only.");
                return -1;
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
        private CompositeCounts WriteComposited(
            SinkWriter sink, int videoStream, SourceReader videoReader,
            int audioStream, SourceReader audioReader,
            ToastOverlayTrack track,
            double toastStartSeconds, double toastMaxSeconds, double trimLeadSeconds,
            double endSeconds, byte[] chimePcm, double chimeStartSeconds,
            int frameW, int frameH, int stride)
        {
            var trimLead = ToTicks(trimLeadSeconds);
            var toastStart = ToTicks(toastStartSeconds);
            var toastEnd = toastStart + ToTicks(Math.Min(Math.Max(0, toastMaxSeconds), track.DurationSeconds));
            // Output-timeline end cut (base timeline minus the lead): both streams stop here.
            var endLimit = ToTicks(endSeconds) - trimLead;
            // Output-timeline chime onset; may be negative (chime head before the clip start),
            // which the mix offsets handle by skipping the chime's head.
            var chimeStartOut = ToTicks(chimeStartSeconds) - trimLead;

            var absStride = Math.Abs(stride);
            var bottomUp = stride < 0;
            var frameBuffer = new byte[absStride * frameH];
            byte[] inflated = null;
            var inflatedIndex = -1;

            var pendingAudio = audioStream >= 0 ? ReadNextAudio(audioReader, trimLead) : null;

            // Video is held back one frame so each duration can span the gap to the next frame's
            // output time. The sink builds the track by accumulating durations and ignores the times
            // stamped here, so passing the decoder's duration through unchanged would re-flatten the
            // base clip's real-time spacing onto a rigid fps grid.
            Sample pendingVideo = null;
            var pendingVideoTime = 0L;
            var counts = default(CompositeCounts);

            try
            {
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
                        WriteAndDispose(sink, audioStream, MixChime(pendingAudio, chimePcm, chimeStartOut));
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
                            // Outside the toast interval (or no overlay): pass the frame through.
                            outSample = sample;
                            sample = null;
                            counts.PassedThrough++;
                        }
                        else
                        {
                            counts.Composited++;
                        }
                    }
                    finally
                    {
                        sample?.Dispose();
                    }

                    // Take ownership of the new frame before writing the previous one, so a throw
                    // leaves exactly one frame for the finally below.
                    var ready = pendingVideo;
                    var readyTime = pendingVideoTime;
                    pendingVideo = outSample;
                    pendingVideoTime = time - trimLead;
                    if (ready != null)
                    {
                        WriteVideoAndDispose(sink, videoStream, ready, readyTime, pendingVideoTime - readyTime);
                    }

                    WaitForEncoderQueue(sink, videoStream);
                }

                if (pendingVideo != null)
                {
                    // The last frame runs to the end cut rather than for one frame's worth, so the
                    // track ends where the clip does.
                    var last = pendingVideo;
                    pendingVideo = null;
                    WriteVideoAndDispose(sink, videoStream, last, pendingVideoTime, endLimit - pendingVideoTime);
                }
            }
            finally
            {
                pendingVideo?.Dispose();
            }

            // Trailing audio after the last video sample, up to the end cut.
            while (pendingAudio != null && pendingAudio.SampleTime <= endLimit)
            {
                WriteAndDispose(sink, audioStream, MixChime(pendingAudio, chimePcm, chimeStartOut));
                pendingAudio = ReadNextAudio(audioReader, trimLead);
            }

            pendingAudio?.Dispose();
            return counts;
        }

        /// <summary>What one pass wrote, for the cost line below.</summary>
        private struct CompositeCounts
        {
            public int Composited;
            public int PassedThrough;
        }

        /// <summary>
        /// Reports what the pass cost. Every frame of the clip is decoded and re-encoded here, not just
        /// the ones the toast covers, so this is the bulk of the time between an unlock and its clip
        /// appearing — worth being able to see per clip rather than inferring it.
        /// </summary>
        private void LogPassCost(Stopwatch timer, CompositeCounts counts, int frameW, int frameH)
        {
            var frames = counts.Composited + counts.PassedThrough;
            var seconds = Math.Max(0.001, timer.Elapsed.TotalSeconds);
            _logger?.Debug(
                $"[Recording] Toast composite: {frames} frames ({counts.Composited} with the card) at " +
                $"{frameW}x{frameH} in {timer.ElapsedMilliseconds}ms ({frames / seconds:0.0} fps).");
        }

        /// <summary>
        /// Stamps a frame onto the output timeline and writes it. Durations are floored at one tick.
        /// </summary>
        private static void WriteVideoAndDispose(
            SinkWriter sink, int streamIndex, Sample sample, long time, long duration)
        {
            try
            {
                sample.SampleTime = time;
                sample.SampleDuration = Math.Max(1, duration);
                sink.WriteSample(streamIndex, sample);
            }
            finally
            {
                sample.Dispose();
            }
        }

        /// <summary>
        /// Mixes the chime PCM into an audio sample when their spans overlap, returning a fresh
        /// sample (the reader's buffer may be a detached copy, so in-place mutation is not
        /// reliable). Non-overlapping samples (or passthrough mode, chime null) return unchanged.
        /// Only valid in PCM mode — 48 kHz stereo 16-bit on both sides.
        /// </summary>
        private static Sample MixChime(Sample sample, byte[] chimePcm, long chimeStartOut)
        {
            if (chimePcm == null || chimePcm.Length == 0)
            {
                return sample;
            }

            var time = sample.SampleTime;
            var duration = Math.Max(0, sample.SampleDuration);
            var chimeEnd = chimeStartOut + (long)(chimePcm.Length * 10_000_000.0 / PcmAudio.BytesPerSecond);
            if (time + duration <= chimeStartOut || time >= chimeEnd)
            {
                return sample;
            }

            byte[] bytes;
            using (var buffer = sample.ConvertToContiguousBuffer())
            {
                var ptr = buffer.Lock(out _, out var length);
                try
                {
                    bytes = new byte[length];
                    Marshal.Copy(ptr, bytes, 0, length);
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            var destOffset = PcmAudio.TicksToAlignedBytes(Math.Max(0, chimeStartOut - time));
            var sourceOffset = PcmAudio.TicksToAlignedBytes(Math.Max(0, time - chimeStartOut));
            PcmAudio.MixInto(bytes, destOffset, chimePcm, sourceOffset, bytes.Length);

            var outBuffer = MediaFactory.CreateMemoryBuffer(bytes.Length);
            try
            {
                var outPtr = outBuffer.Lock(out _, out _);
                try
                {
                    Marshal.Copy(bytes, 0, outPtr, bytes.Length);
                }
                finally
                {
                    outBuffer.Unlock();
                }

                outBuffer.CurrentLength = bytes.Length;

                var outSample = MediaFactory.CreateSample();
                outSample.AddBuffer(outBuffer);
                outSample.SampleTime = time;
                outSample.SampleDuration = duration;
                sample.Dispose();
                return outSample;
            }
            finally
            {
                outBuffer.Dispose();
            }
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

                // Time and duration are stamped by the caller, which knows where this frame lands on
                // the output timeline and how far it is to the next one.
                var outSample = MediaFactory.CreateSample();
                outSample.AddBuffer(outBuffer);
                return outSample;
            }
            finally
            {
                outBuffer.Dispose();
            }
        }

        /// <summary>
        /// The track frame for a sample, reconstructed lazily and cached (tracks are sampled per
        /// recording frame, but consecutive samples share a frame whenever the card's pixels did not
        /// change, and frames are stored as XOR deltas against their predecessor). Samples are walked
        /// forward, so the usual cost is one inflate-and-XOR onto the frame already in hand.
        ///
        /// A broken chain — a frame whose compression failed — falls back to the previously
        /// reconstructed frame so the card holds instead of flickering out, and recovers at the track's
        /// next keyframe.
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

            // Keep the last good reconstruction so a failure can fall back to it: TryReconstructFrame
            // clears the index it is handed when it gives up partway.
            var held = inflated;
            var heldIndex = inflatedIndex;
            if (track.TryReconstructFrame(frameIndex, ref inflated, ref inflatedIndex))
            {
                frame = track.Frames[frameIndex];
                return inflated != null;
            }

            inflated = held;
            inflatedIndex = heldIndex;
            if (inflatedIndex < 0 || inflated == null)
            {
                return false;
            }

            frame = track.Frames[inflatedIndex];
            return frame != null;
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

        // Falls back to the rate the clip was captured at rather than a fixed guess: a 30 fps capture
        // declared as 60 misprices both the bitrate and the keyframe spacing.
        private static int ReadFps(MediaType type, int configuredFps)
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

            return Math.Max(1, configuredFps);
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
