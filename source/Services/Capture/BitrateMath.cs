using System;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure H.264 bitrate selection for unlock clips: the size-versus-picture trade a user picked,
    /// applied to a frame size and rate. Shared by the live segment encoder and the export-time
    /// overlay re-encoder, so compositing the toast cannot quietly change a clip's bitrate.
    ///
    /// Native is ~0.12 bits per pixel per frame — about 15 Mbps at 1080p60, 27 at 1440p60, 60 at
    /// 4K60 — and the lower tiers scale that down proportionally. Lowering it shrinks clips and,
    /// because the capture buffer is a fixed number of bytes, lets it reach further back in time.
    /// </summary>
    internal static class BitrateMath
    {
        /// <summary>Bits per pixel per frame at the Native tier, the reference the others scale from.</summary>
        public const double NativeBitsPerPixel = 0.12;

        private const long NativeFloorBits = 8_000_000L;
        private const long NativeCeilingBits = 120_000_000L;

        /// <summary>Bits per pixel per frame a quality tier asks for.</summary>
        public static double BitsPerPixelFor(RecordingQuality quality)
        {
            switch (quality)
            {
                case RecordingQuality.Low: return 0.05;
                case RecordingQuality.Medium: return 0.08;
                case RecordingQuality.High: return 0.10;
                default: return NativeBitsPerPixel;
            }
        }

        /// <summary>
        /// Target bitrate in bits per second for a frame size, rate and quality tier.
        ///
        /// The floor and ceiling scale with the tier rather than staying fixed. A fixed floor would
        /// collapse the tiers wherever the computed rate falls beneath it — at 1080p30 and below,
        /// which is most captures, every tier would land on the same 8 Mbps — leaving a setting
        /// that visibly does nothing for the people most likely to want smaller files.
        /// </summary>
        public static int Compute(int width, int height, int fps, RecordingQuality quality)
        {
            if (width <= 0 || height <= 0 || fps <= 0)
            {
                return (int)NativeFloorBits;
            }

            var bitsPerPixel = BitsPerPixelFor(quality);
            var scale = bitsPerPixel / NativeBitsPerPixel;
            var bits = (long)(width * (double)height * fps * bitsPerPixel);
            var floor = (long)(NativeFloorBits * scale);
            var ceiling = (long)(NativeCeilingBits * scale);
            return (int)Math.Max(floor, Math.Min(ceiling, bits));
        }
    }
}
