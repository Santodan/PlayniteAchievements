using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure 16-bit PCM helpers for the clip export: mixing the recorded chime into a clip's
    /// audio. Kept free of Media Foundation so it unit-tests directly.
    /// </summary>
    internal static class PcmAudio
    {
        /// <summary>Bytes per second of the export PCM format (48 kHz, stereo, 16-bit).</summary>
        public const int BytesPerSecond = 48000 * 2 * 2;

        /// <summary>Sample-frame alignment in bytes (stereo 16-bit).</summary>
        public const int BlockAlign = 4;

        /// <summary>Converts a 100-ns tick offset to a block-aligned byte offset.</summary>
        public static long TicksToAlignedBytes(long ticks)
        {
            var bytes = (long)(ticks / 10_000_000.0 * BytesPerSecond);
            return bytes & ~(long)(BlockAlign - 1);
        }

        /// <summary>
        /// Saturating add of 16-bit little-endian source samples into the destination in place.
        /// Offsets and count are in bytes and are clamped to both buffers; odd trailing bytes are
        /// ignored (16-bit samples only move in pairs).
        /// </summary>
        public static void MixInto(byte[] dest, long destOffset, byte[] source, long sourceOffset, long byteCount)
        {
            if (dest == null || source == null || destOffset < 0 || sourceOffset < 0)
            {
                return;
            }

            var count = Math.Min(byteCount, Math.Min(dest.Length - destOffset, source.Length - sourceOffset));
            count &= ~1L;
            if (count <= 0)
            {
                return;
            }

            for (long i = 0; i + 1 < count; i += 2)
            {
                var d = (short)(dest[destOffset + i] | (dest[destOffset + i + 1] << 8));
                var s = (short)(source[sourceOffset + i] | (source[sourceOffset + i + 1] << 8));
                var mixed = d + s;
                if (mixed > short.MaxValue)
                {
                    mixed = short.MaxValue;
                }
                else if (mixed < short.MinValue)
                {
                    mixed = short.MinValue;
                }

                dest[destOffset + i] = (byte)(mixed & 0xff);
                dest[destOffset + i + 1] = (byte)((mixed >> 8) & 0xff);
            }
        }
    }
}
