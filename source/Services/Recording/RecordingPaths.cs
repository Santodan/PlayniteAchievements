using System;
using System.Globalization;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Filename conventions for the rolling capture buffer, shared by the writers
    /// (<see cref="WgcVideoRecorder"/>, <see cref="AudioLoopbackRecorder"/>) and the readers
    /// (<see cref="SegmentTimeline"/>, the clip exporter). Both video and audio chunks are named
    /// by UTC timeline time so the timeline can order and window them without local-time or DST
    /// ambiguity. The parser still accepts the older local-wall-clock names.
    /// </summary>
    internal static class RecordingPaths
    {
        /// <summary>
        /// Video segment filenames: seg_yyyyMMdd-HHmmssfffffffZ_WxH.mp4 (H.264 written by WGC + Media
        /// Foundation). The encoded dimensions are part of the name so the timeline can group
        /// segments by size without opening any of them: a clip is stream-copied against one
        /// declared media type, so all of its segments must share dimensions.
        /// </summary>
        public const string SegmentFilePrefix = "seg_";

        public const string SegmentFileExtension = ".mp4";

        /// <summary>Separates the wall-clock stamp from the WxH dimension token.</summary>
        public const char DimensionSeparator = '_';

        /// <summary>
        /// Legacy local wall-clock stamp. Milliseconds matter: the exporter trims
        /// each stream by the offset from its file's stamp to the window start, while the samples
        /// inside are timed from the file's true beginning. A stamp rounded to the second therefore
        /// shifts that stream by up to a second, and because video segments and audio chunks roll
        /// at unrelated instants their roundings differ — which lands as audio drifting against
        /// picture by whatever the two errors differ by.
        /// </summary>
        public const string StampFormat = "yyyyMMdd-HHmmssfff";

        /// <summary>
        /// UTC form written by current recorders. Seven fractional digits preserve every DateTime
        /// tick (100 ns), so a filename round-trip cannot move an audio chunk by even one sample.
        /// The Z also distinguishes it from legacy local stamps.
        /// </summary>
        public const string UtcStampFormat = "yyyyMMdd-HHmmssfffffff'Z'";

        public const int UtcStampLength = 23;

        /// <summary>UTC millisecond form written before tick-precise names were introduced.</summary>
        public const string LegacyUtcStampFormat = "yyyyMMdd-HHmmssfff'Z'";

        public const int LegacyUtcStampLength = 19;

        /// <summary>Length of <see cref="StampFormat"/>, and of the second-resolution stamp before it.</summary>
        public const int StampLength = 18;

        /// <summary>Legacy second-resolution stamp length, still parsed for buffers written earlier.</summary>
        public const int LegacyStampLength = 15;

        /// <summary>The segment file name for a capture of the given size started at a UTC timeline time.</summary>
        public static string BuildSegmentFileName(DateTime utcStart, int width, int height)
        {
            return SegmentFilePrefix +
                AsUtc(utcStart).ToString(UtcStampFormat, CultureInfo.InvariantCulture) +
                DimensionSeparator +
                width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture) +
                SegmentFileExtension;
        }

        /// <summary>The audio chunk file name for <paramref name="prefix"/> started at a UTC timeline time.</summary>
        public static string BuildAudioChunkFileName(string prefix, DateTime utcStart)
        {
            return prefix +
                AsUtc(utcStart).ToString(UtcStampFormat, CultureInfo.InvariantCulture) +
                AudioChunkFileExtension;
        }

        /// <summary>
        /// Converts one position on an audio timeline to its nearest sample frame. All independently
        /// captured tracks use this same conversion before writing a timestamped packet.
        /// </summary>
        public static long AudioFrameAt(DateTime originUtc, DateTime sampleUtc, int sampleRate)
        {
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            var ticks = (sampleUtc - originUtc).Ticks;
            var wholeSeconds = ticks / TimeSpan.TicksPerSecond;
            var remainder = ticks % TimeSpan.TicksPerSecond;
            var remainderFrames = remainder >= 0
                ? (remainder * sampleRate + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond
                : -((-remainder * sampleRate + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond);
            return checked(wholeSeconds * sampleRate + remainderFrames);
        }

        /// <summary>
        /// Converts a sample-frame position back to the nearest representable UTC tick. Combined
        /// with <see cref="AudioFrameAt"/>, this keeps chunk names and PCM offsets on one grid.
        /// </summary>
        public static DateTime AudioFrameUtc(DateTime originUtc, long frame, int sampleRate)
        {
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            var wholeSeconds = frame / sampleRate;
            var remainder = frame % sampleRate;
            var remainderTicks = remainder >= 0
                ? (remainder * TimeSpan.TicksPerSecond + sampleRate / 2) / sampleRate
                : -((-remainder * TimeSpan.TicksPerSecond + sampleRate / 2) / sampleRate);
            return originUtc.AddTicks(checked(
                wholeSeconds * TimeSpan.TicksPerSecond + remainderTicks));
        }

        private static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        /// <summary>Audio chunk filenames: aud_yyyyMMdd-HHmmssfffffffZ.wav (WASAPI loopback PCM).</summary>
        public const string AudioChunkFilePrefix = "aud_";

        /// <summary>
        /// Fallback chunk filenames: alt_yyyyMMdd-HHmmssfffffffZ.wav. Game Only records the game's
        /// process tree as its clip track and this exclude-sound-host track beside it; a clip whose
        /// game-tree window is silent (the game renders outside its tracked tree) is exported from
        /// this track instead, so it carries the game rather than nothing.
        /// </summary>
        public const string FallbackChunkFilePrefix = "alt_";

        public const string AudioChunkFileExtension = ".wav";
    }
}
