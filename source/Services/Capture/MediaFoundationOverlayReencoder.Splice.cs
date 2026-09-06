using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PlayniteAchievements.Models.Settings;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The spliced pass: overlays cover only a few seconds of a clip that is mostly untouched
    /// footage, so instead of decoding and re-encoding every frame, the compressed video no overlay
    /// touches is stream-copied whole-GOP and only the runs around the overlays are re-encoded —
    /// plus the head between the clip window's start and the first keyframe, which cannot be copied
    /// because a copied run must begin on a keyframe (see <see cref="OverlaySplicePlan"/>). Each
    /// re-encoded run is written to its own temporary MP4 by the same encoder configuration as the
    /// whole-clip pass, then a remux stitches the runs in order into one H.264 track along with the
    /// audio. Any number of overlays and runs interleave this way.
    /// <para>
    /// One track can only hold GOPs from two encoders if they agree on the H.264 parameter sets
    /// the MP4's <c>avcC</c> declares, so each re-encoded run's sequence header is compared byte for
    /// byte with the base clip's before the remux is attempted; a mismatch (a vendor whose encoder
    /// signals differently) turns the splice off for the rest of the process and the whole-clip
    /// pass takes over. The same happens on any other failure: the pass returns false with the
    /// output removed, and nothing about the whole-clip fallback changes.
    /// </para>
    /// </summary>
    internal sealed partial class MediaFoundationOverlayReencoder
    {
        // A copied run shorter than this saves less than the extra encoder setup and finalize a
        // split costs, so the planner folds it into its re-encoded neighbours instead.
        private const double MinCopySeconds = 3.0;

        // 0: untested. 1: a re-encoded run's parameter sets matched the capture's. -1: they
        // differed, so no further clip in this process tries the splice.
        private static volatile int s_spliceCompatibility;

        /// <summary>Diagnostic switch: the whole-clip pass runs unconditionally when false.</summary>
        internal bool SpliceEnabled { get; set; } = true;

        /// <summary>A frame's place on the base clip's timeline, recorded as its run was encoded.</summary>
        private struct FrameStamp
        {
            public long Time;
            public long Duration;
        }

        /// <summary>One re-encoded run's temporary file and the base-clip frames it holds, in order.</summary>
        private sealed class EncodedRun
        {
            public OverlaySplicePlan.Run Run;
            public string Path;
            public List<FrameStamp> Stamps;
        }

        private bool TrySpliceExport(
            string baseClipPath, IReadOnlyList<IFrameOverlaySource> overlays, double trimLeadSeconds,
            double endSeconds, byte[] chimePcm, double chimeStartSeconds, string outputPath,
            int configuredFps, RecordingQuality quality, DXGIDeviceManager deviceManager)
        {
            if (s_spliceCompatibility < 0)
            {
                return false;
            }

            var encoded = new List<EncodedRun>();
            try
            {
                var trimLead = ToTicks(trimLeadSeconds);
                var endInclusive = ToTicks(endSeconds);
                var plan = OverlaySplicePlan.TryPlan(
                    ScanVideoSamples(baseClipPath), trimLead, FrameOverlayStack.ChangedIntervals(overlays),
                    endInclusive, ToTicks(MinCopySeconds));
                if (plan == null)
                {
                    _logger?.Debug("[Recording] Toast splice: no run worth copying; re-encoding the whole clip.");
                    return false;
                }

                var baseHeader = ReadSequenceHeader(baseClipPath);
                if (baseHeader == null || baseHeader.Length == 0)
                {
                    _logger?.Debug("[Recording] Toast splice: the base clip declares no H.264 sequence header; re-encoding the whole clip.");
                    return false;
                }

                var timer = Stopwatch.StartNew();
                var counts = default(CompositeCounts);
                var frameW = 0;
                var frameH = 0;
                foreach (var run in plan.Runs)
                {
                    if (run.Kind != OverlaySplicePlan.RunKind.Reencode)
                    {
                        continue;
                    }

                    // Runs go in order, so the head (short, card-free) usually settles the
                    // parameter-set question before the long runs are paid for.
                    var item = new EncodedRun
                    {
                        Run = run,
                        Path = RunPath(outputPath, encoded.Count),
                        Stamps = new List<FrameStamp>(run.Frames),
                    };
                    encoded.Add(item);
                    EncodeRun(
                        baseClipPath, deviceManager, run, overlays, item.Path, configuredFps, quality,
                        item.Stamps, ref counts, out frameW, out frameH);
                    if (!RunIsSpliceable(baseHeader, item.Path, item.Stamps.Count))
                    {
                        return false;
                    }
                }

                s_spliceCompatibility = 1;
                var encodeMs = timer.ElapsedMilliseconds;
                Remux(
                    baseClipPath, plan, encoded, trimLead, endInclusive - trimLead,
                    chimePcm, ToTicks(chimeStartSeconds) - trimLead, outputPath);

                _logger?.Debug(
                    $"[Recording] Toast splice: copied {plan.CopyFrames} of {plan.CopyFrames + plan.ReencodeFrames} frames " +
                    $"({plan.CopyTicks / (double)OneSecond100ns:0.00}s), re-encoded {plan.ReencodeFrames} in " +
                    $"{plan.ReencodeRuns} run(s) ({counts.Composited} with an overlay) at {frameW}x{frameH} in " +
                    $"{timer.ElapsedMilliseconds}ms (encode {encodeMs}ms, remux {timer.ElapsedMilliseconds - encodeMs}ms).");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Toast splice failed; re-encoding the whole clip.");
                TryDelete(outputPath);
                return false;
            }
            finally
            {
                foreach (var item in encoded)
                {
                    TryDelete(item.Path);
                }
            }
        }

        private static string RunPath(string outputPath, int index)
        {
            return Path.Combine(
                Path.GetDirectoryName(outputPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(outputPath) + "_run" + index + ".mp4");
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A stranded temp in the buffer directory is pruned with the session.
            }
        }

        /// <summary>
        /// Whether a re-encoded run can share a track with the base clip: its parameter sets must
        /// match the base clip's byte for byte, and it must hold exactly the frames it was given (an
        /// encoder that dropped or reordered frames would put the stamps on the wrong pictures). A
        /// parameter-set mismatch is a property of the encoder, so it is remembered.
        /// </summary>
        private bool RunIsSpliceable(byte[] baseHeader, string runPath, int expectedFrames)
        {
            var runHeader = ReadSequenceHeader(runPath);
            if (!BytesEqual(baseHeader, runHeader))
            {
                s_spliceCompatibility = -1;
                _logger?.Info(
                    "[Recording] Toast splice: the re-encoder's H.264 parameter sets differ from the capture's " +
                    $"({runHeader?.Length ?? 0} vs {baseHeader.Length} bytes), so copied and re-encoded video " +
                    "cannot share a track; clips re-encode whole from now on.");
                return false;
            }

            var actualFrames = CountVideoSamples(runPath);
            if (actualFrames != expectedFrames)
            {
                _logger?.Debug(
                    $"[Recording] Toast splice: a re-encoded run holds {actualFrames} frames for {expectedFrames} " +
                    "given; re-encoding the whole clip.");
                return false;
            }

            return true;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A reader on a file's first video stream at its native (compressed) type.</summary>
        private static SourceReader CreateCompressedVideoReader(string path)
        {
            var reader = new SourceReader(path);
            try
            {
                reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                return reader;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        /// <summary>Every compressed video sample of a file: time, duration, keyframe flag.</summary>
        private static List<OverlaySplicePlan.SampleInfo> ScanVideoSamples(string path)
        {
            var samples = new List<OverlaySplicePlan.SampleInfo>();
            using (var reader = CreateCompressedVideoReader(path))
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

                    using (sample)
                    {
                        samples.Add(new OverlaySplicePlan.SampleInfo
                        {
                            Time = sample.SampleTime,
                            Duration = sample.SampleDuration,
                            IsKeyframe = MediaFoundationClipExporter.IsKeyframe(sample),
                        });
                    }
                }
            }

            return samples;
        }

        private static int CountVideoSamples(string path)
        {
            return ScanVideoSamples(path).Count;
        }

        /// <summary>The H.264 sequence header (SPS + PPS) a file's video track declares, or null.</summary>
        private static byte[] ReadSequenceHeader(string path)
        {
            try
            {
                using (var reader = CreateCompressedVideoReader(path))
                using (var nativeType = reader.GetNativeMediaType((int)SourceReaderIndex.FirstVideoStream, 0))
                {
                    return nativeType.Get(MediaTypeAttributeKeys.MpegSequenceHeader);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Decodes the base clip's frames in one re-encoded run, composites the ones an overlay
        /// covers, and encodes them into <paramref name="tempPath"/> as a video-only MP4. Each written
        /// frame's base-clip time and duration is appended to <paramref name="stamps"/> so the remux
        /// can put the encoded frames back where they came from. A run that does not start at the
        /// clip's head seeks the reader to its start (a keyframe) instead of decoding everything
        /// before it.
        /// </summary>
        private void EncodeRun(
            string baseClipPath, DXGIDeviceManager deviceManager, OverlaySplicePlan.Run run,
            IReadOnlyList<IFrameOverlaySource> overlays, string tempPath, int configuredFps,
            RecordingQuality quality, List<FrameStamp> stamps, ref CompositeCounts counts,
            out int frameW, out int frameH)
        {
            var phase = Stopwatch.StartNew();
            using (var videoReader = CreateDecodingVideoReader(baseClipPath))
            using (var decodedType = ConfigureRgb32Output(
                videoReader, configuredFps, out frameW, out frameH, out var fps, out var stride))
            {
                if (run.Start > 0)
                {
                    // The source lands on the keyframe at or before the position; the run starts on
                    // a keyframe (or at the clip's head), and anything earlier is discarded below
                    // regardless.
                    videoReader.SetCurrentPosition(run.Start);
                }

                var stack = FrameOverlayStack.Create(overlays, frameW, frameH, stride);
                var nominalDuration = OneSecond100ns / Math.Max(1, fps);
                var width = frameW;
                var height = frameH;
                var readerMs = phase.ElapsedMilliseconds;
                SinkWriter sink = null;
                try
                {
                    var videoStream = -1;
                    sink = CreateEncodingSink(
                        tempPath, deviceManager,
                        s => videoStream = AddVideoStream(s, decodedType, width, height, fps, quality),
                        null, out var usedManager);
                    var sinkMs = phase.ElapsedMilliseconds - readerMs;

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
                        var sourceDuration = sample.SampleDuration;
                        if (time < run.Start)
                        {
                            sample.Dispose();
                            continue;
                        }

                        if (time >= run.End)
                        {
                            sample.Dispose();
                            break;
                        }

                        var outSample = ComposeOrPassThrough(stack, sample, time, ref counts);
                        var duration = sourceDuration > 0 ? sourceDuration : nominalDuration;
                        stamps.Add(new FrameStamp { Time = time, Duration = duration });
                        // The run's own timeline starts at zero; the remux re-stamps from `stamps`.
                        WriteVideoAndDispose(sink, videoStream, outSample, time - run.Start, duration);
                        WaitForEncoderQueue(sink, videoStream);
                    }

                    var loopMs = phase.ElapsedMilliseconds - readerMs - sinkMs;
                    sink.Finalize();
                    _logger?.Debug(
                        $"[Recording] Toast splice run: {stamps.Count} frames from {run.Start / (double)OneSecond100ns:0.00}s " +
                        $"via {MediaFoundationH264Encoder.DescribeTransforms(sink, videoStream)}" +
                        $"{(usedManager ? " (D3D manager bound)" : " (no D3D manager)")}; " +
                        $"reader {readerMs}ms, sink {sinkMs}ms, frames {loopMs}ms, " +
                        $"finalize {phase.ElapsedMilliseconds - readerMs - sinkMs - loopMs}ms.");
                }
                finally
                {
                    sink?.Dispose();
                }
            }
        }

        /// <summary>
        /// Stitches the output: one stream-copied H.264 track carrying the plan's runs in order —
        /// copied GOPs straight from the base clip, re-encoded runs from their temporary files —
        /// each frame stamped with its base-clip time minus the lead, plus the audio exactly as the
        /// whole-clip pass writes it.
        /// </summary>
        private void Remux(
            string baseClipPath, OverlaySplicePlan plan, List<EncodedRun> encoded,
            long trimLead, long endLimit, byte[] chimePcm, long chimeStartOut, string outputPath)
        {
            using (var baseReader = CreateCompressedVideoReader(baseClipPath))
            using (var nativeType = baseReader.GetNativeMediaType((int)SourceReaderIndex.FirstVideoStream, 0))
            {
                SinkWriter sink = null;
                SourceReader audioReader = null;
                try
                {
                    using (var sinkAttributes = new MediaAttributes(1))
                    {
                        // A remux gains nothing from the sink's interleave throttling — the write
                        // loop interleaves by timestamp — and the video is compressed, so
                        // WriteSample never needs to block.
                        sinkAttributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1);
                        sink = MediaFactory.CreateSinkWriterFromURL(outputPath, null, sinkAttributes);
                    }

                    // Stream copy: input type == output type, so no encoder MFT is inserted. The
                    // base clip's type supplies the avcC every run was checked against.
                    sink.AddStream(nativeType, out var videoStream);
                    sink.SetInputMediaType(videoStream, nativeType, null);
                    var audioStream = TryAddAudio(sink, baseClipPath, decodeToPcm: chimePcm != null, out audioReader);
                    sink.BeginWriting();

                    using (audioReader)
                    {
                        var mixedChime = audioStream >= 0 ? chimePcm : null;
                        var pendingAudio = ReadFirstAudio(audioStream, audioReader, trimLead);
                        foreach (var video in SplicedVideoSamples(plan, encoded, baseReader, trimLead, endLimit))
                        {
                            while (pendingAudio != null && pendingAudio.SampleTime <= video.SampleTime)
                            {
                                WriteAndDispose(sink, audioStream, MixChime(pendingAudio, mixedChime, chimeStartOut));
                                pendingAudio = ReadNextAudio(audioReader, trimLead);
                            }

                            WriteAndDispose(sink, videoStream, video);
                        }

                        WriteTrailingAudio(
                            sink, audioStream, audioReader, pendingAudio, trimLead, endLimit, mixedChime, chimeStartOut);
                    }

                    sink.Finalize();
                }
                finally
                {
                    sink?.Dispose();
                }
            }
        }

        /// <summary>
        /// The output's video samples in run order, each already stamped onto the output timeline.
        /// Copied runs come from one forward pass over the base clip's compressed samples; the
        /// samples between them (the re-encoded stretches) are read and discarded, which costs
        /// nothing compared to decoding them. Yielded samples are the caller's to dispose.
        /// </summary>
        private static IEnumerable<Sample> SplicedVideoSamples(
            OverlaySplicePlan plan, List<EncodedRun> encoded, SourceReader baseReader, long trimLead, long endLimit)
        {
            var encodedIndex = 0;
            foreach (var run in plan.Runs)
            {
                if (run.Kind == OverlaySplicePlan.RunKind.Copy)
                {
                    foreach (var sample in CopiedSamples(baseReader, run.Start, run.End, trimLead, endLimit))
                    {
                        yield return sample;
                    }

                    continue;
                }

                var item = encoded[encodedIndex++];
                if (item.Run != run)
                {
                    throw new InvalidDataException("Re-encoded runs are out of step with the plan.");
                }

                foreach (var sample in RunSamples(item.Path, item.Stamps, trimLead, endLimit))
                {
                    yield return sample;
                }
            }
        }

        /// <summary>
        /// A re-encoded run's compressed samples, the i-th stamped with the i-th frame it was
        /// encoded from. The frame count was verified before the remux began; a disagreement here
        /// still throws rather than write frames onto the wrong stamps.
        /// </summary>
        private static IEnumerable<Sample> RunSamples(string runPath, List<FrameStamp> stamps, long trimLead, long endLimit)
        {
            using (var reader = CreateCompressedVideoReader(runPath))
            {
                var index = 0;
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

                    if (index >= stamps.Count)
                    {
                        sample.Dispose();
                        throw new InvalidDataException("A re-encoded run holds more frames than it was given.");
                    }

                    var stamp = stamps[index++];
                    var outTime = stamp.Time - trimLead;
                    sample.SampleTime = outTime;
                    sample.SampleDuration = Math.Max(1, ClampDuration(stamp.Duration, outTime, endLimit));
                    yield return sample;
                }

                if (index != stamps.Count)
                {
                    throw new InvalidDataException("A re-encoded run holds fewer frames than it was given.");
                }
            }
        }

        /// <summary>
        /// The base clip's compressed samples in [<paramref name="copyStart"/>, <paramref name="copyEnd"/>),
        /// re-stamped onto the output timeline with the durations they already carry. Samples before
        /// the run are discarded; the reader is left on the first sample past it.
        /// </summary>
        private static IEnumerable<Sample> CopiedSamples(
            SourceReader baseReader, long copyStart, long copyEnd, long trimLead, long endLimit)
        {
            while (true)
            {
                var sample = baseReader.ReadSample(
                    (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlags.None,
                    out _, out var flags, out _);
                if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                {
                    sample?.Dispose();
                    yield break;
                }

                var time = sample.SampleTime;
                if (time < copyStart)
                {
                    sample.Dispose();
                    continue;
                }

                if (time >= copyEnd)
                {
                    sample.Dispose();
                    yield break;
                }

                var outTime = time - trimLead;
                sample.SampleTime = outTime;
                sample.SampleDuration = Math.Max(1, ClampDuration(sample.SampleDuration, outTime, endLimit));
                yield return sample;
            }
        }
    }
}
