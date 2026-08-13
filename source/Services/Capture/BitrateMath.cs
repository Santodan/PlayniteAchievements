using System;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure H.264 bitrate selection for unlock clips: the size-versus-picture trade a user picked,
    /// applied to a frame size and rate.
    ///
    /// Native is ~0.12 bits per pixel per frame — about 15 Mbps at 1080p60, 27 at 1440p60, 60 at
    /// 4K60 — and the lower tiers scale that down proportionally. Lowering it shrinks clips and,
    /// because the capture buffer is a fixed number of bytes, lets it reach further back in time.
    ///
    /// <see cref="Compute"/> is what capture records at. The export-time re-encode that composites the
    /// toast asks for <see cref="ComputeReencode"/> instead, which is deliberately higher: see the
    /// reasoning there. Both derive from the tier the user chose, so the tier still decides how the
    /// finished clip looks — it just costs more bits to look that way the second time around.
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

        /// <summary>
        /// How much more bitrate the export-time re-encode gets than the capture it re-encodes.
        /// </summary>
        public const double ReencodeHeadroom = 1.5;

        /// <summary>
        /// Target bitrate for the re-encode that composites the toast into a finished clip.
        ///
        /// Deliberately above <see cref="Compute"/>. That pass decodes footage the capture encoder
        /// already quantised and encodes it again, so at the same bitrate it cannot reproduce what it
        /// was handed: the second encoder spends bits describing the first one's blocking and ringing as
        /// though they were detail, and the clip comes out softer than the tier promises. Extra headroom
        /// spends bits to keep the finished clip looking like the tier the user picked rather than like
        /// a copy of it. The tier's own ceiling still applies, so a bump can never exceed it.
        /// </summary>
        public static int ComputeReencode(int width, int height, int fps, RecordingQuality quality)
        {
            var captured = Compute(width, height, fps, quality);
            var ceiling = (long)(NativeCeilingBits * (BitsPerPixelFor(quality) / NativeBitsPerPixel));
            return (int)Math.Min(ceiling, (long)(captured * ReencodeHeadroom));
        }
    }
}
