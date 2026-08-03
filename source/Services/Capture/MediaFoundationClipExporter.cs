using System;
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
        /// Returns false (with one log line) on any failure so the caller can drop the clip cleanly.
        /// </summary>
        public bool Export(
            SegmentTimeline.ClipPlan videoPlan, SegmentTimeline.ClipPlan audioPlan, string outputPath)
        {
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
                    sink = MediaFactory.CreateSinkWriterFromURL(outputPath, null, null);

                    var videoStream = AddVideoStream(sink, videoPlan.Segments[0].Path);
                    var audioStream = -1;
                    MediaType pcmType = null;
                    if (audioPlan?.Segments != null && audioPlan.Segments.Count > 0)
                    {
                        audioStream = TryAddAudioStream(sink, out pcmType);
                    }

                    sink.BeginWriting();

                    var clipStart = ToTicks(videoPlan.StartOffsetSeconds);
                    var clipEnd = clipStart + ToTicks(videoPlan.DurationSeconds);
                    var keyframeStart = FindKeyframeStart(videoPlan.Segments[0].Path, clipStart);
                    var videoLead = clipStart - keyframeStart; // ≥ 0

                    WriteVideo(sink, videoStream, videoPlan, keyframeStart, clipEnd);

                    if (audioStream >= 0)
                    {
                        try
                        {
                            WriteAudio(sink, audioStream, pcmType, audioPlan, videoLead);
                        }
                        catch (Exception ex)
                        {
                            // Audio is best-effort: a mux failure must not lose the (already written) video.
                            _logger?.Debug(ex, "[Recording] Clip audio mux failed; clip will be video-only.");
                        }
                    }

                    pcmType?.Dispose();
                    sink.Finalize();
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

        private int TryAddAudioStream(SinkWriter sink, out MediaType pcmType)
        {
            pcmType = null;
            try
            {
                using (var aacType = new MediaType())
                {
                    aacType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    aacType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    aacType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioSampleRate);
                    aacType.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
                    aacType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBitsPerSample);
                    aacType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AudioBytesPerAacSecond);
                    sink.AddStream(aacType, out var streamIndex);

                    pcmType = new MediaType();
                    pcmType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    pcmType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    pcmType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioSampleRate);
                    pcmType.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
                    pcmType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBitsPerSample);
                    pcmType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, AudioChannels * AudioBitsPerSample / 8);
                    pcmType.Set(
                        MediaTypeAttributeKeys.AudioAvgBytesPerSecond,
                        AudioSampleRate * AudioChannels * AudioBitsPerSample / 8);
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

        private static void WriteVideo(
            SinkWriter sink, int streamIndex, SegmentTimeline.ClipPlan plan, long keyframeStart, long clipEnd)
        {
            long prefix = 0;      // concat time at the start of the current segment
            var started = false;

            foreach (var segment in plan.Segments)
            {
                long firstTime = -1;
                long segSpanEnd = 0;
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
                        segSpanEnd = concat + Math.Max(0, sample.SampleDuration);

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

                        sample.SampleTime = concat - keyframeStart;
                        sink.WriteSample(streamIndex, sample);
                        sample.Dispose();
                    }
                }

                if (reachedEnd)
                {
                    break;
                }

                prefix = segSpanEnd;
            }
        }

        private static void WriteAudio(
            SinkWriter sink, int streamIndex, MediaType pcmType, SegmentTimeline.ClipPlan plan, long videoLead)
        {
            var clipStart = ToTicks(plan.StartOffsetSeconds);
            var clipEnd = clipStart + ToTicks(plan.DurationSeconds);
            long prefix = 0;
            var started = false;

            foreach (var chunk in plan.Segments)
            {
                long firstTime = -1;
                long segSpanEnd = 0;
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
                        segSpanEnd = concat + Math.Max(0, sample.SampleDuration);

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
                        sink.WriteSample(streamIndex, sample);
                        sample.Dispose();
                    }
                }

                if (reachedEnd)
                {
                    break;
                }

                prefix = segSpanEnd;
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
