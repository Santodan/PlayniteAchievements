using System;
using System.Collections.Generic;
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
        /// Video segment filenames: seg_yyyyMMdd-HHmmssfffZ_WxH.mp4 (H.264 written by WGC + Media
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

        /// <summary>UTC form written by current recorders; the Z also distinguishes it from legacy local stamps.</summary>
        public const string UtcStampFormat = "yyyyMMdd-HHmmssfff'Z'";

        public const int UtcStampLength = 19;

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

        /// <summary>Audio chunk filenames: aud_yyyyMMdd-HHmmssfffZ.wav (WASAPI loopback PCM).</summary>
        public const string AudioChunkFilePrefix = "aud_";

        /// <summary>
        /// Chime chunk filenames: chm_yyyyMMdd-HHmmssfffZ.wav — the Playnite process-tree
        /// sidecar. When the game is also in that tree, its matching game-reference window is
        /// cancelled from this track before the isolated chime is re-timed to the toast.
        /// </summary>
        public const string ChimeChunkFilePrefix = "chm_";

        /// <summary>
        /// Game-reference chunk filenames: gam_yyyyMMdd-HHmmssfffZ.wav. Capture uses this only when
        /// Playnite's process tree contains the game, providing the raw game-only signal that must
        /// be removed from the overlapping chime sidecar.
        /// </summary>
        public const string GameReferenceChunkFilePrefix = "gam_";

        /// <summary>
        /// How many controller endpoints can be captured as separate references.
        /// </summary>
        public const int MaxHapticReferences = 4;

        /// <summary>
        /// Haptic-reference chunk filenames: hap0_yyyyMMdd-HHmmssfffZ.wav — everything rendered to
        /// one controller's own audio endpoint. Process loopback mixes every endpoint the game
        /// renders to, so this is the copy of its haptic waveform that the clip's audio is cleaned
        /// against. Written only while such an endpoint exists.
        /// <para>
        /// One track per endpoint, never a mix of them: cancellation fits a single gain and lag per
        /// reference, so two endpoints summed into one track cannot both be removed. They are
        /// subtracted one after another instead.
        /// </para>
        /// </summary>
        public static string HapticReferenceChunkFilePrefix(int index)
        {
            return "hap" + index.ToString(CultureInfo.InvariantCulture) + "_";
        }

        /// <summary>Every haptic-reference prefix, for buffer maintenance.</summary>
        public static IEnumerable<string> HapticReferenceChunkFilePrefixes()
        {
            for (var index = 0; index < MaxHapticReferences; index++)
            {
                yield return HapticReferenceChunkFilePrefix(index);
            }
        }

        public const string AudioChunkFileExtension = ".wav";
    }
}
