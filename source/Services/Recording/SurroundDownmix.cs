using System;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Folds a multichannel float capture to stereo float. The layouts are the standard speaker
    /// masks <see cref="ProcessLoopbackCapture.SpeakerMaskFor"/> requests: 4 = FL FR BL BR,
    /// 6 = FL FR C LFE BL BR, 8 = FL FR C LFE BL BR SL SR. Front L/R pass at unity; every other
    /// channel folds into its side at -3 dB, the usual two-channel downmix. With
    /// <paramref name="dropBackChannels"/> the back pair (BL/BR) is discarded instead: a DualSense's
    /// actuators arrive there by speaker position, so the haptics never reach the clip, at the
    /// cost of a surround system's own rear pair while a controller is connected.
    /// </summary>
    internal static class SurroundDownmix
    {
        private const float FoldGain = 0.70710678f;

        /// <summary>
        /// Positions of the back-left and back-right channels for each supported channel count.
        /// </summary>
        internal static bool TryGetBackPair(int channels, out int backLeft, out int backRight)
        {
            switch (channels)
            {
                case 4:
                    backLeft = 2;
                    backRight = 3;
                    return true;
                case 6:
                case 8:
                    backLeft = 4;
                    backRight = 5;
                    return true;
                default:
                    backLeft = backRight = -1;
                    return false;
            }
        }

        /// <summary>
        /// Interleaved float32 <paramref name="source"/> with <paramref name="channels"/> channels to
        /// interleaved stereo float32. Null for an unsupported channel count.
        /// </summary>
        public static byte[] ToStereo(byte[] source, int bytes, int channels, bool dropBackChannels)
        {
            if (source == null || !TryGetBackPair(channels, out var backLeft, out var backRight))
            {
                return null;
            }

            var inputBlock = channels * sizeof(float);
            const int outputBlock = 2 * sizeof(float);
            var frames = Math.Min(Math.Max(0, bytes), source.Length) / inputBlock;
            var output = new byte[checked(frames * outputBlock)];
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = frame * inputBlock;
                var left = BitConverter.ToSingle(source, offset);
                var right = BitConverter.ToSingle(source, offset + 4);
                for (var channel = 2; channel < channels; channel++)
                {
                    if (dropBackChannels && (channel == backLeft || channel == backRight))
                    {
                        continue;
                    }

                    var value = FoldGain * BitConverter.ToSingle(source, offset + channel * 4);
                    if (channel == 2 && channels > 4 || channel == 3 && channels > 4)
                    {
                        // Center and LFE have no side: both halves receive them.
                        left += value;
                        right += value;
                    }
                    else if ((channel & 1) == 0)
                    {
                        left += value;
                    }
                    else
                    {
                        right += value;
                    }
                }

                WriteClamped(output, frame * outputBlock, left);
                WriteClamped(output, frame * outputBlock + 4, right);
            }

            return output;
        }

        private static void WriteClamped(byte[] output, int offset, float value)
        {
            var clamped = value > 1f ? 1f : value < -1f ? -1f : value;
            var bits = BitConverter.GetBytes(clamped);
            Buffer.BlockCopy(bits, 0, output, offset, 4);
        }
    }
}
