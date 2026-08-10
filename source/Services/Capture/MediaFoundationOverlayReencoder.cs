using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using SharpDX.MediaFoundation;
using D3D11 = SharpDX.Direct3D11;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Re-encodes an already-exported unlock clip with one achievement's toast overlay track
    /// composited in: the base clip's video decodes to BGRA through a SourceReader (advanced
    /// video processing inserts the H.264 decoder and color converter), frames inside the toast
    /// interval get the track's card blended in at its recorded client-relative position
    /// (translated to the synthetic single-toast corner), and everything re-encodes through a
    /// SinkWriter H.264 stream (hardware MFT where present). Audio passes through as native AAC,
    /// stream-copied. Samples before <c>trimLeadSeconds</c> (the base clip's keyframe lead) are
    /// dropped and the rest re-stamped, so the output starts exactly at the clip window. Any
    /// failure returns false — the caller keeps the toastless base clip, so a re-encode failure
    /// can never lose a clip.
    /// <para>
    /// The reader and sink share one D3D11 device, so decoded frames arrive as textures, the card is
    /// blended by <see cref="GpuOverlayCompositor"/>, and the encoder reads them without a trip
    /// through system memory. Where that device or those textures are unavailable the card is
    /// composited by <see cref="CpuOverlayCompositor"/> instead — measurably slower, but the pass
    /// still produces the same clip.
    /// </para>
    /// </summary>
    internal sealed class MediaFoundationOverlayReencoder
    {
        private const long OneSecond100ns = 10_000_000L;

        // Backpressure cap on the sink writer's input queue. Decoding runs much faster than the
        // H.264 encoder drains, and uncompressed RGB32 frames are huge (~14 MB at 1440p) — an
        // unthrottled write loop balloons the queue by gigabytes of native memory and the whole
        // export dies with E_OUTOFMEMORY. ~96 MB keeps a handful of frames in flight, plenty to
        // keep the encoder busy.
        /// <summary>
        /// Whether to composite on the GPU. Off: it is roughly twenty times faster per frame, but it
        /// produces frames carrying the wrong picture. Verified by recording a window whose every frame
        /// shows its own sequence number and reading those numbers back out of the finished clip: the
        /// composited frames arrive with content from about 2.75 s earlier, so a clip flickers between
        /// current frames without the card and stale ones with it. Copying out of the reader's surface and
        /// waiting for the GPU each frame reduced it (twelve misordered frames to four) but did not
        /// remove it, so the cause is still not understood. The CPU compositor has never shown this.
        /// Flip this back on only when the harness reports zero order regressions on the composited clip.
        /// </summary>
        private const bool UseGpuCompositor = false;

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
        /// one. This sets the declared rate, the bitrate and the keyframe spacing — and the declared rate
        /// is what the output cadence actually follows, because the encoder rewrites per-sample durations
        /// onto the grid it implies. Capture paces itself to the same rate so that grid is truthful.
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
            D3D11.Device device = null;
            DXGIDeviceManager deviceManager = null;
            try
            {
                // One device shared by the decoder and the encoder, so frames can stay in video memory
                // for the whole pass. Optional: without it the reader decodes into system memory and the
                // CPU compositor handles the card, which is what this pass always used to do.
                TryCreateDeviceManager(out device, out deviceManager);

                using (var readerAttributes = new MediaAttributes(2))
                {
                    // Advanced video processing lets the reader chain the H.264 decoder plus a
                    // color converter so it can hand us BGRA directly.
                    readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
                    if (deviceManager != null)
                    {
                        readerAttributes.Set(SourceReaderAttributeKeys.D3DManager, deviceManager);
                    }

                    using (var videoReader = new SourceReader(baseClipPath, readerAttributes))
                    {
                        videoReader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                        videoReader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);

                        int frameW, frameH, fps;
                        using (var request = new MediaType())
                        {
                            // BGRA either way, but ARGB32 is the subtype MF pairs with D3D11 BGRA
                            // surfaces, so ask for that when the reader has a device and fall back to
                            // RGB32 if the video processor refuses it.
                            request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                            request.Set(
                                MediaTypeAttributeKeys.Subtype,
                                deviceManager != null ? VideoFormatGuids.Argb32 : VideoFormatGuids.Rgb32);
                            try
                            {
                                videoReader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                            }
                            catch (Exception ex) when (deviceManager != null)
                            {
                                _logger?.Debug(ex, "[Recording] ARGB32 output refused; asking for RGB32.");
                                request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                                videoReader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                            }
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
                                using (var sinkAttributes = new MediaAttributes(3))
                                {
                                    sinkAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
                                    if (deviceManager != null)
                                    {
                                        sinkAttributes.Set(SinkWriterAttributeKeys.D3DManager, deviceManager);
                                        // Let the sink fall back to software rather than failing outright
                                        // if the hardware encoder will not take our device. This key is
                                        // typed bool, unlike the int-typed hardware-transforms one above.
                                        sinkAttributes.Set(SinkWriterAttributeKeys.ReadwriteD3DOptional, true);
                                    }

                                    sink = MediaFactory.CreateSinkWriterFromURL(outputPath, null, sinkAttributes);
                                }

                                var videoStream = AddVideoStream(sink, decodedType, frameW, frameH, fps, quality);
                                var audioStream = TryAddAudio(
                                    sink, baseClipPath, decodeToPcm: chimePcm != null, out var audioReader);
                                using (var gpuCompositor = device != null && UseGpuCompositor
                                    ? new GpuOverlayCompositor(device, frameW, frameH)
                                    : null)
                                using (var cpuCompositor = new CpuOverlayCompositor(frameW, frameH, stride))
                                using (audioReader)
                                {
                                    sink.BeginWriting();
                                    var timer = Stopwatch.StartNew();
                                    var counts = WriteComposited(
                                        sink, videoStream, videoReader, audioStream, audioReader,
                                        track, toastStartSeconds, toastMaxSeconds, trimLeadSeconds,
                                        endSeconds, audioStream >= 0 ? chimePcm : null, chimeStartSeconds,
                                        frameW, frameH, OneSecond100ns / Math.Max(1, fps),
                                        gpuCompositor, cpuCompositor);
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
                deviceManager?.Dispose();
                device?.ImmediateContext?.Dispose();
                device?.Dispose();
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

        /// <summary>
        /// Creates the D3D11 device the decoder and encoder share, or leaves both null when the machine
        /// cannot give us one — a software-only or otherwise restricted setup, where the pass still runs
        /// through system memory as it always did.
        /// </summary>
        private void TryCreateDeviceManager(out D3D11.Device device, out DXGIDeviceManager manager)
        {
            device = null;
            manager = null;
            try
            {
                device = new D3D11.Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);

                // Media Foundation serializes its own use of the device across threads only if told the
                // device is multithread-safe; the decoder and our blitter both touch it.
                using (var multithread = device.QueryInterface<D3D11.Multithread>())
                {
                    multithread.SetMultithreadProtected(true);
                }

                manager = new DXGIDeviceManager();
                manager.ResetDevice(device);
            }
            catch (Exception ex)
            {
                _logger?.Debug(
                    ex, "[Recording] No shared D3D11 device for the toast composite; it runs on the CPU.");
                manager?.Dispose();
                manager = null;
                device?.Dispose();
                device = null;
            }
        }

        private static int AddVideoStream(SinkWriter sink, MediaType decodedType, int frameW, int frameH, int fps, RecordingQuality quality)
        {
            using (var outputType = new MediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                // Above the capture bitrate on purpose — this is a second generation of the same
                // footage; see BitrateMath.ComputeReencode.
                outputType.Set(
                    MediaTypeAttributeKeys.AvgBitrate,
                    BitrateMath.ComputeReencode(frameW, frameH, fps, quality));
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
            int frameW, int frameH, long nominalDuration,
            IOverlayCompositor gpuCompositor, IOverlayCompositor cpuCompositor)
        {
            var trimLead = ToTicks(trimLeadSeconds);
            var toastStart = ToTicks(toastStartSeconds);
            var toastEnd = toastStart + ToTicks(Math.Min(Math.Max(0, toastMaxSeconds), track.DurationSeconds));
            // Output-timeline end cut (base timeline minus the lead): both streams stop here.
            var endLimit = ToTicks(endSeconds) - trimLead;
            // Output-timeline chime onset; may be negative (chime head before the clip start),
            // which the mix offsets handle by skipping the chime's head.
            var chimeStartOut = ToTicks(chimeStartSeconds) - trimLead;

            byte[] inflated = null;
            var inflatedIndex = -1;

            var pendingAudio = audioStream >= 0 ? ReadNextAudio(audioReader, trimLead) : null;

            var counts = default(CompositeCounts);

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
                // Read before the sample is handed on or nulled below.
                var sourceDuration = sample.SampleDuration;
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
                            outSample = gpuCompositor?.Compose(
                                sample, inflated, overlayFrame.Width, overlayFrame.Height, destRect);
                            if (outSample != null)
                            {
                                counts.GpuComposited++;
                            }
                            else
                            {
                                // The frame came back in system memory: compose it there rather than
                                // dropping the card off this frame.
                                outSample = cpuCompositor.Compose(
                                    sample, inflated, overlayFrame.Width, overlayFrame.Height, destRect);
                                if (outSample != null)
                                {
                                    counts.CpuComposited++;
                                }
                            }
                        }
                    }

                    if (outSample == null)
                    {
                        // Outside the toast interval. Still take the frame out of the reader's own surface
                        // where the compositor asks to: the sink queues far more samples than the reader
                        // keeps surfaces, so handing one straight through lets a later frame overwrite
                        // this one's pixels while it waits to be encoded.
                        outSample = gpuCompositor?.CopyForOutput(sample);
                        if (outSample == null)
                        {
                            outSample = sample;
                            sample = null;
                        }

                        counts.PassedThrough++;
                    }
                }
                finally
                {
                    sample?.Dispose();
                }

                // Write straight away, with the duration the base clip already carries. Holding a
                // frame back to measure the gap to the next one would keep the reader's decoded
                // surface alive past the read that may recycle it, which shows up as the wrong
                // picture on some frames.
                var outTime = time - trimLead;
                var duration = sourceDuration > 0 ? sourceDuration : nominalDuration;
                var remaining = endLimit - outTime;
                if (remaining > 0 && duration > remaining)
                {
                    duration = remaining;
                }

                WriteVideoAndDispose(sink, videoStream, outSample, outTime, duration);
                WaitForEncoderQueue(sink, videoStream);
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
            public int GpuComposited;
            public int CpuComposited;
            public int PassedThrough;
        }

        /// <summary>
        /// Reports what the pass cost. Every frame of the clip is decoded and re-encoded here, not just
        /// the ones the toast covers, so this is the bulk of the time between an unlock and its clip
        /// appearing — worth being able to see per clip rather than inferring it.
        /// </summary>
        private void LogPassCost(Stopwatch timer, CompositeCounts counts, int frameW, int frameH)
        {
            var carded = counts.GpuComposited + counts.CpuComposited;
            var frames = carded + counts.PassedThrough;
            var seconds = Math.Max(0.001, timer.Elapsed.TotalSeconds);
            var where = counts.CpuComposited == 0
                ? (counts.GpuComposited > 0 ? "GPU" : "none")
                : (counts.GpuComposited > 0 ? $"GPU+CPU ({counts.CpuComposited} on the CPU)" : "CPU");
            _logger?.Debug(
                $"[Recording] Toast composite: {frames} frames ({carded} with the card, composited on " +
                $"{where}) at {frameW}x{frameH} in {timer.ElapsedMilliseconds}ms ({frames / seconds:0.0} fps).");
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
