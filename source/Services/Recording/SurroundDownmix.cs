using System;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Folds a multichannel float capture to stereo. The layouts are the standard speaker masks
    /// <see cref="ProcessLoopbackCapture.SpeakerMaskFor"/> requests: 4 = FL FR BL BR,
    /// 6 = FL FR C LFE BL BR, 8 = FL FR C LFE BL BR SL SR. Front L/R pass at unity; every other
    /// channel folds into its side at -3 dB, the usual two-channel downmix. With
    /// <c>dropBackChannels</c> the back pair (BL/BR) is discarded instead: a DualSense's actuators
    /// arrive there by speaker position, so the haptics never reach the clip, at the cost of a
    /// surround system's own rear pair while a controller is connected.
    /// <para>
    /// The static entry points allocate their result and exist for tests; the recorder uses one
    /// <see cref="SurroundDownmixer"/> per capture, which reuses its buffers so a 100-packets-a-second
    /// stream produces no garbage at all.
    /// </para>
    /// </summary>
    internal static class SurroundDownmix
    {
        internal const float FoldGain = 0.70710678f;

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

        /// <summary>Interleaved float32 in, interleaved stereo float32 out. Null for an unsupported channel count.</summary>
        public static byte[] ToStereo(byte[] source, int bytes, int channels, bool dropBackChannels)
        {
            var downmixer = new SurroundDownmixer();
            var count = downmixer.ToStereoFloat(source, bytes, channels, dropBackChannels, out var output);
            return Trim(output, count, channels);
        }

        /// <summary>Interleaved float32 in, interleaved stereo 16-bit PCM out. Null for an unsupported channel count.</summary>
        public static byte[] ToStereoPcm16(byte[] source, int bytes, int channels, bool dropBackChannels)
        {
            var downmixer = new SurroundDownmixer();
            var count = downmixer.ToStereoPcm16(source, bytes, channels, dropBackChannels, out var output);
            return Trim(output, count, channels);
        }

        private static byte[] Trim(byte[] output, int count, int channels)
        {
            if (count < 0)
            {
                return null;
            }

            var result = new byte[count];
            Buffer.BlockCopy(output, 0, result, 0, count);
            return result;
        }

        /// <summary>
        /// Folds <paramref name="frames"/> frames of <paramref name="channels"/>-channel float samples
        /// into <paramref name="stereo"/> (two floats per frame, unclamped).
        /// </summary>
        internal static void Fold(float[] input, int frames, int channels, bool dropBackChannels, float[] stereo)
        {
            TryGetBackPair(channels, out var backLeft, out var backRight);
            var hasCenterPair = channels > 4;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = frame * channels;
                var left = input[offset];
                var right = input[offset + 1];
                for (var channel = 2; channel < channels; channel++)
                {
                    if (dropBackChannels && (channel == backLeft || channel == backRight))
                    {
                        continue;
                    }

                    var value = FoldGain * input[offset + channel];
                    if (hasCenterPair && (channel == 2 || channel == 3))
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

                stereo[frame * 2] = left;
                stereo[frame * 2 + 1] = right;
            }
        }
    }

    /// <summary>
    /// A reusable fold for one capture: its scratch and output buffers grow to the largest packet
    /// seen and are then reused, so the per-packet cost is two block copies and the arithmetic.
    /// Not thread-safe; give every capture its own instance.
    /// </summary>
    internal sealed class SurroundDownmixer
    {
        private float[] _input = new float[0];
        private float[] _stereo = new float[0];
        private byte[] _output = new byte[0];

        /// <summary>
        /// Folds to stereo float32. Returns the byte count written to <paramref name="output"/>,
        /// which is this instance's buffer and is only valid until the next call; -1 for an
        /// unsupported channel count.
        /// </summary>
        public int ToStereoFloat(byte[] source, int bytes, int channels, bool dropBackChannels, out byte[] output)
        {
            var frames = Prepare(source, bytes, channels, dropBackChannels, out output);
            if (frames < 0)
            {
                return -1;
            }

            var count = frames * 2 * sizeof(float);
            Ensure(ref _output, count);
            for (var i = 0; i < frames * 2; i++)
            {
                var value = _stereo[i];
                _stereo[i] = value > 1f ? 1f : value < -1f ? -1f : value;
            }

            Buffer.BlockCopy(_stereo, 0, _output, 0, count);
            output = _output;
            return count;
        }

        /// <summary>
        /// Folds to stereo 16-bit PCM, the format the clip tracks are written in. Same buffer
        /// contract as <see cref="ToStereoFloat"/>.
        /// </summary>
        public int ToStereoPcm16(byte[] source, int bytes, int channels, bool dropBackChannels, out byte[] output)
        {
            var frames = Prepare(source, bytes, channels, dropBackChannels, out output);
            if (frames < 0)
            {
                return -1;
            }

            var count = frames * 2 * sizeof(short);
            Ensure(ref _output, count);
            for (var i = 0; i < frames * 2; i++)
            {
                var value = _stereo[i];
                var clamped = value > 1f ? 1f : value < -1f ? -1f : value;
                var sample = (short)Math.Round(clamped * short.MaxValue);
                _output[i * 2] = (byte)(sample & 0xff);
                _output[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }

            output = _output;
            return count;
        }

        private int Prepare(byte[] source, int bytes, int channels, bool dropBackChannels, out byte[] output)
        {
            output = null;
            if (source == null || !SurroundDownmix.TryGetBackPair(channels, out _, out _))
            {
                return -1;
            }

            var inputBlock = channels * sizeof(float);
            var frames = Math.Min(Math.Max(0, bytes), source.Length) / inputBlock;
            Ensure(ref _input, frames * channels);
            Ensure(ref _stereo, frames * 2);
            Buffer.BlockCopy(source, 0, _input, 0, frames * inputBlock);
            SurroundDownmix.Fold(_input, frames, channels, dropBackChannels, _stereo);
            return frames;
        }

        private static void Ensure<T>(ref T[] buffer, int length)
        {
            if (buffer.Length < length)
            {
                buffer = new T[Math.Max(length, buffer.Length * 2)];
            }
        }
    }
}
