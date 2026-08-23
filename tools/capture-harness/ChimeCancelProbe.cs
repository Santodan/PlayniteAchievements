// Runs the plugin's real chime game-audio cancellation (PcmAudio.CancelCorrelated, compiled in
// from source) against the chm_/gam_ WAV chunks of a RecordingBuffer session, so cancellation
// quality can be measured on real captures without loading Playnite.
//
//   ChimeCancelProbe.exe <sessionDir> [--start yyyyMMdd-HHmmssfffffff[Z]] [--seconds 4.5] [--wav-out dir]
//   ChimeCancelProbe.exe --selftest
//
// With no --start, the whole chm_/gam_ overlap is swept in consecutive windows and each window is
// reported: RMS of both tracks, outcome, lag, gain, correlation, and achieved suppression.
// --wav-out additionally writes <stamp>_mixture.wav / _cancelled.wav / _reference.wav per window,
// so the residual can be listened to directly.
//
// The chunk reader here is deliberately simpler than the plugin's Media Foundation path: chunks
// are already 48 kHz stereo, so it parses RIFF directly (16-bit PCM or 32-bit float, including
// mid-write chunks whose RIFF sizes are still placeholders) and places samples into the window by
// each chunk's filename timestamp, zero-padding gaps — the same placement semantics the exporter
// uses.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PlayniteAchievements.Services.Capture;

internal static class ChimeCancelProbe
{
    private const int SampleRate = 48000;
    private const string StampFormat = "yyyyMMdd-HHmmssfff";
    private const string PreciseStampFormat = "yyyyMMdd-HHmmssfffffff";

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--selftest")
        {
            return SelfTest();
        }

        if (args.Length < 1 || !Directory.Exists(args[0]))
        {
            Console.WriteLine("usage: ChimeCancelProbe.exe <sessionDir> [--start yyyyMMdd-HHmmssfffffff[Z]] [--seconds 4.5] [--wav-out dir]");
            Console.WriteLine("       ChimeCancelProbe.exe --selftest");
            return 2;
        }

        var sessionDir = args[0];
        DateTime? startUtc = null;
        var windowSeconds = 4.5;
        string wavOut = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--start" && TryParseStamp(args[i + 1], out var parsed))
            {
                startUtc = parsed;
            }
            else if (args[i] == "--seconds")
            {
                windowSeconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--wav-out")
            {
                wavOut = args[i + 1];
            }
        }

        var chime = LoadTrack(sessionDir, "chm_");
        var reference = LoadTrack(sessionDir, "gam_");
        if (chime.Count == 0)
        {
            Console.WriteLine("no chm_*.wav chunks in " + sessionDir);
            return 2;
        }

        Console.WriteLine($"chime track:     {chime.Count} chunks, {TrackStart(chime):HH:mm:ss.fff} .. {TrackEnd(chime):HH:mm:ss.fff} UTC");
        Console.WriteLine(reference.Count == 0
            ? "reference track: none (gam_ absent — cancellation cannot run, raw sidecar shown)"
            : $"reference track: {reference.Count} chunks, {TrackStart(reference):HH:mm:ss.fff} .. {TrackEnd(reference):HH:mm:ss.fff} UTC");
        Console.WriteLine();
        Console.WriteLine("window start (UTC)      chmRMS    gamRMS    outcome              lag ms          gain   corr    supp dB");

        var sweepStart = startUtc ?? Max(TrackStart(chime), reference.Count > 0 ? TrackStart(reference) : TrackStart(chime));
        var sweepEnd = startUtc.HasValue
            ? startUtc.Value.AddSeconds(windowSeconds)
            : Min(TrackEnd(chime), reference.Count > 0 ? TrackEnd(reference) : TrackEnd(chime));

        if (wavOut != null)
        {
            Directory.CreateDirectory(wavOut);
        }

