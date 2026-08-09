using System;
using System.Globalization;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Filename conventions for the rolling capture buffer, shared by the writers
    /// (<see cref="WgcVideoRecorder"/>, <see cref="AudioLoopbackRecorder"/>) and the readers
    /// (<see cref="SegmentTimeline"/>, the clip exporter). Both video and audio chunks are named
    /// by local wall-clock time (yyyyMMdd-HHmmss) so the timeline can order and window them.
    /// </summary>
    internal static class RecordingPaths
    {
        /// <summary>
        /// Video segment filenames: seg_yyyyMMdd-HHmmss_WxH.mp4 (H.264 written by WGC + Media
        /// Foundation). The encoded dimensions are part of the name so the timeline can group
        /// segments by size without opening any of them: a clip is stream-copied against one
        /// declared media type, so all of its segments must share dimensions.
        /// </summary>
        public const string SegmentFilePrefix = "seg_";

        public const string SegmentFileExtension = ".mp4";

        /// <summary>Separates the wall-clock stamp from the WxH dimension token.</summary>
        public const char DimensionSeparator = '_';

        /// <summary>
        /// Wall-clock stamp every buffer file name carries. Milliseconds matter: the exporter trims
        /// each stream by the offset from its file's stamp to the window start, while the samples
        /// inside are timed from the file's true beginning. A stamp rounded to the second therefore
        /// shifts that stream by up to a second, and because video segments and audio chunks roll
        /// at unrelated instants their roundings differ — which lands as audio drifting against
        /// picture by whatever the two errors differ by.
        /// </summary>
        public const string StampFormat = "yyyyMMdd-HHmmssfff";

        /// <summary>Length of <see cref="StampFormat"/>, and of the second-resolution stamp before it.</summary>
        public const int StampLength = 18;

        /// <summary>Legacy second-resolution stamp length, still parsed for buffers written earlier.</summary>
        public const int LegacyStampLength = 15;

        /// <summary>The segment file name for a capture of the given size started at a local time.</summary>
        public static string BuildSegmentFileName(DateTime localStart, int width, int height)
        {
            return SegmentFilePrefix +
                localStart.ToString(StampFormat, CultureInfo.InvariantCulture) +
                DimensionSeparator +
                width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture) +
                SegmentFileExtension;
        }

        /// <summary>The audio chunk file name for <paramref name="prefix"/> started at a local time.</summary>
        public static string BuildAudioChunkFileName(string prefix, DateTime localStart)
        {
            return prefix +
                localStart.ToString(StampFormat, CultureInfo.InvariantCulture) +
                AudioChunkFileExtension;
        }

        /// <summary>Audio chunk filenames: aud_yyyyMMdd-HHmmss.wav (WASAPI loopback PCM).</summary>
        public const string AudioChunkFilePrefix = "aud_";

        /// <summary>
        /// Chime chunk filenames: chm_yyyyMMdd-HHmmss.wav — the Playnite-only sidecar audio
        /// track. The main track excludes Playnite's process tree, so unlock chimes live only
        /// here; the clip re-encode mixes this wave's chime back in aligned with the composited
        /// toast.
        /// </summary>
        public const string ChimeChunkFilePrefix = "chm_";

        public const string AudioChunkFileExtension = ".wav";
    }
}
