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
        /// <summary>Video segment filenames: seg_yyyyMMdd-HHmmss.mp4 (H.264 written by WGC + Media Foundation).</summary>
        public const string SegmentFilePrefix = "seg_";

        public const string SegmentFileExtension = ".mp4";

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
