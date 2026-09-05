using System;
using System.IO;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure 16-bit PCM helpers for the clip export: mixing the composited chime into a clip's
    /// audio, fading a cut tail, writing a processed window back as a WAV chunk, and telling a
    /// silent window from one that carries signal. Kept free of Media Foundation so it unit-tests
    /// directly.
    /// </summary>
    internal static class PcmAudio
    {
        /// <summary>Sample rate of the export PCM format.</summary>
        public const int SampleRate = 48000;

        /// <summary>Channel count of the export PCM format.</summary>
        public const int Channels = 2;

        /// <summary>Sample depth of the export PCM format.</summary>
        public const int BitsPerSample = 16;

        /// <summary>Bytes per second of the export PCM format (48 kHz, stereo, 16-bit).</summary>
        public const int BytesPerSecond = SampleRate * Channels * BitsPerSample / 8;

        /// <summary>Sample-frame alignment in bytes (stereo 16-bit).</summary>
        public const int BlockAlign = 4;

        /// <summary>
        /// Writes a buffer of this format's PCM as a RIFF/WAVE file, so a processed audio window can
        /// be handed back to the export pipeline as an ordinary chunk. Keeping the exporter on files
        /// is what leaves its planning and A/V alignment untouched.
        /// </summary>
        public static void WriteWav(string path, byte[] pcm)
        {
            if (string.IsNullOrEmpty(path) || pcm == null)
            {
                throw new ArgumentNullException(pcm == null ? nameof(pcm) : nameof(path));
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Tag("RIFF"));
                writer.Write(36 + pcm.Length);
                writer.Write(Tag("WAVE"));

                writer.Write(Tag("fmt "));
                writer.Write(16);                                   // PCM fmt chunk size
                writer.Write((short)1);                             // WAVE_FORMAT_PCM
                writer.Write((short)Channels);
                writer.Write(SampleRate);
                writer.Write(BytesPerSecond);
                writer.Write((short)BlockAlign);
                writer.Write((short)BitsPerSample);

                writer.Write(Tag("data"));
                writer.Write(pcm.Length);
                writer.Write(pcm);
            }
        }

        /// <summary>A RIFF four-character chunk id. Written as bytes, never through an encoding.</summary>
        private static byte[] Tag(string fourCc)
        {
            var bytes = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                bytes[i] = (byte)fourCc[i];
            }

            return bytes;
        }

        /// <summary>Converts a 100-ns tick offset to a block-aligned byte offset.</summary>
        public static long TicksToAlignedBytes(long ticks)
        {
            if (ticks <= 0)
            {
                return 0;
            }

            // Convert to the nearest sample frame, not first to a truncated byte count. One 48 kHz
            // frame is 208.333 DateTime ticks; truncating 208 ticks to three bytes and aligning down
            // moved a timestamp that represents frame 1 back onto frame 0.
            var wholeSeconds = ticks / TimeSpan.TicksPerSecond;
            var remainder = ticks % TimeSpan.TicksPerSecond;
            var frames = checked(
                wholeSeconds * SampleRate +
                (remainder * SampleRate + TimeSpan.TicksPerSecond / 2) /
                    TimeSpan.TicksPerSecond);
            return checked(frames * BlockAlign);
        }

        /// <summary>
        /// Applies a linear fade-out over the final <paramref name="seconds"/> of a 16-bit PCM
        /// buffer in place, so a chime cut mid-ring ends silently instead of clicking.
        /// </summary>
        public static void FadeOutTail(byte[] pcm, double seconds)
        {
            if (pcm == null || pcm.Length < BlockAlign || seconds <= 0)
            {
                return;
            }

            var fadeBytes = Math.Min((long)pcm.Length & ~(long)(BlockAlign - 1), TicksToAlignedBytes((long)(seconds * 10_000_000)));
            if (fadeBytes < BlockAlign)
            {
                return;
            }

            var start = pcm.Length - fadeBytes;
            for (long i = start; i + 1 < pcm.Length; i += 2)
            {
                var scale = 1.0 - ((i - start) / (double)fadeBytes);
                var value = (short)(pcm[i] | (pcm[i + 1] << 8));
                var faded = (short)(value * scale);
                pcm[i] = (byte)(faded & 0xff);
                pcm[i + 1] = (byte)((faded >> 8) & 0xff);
            }
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

        /// <summary>
        /// Whether a 16-bit PCM buffer carries no audible signal: its RMS sits below
        /// <paramref name="thresholdDbfs"/> (default -60 dBFS, about 33 on the 16-bit scale). A
        /// Game Only clip track that records only the game tree is silent when the game rendered
        /// nothing or rendered outside the tracked tree; the export then falls back to the wider
        /// track rather than shipping silence.
        /// </summary>
        public static bool IsSilent(byte[] pcm, double thresholdDbfs = -60.0)
        {
            if (pcm == null || pcm.Length < 2)
            {
                return true;
            }

            double energy = 0;
            long samples = 0;
            for (long i = 0; i + 1 < pcm.Length; i += 2)
            {
                double value = (short)(pcm[i] | (pcm[i + 1] << 8));
                energy += value * value;
                samples++;
            }

            var rms = Math.Sqrt(energy / Math.Max(1, samples));
            var threshold = short.MaxValue * Math.Pow(10.0, thresholdDbfs / 20.0);
            return rms < threshold;
        }
    }
}