        for (var winStart = sweepStart; winStart < sweepEnd; winStart = winStart.AddSeconds(windowSeconds))
        {
            var winEnd = winStart.AddSeconds(windowSeconds);
            var mixture = ReadWindow(chime, winStart, winEnd);
            var refPcm = reference.Count > 0 ? ReadWindow(reference, winStart, winEnd) : null;
            var stamp = winStart.ToString(StampFormat, CultureInfo.InvariantCulture);
            var chmRms = RmsDbfs(mixture);
            var gamRms = refPcm != null ? RmsDbfs(refPcm) : double.NegativeInfinity;

            byte[] before = null;
            var outcomeText = "no reference";
            var detail = "";
            if (refPcm != null)
            {
                before = (byte[])mixture.Clone();
                var outcome = PcmAudio.CancelCorrelated(
                    mixture, refPcm, out var d,
                    preferEarlyAlignmentWindow: true,
                    verificationLagRadiusFrames: 480);
                outcomeText = outcome.ToString();
                detail = $"{d.StartLagMs,7:0.000}->{d.EndLagMs,-7:0.000} {d.Gain,5:0.00}  {d.Correlation,5:0.000}  {d.SuppressionDb,7:0.0}  " +
                    $"blocks={d.SubtractedBlocks}/{d.TotalBlocks} muted={d.MutedBlocks} quiet={d.QuietBlocks} weak={d.WeakestBlockSuppressionDb:0.0}";
            }

            Console.WriteLine($"{stamp}     {chmRms,6:0.0}    {gamRms,6:0.0}    {outcomeText,-20} {detail}");

            if (wavOut != null && before != null)
            {
                WriteWav(Path.Combine(wavOut, stamp + "_mixture.wav"), before);
                WriteWav(Path.Combine(wavOut, stamp + "_cancelled.wav"), mixture);
                WriteWav(Path.Combine(wavOut, stamp + "_reference.wav"), refPcm);
            }
        }

