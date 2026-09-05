using System;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Decodes the resolved unlock-chime sound file into the clip pipeline's PCM format
    /// (48 kHz stereo 16-bit) so export can mix the exact source waveform at the composited toast
    /// — no captured copy, no separation. Any failure returns null and the caller falls back to
    /// the capture-based chime path.
    /// </summary>
    internal static class ChimeSoundFile
    {
        private const int SampleRate = 48000;

        /// <summary>
        /// Reads up to <paramref name="maxSeconds"/> of the file, resampled to 48 kHz stereo and
        /// scaled by <paramref name="gain"/>. Returns null when the file cannot be decoded or has
        /// a channel layout the mixer does not handle (more than two channels).
        /// </summary>
        public static byte[] TryReadPcm(string path, double maxSeconds, double gain, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(path) || maxSeconds <= 0)
            {
                return null;
            }

            try
            {
                MediaFoundationApi.Startup();
                using (var reader = new MediaFoundationReader(path))
                {
                    var samples = reader.ToSampleProvider();
                    if (samples.WaveFormat.SampleRate != SampleRate)
                    {
                        samples = new WdlResamplingSampleProvider(samples, SampleRate);
                    }

                    if (samples.WaveFormat.Channels == 1)
                    {
                        samples = new MonoToStereoSampleProvider(samples);
                    }
                    else if (samples.WaveFormat.Channels != 2)
                    {
                        logger?.Debug(
                            $"Chime sound file has {samples.WaveFormat.Channels} channels; " +
                            "using the capture-based chime instead.");
                        return null;
                    }

                    var maxFrames = (long)(maxSeconds * SampleRate);
                    var buffer = new float[SampleRate * 2];
                    var pcm = new System.IO.MemoryStream();
                    long framesWritten = 0;
                    while (framesWritten < maxFrames)
                    {
                        var wanted = (int)Math.Min(buffer.Length, (maxFrames - framesWritten) * 2);
                        var read = samples.Read(buffer, 0, wanted);
                        if (read <= 0)
                        {
                            break;
                        }

                        for (var i = 0; i < read; i++)
                        {
                            var value = (int)Math.Round(
                                Math.Max(-1.0, Math.Min(1.0, buffer[i] * gain)) * short.MaxValue);
                            pcm.WriteByte((byte)(value & 0xff));
                            pcm.WriteByte((byte)((value >> 8) & 0xff));
                        }

                        framesWritten += read / 2;
                    }

                    return pcm.Length >= 4 ? pcm.ToArray() : null;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Chime sound file could not be decoded: '{path}'.");
                return null;
            }
        }
    }
}
