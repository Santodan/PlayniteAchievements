// ChimeRoundTripProbe — measures what the clip pipeline actually does to a live chime.
//
//   ChimeRoundTripProbe.exe            run every scenario
//   ChimeRoundTripProbe.exe --wav-out <dir>   also write mixture/cancelled/final WAVs to listen to
//
// The clip pipeline is supposed to remove the live chime from the captured speaker mix and
// composite a clean copy at the card. Two things can go wrong and both are audible as "two
// chimes": the removal leaves a residue, or the removal over-subtracts and injects a
// phase-inverted copy. Overall suppression cannot tell them apart, because it is dominated by the
// game bed the chime sits on.
//
// So this probe keeps the game bed and the chime as separate ground truth and measures the CHIME
// ERROR on its own: (cancelled - gameBed) against the chime that was really there. That number is
// what the ear hears as a leftover. It also measures how much of the game bed the cancellation
// damaged, because a removal that eats the game is not a win either.
using System;
using System.Globalization;
using System.IO;
using PlayniteAchievements.Services.Capture;

internal static class ChimeRoundTripProbe
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    // The service's chime pass: wide search because the out-of-process onset is late by a
    // variable player spin-up (measured up to ~500 ms cold).
    private const int ChimeMaxLagFrames = 36000;

    // Below this the leftover is inaudible under the composited copy.
    private const double ResidueTargetDb = -20.0;

    // How far either side of the reference position the chime's real onset may sit.
    private const int SpinUpMarginFrames = SampleRate * 3 / 4;

    private static bool Cropped;

    /// <summary>Use the captured sidecar's narrow search rather than the file reference's wide one.</summary>
    private static bool CapturedPath;

    private static int PassMaxLag => CapturedPath ? 12000 : ChimeMaxLagFrames;

    private static int Main(string[] args)
    {
        string wavOut = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--wav-out") { wavOut = args[i + 1]; }
        }

        var failures = 0;

        // Does the search FIND the lag that was injected? Everything else is downstream of this.
        Console.WriteLine();
        Console.WriteLine("=== LAG SEARCH ACCURACY (captured sidecar, maxLag 12000) ===");
        CapturedPath = true;
        foreach (var ms in new[] { 0, 2, 5, 10, 15, 20, 30, 60 })
        {
            failures += Run($"skew {ms,3} ms", ms, 0, 1.0, wavOut);
        }

        CapturedPath = false;
        if (args.Length > 0 && args[0] == "--lag-only")
        {
            return failures;
        }

        // Direction check. The file reference sits at the sound LAUNCH stamp and the real chime
        // renders AFTER it, so the mixture's copy is later than the reference — a positive offset
        // here. A negative offset is the opposite sign, which is the shape the older selftest
        // happens to build. If only one sign cancels, the lag search is directional.
        Console.WriteLine();
        Console.WriteLine("=== LAG DIRECTION (whole window) ===");
        Console.WriteLine("scenario                         outcome              chimeErr  gameDmg  verdict");
        Console.WriteLine("-------------------------------- -------------------- --------  -------  -------");
        foreach (var ms in new[] { -500, -300, -120, -40, 0, 40, 120, 300, 500 })
        {
            failures += Run($"offset {ms,4} ms", ms, 0, 1.0, wavOut);
        }

        // How much slack the ALIGNED path has. The captured chm_ sidecar shares the main track's
        // clock but is tapped through a different client, so a few ms of engine latency remains.
        Console.WriteLine();
        Console.WriteLine("=== ALIGNED-PATH SLACK (captured sidecar, maxLag 12000) ===");
        Console.WriteLine("scenario                         outcome              chimeErr  gameDmg  verdict");
        Console.WriteLine("-------------------------------- -------------------- --------  -------  -------");
        CapturedPath = true;
        foreach (var ms in new[] { 0, 2, 5, 10, 20, 30 })
        {
            failures += Run($"sidecar skew {ms,3} ms", ms, 0, 1.0, wavOut);
        }

        // Two chimes in one window, which one whole-window fit has to cover with a single gain
        // and a single lag. Both must come out: the clip re-adds only its own wave's chime.
        Console.WriteLine();
        Console.WriteLine("=== TWO CHIMES IN ONE WINDOW (captured sidecar) ===");
        Console.WriteLine("scenario                         outcome              chime1    chime2   verdict");
        Console.WriteLine("-------------------------------- -------------------- --------  -------  -------");
        failures += RunTwo("both aligned, equal volume", 0, 0, 1.0, 1.0);
        failures += RunTwo("both skewed 10 ms", 10, 10, 1.0, 1.0);
        failures += RunTwo("skewed apart 5/25 ms", 5, 25, 1.0, 1.0);
        failures += RunTwo("second quieter (1.0/0.3)", 10, 10, 1.0, 0.3);
        failures += RunTwo("second much quieter (1.0/0.05)", 10, 10, 1.0, 0.05);
        CapturedPath = false;

        // The real shape: a ~2 s chime inside a ~22 s clip window, sidecar skewed 15 ms, which is
        // what the field log shows (lag=15.469ms correlation=0.441 suppression=6.5dB blocks=0/1
        // restored=1). One block spanning the whole window cannot clear a 10 dB keep gate on
        // window-wide suppression, because the chime is nowhere near 90% of the window's energy,
        // so the block is always restored and nothing is removed. Half-second blocks score the
        // chime locally, which is what 3.1.3 did by default.
        Console.WriteLine();
        Console.WriteLine("=== REAL WINDOW SHAPE (22 s window, 2 s chime, 15 ms skew) ===");
        Console.WriteLine("blocking                         outcome              chimeErr  gameDmg  verdict");
        Console.WriteLine("-------------------------------- -------------------- --------  -------  -------");
        CapturedPath = true;
        failures += RunLongWindow("whole window (current)", null);
        failures += RunLongWindow("half-second blocks (3.1.3)", 24000);
        CapturedPath = false;

        // Block size has to hold for any chime length in any window length. Blocked scoring should
        // make the result independent of window length -- each block is judged where it sits -- and
        // the block only has to be short enough that a block the chime occupies is dominated by
        // the chime. This sweep is what that claim is checked against rather than assumed.
        Console.WriteLine();
        Console.WriteLine("=== BLOCK SIZE x CHIME LENGTH x WINDOW LENGTH (15 ms skew) ===");
        Console.WriteLine("window  chime   block     outcome              chimeErr  gameDmg  verdict");
        Console.WriteLine("------  ------  --------  -------------------- --------  -------  -------");
        CapturedPath = true;
        foreach (var windowSeconds in new[] { 8, 22, 45 })
        {
            foreach (var chimeSeconds in new[] { 0.3, 1.0, 2.0, 4.0 })
            {
                foreach (var block in new[] { 6000, 12000, 24000, 48000 })
                {
                    failures += RunMatrix(windowSeconds, chimeSeconds, block);
                }
            }
        }

        CapturedPath = false;

        Console.WriteLine();
        Console.WriteLine("chimeErr 0 dB = chime untouched; -20 dB or lower = gone.");
        return failures;
    }

    private static int RunAll(string wavOut)
    {
        var failures = 0;
        Console.WriteLine("scenario                         outcome              chimeErr  gameDmg  verdict");
        Console.WriteLine("-------------------------------- -------------------- --------  -------  -------");

        // Spin-up: the file reference sits at the launch stamp, the real chime rendered later.
        foreach (var spinUpMs in new[] { 0, 40, 120, 300, 500 })
        {
            failures += Run($"spin-up {spinUpMs,3} ms", spinUpMs, rateDriftPpm: 0, chimeGain: 1.0, wavOut);
        }

        // Rate drift across the chime: a player clocked slightly off the capture.
        foreach (var ppm in new[] { 50, 200, 1000 })
        {
            failures += Run($"drift {ppm,4} ppm", 120, ppm, 1.0, wavOut);
        }

        // A quiet chime: the field case where a 5% music volume read as clean and got doubled.
        foreach (var gain in new[] { 0.5, 0.15, 0.05 })
        {
            failures += Run($"volume {gain,4:0.00}", 120, 0, gain, wavOut);
        }

        return failures;
    }

    /// <summary>
    /// Two non-overlapping chimes in one window, both of which have to be removed: the clip
    /// re-adds only its own wave's chime, so a survivor from the other wave is a stray chime.
    /// The chime is 2 s and they are placed 3 s apart, so neither rings into the other.
    /// </summary>
    private static int RunTwo(
        string label, int skew1Ms, int skew2Ms, double gain1, double gain2)
    {
        const int frames = SampleRate * 9;
        var chimeFile = Chime(SampleRate * 2);
        var chimeFrames = chimeFile.Length / Channels;

        var at1 = SampleRate * 2;
        var at2 = at1 + (SampleRate * 3);   // 3 s apart, chime is 2 s: no overlap
        var rendered1 = at1 + (skew1Ms * SampleRate / 1000);
        var rendered2 = at2 + (skew2Ms * SampleRate / 1000);

        var gameBed = GameBed(frames);

        // The captured sidecar carries both chimes, at their rendered positions.
        var reference = new short[frames * Channels];
        Place(reference, chimeFile, at1, gain1, 0);
        Place(reference, chimeFile, at2, gain2, 0);

        var mixture = new short[frames * Channels];
        Array.Copy(gameBed, mixture, gameBed.Length);
        Place(mixture, chimeFile, rendered1, gain1, 0);
        Place(mixture, chimeFile, rendered2, gain2, 0);

        var truth1 = new short[frames * Channels];
        Place(truth1, chimeFile, rendered1, gain1, 0);
        var truth2 = new short[frames * Channels];
        Place(truth2, chimeFile, rendered2, gain2, 0);

        var mixBytes = ToBytes(mixture);
        var maxLag = CapturedPath ? 12000 : ChimeMaxLagFrames;
        var outcome = ReferenceCancellationPolicy.Subtract(
            mixBytes, ToBytes(reference), out var d,
            residualPass: false, maxLagFrames: maxLag, detectClean: true);
        if (outcome == PcmCancellationOutcome.Unseparable ||
            (outcome == PcmCancellationOutcome.CleanNoGameDetected && d.SubtractedBlocks == 0))
        {
            outcome = ReferenceCancellationPolicy.Subtract(
                mixBytes, ToBytes(reference), out d,
                residualPass: true, maxLagFrames: maxLag, detectClean: true);
        }

        var cancelled = ToShorts(mixBytes);
        var error = new short[frames * Channels];
        for (var i = 0; i < error.Length; i++)
        {
            error[i] = Clamp(cancelled[i] - gameBed[i]);
        }

        var e1 = RelativeDb(error, truth1, rendered1, chimeFrames);
        var e2 = RelativeDb(error, truth2, rendered2, chimeFrames);
        var ok = e1 <= ResidueTargetDb && e2 <= ResidueTargetDb;
        Console.WriteLine(
            $"{label,-32} {outcome,-20} {e1,7:0.0}dB {e2,7:0.0}dB  {(ok ? "ok" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// A short chime inside a long clip window, which is the shape that actually ships. The chime
    /// is a small fraction of the window's energy, so a keep gate scored across the whole window
    /// is unreachable however well the chime itself was cancelled.
    /// </summary>
    private static int RunLongWindow(string label, int? blockFrames)
    {
        const int frames = SampleRate * 22;
        var chimeFile = Chime(SampleRate * 2);
        var chimeFrames = chimeFile.Length / Channels;
        var at = SampleRate * 15;                       // where the card lands in a 22 s window
        var rendered = at + (15 * SampleRate / 1000);   // the 15 ms skew the field log reports

        var gameBed = GameBed(frames);
        var reference = new short[frames * Channels];
        Place(reference, chimeFile, at, 1.0, 0);

        var mixture = new short[frames * Channels];
        Array.Copy(gameBed, mixture, gameBed.Length);
        Place(mixture, chimeFile, rendered, 1.0, 0);

        var truth = new short[frames * Channels];
        Place(truth, chimeFile, rendered, 1.0, 0);

        var mixBytes = ToBytes(mixture);
        var outcome = ReferenceCancellationPolicy.Subtract(
            mixBytes, ToBytes(reference), out var d,
            residualPass: false, blockFrames: blockFrames, maxLagFrames: 12000, detectClean: true);

        var cancelled = ToShorts(mixBytes);
        var error = new short[frames * Channels];
        for (var i = 0; i < error.Length; i++)
        {
            error[i] = Clamp(cancelled[i] - gameBed[i]);
        }

        var errDb = RelativeDb(error, truth, rendered, chimeFrames);
        var dmgDb = RelativeDbOutside(error, gameBed, rendered, chimeFrames);
        var ok = errDb <= ResidueTargetDb && dmgDb <= -30.0;
        Console.WriteLine(
            $"{label,-32} {outcome,-20} {errDb,7:0.0}dB {dmgDb,7:0.0}dB  {(ok ? "ok" : "FAIL")}");
        Console.WriteLine(
            $"    suppression={d.SuppressionDb:0.0}dB correlation={d.Correlation:0.000} " +
            $"blocks={d.SubtractedBlocks}/{d.TotalBlocks} restored={d.RestoredBlocks}");
        return ok ? 0 : 1;
    }

    /// <summary>One cell of the block-size sweep: chime length and window length both vary.</summary>
    private static int RunMatrix(int windowSeconds, double chimeSeconds, int blockFrames)
    {
        var frames = SampleRate * windowSeconds;
        var chimeFile = Chime((int)(SampleRate * chimeSeconds));
        var chimeFrames = chimeFile.Length / Channels;

        // Sit the chime late in the window, as a card near the end of a clip does, while leaving
        // room for its own length.
        var at = Math.Max(SampleRate, frames - chimeFrames - (SampleRate * 2));
        var rendered = at + (15 * SampleRate / 1000);

        var gameBed = GameBed(frames);
        var reference = new short[frames * Channels];
        Place(reference, chimeFile, at, 1.0, 0);

        var mixture = new short[frames * Channels];
        Array.Copy(gameBed, mixture, gameBed.Length);
        Place(mixture, chimeFile, rendered, 1.0, 0);

        var truth = new short[frames * Channels];
        Place(truth, chimeFile, rendered, 1.0, 0);

        var mixBytes = ToBytes(mixture);
        var outcome = ReferenceCancellationPolicy.Subtract(
            mixBytes, ToBytes(reference), out var d,
            residualPass: false, blockFrames: blockFrames, maxLagFrames: 12000, detectClean: true);

        var cancelled = ToShorts(mixBytes);
        var error = new short[frames * Channels];
        for (var i = 0; i < error.Length; i++)
        {
            error[i] = Clamp(cancelled[i] - gameBed[i]);
        }

        var errDb = RelativeDb(error, truth, rendered, chimeFrames);
        var dmgDb = RelativeDbOutside(error, gameBed, rendered, chimeFrames);
        var ok = errDb <= ResidueTargetDb && dmgDb <= -30.0;
        Console.WriteLine(
            $"{windowSeconds,4}s   {chimeSeconds,4:0.0}s  {blockFrames / (double)SampleRate,6:0.000}s  " +
            $"{outcome,-20} {errDb,7:0.0}dB {dmgDb,7:0.0}dB  {(ok ? "ok" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static byte[] Slice(byte[] source, int fromFrame, int lengthFrames)
    {
        var bytes = new byte[lengthFrames * Channels * 2];
        Buffer.BlockCopy(source, fromFrame * Channels * 2, bytes, 0, bytes.Length);
        return bytes;
    }

    private static int Run(
        string label, int spinUpMs, int rateDriftPpm, double chimeGain, string wavOut)
    {
        const int frames = SampleRate * 6;
        var chimeAtFrame = SampleRate * 2;             // reference position (the launch stamp)
        var renderedAt = chimeAtFrame + (spinUpMs * SampleRate / 1000);

        var gameBed = GameBed(frames);
        var chimeFile = Chime(SampleRate * 2);          // 2 s chime, decaying

        // What the file reference looks like: the chime at the launch stamp, at played volume.
        var reference = new short[frames * Channels];
        Place(reference, chimeFile, chimeAtFrame, chimeGain, 0);

        // What was really captured: game bed plus the chime as actually rendered.
        var mixture = new short[frames * Channels];
        Array.Copy(gameBed, mixture, gameBed.Length);
        Place(mixture, chimeFile, renderedAt, chimeGain, rateDriftPpm);

        // The chime alone, exactly as captured — ground truth for the error measurement.
        var chimeTruth = new short[frames * Channels];
        Place(chimeTruth, chimeFile, renderedAt, chimeGain, rateDriftPpm);

        var mixBytes = ToBytes(mixture);
        var refBytes = ToBytes(reference);
        PcmCancellationOutcome outcome;
        PcmCancellationDiagnostics d;

        if (Cropped)
        {
            // Cancel only across the chime's own span plus the spin-up margin, instead of the
            // whole clip window. Correlation over a window that is mostly game-only audio is
            // diluted by everything the reference is silent through, which is what stops the lag
            // search finding a late onset at all.
            var chimeFrames = chimeFile.Length / Channels;
            var from = Math.Max(0, chimeAtFrame - SpinUpMarginFrames);
            var to = Math.Min(frames, chimeAtFrame + chimeFrames + SpinUpMarginFrames);
            var slice = Slice(mixBytes, from, to - from);
            var refSlice = Slice(refBytes, from, to - from);

            outcome = ReferenceCancellationPolicy.Subtract(
                slice, refSlice, out d,
                residualPass: false, maxLagFrames: ChimeMaxLagFrames, detectClean: true);
            if (outcome == PcmCancellationOutcome.Unseparable ||
                (outcome == PcmCancellationOutcome.CleanNoGameDetected && d.SubtractedBlocks == 0))
            {
                slice = Slice(mixBytes, from, to - from);
                outcome = ReferenceCancellationPolicy.Subtract(
                    slice, refSlice, out d,
                    residualPass: true, maxLagFrames: ChimeMaxLagFrames, detectClean: true);
            }

            Buffer.BlockCopy(slice, 0, mixBytes, from * Channels * 2, slice.Length);
        }
        else
        {
            outcome = ReferenceCancellationPolicy.Subtract(
                mixBytes, refBytes, out d,
                residualPass: false, maxLagFrames: PassMaxLag, detectClean: true);
            if (outcome == PcmCancellationOutcome.Unseparable ||
                (outcome == PcmCancellationOutcome.CleanNoGameDetected && d.SubtractedBlocks == 0))
            {
                outcome = ReferenceCancellationPolicy.Subtract(
                    mixBytes, refBytes, out d,
                    residualPass: true, maxLagFrames: PassMaxLag, detectClean: true);
            }
        }

        var cancelled = ToShorts(mixBytes);

        // Chime error: what is left where the chime was, against the chime that was there. 0 dB
        // means untouched; -20 dB or lower means gone. A residue and an inverted over-subtraction
        // both show up here, which is the point.
        var chimeError = new short[frames * Channels];
        for (var i = 0; i < chimeError.Length; i++)
        {
            chimeError[i] = Clamp(cancelled[i] - gameBed[i]);
        }

        var errDb = RelativeDb(chimeError, chimeTruth, renderedAt, chimeFile.Length / Channels);

        // Game damage: how much of the bed the cancellation destroyed, outside the chime span.
        var gameDamage = new short[frames * Channels];
        for (var i = 0; i < gameDamage.Length; i++)
        {
            gameDamage[i] = Clamp(cancelled[i] - gameBed[i]);
        }
        var dmgDb = RelativeDbOutside(gameDamage, gameBed, renderedAt, chimeFile.Length / Channels);

        var ok = errDb <= ResidueTargetDb && dmgDb <= -30.0;

        // The lag it FOUND against the lag actually injected. The reference here is shifted by a
        // whole number of frames, so a correct lag makes the subtraction exact; anything short of
        // that is the search missing, not the audio being uncancellable.
        var trueLagMs = -(renderedAt - chimeAtFrame) * 1000.0 / SampleRate;
        Console.WriteLine(
            $"{label,-32} {outcome,-20} {errDb,7:0.0}dB {dmgDb,7:0.0}dB  {(ok ? "ok" : "FAIL")}" +
            $"   lag found={d.StartLagMs,8:0.000}ms true={trueLagMs,8:0.000}ms gain={d.Gain:0.000}");

        if (wavOut != null)
        {
            Directory.CreateDirectory(wavOut);
            var stem = label.Replace(' ', '_').Replace('.', '_');
            WriteWav(Path.Combine(wavOut, stem + "_mixture.wav"), mixture);
            WriteWav(Path.Combine(wavOut, stem + "_cancelled.wav"), cancelled);
            WriteWav(Path.Combine(wavOut, stem + "_chime_error.wav"), chimeError);
        }

        return ok ? 0 : 1;
    }

    /// <summary>A chime-like decaying triad, which is what these jingles actually are.</summary>
    /// <summary>
    /// A chime-like decaying tone with a percussive attack and INHARMONIC partials, which is what
    /// a real jingle is. Harmonic partials alone are near-periodic, so their autocorrelation at a
    /// 15 ms lag is almost as high as at zero and no lag search can be expected to tell those
    /// apart -- a synthetic detail that looks exactly like a broken search. The inharmonic ratios
    /// and the attack transient give the autocorrelation a single distinct peak, so a lag miss
    /// here is the search's, not the signal's.
    /// </summary>
    private static short[] Chime(int frames)
    {
        var data = new short[frames * Channels];
        double[] partials = { 880.0, 1279.0, 1834.0, 2503.0, 3391.0 };
        double[] decays = { 3.0, 4.1, 5.7, 7.3, 9.1 };
        var random = new Random(99);
        for (var f = 0; f < frames; f++)
        {
            var t = f / (double)SampleRate;
            var sum = 0.0;
            for (var p = 0; p < partials.Length; p++)
            {
                sum += Math.Exp(-decays[p] * t) * Math.Sin(2 * Math.PI * partials[p] * t) / (p + 1);
            }

            // 4 ms noise transient: every struck or plucked sound has one, and it is what makes a
            // correlation peak unambiguous.
            if (t < 0.004)
            {
                sum += (random.NextDouble() - 0.5) * 2.0 * Math.Exp(-600.0 * t);
            }

            var v = (short)Math.Round(9000 * sum / 1.9);
            data[f * Channels] = v;
            data[f * Channels + 1] = v;
        }

        return data;
    }

    /// <summary>Broadband bed standing in for game audio — uncorrelated with the chime.</summary>
    private static short[] GameBed(int frames)
    {
        var random = new Random(4242);
        var data = new short[frames * Channels];
        double l = 0, r = 0;
        for (var f = 0; f < frames; f++)
        {
            l = (l * 0.85) + (random.NextDouble() - 0.5) * 4000;
            r = (r * 0.85) + (random.NextDouble() - 0.5) * 4000;
            data[f * Channels] = Clamp(l);
            data[f * Channels + 1] = Clamp(r);
        }

        return data;
    }

    private static void Place(short[] target, short[] source, int atFrame, double gain, int driftPpm)
    {
        var sourceFrames = source.Length / Channels;
        var targetFrames = target.Length / Channels;
        for (var f = 0; f < sourceFrames; f++)
        {
            var dest = atFrame + f;
            if (dest < 0 || dest >= targetFrames) { continue; }

            // Drift stretches the chime against the capture clock, as an off-rate player would.
            var read = f * (1.0 + (driftPpm / 1e6));
            for (var ch = 0; ch < Channels; ch++)
            {
                var v = target[dest * Channels + ch] + (gain * Interpolate(source, read, ch));
                target[dest * Channels + ch] = Clamp(v);
            }
        }
    }

    private static double Interpolate(short[] data, double frame, int channel)
    {
        var frames = data.Length / Channels;
        var i = (int)Math.Floor(frame);
        if (i < 0 || i + 1 >= frames) { return 0; }

        var frac = frame - i;
        var a = data[i * Channels + channel];
        var b = data[(i + 1) * Channels + channel];
        return a + ((b - a) * frac);
    }

    /// <summary>Energy of <paramref name="signal"/> against <paramref name="truth"/>, in dB, over the chime span.</summary>
    private static double RelativeDb(short[] signal, short[] truth, int fromFrame, int lengthFrames)
    {
        double se = 0, te = 0;
        var frames = signal.Length / Channels;
        for (var f = fromFrame; f < Math.Min(frames, fromFrame + lengthFrames); f++)
        {
            for (var ch = 0; ch < Channels; ch++)
            {
                se += (double)signal[f * Channels + ch] * signal[f * Channels + ch];
                te += (double)truth[f * Channels + ch] * truth[f * Channels + ch];
            }
        }

        if (te <= 0) { return double.NegativeInfinity; }
        if (se <= 0) { return -120.0; }
        return 10.0 * Math.Log10(se / te);
    }

    private static double RelativeDbOutside(short[] signal, short[] truth, int fromFrame, int lengthFrames)
    {
        double se = 0, te = 0;
        var frames = signal.Length / Channels;
        for (var f = 0; f < frames; f++)
        {
            if (f >= fromFrame && f < fromFrame + lengthFrames) { continue; }

            for (var ch = 0; ch < Channels; ch++)
            {
                se += (double)signal[f * Channels + ch] * signal[f * Channels + ch];
                te += (double)truth[f * Channels + ch] * truth[f * Channels + ch];
            }
        }

        if (te <= 0) { return double.NegativeInfinity; }
        if (se <= 0) { return -120.0; }
        return 10.0 * Math.Log10(se / te);
    }

    private static short Clamp(double v)
    {
        return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(v)));
    }

    private static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static short[] ToShorts(byte[] bytes)
    {
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private static void WriteWav(string path, short[] samples)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            var dataBytes = samples.Length * 2;
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataBytes);
            writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(SampleRate * Channels * 2);
            writer.Write((short)(Channels * 2));
            writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataBytes);
            foreach (var s in samples) { writer.Write(s); }
        }
    }
}
