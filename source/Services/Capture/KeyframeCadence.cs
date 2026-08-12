using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// How densely H.264 keyframes are written into recorded clips.
    ///
    /// This sets the seek precision of the finished clip rather than being an encoder detail: WPF's
    /// MediaElement seeks to the nearest keyframe at or before the requested position, so the
    /// capture viewer's timeline can only land within one keyframe interval of where the user
    /// clicked. It also bounds the export's trim snap-back, since the clip is stream-copied from the
    /// nearest keyframe at or before the window start.
    ///
    /// Measured over 60fps capture clips: an I-frame costs about 4x a P-frame, but I-frames are only
    /// ~6.5% of the video payload at one per second, so quartering the interval costs ~15% of the
    /// payload at constant quality — and nothing in file size, because both encoders are driven by
    /// an average bitrate and simply redistribute the bits.
    /// </summary>
    internal static class KeyframeCadence
    {
        /// <summary>
        /// Keyframes per second of video, so seeks land within 1/this of the requested position.
        /// </summary>
        public const int PerSecond = 4;

        /// <summary>
        /// Max frames between keyframes, for MF_MT_MAX_KEYFRAME_SPACING. Never below 1: a capture
        /// frame rate under <see cref="PerSecond"/> would otherwise ask for a spacing of zero.
        /// </summary>
        public static int MaxSpacingFrames(int fps) => Math.Max(1, fps / PerSecond);
    }
}
