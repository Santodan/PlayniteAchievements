using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Pure builders for every ffmpeg command line the recording feature runs: the rolling
    /// segment capture, the concat/trim clip export, validation probes, and the concat list
    /// file content. No process or filesystem access — fully unit-testable.
    /// </summary>
    internal static class RecordingCommandBuilder
    {
        /// <summary>Segment filename pattern written by the capture command (strftime, local time).</summary>
        public const string SegmentFilePrefix = "seg_";

        public const string SegmentFileExtension = ".ts";

        /// <summary>
        /// Audio chunk filename pattern written by <see cref="AudioLoopbackRecorder"/> into the
        /// same buffer directory (local wall-clock names, mirroring the video segments).
        /// </summary>
        public const string AudioChunkFilePrefix = "aud_";

        public const string AudioChunkFileExtension = ".wav";

        private const string SegmentStrftimePattern = "seg_%Y%m%d-%H%M%S.ts";

        public sealed class CaptureOptions
        {
            public RecordingCaptureBackend Backend { get; set; }

            public int Fps { get; set; }

            /// <summary>Monitor bounds in physical pixels (virtual-desktop coordinates for gdigrab).</summary>
            public int MonitorX { get; set; }

            public int MonitorY { get; set; }

            public int MonitorWidth { get; set; }

            public int MonitorHeight { get; set; }

            /// <summary>Zero-based output index for ddagrab (best-effort mapping from the monitor).</summary>
            public int MonitorIndex { get; set; }

            public RecordingResolution Resolution { get; set; }

            /// <summary>Encoder argument fragment from <see cref="BuildEncoderArguments"/>.</summary>
            public string EncoderArguments { get; set; }

            public int SegmentSeconds { get; set; }

            public string BufferDirectory { get; set; }
        }

        /// <summary>
        /// The rolling capture command: grabs the monitor, encodes with 1s forced keyframes (so
        /// stream-copy trims snap at most 1s early), and writes strftime-named MPEG-TS segments —
        /// a killed ffmpeg leaves every fully written segment decodable.
        /// </summary>
        public static string BuildCaptureArguments(CaptureOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var fps = Math.Max(1, options.Fps);
            // yuv420p requires even dimensions; round the capture size down to even so a native
            // capture of an odd-sized monitor region doesn't fail. The scale filter (-2) handles
            // evenness itself for the fixed resolutions.
            var width = Math.Max(2, options.MonitorWidth & ~1);
            var height = Math.Max(2, options.MonitorHeight & ~1);

            var builder = new StringBuilder();
            builder.Append("-hide_banner -loglevel warning -y");

            if (options.Backend == RecordingCaptureBackend.Ddagrab)
            {
                builder.Append(Invariant(
                    " -f lavfi -i \"ddagrab=output_idx={0}:framerate={1}:draw_mouse=1,hwdownload,format=bgra\"",
                    Math.Max(0, options.MonitorIndex),
                    fps));
            }
            else
            {
                builder.Append(Invariant(
                    " -f gdigrab -framerate {0} -offset_x {1} -offset_y {2} -video_size {3}x{4} -draw_mouse 1 -i desktop",
                    fps,
                    options.MonitorX,
                    options.MonitorY,
                    width,
                    height));
            }

            var scale = ScaleFilter(options.Resolution);
            if (scale != null)
            {
                builder.Append(" -vf ").Append(scale);
            }

            if (!string.IsNullOrWhiteSpace(options.EncoderArguments))
            {
                builder.Append(' ').Append(options.EncoderArguments.Trim());
            }

            builder.Append(" -pix_fmt yuv420p");
            builder.Append(Invariant(" -g {0} -keyint_min {0}", fps));
            builder.Append(" -force_key_frames \"expr:gte(t,n_forced*1)\"");
            builder.Append(Invariant(" -f segment -segment_time {0} -reset_timestamps 1 -strftime 1", Math.Max(1, options.SegmentSeconds)));
            builder.Append(" \"").Append(TrimTrailingSeparators(options.BufferDirectory)).Append('\\').Append(SegmentStrftimePattern).Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Resolves the configured capture backend to the one actually used. Explicit choices
        /// pass through; Auto prefers Ddagrab (Desktop Duplication draws the cursor without the
        /// on-screen flicker gdigrab's GDI BitBlt causes) when the probed ffmpeg build has the
        /// filter, else Gdigrab.
        /// </summary>
        public static RecordingCaptureBackend ResolveBackend(RecordingCaptureBackend configured, bool supportsDdagrab)
        {
            if (configured != RecordingCaptureBackend.Auto)
            {
                return configured;
            }

            return supportsDdagrab ? RecordingCaptureBackend.Ddagrab : RecordingCaptureBackend.Gdigrab;
        }

        /// <summary>
        /// Encoder argument fragment for the capture command. Auto prefers hardware encoders
        /// (NVENC &gt; QSV &gt; AMF) that appear in the probed encoder set, falling back to
        /// libx264; explicit choices are honored as-is.
        /// </summary>
        public static string BuildEncoderArguments(RecordingEncoder encoder, IReadOnlyCollection<string> availableEncoders)
        {
            switch (encoder)
            {
                case RecordingEncoder.Nvenc:
                    return NvencArguments;
                case RecordingEncoder.Qsv:
                    return QsvArguments;
                case RecordingEncoder.Amf:
                    return AmfArguments;
                case RecordingEncoder.X264:
                    return X264Arguments;
                case RecordingEncoder.Auto:
                default:
                    if (Contains(availableEncoders, "h264_nvenc"))
                    {
                        return NvencArguments;
                    }

                    if (Contains(availableEncoders, "h264_qsv"))
                    {
                        return QsvArguments;
                    }

                    if (Contains(availableEncoders, "h264_amf"))
                    {
                        return AmfArguments;
                    }

                    return X264Arguments;
            }
        }

        private const string X264Arguments = "-c:v libx264 -preset ultrafast -crf 23";
        private const string NvencArguments = "-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0";
        private const string QsvArguments = "-c:v h264_qsv -global_quality 23";
        private const string AmfArguments = "-c:v h264_amf -quality speed -rc cqp";

        /// <summary>
        /// The clip export command: concat demuxer over the selected segments, seek/trim, and an
        /// mp4 with faststart. The default stream-copy is near-free while a game runs (trim start
        /// snaps at most 1s early thanks to the forced keyframes); the re-encode variant is the
        /// retry path when the copy produces a broken or empty file. When an audio concat list is
        /// given, the loopback WAV chunks are muxed in as a second concat input with its own seek
        /// (audio re-encodes to AAC — WAV can't live in mp4 — while video stays copied);
        /// <c>-map 1:a?</c> keeps the audio optional so a chunk list that yields no usable audio
        /// stream degrades to a silent clip instead of failing the export.
        /// </summary>
        public static string BuildTrimArguments(
            string concatListPath,
            double startOffsetSeconds,
            double durationSeconds,
            string outputPath,
            bool reencode = false,
            string audioConcatListPath = null,
            double audioStartOffsetSeconds = 0,
            System.Drawing.Rectangle? crop = null,
            string cropEncoderArguments = null)
        {
            const string SoftwareEncode = "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p";

            // A crop filter is incompatible with stream copy, so cropped exports always encode:
            // with the session's (typically hardware) encoder on the first attempt and the
            // software fallback on the re-encode retry.
            var cropFilter = crop.HasValue
                ? Invariant(
                    "-filter:v \"crop={0}:{1}:{2}:{3}\" ",
                    crop.Value.Width, crop.Value.Height, crop.Value.X, crop.Value.Y)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(audioConcatListPath))
            {
                var codec = crop.HasValue
                    ? (reencode || string.IsNullOrWhiteSpace(cropEncoderArguments)
                        ? SoftwareEncode
                        : cropEncoderArguments + " -pix_fmt yuv420p")
                    : (reencode ? SoftwareEncode : "-c copy");
                return Invariant(
                    "-hide_banner -loglevel warning -y -f concat -safe 0 -ss {0} -i \"{1}\" -t {2} {3}{4} -movflags +faststart \"{5}\"",
                    Seconds(startOffsetSeconds),
                    concatListPath,
                    Seconds(durationSeconds),
                    cropFilter,
                    codec,
                    outputPath);
            }

            var videoCodec = crop.HasValue
                ? (reencode || string.IsNullOrWhiteSpace(cropEncoderArguments)
                    ? SoftwareEncode
                    : cropEncoderArguments + " -pix_fmt yuv420p")
                : (reencode ? SoftwareEncode : "-c:v copy");
            return Invariant(
                "-hide_banner -loglevel warning -y -f concat -safe 0 -ss {0} -i \"{1}\" -f concat -safe 0 -ss {2} -i \"{3}\" -t {4} -map 0:v -map 1:a? {5}{6} -c:a aac -b:a 160k -movflags +faststart \"{7}\"",
                Seconds(startOffsetSeconds),
                concatListPath,
                Seconds(audioStartOffsetSeconds),
                audioConcatListPath,
                Seconds(durationSeconds),
                cropFilter,
                videoCodec,
                outputPath);
        }

        /// <summary>
        /// Maps the game window's client rectangle (physical screen coordinates) into the
        /// captured video's pixel space so exports can be cropped to the game like screenshots
        /// are — accounting for the even-rounded capture size and the optional downscale filter.
        /// Returns null when the window effectively fills the monitor (keep stream copy) or the
        /// resulting region is degenerate.
        /// </summary>
        public static System.Drawing.Rectangle? ComputeCropRectangle(
            System.Drawing.Rectangle monitorBounds,
            System.Drawing.Rectangle clientBounds,
            RecordingResolution resolution)
        {
            if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0 ||
                clientBounds.Width <= 0 || clientBounds.Height <= 0)
            {
                return null;
            }

            var client = System.Drawing.Rectangle.Intersect(clientBounds, monitorBounds);
            if (client.Width <= 0 || client.Height <= 0)
            {
                return null;
            }

            // Borderless/fullscreen windows cover (almost) the whole monitor: no crop, so the
            // export keeps the cheap stream copy.
            var coverage = (double)client.Width * client.Height / ((double)monitorBounds.Width * monitorBounds.Height);
            if (coverage >= 0.97)
            {
                return null;
            }

            var capturedWidth = Math.Max(2, monitorBounds.Width & ~1);
            var capturedHeight = Math.Max(2, monitorBounds.Height & ~1);

            var targetHeight = resolution == RecordingResolution.P1080 ? 1080
                : resolution == RecordingResolution.P720 ? 720
                : 0;
            var scale = targetHeight > 0 && capturedHeight > targetHeight
                ? targetHeight / (double)capturedHeight
                : 1.0;
            var scaledWidth = scale < 1.0
                ? Math.Max(2, (int)Math.Round(capturedWidth * scale / 2.0) * 2)
                : capturedWidth;
            var scaledHeight = scale < 1.0 ? targetHeight : capturedHeight;

            // The crop must sit strictly INSIDE the client area: the origin rounds UP to even
            // and the far edge rounds DOWN, so no window-border pixels bleed into the clip
            // (a floor-rounded origin lets a 1px sliver of chrome show as a line at the edge).
            var left = (client.X - monitorBounds.X) * scale;
            var top = (client.Y - monitorBounds.Y) * scale;
            var right = (client.Right - monitorBounds.X) * scale;
            var bottom = (client.Bottom - monitorBounds.Y) * scale;

            var x = Math.Max(0, ((int)Math.Ceiling(left) + 1) & ~1);
            var y = Math.Max(0, ((int)Math.Ceiling(top) + 1) & ~1);
            var width = Math.Min((int)Math.Floor(right), scaledWidth) - x;
            var height = Math.Min((int)Math.Floor(bottom), scaledHeight) - y;
            width &= ~1;
            height &= ~1;
            if (width < 64 || height < 64)
            {
                return null;
            }

            return new System.Drawing.Rectangle(x, y, width, height);
        }

        /// <summary>
        /// Content of the concat demuxer list file: one absolute path per line, single-quoted
        /// with embedded quotes escaped per the demuxer's quoting rules.
        /// </summary>
        public static string BuildConcatListContent(IEnumerable<string> segmentPaths)
        {
            var builder = new StringBuilder();
            foreach (var path in segmentPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                builder.Append("file '").Append(path.Replace("'", "'\\''")).Append("'\n");
            }

            return builder.ToString();
        }

        /// <summary>
        /// A 1-second, tiny gdigrab capture to the null muxer — verifies the ffmpeg build can
        /// actually grab the screen, without writing anything.
        /// </summary>
        public static string BuildSmokeTestArguments(int offsetX = 0, int offsetY = 0)
        {
            return Invariant(
                "-hide_banner -loglevel warning -f gdigrab -framerate 10 -offset_x {0} -offset_y {1} -video_size 64x64 -i desktop -t 1 -f null -",
                offsetX,
                offsetY);
        }

        /// <summary>
        /// A 1-second ddagrab capture to the null muxer — verifies Desktop Duplication actually
        /// works on this machine (it can fail under RDP or on some hybrid-GPU setups even when
        /// the ffmpeg build has the filter).
        /// </summary>
        public static string BuildDdagrabSmokeTestArguments(int monitorIndex = 0)
        {
            return Invariant(
                "-hide_banner -loglevel warning -f lavfi -i \"ddagrab=output_idx={0}:framerate=10,hwdownload,format=bgra\" -t 1 -f null -",
                Math.Max(0, monitorIndex));
        }

        public const string VersionProbeArguments = "-hide_banner -version";

        public const string EncodersProbeArguments = "-hide_banner -encoders";

        public const string FiltersProbeArguments = "-hide_banner -filters";

        private static string ScaleFilter(RecordingResolution resolution)
        {
            switch (resolution)
            {
                case RecordingResolution.P1080:
                    return "scale=-2:1080";
                case RecordingResolution.P720:
                    return "scale=-2:720";
                default:
                    return null;
            }
        }

        private static bool Contains(IReadOnlyCollection<string> set, string encoder)
        {
            return set != null && set.Any(e => string.Equals(e?.Trim(), encoder, StringComparison.OrdinalIgnoreCase));
        }

        private static string Seconds(double value)
        {
            return Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string TrimTrailingSeparators(string directory)
        {
            return (directory ?? string.Empty).TrimEnd('\\', '/');
        }

        private static string Invariant(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }
}
