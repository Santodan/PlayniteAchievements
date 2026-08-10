using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Playnite.SDK;
using PlayniteAchievements.Services.Recording;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Assembles the final unlock clip from the rolling buffer with Media Foundation — the
    /// ffmpeg-free replacement for the old trim/concat/audio-mux export. The planned video segments
    /// (already H.264 .mp4, encoded at the target resolution) are concatenated and trimmed to the
    /// clip window by stream-copy (no re-encode: the source reader is left at the native compressed
    /// type and samples are written straight through, so capture quality is preserved). The trim
    /// start snaps back to the nearest keyframe at/before the window start — the encoder writes ~1
    /// keyframe/second, so this loses no content and adds ≤1s of lead, exactly matching the old
    /// `-c copy` seek. The planned loopback WAV chunks are read as PCM, converted to 48 kHz stereo
    /// 16-bit, encoded to AAC, and muxed into the same file, offset by that keyframe lead so audio
    /// and video stay aligned. Audio absent/failed → a video-only clip.
    /// </summary>
    internal sealed class MediaFoundationClipExporter
    {
        private const long OneSecond100ns = 10_000_000L;
        private const int AudioSampleRate = 48000;
        private const int AudioChannels = 2;
        private const int AudioBitsPerSample = 16;
        private const int AudioBytesPerAacSecond = 20000; // ~160 kbps, matching the old mux

        private readonly ILogger _logger;

        public MediaFoundationClipExporter(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Writes the trimmed, concatenated clip (video + optional audio) to <paramref name="outputPath"/>.
        /// <paramref name="videoLeadSeconds"/> reports how far before the requested window start
        /// the output begins (the keyframe snap-back) — the overlay re-encode uses it to place
        /// the toast and to trim the lead back off. Returns false (with one log line) on any
        /// failure so the caller can drop the clip cleanly.
        /// </summary>
        // The Media Foundation interop can surface native corrupted-state exceptions (access
        // violations from the source reader / sink writer); catch them so a failure degrades to
        // "no clip" with a log line instead of silently faulting the producer task.
        [HandleProcessCorruptedStateExceptions, System.Security.SecurityCritical]
        public bool Export(
            SegmentTimeline.ClipPlan videoPlan, SegmentTimeline.ClipPlan audioPlan, string outputPath,
            out double videoLeadSeconds)
        {
            videoLeadSeconds = 0;
            if (videoPlan?.Segments == null || videoPlan.Segments.Count == 0 || string.IsNullOrEmpty(outputPath))
            {
                return false;
            }

            MediaManager.Startup();
            try
            {
                SinkWriter sink = null;
                try
                {
                    _logger?.Debug($"[Recording] MF export: creating sink for {System.IO.Path.GetFileName(outputPath)} ({videoPlan.Segments.Count} video segs, audio={audioPlan?.Segments?.Count ?? 0}).");
                    sink = MediaFactory.CreateSinkWriterFromURL(outputPath, null, null);

                    var videoStream = AddVideoStream(sink, videoPlan.Segments[0].Path);
                    _logger?.Debug("[Recording] MF export: video stream added.");
                    var audioStream = -1;
                    MediaType pcmType = null;
                    if (audioPlan?.Segments != null && audioPlan.Segments.Count > 0)
                    {
                        audioStream = TryAddAudioStream(sink, out pcmType);
                        _logger?.Debug($"[Recording] MF export: audio stream added ({audioStream}).");
                    }

                    sink.BeginWriting();

                    var clipStart = ToTicks(videoPlan.StartOffsetSeconds);
                    var clipEnd = clipStart + ToTicks(videoPlan.DurationSeconds);
                    var keyframeStart = FindKeyframeStart(videoPlan.Segments[0].Path, clipStart);
                    var videoLead = clipStart - keyframeStart; // ≥ 0
                    videoLeadSeconds = videoLead / (double)OneSecond100ns;
                    _logger?.Debug($"[Recording] MF export: keyframeStart={keyframeStart / 10000}ms lead={videoLead / 10000}ms; writing video.");

                    WriteInterleaved(sink, videoStream, videoPlan, keyframeStart, clipEnd, audioStream, pcmType, audioPlan, videoLead);
                    _logger?.Debug("[Recording] MF export: samples written.");

                    pcmType?.Dispose();
                    sink.Finalize();
                    _logger?.Debug("[Recording] MF export: finalized.");
                    return true;
                }
                finally
                {
                    sink?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Recording] Media Foundation clip export failed.");
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

        private static int AddVideoStream(SinkWriter sink, string firstSegmentPath)
        {
            using (var reader = new SourceReader(firstSegmentPath))
            {
                reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                using (var nativeType = reader.GetNativeMediaType((int)SourceReaderIndex.FirstVideoStream, 0))
                {
                    sink.AddStream(nativeType, out var streamIndex);
                    // Stream copy: input type == output type, so no encoder MFT is inserted.
                    sink.SetInputMediaType(streamIndex, nativeType, null);
                    return streamIndex;
                }
            }
        }

        /// <summary>The AAC output type used for clip audio (shared with the overlay re-encoder).</summary>
        internal static MediaType CreateAacType()
        {
            var aacType = new MediaType();
            aacType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            aacType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            aacType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioSampleRate);
            aacType.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
            aacType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBitsPerSample);
            aacType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AudioBytesPerAacSecond);
            return aacType;
        }

        /// <summary>The 48 kHz stereo 16-bit PCM type used for clip audio (shared with the overlay re-encoder).</summary>
        internal static MediaType CreatePcmType()
        {
            var pcmType = new MediaType();
            pcmType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            pcmType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            pcmType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioSampleRate);
            pcmType.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
            pcmType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBitsPerSample);
            pcmType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, AudioChannels * AudioBitsPerSample / 8);
            pcmType.Set(
                MediaTypeAttributeKeys.AudioAvgBytesPerSecond,
                AudioSampleRate * AudioChannels * AudioBitsPerSample / 8);
            return pcmType;
        }

        private int TryAddAudioStream(SinkWriter sink, out MediaType pcmType)
        {
            pcmType = null;
            try
            {
                using (var aacType = CreateAacType())
                {
                    sink.AddStream(aacType, out var streamIndex);
                    pcmType = CreatePcmType();
                    sink.SetInputMediaType(streamIndex, pcmType, null);
                    return streamIndex;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Could not add an AAC audio stream; clip will be video-only.");
                pcmType?.Dispose();
                pcmType = null;
                return -1;
            }
        }

        /// <summary>
        /// Reads a planned audio window (e.g. the chime sidecar chunks around one wave's unlock
        /// sound) into one contiguous 48 kHz stereo 16-bit PCM buffer. Returns null on failure or
        /// when nothing overlaps.
        /// </summary>
        [HandleProcessCorruptedStateExceptions, System.Security.SecurityCritical]
        public static byte[] TryReadPcmWindow(SegmentTimeline.ClipPlan plan, ILogger logger)
        {
            if (plan?.Segments == null || plan.Segments.Count == 0)
            {
                return null;
            }

            MediaManager.Startup();
            try
            {
                using (var pcmType = CreatePcmType())
                using (var stream = new System.IO.MemoryStream())
                {
                    foreach (var timed in AudioSamples(plan, pcmType, videoLead: 0))
                    {
                        using (var sample = timed.Sample)
                        using (var buffer = sample.ConvertToContiguousBuffer())
                        {
                            var ptr = buffer.Lock(out _, out var length);
                            try
                            {
                                var bytes = new byte[length];
                                System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, length);
                                stream.Write(bytes, 0, length);
                            }
                            finally
                            {
                                buffer.Unlock();
                            }
                        }
                    }

                    return stream.Length > 0 ? stream.ToArray() : null;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[Recording] Chime PCM window read failed; the clip keeps its audio without the chime.");
                return null;
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

        /// <summary>
        /// The concat time (relative to the first segment's start) of the last keyframe at or before
        /// <paramref name="clipStart"/> — where a stream-copy trim must begin so the first written
        /// sample is decodable. Falls back to 0 (the segment's own opening IDR).
        /// </summary>
        private static long FindKeyframeStart(string firstSegmentPath, long clipStart)
        {
            using (var reader = new SourceReader(firstSegmentPath))
            {
                reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);

                long firstTime = -1;
                long keyframe = 0;
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

                    if (firstTime < 0)
                    {
                        firstTime = sample.SampleTime;
                    }

                    var concat = sample.SampleTime - firstTime;
                    var isKeyframe = IsKeyframe(sample);
                    sample.Dispose();

                    if (concat > clipStart)
                    {
                        break;
                    }

                    if (isKeyframe)
                    {
                        keyframe = concat;
                    }
                }

                return keyframe;
            }
        }

        private struct TimedSample
        {
            public long Time;
            public Sample Sample;
        }

        /// <summary>
        /// Writes video and audio samples to the sink in a single timestamp-ordered stream. A
        /// multi-stream Media Foundation SinkWriter blocks a stream that runs too far ahead of the
        /// others, so writing all video before any audio deadlocks — interleaving by output time
        /// keeps both streams advancing. Audio is best-effort: a read failure degrades to a
        /// video-only clip.
        /// </summary>
        private void WriteInterleaved(
            SinkWriter sink, int videoStream, SegmentTimeline.ClipPlan videoPlan, long keyframeStart, long clipEnd,
            int audioStream, MediaType pcmType, SegmentTimeline.ClipPlan audioPlan, long videoLead)
        {
            using (var video = VideoSamples(videoPlan, keyframeStart, clipEnd).GetEnumerator())
            {
                IEnumerator<TimedSample> audio = null;
                var hasAudio = false;
                if (audioStream >= 0 && audioPlan?.Segments != null && audioPlan.Segments.Count > 0)
                {
                    audio = AudioSamples(audioPlan, pcmType, videoLead).GetEnumerator();
                    hasAudio = TryMoveNext(audio);
                }

                try
                {
                    var hasVideo = video.MoveNext();
                    while (hasVideo || hasAudio)
                    {
                        if (hasVideo && (!hasAudio || video.Current.Time <= audio.Current.Time))
                        {
                            WriteAndDispose(sink, videoStream, video.Current);
                            hasVideo = video.MoveNext();
                        }
                        else
                        {
                            WriteAndDispose(sink, audioStream, audio.Current);
                            hasAudio = TryMoveNext(audio);
                        }
                    }
                }
                finally
                {
                    audio?.Dispose();
                }
            }
        }

        private static void WriteAndDispose(SinkWriter sink, int streamIndex, TimedSample timed)
        {
            try
            {
                sink.WriteSample(streamIndex, timed.Sample);
            }
            finally
            {
                timed.Sample.Dispose();
            }
        }

        private bool TryMoveNext(IEnumerator<TimedSample> enumerator)
        {
            try
            {
                return enumerator.MoveNext();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Recording] Clip audio read failed; clip will be video-only.");
                return false;
            }
        }

        /// <summary>
        /// The kept video samples (stream-copied H.264), concatenated across segments and trimmed to
        /// the window, each stamped with its output time (0 = the start keyframe). Skipped samples are
        /// disposed; yielded samples are the caller's to dispose after writing.
        /// <para>
        /// Each frame is held back one step so its duration can span the gap to the next frame's
        /// output time. The MP4 sink lays a track out by accumulating durations and ignores the
        /// sample times set here, so without that the wall-clock positions <see cref="SegmentPrefix"/>
        /// computes never reach the file: the sink packs frames back to back and swallows the dead
        /// time between one segment being finalized and the next one starting, shortening video
        /// against real-time audio by that much per rotation.
        /// </para>
        /// </summary>
        private static IEnumerable<TimedSample> VideoSamples(
            SegmentTimeline.ClipPlan plan, long keyframeStart, long clipEnd)
        {
            var started = false;
            Sample pending = null;
            var pendingTime = 0L;

            try
            {
                foreach (var segment in plan.Segments)
                {
                    var prefix = SegmentPrefix(plan, segment);
                    long firstTime = -1;
                    var reachedEnd = false;

                    using (var reader = new SourceReader(segment.Path))
                    {
                        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                        reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);

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

                            if (firstTime < 0)
                            {
                                firstTime = sample.SampleTime;
                            }

                            var concat = prefix + (sample.SampleTime - firstTime);

                            if (concat >= clipEnd)
                            {
                                sample.Dispose();
                                reachedEnd = true;
                                break;
                            }

                            if (!started)
                            {
                                if (concat < keyframeStart)
                                {
                                    sample.Dispose();
                                    continue;
                                }

                                started = true;
                            }

                            // Take ownership of the new frame before yielding the previous one, so an
                            // abandoned enumeration leaves exactly one sample for the finally below.
                            var ready = pending;
                            var readyTime = pendingTime;
                            pending = sample;
                            pendingTime = concat - keyframeStart;
                            if (ready != null)
                            {
                                yield return Timed(ready, readyTime, pendingTime - readyTime);
                            }
                        }
                    }

                    if (reachedEnd)
                    {
                        break;
                    }
                }

                if (pending != null)
                {
                    // The last frame runs to the end of the window rather than for one frame's worth,
                    // so the track ends where the clip does.
                    var last = pending;
                    pending = null;
                    yield return Timed(last, pendingTime, (clipEnd - keyframeStart) - pendingTime);
                }
            }
            finally
            {
                pending?.Dispose();
            }
        }

        /// <summary>Stamps a sample onto the output timeline. Durations are floored at one tick.</summary>
        private static TimedSample Timed(Sample sample, long time, long duration)
        {
            sample.SampleTime = time;
            sample.SampleDuration = Math.Max(1, duration);
            return new TimedSample { Time = time, Sample = sample };
        }

        /// <summary>
        /// Where a segment starts on the clip's concatenated timeline, measured from the first
        /// planned segment's recorded start.
        /// <para>
        /// Taken from the recorded wall-clock start rather than by accumulating each file's media
        /// span. A segment's media span is shorter than the time it covers whenever capture stalled
        /// before it rotated -- an alt-tab, a loading screen, a capture rebuild -- because its last
        /// frame lands well before the boundary and carries only a nominal frame duration.
        /// Accumulating those spans pulled every later segment earlier and drifted video ahead of
        /// audio by the total stalled time. Both streams stamp their files from the same wall
        /// clock, so anchoring to it keeps them on one basis and cannot accumulate.
        /// </para>
        /// </summary>
        private static long SegmentPrefix(SegmentTimeline.ClipPlan plan, SegmentTimeline.SegmentInfo segment)
        {
            // DateTime ticks are already 100-ns units, MF's own.
            var offset = (segment.StartUtc - plan.Segments[0].StartUtc).Ticks;
            return offset > 0 ? offset : 0;
        }

        /// <summary>
        /// The kept audio samples (PCM, converted for the AAC encoder), concatenated + trimmed, each
        /// stamped so its window start aligns with the video (offset by <paramref name="videoLead"/>).
        /// </summary>
        private static IEnumerable<TimedSample> AudioSamples(
            SegmentTimeline.ClipPlan plan, MediaType pcmType, long videoLead)
        {
            var clipStart = ToTicks(plan.StartOffsetSeconds);
            var clipEnd = clipStart + ToTicks(plan.DurationSeconds);
            var started = false;

            foreach (var chunk in plan.Segments)
            {
                var prefix = SegmentPrefix(plan, chunk);
                long firstTime = -1;
                var reachedEnd = false;

                using (var reader = new SourceReader(chunk.Path))
                {
                    reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection((int)SourceReaderIndex.FirstAudioStream, true);
                    // Force 48 kHz stereo 16-bit PCM: the reader inserts a converter/resampler for the
                    // WAV's native float format so the AAC encoder gets the input it requires.
                    reader.SetCurrentMediaType((int)SourceReaderIndex.FirstAudioStream, pcmType);

                    while (true)
                    {
                        var sample = reader.ReadSample(
                            (int)SourceReaderIndex.FirstAudioStream, SourceReaderControlFlags.None,
                            out _, out var flags, out _);
                        if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                        {
                            sample?.Dispose();
                            break;
                        }

                        if (firstTime < 0)
                        {
                            firstTime = sample.SampleTime;
                        }

                        var concat = prefix + (sample.SampleTime - firstTime);

                        if (concat >= clipEnd)
                        {
                            sample.Dispose();
                            reachedEnd = true;
                            break;
                        }

                        if (!started)
                        {
                            if (concat < clipStart)
                            {
                                sample.Dispose();
                                continue;
                            }

                            started = true;
                        }

                        // Align to video: video output 0 is the keyframe, which is `videoLead` before
                        // the window start; audio's window start therefore sits at `videoLead`.
                        sample.SampleTime = (concat - clipStart) + videoLead;
                        yield return new TimedSample { Time = sample.SampleTime, Sample = sample };
                    }
                }

                if (reachedEnd)
                {
                    yield break;
                }
            }
        }

        private static bool IsKeyframe(Sample sample)
        {
            try
            {
                return sample.Get(SampleAttributeKeys.CleanPoint);
            }
            catch
            {
                return false;
            }
        }

        private static long ToTicks(double seconds)
        {
            return (long)(Math.Max(0, seconds) * OneSecond100ns);
        }
    }
}
