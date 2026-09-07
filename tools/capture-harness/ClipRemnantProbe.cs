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
        var fitTaps = 0;
        var tones = Array.IndexOf(args, "--tones") >= 0;
        var scan = Array.IndexOf(args, "--scan") >= 0;
        var echo = Array.IndexOf(args, "--echo") >= 0;
        var gainCurve = Array.IndexOf(args, "--gaincurve") >= 0;
        var lagCurve = Array.IndexOf(args, "--lagcurve") >= 0;
        var atSeconds = new List<double>();
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--volume") { volume = double.Parse(args[i + 1]); }
            if (args[i] == "--floor") { floor = double.Parse(args[i + 1]); }
            if (args[i] == "--block") { blockSeconds = double.Parse(args[i + 1]); }
            if (args[i] == "--taps") { fitTaps = int.Parse(args[i + 1]); }
            if (args[i] == "--at")
            {
                foreach (var part in args[i + 1].Split(',')) { atSeconds.Add(double.Parse(part)); }
            }
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

        if (fitTaps > 0)
        {
            // On every strong occurrence, compare what a per-block gain leaves behind with what a
            // short per-block least-squares filter leaves behind. The filter models a fractional
            // delay and a mild spectral difference (two decoders, two resamplers) at once.
            Console.WriteLine();
            Console.WriteLine($"per-block residual re played copy (0.5 s blocks): as-is / after gain fit / after {fitTaps}-tap filter fit");
            var positions = new List<int>();
            if (atSeconds.Count > 0)
            {
                foreach (var s in atSeconds) { positions.Add((int)Math.Round(s * Rate)); }
            }
            else
            {
                foreach (var coarseStart in peaks)
                {
                    var start = coarseStart;
                    var corr = Correlation(clip, sound, start, template, 1);
                    for (var delta = coarseStart - 12; delta <= coarseStart + 12; delta++)
                    {
                        if (delta < 0 || delta + template > clipFrames) { continue; }
                        var c = Correlation(clip, sound, delta, template, 1);
                        if (c > corr) { corr = c; start = delta; }
                    }
                    if (corr >= 0.9) { positions.Add(start); }
                }
            }

            foreach (var start in positions)
            {
                var row = new List<string>();
                for (var offset = 0; offset < soundFrames && start + offset < clipFrames; offset += 24000)
                {
                    var count = Math.Min(24000, Math.Min(soundFrames - offset, clipFrames - start - offset));
                    if (count < 4800 || Energy(sound, offset, count) < 1e3) { continue; }
                    var played = volume * volume * Energy(sound, offset, count);
                    var asIs = 10 * Math.Log10(Math.Max(1, Energy(clip, start + offset, count)) / Math.Max(1, played));
                    var gainOnly = FitResidualEnergy(clip, sound, start, offset, count, 1);
                    var filtered = FitResidualEnergy(clip, sound, start, offset, count, fitTaps);
                    row.Add($"{asIs:0}/{10 * Math.Log10(Math.Max(1, gainOnly) / Math.Max(1, played)):0}/{10 * Math.Log10(Math.Max(1, filtered) / Math.Max(1, played)):0}");
                }
                Console.WriteLine($"  at {start / (double)Rate:0.000}s: {string.Join(" ", row)}");
            }
        }

        if (tones && atSeconds.Count > 0)
        {
            // Tonal test: energy at the sound's own strongest partials, per 0.5 s block, relative
            // to the played copy's energy at those partials. A residual the listener hears as the
            // chime shows here even when it is below the broadband bed in total energy; a bed-only
            // block does not.
            var peaksHz = TopPartials(sound, 0, Math.Min(24000, soundFrames), 8);
            Console.WriteLine();
            Console.WriteLine($"energy at the sound's partials ({string.Join(",", peaksHz.Select(h => h.ToString("0")))} Hz), dB re played, per 0.5 s block:");
            foreach (var s in atSeconds)
            {
                var start = (int)Math.Round(s * Rate);
                var row = new List<string>();
                for (var offset = 0; offset < soundFrames && start + offset < clipFrames; offset += 24000)
                {
                    var count = Math.Min(24000, Math.Min(soundFrames - offset, clipFrames - start - offset));
                    if (count < 4800) { continue; }
                    double played = 0, observed = 0;
                    foreach (var hz in peaksHz)
                    {
                        played += Goertzel(sound, offset, count, hz);
                        observed += Goertzel(clip, start + offset, count, hz);
                    }
                    played *= volume * volume;
                    row.Add((10 * Math.Log10(Math.Max(1e-9, observed) / Math.Max(1e-9, played))).ToString("0"));
                }
                Console.WriteLine($"  at {s:0.000}s: {string.Join(" ", row)}");
            }
        }

        if (lagCurve && atSeconds.Count > 0)
        {
            // Lag-curve test: the live copy's best lag against the file in 50 ms steps, in quarter
            // frames within +-4 frames of the direct lag. Steady playback reads a flat line; sample
            // slips in the render or the capture read as steps of one frame that persist.
            Console.WriteLine();
            Console.WriteLine("live lag offset re direct lag per 50 ms step (frames, quarter-frame steps):");
            foreach (var s in atSeconds)
            {
                var guess = (int)Math.Round(s * Rate);
                var start = guess;
                var best = double.NegativeInfinity;
                for (var delta = -480; delta <= 480; delta++)
                {
                    var st = guess + delta;
                    if (st < 0 || st + 24000 > clipFrames) { continue; }
                    var c = StridedBlockCorrelation(clip, sound, st, 0, Math.Min(24000, soundFrames), 2);
                    if (c > best) { best = c; start = st; }
                }

                var row = new List<string>();
                for (var offset = 0; offset + 2400 <= soundFrames && start + offset + 2400 + 8 <= clipFrames; offset += 2400)
                {
                    if (Energy(sound, offset, 2400) < 1e4) { row.Add("."); continue; }
                    var bestLag = 0.0;
                    var bestC = double.NegativeInfinity;
                    for (var q = -16; q <= 16; q++)
                    {
                        var lag = q / 4.0;
                        var c = FractionalCorrelation(clip, sound, start, offset, 2400, lag);
                        if (c > bestC) { bestC = c; bestLag = lag; }
                    }
                    row.Add(bestLag.ToString("+0.##;-0.##;0"));
                }
                Console.WriteLine($"  at {start / (double)Rate:0.000}s: {string.Join(" ", row)}");
            }
        }

        if (gainCurve && atSeconds.Count > 0)
        {
            // Gain-curve test: the live copy's least-squares gain against the file in 50 ms steps
            // at the exact (frame-refined) direct lag. A steady playback reads a flat line; a
            // compressor or loudness enhancement on the endpoint reads as a curve that moves by
            // several dB over tens to hundreds of milliseconds, which no 0.5 s block gain follows.
            Console.WriteLine();
            Console.WriteLine("live gain re played per 50 ms step (dB), at the frame-refined direct lag:");
            foreach (var s in atSeconds)
            {
                var guess = (int)Math.Round(s * Rate);
                var start = guess;
                var best = double.NegativeInfinity;
                for (var delta = -480; delta <= 480; delta++)
                {
                    var st = guess + delta;
                    if (st < 0 || st + 24000 > clipFrames) { continue; }
                    var c = StridedBlockCorrelation(clip, sound, st, 0, Math.Min(24000, soundFrames), 2);
                    if (c > best) { best = c; start = st; }
                }

                var row = new List<string>();
                for (var offset = 0; offset + 2400 <= soundFrames && start + offset + 2400 <= clipFrames; offset += 2400)
                {
                    if (Energy(sound, offset, 2400) < 1e3) { row.Add("."); continue; }
                    var g = Gain(clip, sound, start, offset, 2400);
                    row.Add(Db(g / volume).ToString("+0;-0;0"));
                }
                Console.WriteLine($"  at {start / (double)Rate:0.000}s (corr {best:0.00}): {string.Join(" ", row)}");
            }
        }

        if (echo && atSeconds.Count > 0)
        {
            // Long-lag test: the sound's onset block correlated against the clip from 50 ms before
            // to 250 ms after the given position, in 4-frame steps. A room, spatial, or other
            // endpoint effect shows as secondary peaks well beyond the direct copy.
            Console.WriteLine();
            Console.WriteLine("secondary correlation peaks of the onset block, lag(ms):corr, beyond +-10 ms of the direct copy:");
            foreach (var s in atSeconds)
            {
                var start = (int)Math.Round(s * Rate);
                var count = Math.Min(24000, soundFrames);
                var results = new List<KeyValuePair<int, double>>();
                var direct = 0.0;
                var directLag = 0;
                for (var delta = -2400; delta <= 12000; delta += 4)
                {
                    var st = start + delta;
                    if (st < 0 || st + count > clipFrames) { continue; }
                    var c = StridedBlockCorrelation(clip, sound, st, 0, count, 8);
                    if (Math.Abs(c) > Math.Abs(direct)) { direct = c; directLag = delta; }
                    results.Add(new KeyValuePair<int, double>(delta, c));
                }
                var secondary = results
                    .Where(r => Math.Abs(r.Key - directLag) > 480 && Math.Abs(r.Value) >= 0.08)
                    .OrderByDescending(r => Math.Abs(r.Value))
                    .Take(12)
                    .OrderBy(r => r.Key)
                    .Select(r => $"{(r.Key - directLag) * 1000.0 / Rate:+0.0;-0.0}:{r.Value:0.00}");
                Console.WriteLine($"  at {s:0.000}s direct {direct:0.00}@{directLag * 1000.0 / Rate:+0.0;-0.0}ms; {string.Join(" ", secondary)}");
            }
        }

        if (scan && atSeconds.Count > 0)
        {
            // Displaced-copy test: per 0.5 s block, the strongest correlation with the sound's
            // matching block at any lag within +-480 frames (10 ms) and the lag it sits at, with
            // the fitted gain there re played. A remnant left at another lag shows as a lag with
            // real correlation; a bed-only block shows noise-level correlation.
            Console.WriteLine();
            Console.WriteLine("strongest block correlation within +-10 ms: corr@lag(gain dB re played), per 0.5 s block:");
            foreach (var s in atSeconds)
            {
                var start = (int)Math.Round(s * Rate);
                var row = new List<string>();
                for (var offset = 0; offset < soundFrames && start + offset < clipFrames; offset += 24000)
                {
                    var count = Math.Min(24000, Math.Min(soundFrames - offset, clipFrames - start - offset));
                    if (count < 4800 || Energy(sound, offset, count) < 1e3) { continue; }
                    var bestC = 0.0;
                    var bestLag = 0;
                    for (var delta = -480; delta <= 480; delta++)
                    {
                        var st = start + delta;
                        if (st < 0 || st + offset + count > clipFrames) { continue; }
                        var c = BlockCorrelation(clip, sound, st, offset, count);
                        if (Math.Abs(c) > Math.Abs(bestC)) { bestC = c; bestLag = delta; }
                    }
                    var g = Gain(clip, sound, start + bestLag, offset, count);
                    row.Add($"{bestC:0.00}@{bestLag:+0;-0;0}({Db(g / volume):0})");
                }
                Console.WriteLine($"  at {s:0.000}s: {string.Join(" ", row)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("A replacement composited at the played volume reads 0 dB. Anything else above the floor is live sound that survived removal; a flat per-block row means a level mismatch, a sloped one a time-varying level, and a row that decays block by block a timing drift. Rows whose gains rise exactly where a replacement starts are the replacement's own audio correlating with the sound's later, repeating motif, not a remnant.");
        return 0;
    }

    /// <summary>
    /// Least-squares fit of the sound onto the clip block with a symmetric FIR of
    /// <paramref name="taps"/> taps (1 = plain gain), returning the residual energy in dB
    /// relative to the fitted copy's energy. Stereo channels share the taps.
    /// </summary>
    private static double FitResidualDb(short[] clip, short[] sound, int clipStart, int offset, int count, int taps)
    {
        var residual = FitResidualEnergy(clip, sound, clipStart, offset, count, taps);
        var copy = Energy(sound, offset, count);
        return 10 * Math.Log10(Math.Max(1, residual) / Math.Max(1, copy));
    }

    /// <summary>Residual energy after a least-squares fit of the sound onto the clip block with <paramref name="taps"/> taps.</summary>
    private static double FitResidualEnergy(short[] clip, short[] sound, int clipStart, int offset, int count, int taps)
    {
        var half = taps / 2;
        var n = taps;
        var ata = new double[n, n];
        var atb = new double[n];
        for (var f = offset; f < offset + count; f++)
        {
            for (var ch = 0; ch < Channels; ch++)
            {
                double y = clip[(clipStart + f) * Channels + ch];
                for (var i = 0; i < n; i++)
                {
                    var fi = f + (i - half);
                    double xi = fi >= 0 && fi < sound.Length / Channels ? sound[fi * Channels + ch] : 0;
                    atb[i] += xi * y;
                    for (var j = i; j < n; j++)
                    {
                        var fj = f + (j - half);
                        double xj = fj >= 0 && fj < sound.Length / Channels ? sound[fj * Channels + ch] : 0;
                        ata[i, j] += xi * xj;
                    }
                }
            }
        }
        for (var i = 0; i < n; i++) { for (var j = 0; j < i; j++) { ata[i, j] = ata[j, i]; } }
        for (var i = 0; i < n; i++) { ata[i, i] *= 1.000001; }
        var h = Solve(ata, atb, n);

        double residual = 0, fitted = 0;
        for (var f = offset; f < offset + count; f++)
        {
            for (var ch = 0; ch < Channels; ch++)
            {
                double y = clip[(clipStart + f) * Channels + ch];
                double est = 0;
                for (var i = 0; i < n; i++)
                {
                    var fi = f + (i - half);
                    est += h[i] * (fi >= 0 && fi < sound.Length / Channels ? sound[fi * Channels + ch] : 0);
                }
                residual += (y - est) * (y - est);
                fitted += est * est;
            }
        }
        return residual;
    }

    /// <summary>Goertzel energy (both channels summed) of one frequency over a block.</summary>
    private static double Goertzel(short[] pcm, int offset, int count, double hz)
    {
        var coeff = 2 * Math.Cos(2 * Math.PI * hz / Rate);
        double total = 0;
        for (var ch = 0; ch < Channels; ch++)
        {
            double s0 = 0, s1 = 0, s2 = 0;
            for (var f = offset; f < offset + count; f++)
            {
                s0 = pcm[f * Channels + ch] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            total += s1 * s1 + s2 * s2 - coeff * s1 * s2;
        }
        return total / Math.Max(1, count);
    }

    /// <summary>The <paramref name="n"/> strongest spectral peaks (local maxima, 4 Hz grid, above 100 Hz) of a block.</summary>
    private static List<double> TopPartials(short[] pcm, int offset, int count, int n)
    {
        var grid = new List<KeyValuePair<double, double>>();
        for (var hz = 100.0; hz < 8000; hz += 4)
        {
            grid.Add(new KeyValuePair<double, double>(hz, Goertzel(pcm, offset, count, hz)));
        }
        var peaks = new List<KeyValuePair<double, double>>();
        for (var i = 1; i < grid.Count - 1; i++)
        {
            if (grid[i].Value > grid[i - 1].Value && grid[i].Value >= grid[i + 1].Value)
            {
                peaks.Add(grid[i]);
            }
        }
        return peaks.OrderByDescending(p => p.Value).Take(n).Select(p => p.Key).OrderBy(h => h).ToList();
    }

    private static double[] Solve(double[,] a, double[] b, int n)
    {
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++) { for (var j = 0; j < n; j++) { m[i, j] = a[i, j]; } m[i, n] = b[i]; }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) { if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) { pivot = r; } }
            for (var j = 0; j <= n; j++) { var t = m[col, j]; m[col, j] = m[pivot, j]; m[pivot, j] = t; }
            if (Math.Abs(m[col, col]) < 1e-12) { continue; }
            for (var r = 0; r < n; r++)
            {
                if (r == col) { continue; }
                var factor = m[r, col] / m[col, col];
                for (var j = col; j <= n; j++) { m[r, j] -= factor * m[col, j]; }
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) { x[i] = Math.Abs(m[i, i]) < 1e-12 ? 0 : m[i, n] / m[i, i]; }
        return x;
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

    /// <summary>Correlation of a clip block with the sound read at a fractional lag (linear interpolation).</summary>
    private static double FractionalCorrelation(short[] clip, short[] sound, int clipStart, int offset, int frames, double lagFrames)
    {
        var soundFrames = sound.Length / Channels;
        double dot = 0, a = 0, b = 0;
        for (var f = offset; f < offset + frames; f++)
        {
            var position = f + lagFrames;
            var lower = (int)Math.Floor(position);
            var fraction = position - lower;
            for (var ch = 0; ch < Channels; ch++)
            {
                double first = lower >= 0 && lower < soundFrames ? sound[lower * Channels + ch] : 0;
                double second = lower + 1 >= 0 && lower + 1 < soundFrames ? sound[(lower + 1) * Channels + ch] : 0;
                var y = first + (second - first) * fraction;
                double x = clip[(clipStart + f) * Channels + ch];
                dot += x * y;
                a += x * x;
                b += y * y;
            }
        }
        return a <= 0 || b <= 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    private static double StridedBlockCorrelation(short[] clip, short[] sound, int clipStart, int offset, int frames, int stride)
    {
        double dot = 0, a = 0, b = 0;
        for (var f = offset; f < offset + frames; f += stride)
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

    // Frames outside either buffer are skipped rather than read. An occurrence close to the end of
    // the clip has fewer clip frames left than the sound is long, and the per-block lag search
    // leaves its previous offset in place when every candidate for a block runs past the end, so
    // callers cannot guarantee the window fits.
    private static double Gain(short[] clip, short[] sound, int clipStart, int offset, int frames)
    {
        var clipFrames = clip.Length / Channels;
        var soundFrames = sound.Length / Channels;
        double dot = 0, b = 0;
        for (var f = offset; f < offset + frames; f++)
        {
            var c = clipStart + f;
            if (f < 0 || f >= soundFrames || c < 0 || c >= clipFrames)
            {
                continue;
            }

            for (var ch = 0; ch < Channels; ch++)
            {
                double x = clip[c * Channels + ch];
                double y = sound[f * Channels + ch];
                dot += x * y;
                b += y * y;
            }
        }
        return b <= 0 ? 0 : dot / b;
    }

    /// <summary>Energy over a frame window, counting only frames the buffer actually holds.</summary>
    private static double Energy(short[] pcm, int offset, int frames)
    {
        var total = pcm.Length / Channels;
        double e = 0;
        for (var f = Math.Max(0, offset); f < Math.Min(total, offset + frames); f++)
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
