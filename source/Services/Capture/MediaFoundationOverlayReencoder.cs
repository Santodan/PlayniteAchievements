using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// composited in: the base clip's video decodes to NV12 through a SourceReader, frames inside
    /// the toast interval get the track's card blended in at its recorded client-relative position
    /// (translated to the synthetic single-toast corner), and everything re-encodes through a
    /// SinkWriter H.264 stream (hardware MFT where present). Audio passes through as native AAC,
    /// stream-copied. Samples before <c>trimLeadSeconds</c> (the base clip's keyframe lead) are
    /// dropped and the rest re-stamped, so the output starts exactly at the clip window. Any
    /// failure returns false — the caller keeps the toastless base clip, so a re-encode failure
    /// can never lose a clip.
    /// <para>
    /// The card is one <see cref="IFrameOverlaySource"/>; the passes below take a list of them, so
    /// further overlays add a source and an interval rather than a code path. When the base clip's
    /// GOP structure allows it, the pass in the <c>.Splice</c> partial stream-copies the compressed
    /// video no overlay touches and re-encodes only the runs around the overlays; this file holds
    /// the whole-clip pass it falls back to and the helpers both share.
    /// </para>
    /// <para>
    /// The card is blended in system memory by <see cref="OverlayCompositor"/>, in place in the decoded
    /// NV12 sample and over the card's own rectangle only, so no colour conversion runs on either side
    /// of the blend. A GPU-resident version of this pass was roughly twenty times faster per composited
    /// frame than the RGB compositor it replaced but produced frames carrying a picture from seconds
    /// earlier, and the cause was never found; blending the card's area on the CPU costs a fraction of
    /// a millisecond per frame, so nothing is left to gain there.
    /// </para>
    /// </summary>
    internal sealed partial class MediaFoundationOverlayReencoder
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

            using (MediaFoundationRuntime.Acquire())
            {
                try
                {
                    var toastStart = ToTicks(toastStartSeconds);
                    var overlays = new IFrameOverlaySource[]
                    {
                        new ToastOverlaySource(track, toastStart, ToastEndTicks(toastStart, toastMaxSeconds, track)),
                    };

                    if (SpliceEnabled && TrySpliceExport(
                            baseClipPath, overlays, trimLeadSeconds, endSeconds, chimePcm, chimeStartSeconds,
                            outputPath, configuredFps, quality))
                    {
                        return true;
                    }

                    return ReencodeWhole(
                        baseClipPath, overlays, trimLeadSeconds, endSeconds, chimePcm, chimeStartSeconds,
                        outputPath, configuredFps, quality);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Recording] Toast overlay re-encode failed; the toastless clip is kept.");
                    return false;
                }
            }
        }

        /// <summary>
        /// The whole-clip pass: every frame of the base clip decodes, the ones an overlay covers
        /// are composited, and everything re-encodes into one sink alongside the audio.
        /// </summary>
        private bool ReencodeWhole(
            string baseClipPath, IReadOnlyList<IFrameOverlaySource> overlays, double trimLeadSeconds,
            double endSeconds, byte[] chimePcm, double chimeStartSeconds, string outputPath,
            int configuredFps, RecordingQuality quality)
        {
            using (var videoReader = CreateDecodingVideoReader(baseClipPath))
            using (var decodedType = ConfigureNv12Output(
                videoReader, configuredFps, out var frameW, out var frameH, out var fps, out var stride))
            {
                SinkWriter sink = null;
                SourceReader audioReader = null;
                try
                {
                    var videoStream = -1;
                    var audioStream = -1;
                    sink = CreateEncodingSink(
                        outputPath,
                        s =>
                        {
                            videoStream = AddVideoStream(s, frameW, frameH, stride, fps, quality);
                            audioStream = TryAddAudio(s, baseClipPath, decodeToPcm: chimePcm != null, out audioReader);
                        });

                    // Says whether this pass actually got a hardware encoder — the pass dominates
                    // clip latency, so a silent software fallback is worth being able to see.
                    _logger?.Debug(
                        "[Recording] Toast re-encode transforms: " +
                        MediaFoundationH264Encoder.DescribeTransforms(sink, videoStream) + ".");

                    var stack = FrameOverlayStack.Create(overlays, frameW, frameH, stride);
                    using (audioReader)
                    {
                        var timer = Stopwatch.StartNew();
                        var counts = WriteComposited(
                            sink, videoStream, videoReader, audioStream, audioReader,
                            stack, trimLeadSeconds, endSeconds,
                            audioStream >= 0 ? chimePcm : null, chimeStartSeconds,
                            OneSecond100ns / Math.Max(1, fps), frameW, frameH);
                        sink.Finalize();
                        LogPassCost(timer, counts, frameW, frameH);
                    }

                    return true;
                }
                catch
                {
                    // The sink's using block above only starts once the sink exists; an audio
                    // reader opened during a failed sink setup would otherwise leak.
                    audioReader?.Dispose();
                    throw;
                }
                finally
                {
                    sink?.Dispose();
                }
            }
        }

        /// <summary>The last base-clip tick the card shows on: bounded by the slot and the track's own length.</summary>
        private static long ToastEndTicks(long toastStart, double toastMaxSeconds, ToastOverlayTrack track)
        {
            return toastStart + ToTicks(Math.Min(Math.Max(0, toastMaxSeconds), track.DurationSeconds));
        }

        /// <summary>
        /// A reader on the base clip's video stream that hands back the decoder's own NV12 frames,
        /// stamped with the compressed samples' own times.
        /// </summary>
        private static SourceReader CreateDecodingVideoReader(string baseClipPath)
        {
            // No video-processing attribute: the frames stay in the decoder's NV12, which the card
            // is blended into and the encoder accepts as it is, so neither a decode-side nor an
            // encode-side colour converter runs. Advanced video processing would also have added
            // frame-rate conversion, which re-times every decoded frame onto the type's declared
            // frame rate — an average the MP4 source derives from the file, so a clip whose capture
            // stalled decodes to timestamps that drift from the compressed ones by the whole stall
            // (404 ms measured). The card would then be placed by that drifted clock, and a spliced
            // run cut by decoded time would take different frames from the ones the compressed plan
            // copies around it.
            var videoReader = new SourceReader(baseClipPath);
            try
            {
                videoReader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                videoReader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                return videoReader;
            }
            catch
            {
                videoReader.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Asks the reader for NV12 and returns the decoded type it settled on, which doubles as
        /// the encoding sink's input type. The caller disposes it.
        /// </summary>
        private static MediaType ConfigureNv12Output(
            SourceReader videoReader, int configuredFps,
            out int frameW, out int frameH, out int fps, out int stride)
        {
            using (var request = new MediaType())
            {
                request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                videoReader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
            }

            var decodedType = videoReader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream);
            try
            {
                var size = decodedType.Get(MediaTypeAttributeKeys.FrameSize);
                frameW = (int)(size >> 32);
                frameH = (int)(size & 0xffffffff);
                fps = ReadFps(decodedType, configuredFps);
                // Every frame is copied out of the decoder into the contiguous layout before it is
                // touched (see DetachFromDecoder), whose luma rows are exactly the frame width, so
                // that is the stride the compositor and the encoder are told regardless of the
                // pitch the decoder's own buffers used.
                stride = frameW;
                return decodedType;
            }
            catch
            {
                decodedType.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates and starts an H.264 encoding sink with hardware transforms enabled;
        /// <paramref name="configureStreams"/> adds the streams. No D3D device manager is bound:
        /// with NV12 input the vendor's hardware encoder is selected directly, and the NVIDIA
        /// transform rejects system-memory samples (E_INVALIDARG on the first write) while a
        /// manager is bound. The manager was only ever needed to get that encoder chosen behind
        /// the RGB colour converter the pass used to feed.
        /// </summary>
        private SinkWriter CreateEncodingSink(string outputPath, Action<SinkWriter> configureStreams)
        {
            SinkWriter sink = null;
            try
            {
                using (var sinkAttributes = new MediaAttributes(1))
                {
                    sinkAttributes.Set(
                        SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, PreferSoftwareEncoder ? 0 : 1);
                    sink = MediaFactory.CreateSinkWriterFromURL(outputPath, null, sinkAttributes);
                }

                configureStreams(sink);
                sink.BeginWriting();
                return sink;
            }
            catch
            {
                sink?.Dispose();
                throw;
            }
        }

        private static int AddVideoStream(SinkWriter sink, int frameW, int frameH, int stride, int fps, RecordingQuality quality)
        {
            int streamIndex;
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
                sink.AddStream(outputType, out streamIndex);
            }

            // A clean NV12 type rather than the decoder's own: the decoder's carries attributes of
            // its own (a mixed interlace mode among them) that the encoder declines, which makes the
            // sink insert a converter that then rejects the samples. Declared progressive and tagged
            // with the same limited-range BT.709 the output carries, the frames go to the encoder as
            // they are.
            using (var inputType = new MediaType())
            {
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(frameW, frameH));
                inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                inputType.Set(MediaTypeAttributeKeys.DefaultStride, stride);
                inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1);
                MediaFoundationColor.ApplyBt709LimitedOutput(inputType);
                sink.SetInputMediaType(streamIndex, inputType, null);
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
                _logger?.Debug(
                    ex,
                    "[Recording] Base clip audio could not be configured; aborting the overlay " +
                    "pass so the caller keeps the toastless clip with its audio.");
                throw;
            }
        }

        /// <summary>
        /// The first audio sample past the lead, or a throw when a declared audio stream yields
        /// none — the pass must abort rather than write a silent track over the base clip's audio.
        /// </summary>
        private static Sample ReadFirstAudio(int audioStream, SourceReader audioReader, long trimLead)
        {
            var pendingAudio = audioStream >= 0 ? ReadNextAudio(audioReader, trimLead) : null;
            if (audioStream >= 0 && pendingAudio == null)
            {
                throw new InvalidDataException(
                    "The base clip declared audio but produced no samples after lead trimming.");
            }

            return pendingAudio;
        }

        /// <summary>
        /// Decodes, composites, re-stamps, and writes both streams interleaved by output time
        /// (a multi-stream SinkWriter blocks a stream that runs too far ahead of the other).
        /// </summary>
        private CompositeCounts WriteComposited(
            SinkWriter sink, int videoStream, SourceReader videoReader,
            int audioStream, SourceReader audioReader,
            FrameOverlayStack overlays, double trimLeadSeconds,
            double endSeconds, byte[] chimePcm, double chimeStartSeconds,
            long nominalDuration, int frameW, int frameH)
        {
            var trimLead = ToTicks(trimLeadSeconds);
            // Output-timeline end cut (base timeline minus the lead): both streams stop here.
            var endLimit = ToTicks(endSeconds) - trimLead;
            // Output-timeline chime onset; may be negative (chime head before the clip start),
            // which the mix offsets handle by skipping the chime's head.
            var chimeStartOut = ToTicks(chimeStartSeconds) - trimLead;

            var pendingAudio = ReadFirstAudio(audioStream, audioReader, trimLead);
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

                sample = DetachFromDecoder(sample, frameW, frameH);

                // Drain audio up to this video timestamp so both streams advance together.
                while (pendingAudio != null && pendingAudio.SampleTime <= time - trimLead)
                {
                    WriteAndDispose(sink, audioStream, MixChime(pendingAudio, chimePcm, chimeStartOut));
                    pendingAudio = ReadNextAudio(audioReader, trimLead);
                }

                Compose(overlays, sample, time, ref counts);

                // Write with the duration the base clip already carries.
                var outTime = time - trimLead;
                WriteVideoAndDispose(
                    sink, videoStream, sample, outTime,
                    ClampDuration(sourceDuration > 0 ? sourceDuration : nominalDuration, outTime, endLimit));
                WaitForEncoderQueue(sink, videoStream);
            }

            WriteTrailingAudio(sink, audioStream, audioReader, pendingAudio, trimLead, endLimit, chimePcm, chimeStartOut);
            return counts;
        }

        /// <summary>
        /// Copies a decoded frame into a buffer of this pass's own and disposes the decoder's
        /// sample. The decoder hands out samples from a small pool and reuses a buffer as soon as
        /// its sample is released — but the encoding sink queues written samples and reads them
        /// later on its own thread, so a frame written straight from the decoder can be partly
        /// overwritten by a later decode before the encoder sees it, which shows as luma and chroma
        /// from two different frames. The RGB path never met this because its colour converter
        /// copied every frame; feeding NV12 straight through needs the copy made here.
        /// <para>
        /// The copy is the contiguous layout, rows packed at the frame width — but the decoder's
        /// surfaces are allocated at a macroblock-aligned height (1088 rows for 1080p), and the
        /// contiguous copy keeps that padding, which puts the chroma plane 8 luma rows later than
        /// the exact-height layout the encoder and the compositor address. Left alone, every
        /// picture's chroma lands 16 rows below its luma while the card, blended by the same wrong
        /// assumption, looks right. The chroma plane is therefore moved up to follow exactly
        /// <paramref name="frameH"/> luma rows and the padding is dropped.
        /// </para>
        /// </summary>
        private Sample DetachFromDecoder(Sample decoded, int frameW, int frameH)
        {
            using (decoded)
            {
                var length = decoded.TotalLength;
                var exactLength = frameW * frameH * 3 / 2;
                var buffer = MediaFactory.CreateMemoryBuffer(Math.Max(length, exactLength));
                try
                {
                    decoded.CopyToBuffer(buffer);
                    if (length != exactLength)
                    {
                        RepackPaddedNv12(buffer, length, frameW, frameH, exactLength);
                    }

                    var copy = MediaFactory.CreateSample();
                    copy.AddBuffer(buffer);
                    copy.SampleTime = decoded.SampleTime;
                    copy.SampleDuration = decoded.SampleDuration;
                    return copy;
                }
                finally
                {
                    buffer.Dispose();
                }
            }
        }

        private byte[] _chromaScratch;
        private bool _paddingLogged;

        /// <summary>
        /// Moves the chroma plane of a padded-height NV12 frame (rows beyond <paramref name="frameH"/>
        /// are allocation padding) to directly after the frame's luma rows, and trims the buffer.
        /// </summary>
        private void RepackPaddedNv12(MediaBuffer buffer, int length, int frameW, int frameH, int exactLength)
        {
            var lumaStride = frameW;
            var planeBytes = lumaStride * 3 / 2;
            if (length < exactLength || length % planeBytes != 0)
            {
                throw new InvalidDataException(
                    $"Decoded NV12 frame of {length} bytes does not fit {frameW}x{frameH} at any padded height.");
            }

            var paddedH = length / planeBytes;
            if (!_paddingLogged)
            {
                _paddingLogged = true;
                _logger?.Debug($"[Recording] Decoder frames carry {paddedH - frameH} padded rows; repacking to {frameW}x{frameH}.");
            }

            var chromaBytes = lumaStride * frameH / 2;
            if (_chromaScratch == null || _chromaScratch.Length < chromaBytes)
            {
                _chromaScratch = new byte[chromaBytes];
            }

            var ptr = buffer.Lock(out _, out _);
            try
            {
                Marshal.Copy(IntPtr.Add(ptr, lumaStride * paddedH), _chromaScratch, 0, chromaBytes);
                Marshal.Copy(_chromaScratch, 0, IntPtr.Add(ptr, lumaStride * frameH), chromaBytes);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = exactLength;
        }

        /// <summary>
        /// Draws the overlays covering a decoded frame into it, in place; frames outside every
        /// overlay's interval pass through untouched. Either way the same sample is written next.
        /// </summary>
        private static void Compose(FrameOverlayStack overlays, Sample sample, long time, ref CompositeCounts counts)
        {
            if (overlays != null && overlays.TryCompose(sample, time))
            {
                counts.Composited++;
            }
            else
            {
                counts.PassedThrough++;
            }
        }

        /// <summary>A frame's duration, shortened so the last frame ends exactly on the end cut.</summary>
        private static long ClampDuration(long duration, long outTime, long endLimit)
        {
            var remaining = endLimit - outTime;
            return remaining > 0 && duration > remaining ? remaining : duration;
        }

        /// <summary>Audio after the last video sample, up to the end cut.</summary>
        private static void WriteTrailingAudio(
            SinkWriter sink, int audioStream, SourceReader audioReader, Sample pendingAudio,
            long trimLead, long endLimit, byte[] chimePcm, long chimeStartOut)
        {
            while (pendingAudio != null && pendingAudio.SampleTime <= endLimit)
            {
                WriteAndDispose(sink, audioStream, MixChime(pendingAudio, chimePcm, chimeStartOut));
                pendingAudio = ReadNextAudio(audioReader, trimLead);
            }

            pendingAudio?.Dispose();
        }

        /// <summary>What one pass wrote, for the cost line below.</summary>
        private struct CompositeCounts
        {
            public int Composited;
            public int PassedThrough;
        }

        /// <summary>
        /// Reports what the whole-clip pass cost. Every frame of the clip is decoded and re-encoded
        /// here, not just the ones the toast covers, so this is the bulk of the time between an unlock
        /// and its clip appearing — worth being able to see per clip rather than inferring it.
        /// </summary>
        private void LogPassCost(Stopwatch timer, CompositeCounts counts, int frameW, int frameH)
        {
            var carded = counts.Composited;
            var frames = carded + counts.PassedThrough;
            var seconds = Math.Max(0.001, timer.Elapsed.TotalSeconds);
            _logger?.Debug(
                $"[Recording] Toast composite: {frames} frames ({carded} with the card) at " +
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
