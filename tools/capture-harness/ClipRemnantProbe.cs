using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.MediaFoundation;
using NAudio.Wave;

// Measures what an exported clip still carries of a notification sound. Decodes the clip's audio
// and the sound file to 48 kHz stereo 16-bit, finds every occurrence of the sound in the clip by
// normalized correlation against the sound's first second, then reports each occurrence's level
// relative to the file and its per-block gain across the sound. A composited replacement shows as
// gain near the played volume; a removal remnant shows as a small gain, and its per-block shape
// says whether a global gain, a time-varying level, or timing drift left it behind.
//
//   ClipRemnantProbe.exe <clip.mp4> <sound file> [--volume 0.5] [--floor 0.06] [--block 0.25]
internal static class ClipRemnantProbe
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ClipRemnantProbe.exe <clip.mp4> <sound file> [--volume 0.5] [--floor 0.06] [--block 0.25]");
            return 2;
        }

        var volume = 0.5;
        var floor = 0.06;
        var blockSeconds = 0.25;
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--volume") { volume = double.Parse(args[i + 1]); }
            if (args[i] == "--floor") { floor = double.Parse(args[i + 1]); }
            if (args[i] == "--block") { blockSeconds = double.Parse(args[i + 1]); }
        }

        var clip = Decode(args[0]);
        var sound = Decode(args[1]);
        var soundFrames = sound.Length / Channels;
        var clipFrames = clip.Length / Channels;
        Console.WriteLine($"clip {Path.GetFileName(args[0])}: {clipFrames / (double)Rate:0.00}s; sound {Path.GetFileName(args[1])}: {soundFrames / (double)Rate:0.00}s; played volume {volume:0.00}");

        // Coarse search with the sound's first second, stride 8, then exact refinement.
        var template = Math.Min(soundFrames, Rate);
        var coarse = new List<Tuple<int, double>>();
        for (var start = 0; start + template <= clipFrames; start += 8)
        {
            coarse.Add(Tuple.Create(start, Correlation(clip, sound, start, template, 8)));
        }

        var peaks = new List<int>();
        for (var i = 1; i < coarse.Count - 1; i++)
        {
            var c = coarse[i].Item2;
            if (c < floor || c < coarse[i - 1].Item2 || c < coarse[i + 1].Item2)
            {
                continue;
            }

            // Keep one peak per 300 ms neighbourhood.
            if (peaks.Count > 0 && coarse[i].Item1 - peaks[peaks.Count - 1] < Rate * 3 / 10)
            {
                if (Correlation(clip, sound, coarse[i].Item1, template, 1) >
                    Correlation(clip, sound, peaks[peaks.Count - 1], template, 1))
                {
                    peaks[peaks.Count - 1] = coarse[i].Item1;
                }
                continue;
            }

            peaks.Add(coarse[i].Item1);
        }

        var blockFrames = (int)(blockSeconds * Rate);
        var peakBlockEnergy = 0.0;
        for (var offset = 0; offset < soundFrames; offset += blockFrames)
        {
            peakBlockEnergy = Math.Max(peakBlockEnergy, Energy(sound, offset, Math.Min(blockFrames, soundFrames - offset)));
        }
        var levels = new List<string>();
        for (var offset = 0; offset < soundFrames; offset += blockFrames)
        {
            var e = Energy(sound, offset, Math.Min(blockFrames, soundFrames - offset));
            levels.Add((10 * Math.Log10(Math.Max(1e-9, e / peakBlockEnergy))).ToString("0"));
        }
        Console.WriteLine($"sound level per {blockSeconds:0.00}s block, dB re loudest block: {string.Join(" ", levels)}");
        Console.WriteLine();
        Console.WriteLine("  at(s)    corr   gain(dB re played)  per-block: signed gain dB re played @ best lag offset in frames (search +-24)");
        foreach (var coarseStart in peaks)
        {
            var best = coarseStart;
            var bestCorr = double.NegativeInfinity;
            for (var start = coarseStart - 12; start <= coarseStart + 12; start++)
            {
                if (start < 0 || start + template > clipFrames)
                {
                    continue;
                }
                var c = Correlation(clip, sound, start, template, 1);
                if (c > bestCorr)
                {
                    bestCorr = c;
                    best = start;
                }
            }

            var globalGain = Gain(clip, sound, best, 0, Math.Min(soundFrames, clipFrames - best));
            var blocks = new List<string>();
            var previousOffset = 0;
            for (var offset = 0; offset < soundFrames && best + offset < clipFrames; offset += blockFrames)
            {
                var count = Math.Min(blockFrames, Math.Min(soundFrames - offset, clipFrames - best - offset));
                var soundEnergy = Energy(sound, offset, count);
                if (soundEnergy < 1e3)
                {
                    blocks.Add("n/a");
                    continue;
                }

                // Re-lock the lag inside this block, tracking from the previous block: a tonal
                // sound repeats every few milliseconds, so an unconstrained search reads aliases;
                // within +-60 frames of the previous block's lag, a tear shows as one step and a
                // rate drift as a steady walk, while the onset block stays at zero.
                var bestOffset = previousOffset;
                var bestBlockCorr = double.NegativeInfinity;
                for (var delta = previousOffset - 60; delta <= previousOffset + 60; delta++)
                {
                    var start = best + delta;
                    if (start < 0 || start + offset + count > clipFrames)
                    {
                        continue;
                    }
                    var c = BlockCorrelation(clip, sound, start, offset, count);
                    if (c > bestBlockCorr)
                    {
                        bestBlockCorr = c;
                        bestOffset = delta;
                    }
                }
                previousOffset = bestOffset;

                var gain = Gain(clip, sound, best + bestOffset, offset, count);
                var signed = gain < 0 ? "-" : "+";
                blocks.Add($"{signed}{Db(gain / volume):0}@{bestOffset:+0;-0;0}");
            }

            Console.WriteLine(
                $"  {best / (double)Rate,6:0.000}  {bestCorr,6:0.000}  {Db(globalGain / volume),8:+0.0;-0.0}   {string.Join(" ", blocks)}");
        }

        Console.WriteLine();
        Console.WriteLine("A replacement composited at the played volume reads 0 dB. Anything else above the floor is live sound that survived removal; a flat per-block row means a level mismatch, a sloped one a time-varying level, and a row that decays block by block a timing drift.");
        return 0;
    }

    private static double Correlation(short[] clip, short[] sound, int clipStart, int frames, int stride)
    {
        double dot = 0, a = 0, b = 0;
        for (var f = 0; f < frames; f += stride)
        {
            for (var ch = 0; ch < Channels; ch++)
            {
                double x = clip[(clipStart + f) * Channels + ch];
                double y = sound[f * Channels + ch];
                dot += x * y;
                a += x * x;
                b += y * y;
            }
        }
        return a <= 0 || b <= 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    private static double BlockCorrelation(short[] clip, short[] sound, int clipStart, int offset, int frames)
    {
        double dot = 0, a = 0, b = 0;
        for (var f = offset; f < offset + frames; f++)
        {
            for (var ch = 0; ch < Channels; ch++)
            {
                double x = clip[(clipStart + f) * Channels + ch];
                double y = sound[f * Channels + ch];
                dot += x * y;
                a += x * x;
                b += y * y;
            }
        }
        return a <= 0 || b <= 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    private static double Gain(short[] clip, short[] sound, int clipStart, int offset, int frames)
    {
        double dot = 0, b = 0;
        for (var f = offset; f < offset + frames; f++)
        {
            for (var ch = 0; ch < Channels; ch++)
            {
                double x = clip[(clipStart + f) * Channels + ch];
                double y = sound[f * Channels + ch];
                dot += x * y;
                b += y * y;
            }
        }
        return b <= 0 ? 0 : dot / b;
    }

    private static double Energy(short[] pcm, int offset, int frames)
    {
        double e = 0;
        for (var f = offset; f < offset + frames; f++)
        {
            for (var ch = 0; ch < Channels; ch++)
            {
                double y = pcm[f * Channels + ch];
                e += y * y;
            }
        }
        return e;
    }

    private static double Db(double gain)
    {
        return 20 * Math.Log10(Math.Max(1e-9, Math.Abs(gain)));
    }

    private static short[] Decode(string path)
    {
        MediaFoundationApi.Startup();
        using (var reader = new MediaFoundationReader(path))
        using (var resampled = new MediaFoundationResampler(reader, new WaveFormat(Rate, 16, Channels)))
        {
            var buffer = new byte[Rate * Channels * 2];
            using (var memory = new MemoryStream())
            {
                int read;
                while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                }
                var bytes = memory.ToArray();
                var samples = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
                return samples;
            }
        }
    }
}