        return 0;
    }

    // === Chunk track loading ===

    private sealed class Chunk
    {
        public DateTime StartUtc;
        public byte[] Pcm; // 16-bit 48 kHz stereo
        public DateTime EndUtc => StartUtc.AddSeconds(Pcm.Length / (double)PcmAudio.BytesPerSecond);
    }

    private static List<Chunk> LoadTrack(string dir, string prefix)
    {
        var chunks = new List<Chunk>();
        foreach (var path in Directory.EnumerateFiles(dir, prefix + "*.wav"))
        {
            var body = Path.GetFileNameWithoutExtension(path).Substring(prefix.Length);
            if (!TryParseStamp(body, out var startUtc))
            {
                continue;
            }

            var pcm = ReadWavAsPcm16(path);
            if (pcm != null && pcm.Length >= PcmAudio.BlockAlign)
            {
                chunks.Add(new Chunk { StartUtc = startUtc, Pcm = pcm });
            }
        }

        return chunks.OrderBy(c => c.StartUtc).ToList();
    }

    private static DateTime TrackStart(List<Chunk> track) => track[0].StartUtc;

    private static DateTime TrackEnd(List<Chunk> track) => track.Max(c => c.EndUtc);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static bool TryParseStamp(string text, out DateTime utc)
    {
        var body = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            ? text.Substring(0, text.Length - 1)
            : text;
        return DateTime.TryParseExact(
            body, new[] { PreciseStampFormat, StampFormat, "yyyyMMdd-HHmmss" }, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    /// <summary>Places chunk samples into the window by timestamp; uncovered spans stay zero.</summary>
    private static byte[] ReadWindow(List<Chunk> track, DateTime startUtc, DateTime endUtc)
    {
        var output = new byte[PcmAudio.TicksToAlignedBytes((endUtc - startUtc).Ticks)];
        foreach (var chunk in track)
        {
            if (chunk.EndUtc <= startUtc || chunk.StartUtc >= endUtc)
            {
                continue;
            }

            var destOffset = PcmAudio.TicksToAlignedBytes(Math.Max(0, (chunk.StartUtc - startUtc).Ticks));
            var sourceOffset = PcmAudio.TicksToAlignedBytes(Math.Max(0, (startUtc - chunk.StartUtc).Ticks));
            var count = Math.Min(chunk.Pcm.Length - sourceOffset, output.Length - destOffset) & ~(long)(PcmAudio.BlockAlign - 1);
            if (count > 0)
            {
                Buffer.BlockCopy(chunk.Pcm, (int)sourceOffset, output, (int)destOffset, (int)count);
            }
        }

        return output;
    }

    // === Minimal RIFF reader (16-bit PCM or 32-bit float, 48 kHz stereo) ===

    private static byte[] ReadWavAsPcm16(string path)
    {
        byte[] file;
        try
        {
            // Mid-write chunks are opened shared by the recorder; a snapshot copy is fine here.
            file = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        if (file.Length < 44 || ReadFourCc(file, 0) != "RIFF" || ReadFourCc(file, 8) != "WAVE")
        {
            return null;
        }

        int formatTag = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
        var dataOffset = -1;
        var dataLength = 0;
        var pos = 12;
        while (pos + 8 <= file.Length)
        {
            var id = ReadFourCc(file, pos);
            var size = BitConverter.ToInt32(file, pos + 4);
            var body = pos + 8;
            if (id == "fmt " && body + 16 <= file.Length)
            {
                formatTag = BitConverter.ToUInt16(file, body);
                channels = BitConverter.ToUInt16(file, body + 2);
                sampleRate = BitConverter.ToInt32(file, body + 4);
                bitsPerSample = BitConverter.ToUInt16(file, body + 14);
                if (formatTag == 0xFFFE && body + 26 <= file.Length)
                {
                    formatTag = BitConverter.ToUInt16(file, body + 24); // extensible subformat
                }
            }
            else if (id == "data")
            {
                dataOffset = body;
                // A chunk still being written carries a placeholder size; take what is on disk.
                dataLength = size <= 0 || body + size > file.Length ? file.Length - body : size;
                break;
            }

            if (size < 0)
            {
                break;
            }

            pos = body + size + (size & 1);
        }

        if (dataOffset < 0 || channels != 2 || sampleRate != SampleRate)
        {
            return null;
        }

        if (formatTag == 1 && bitsPerSample == 16)
        {
            var pcm = new byte[dataLength & ~3];
            Buffer.BlockCopy(file, dataOffset, pcm, 0, pcm.Length);
            return pcm;
        }

        if (formatTag == 3 && bitsPerSample == 32)
        {
            var samples = dataLength / 4;
            var pcm = new byte[(samples * 2) & ~3];
            for (var i = 0; i < pcm.Length / 2; i++)
            {
                var value = BitConverter.ToSingle(file, dataOffset + i * 4);
                var scaled = (int)Math.Round(Math.Max(-1f, Math.Min(1f, value)) * 32767f);
                pcm[i * 2] = (byte)(scaled & 0xff);
                pcm[i * 2 + 1] = (byte)((scaled >> 8) & 0xff);
            }

            return pcm;
        }

        return null;
    }

    private static string ReadFourCc(byte[] bytes, int offset)
    {
        return new string(new[] { (char)bytes[offset], (char)bytes[offset + 1], (char)bytes[offset + 2], (char)bytes[offset + 3] });
    }

    private static void WriteWav(string path, byte[] pcm16)
    {
        using (var stream = new FileStream(path, FileMode.Create))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + pcm16.Length);
            writer.Write("WAVE".ToCharArray());
            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 4);
            writer.Write((ushort)4);
            writer.Write((ushort)16);
            writer.Write("data".ToCharArray());
            writer.Write(pcm16.Length);
            writer.Write(pcm16);
        }
    }

    private static double RmsDbfs(byte[] pcm)
    {
        double energy = 0;
        long count = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var value = (short)(pcm[i] | (pcm[i + 1] << 8));
            energy += value * (double)value;
            count++;
        }

        if (count == 0 || energy <= 0)
        {
            return double.NegativeInfinity;
        }

        return 20.0 * Math.Log10(Math.Sqrt(energy / count) / 32768.0);
    }

    // === Self-test: the field-shaped fixtures from PcmAudioTests, condensed ===

    private static int SelfTest()
    {
        var failures = 0;

        // A timestamp-aligned fixed lag at gain 0.9 with a chime tone. This is the shape the
        // direct QPC-stamped chm/gam writers are required to produce.
        const int frames = 288000;
        var reference = BandLimitedNoise(frames, 7, 8000);
        var mixture = new short[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            const double delay = 873.0;
            for (var ch = 0; ch < 2; ch++)
            {
                mixture[f * 2 + ch] = (short)Math.Round(0.9 * SampleAt(reference, f + delay, ch));
            }
        }

        for (var f = 4800; f < 14400; f++)
        {
            var tone = (short)((f / 24) % 2 == 0 ? 2000 : -2000);
            mixture[f * 2] += tone;
            mixture[f * 2 + 1] += tone;
        }

        var mixBytes = ToBytes(mixture);
        var outcome = PcmAudio.CancelCorrelated(
            mixBytes, ToBytes(reference), out var d,
            preferEarlyAlignmentWindow: true,
            verificationLagRadiusFrames: 480);
        Console.WriteLine($"fixed:  outcome={outcome} lag={d.StartLagMs:0.000}->{d.EndLagMs:0.000}ms gain={d.Gain:0.00} corr={d.Correlation:0.000} supp={d.SuppressionDb:0.0}dB");
        if (outcome != PcmCancellationOutcome.CancelledVerified)
        {
            failures++;
        }

        // If two supposedly stamped tracks ever drift apart, do not report a false success. The
        // wide residual proof must catch it; subtraction itself remains one fixed full-slice lag.
        var driftingMixture = new short[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var delay = 873.0 + 10.0 * f / frames;
            for (var ch = 0; ch < 2; ch++)
            {
                driftingMixture[f * 2 + ch] =
                    (short)Math.Round(0.9 * SampleAt(reference, f + delay, ch));
            }
        }

        outcome = PcmAudio.CancelCorrelated(
            ToBytes(driftingMixture), ToBytes(reference), out d,
            preferEarlyAlignmentWindow: true,
            verificationLagRadiusFrames: 480);
        Console.WriteLine($"drift:  outcome={outcome} lag={d.StartLagMs:0.000}->{d.EndLagMs:0.000}ms gain={d.Gain:0.00} corr={d.Correlation:0.000} supp={d.SuppressionDb:0.0}dB");
        if (outcome != PcmCancellationOutcome.Unseparable)
        {
            failures++;
        }

        // Unrelated loud reference must pass through clean.
        var random = new Random(17);
        var noiseA = new short[96000];
        var noiseB = new short[96000];
        for (var i = 0; i < noiseA.Length; i++) { noiseA[i] = (short)random.Next(-8000, 8001); }
        for (var i = 0; i < noiseB.Length; i++) { noiseB[i] = (short)random.Next(-8000, 8001); }
        outcome = PcmAudio.CancelCorrelated(
            ToBytes(noiseA), ToBytes(noiseB), out d,
            preferEarlyAlignmentWindow: true,
            verificationLagRadiusFrames: 480);
        Console.WriteLine($"clean:  outcome={outcome} gain={d.Gain:0.000} corr={d.Correlation:0.000}");
        if (outcome != PcmCancellationOutcome.CleanNoGameDetected)
        {
            failures++;
        }

        Console.WriteLine(failures == 0 ? "selftest PASS" : "selftest FAIL");
        return failures;
    }

    private static short[] BandLimitedNoise(int frames, int seed, int amplitude)
    {
        var random = new Random(seed);
        var raw = new double[frames + 16];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = random.Next(-amplitude, amplitude + 1);
        }

        var samples = new short[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            double left = 0, right = 0;
            for (var k = 0; k < 8; k++)
            {
                left += raw[frame + k];
                right += raw[frame + k + 4];
            }

            samples[frame * 2] = (short)(left / 8);
            samples[frame * 2 + 1] = (short)(right / 8);
        }

        return samples;
    }

    private static double SampleAt(short[] samples, double framePosition, int channel)
    {
        var frames = samples.Length / 2;
        var lower = (int)Math.Floor(framePosition);
        var fraction = framePosition - lower;
        double first = lower >= 0 && lower < frames ? samples[lower * 2 + channel] : 0;
        var upper = lower + 1;
        double second = upper >= 0 && upper < frames ? samples[upper * 2 + channel] : 0;
        return first + (second - first) * fraction;
    }

    private static byte[] ToBytes(short[] values)
    {
        var bytes = new byte[values.Length * 2];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
