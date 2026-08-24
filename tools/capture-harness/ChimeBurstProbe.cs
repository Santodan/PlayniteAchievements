// Burst test for the chime pipeline: two toast waves of three achievements, on the REAL recorder
// plumbing. Unlike ChimeSeparationProbe (raw loopback clients), this drives two actual
// AudioLoopbackRecorder instances — the GameOnly main recorder and the chime sidecar — exactly as
// UnlockRecordingService wires them, so the mixer graph, direct packet timestamping, wall-clock
// main pump, gap padding, and chunk rotation are all exercised. The process topology is the
// Playnite-launched-emulator shape: this process plays the wave chimes (UniPlaySong's role) while
// a spawned child plays a continuous AM-warbled game tone (RetroArch's role).
//
// A wave of three achievements plays ONE chime (highest tier wins, ToastNotificationService), so
// two waves of three means two chimes at wave cadence: with the default 6 s toast, wave 2's chime
// fires ~7.5 s after wave 1's. Each wave's chime uses a distinct frequency (440 / 587 Hz) so the
// wrong wave's chime showing up in a slice is directly measurable.
//
// Per wave, the probe replicates the production sidecar read (slice = ownSound ..
// +min(toast, 4 s cap)+0.5 s tail), runs PcmAudio.CancelCorrelated against the timestamped gam_
// reference, and asserts by Goertzel power:
//   - aud_ (GameOnly main track) never contains either chime
//   - gam_ exists even though the tree probe returned unknown (the always-cancel fix)
//   - each wave's chm_ slice contains its OWN chime and not the other wave's (the slice cap)
//   - cancellation removes the game tone (>= 10 dB) while the wave's chime survives (within 3 dB)
//
//   ChimeBurstProbe.exe [--keep]                    ~25 s run, plays quiet tones
//   ChimeBurstProbe.exe --tone f s [amp] [amHz]     child mode

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.Recording;

// AudioLoopbackRecorder rotates chunks on UnlockRecordingService.SegmentSeconds; the real class
// does not compile standalone, so mirror the one constant. Keep in sync with
// source\Services\Recording\UnlockRecordingService.cs.
namespace PlayniteAchievements.Services.Recording
{
    internal static class UnlockRecordingService
    {
        internal const int SegmentSeconds = 5;
    }
}

internal static class ChimeBurstProbe
{
    private const int SampleRate = 48000;
    private const double GameToneHz = 1320;
    private const double Wave1ChimeHz = 440;
    private const double Wave2ChimeHz = 587;

    // Production timing being replicated. Wave cadence: with the default 6 s toast the next
    // sequential wave's chime fires ~duration+1.5 s after this one (UnlockRecordingService's
    // wave-cadence comment). Slice: min(toast, ChimeMaxSliceSeconds) + ChimeTailBeyondToastSeconds.
    private const double ToastDurationSeconds = 6.0;
    private const double WaveGapSeconds = 7.5;
    private const double ChimeMaxSliceSeconds = 4.0;
    private const double ChimeTailBeyondToastSeconds = 0.5;

    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--tone")
        {
            PlayTone(
                double.Parse(args[1], CultureInfo.InvariantCulture),
                double.Parse(args[2], CultureInfo.InvariantCulture),
                args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0.25,
                args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 0,
                args.Length > 5 ? double.Parse(args[5], CultureInfo.InvariantCulture) : 0);
            return 0;
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            Console.WriteLine("process loopback unsupported (needs Win10 19041+ and the win10.manifest build)");
            return 2;
        }

        var keep = args.Contains("--keep");
        var bufferDir = Path.Combine(
            Path.GetTempPath(),
            "chime-burst-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(bufferDir);
        Console.WriteLine("buffer: " + bufferDir);

        var exe = Process.GetCurrentProcess().MainModule.FileName;
        // The game signal is band-limited noise with the marker tone embedded: a pure tone's
        // near-periodic autocorrelation would give the cancellation's lag search ambiguous peaks
        // every carrier period, which is a signal pathology rather than a pipeline defect.
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Format(CultureInfo.InvariantCulture, "--tone {0} 30 0.005 3 0.004", GameToneHz),
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        AudioLoopbackRecorder main = null;
        AudioLoopbackRecorder sidecar = null;
        try
        {
            Thread.Sleep(1500); // child render stream up

            // Wired exactly like UnlockRecordingService: GameOnly main + chime sidecar. The tree
            // probe deliberately answers "unknown" — after the always-cancel fix, GameOnly must
            // capture the reference and require cancellation regardless of the probe.
            main = new AudioLoopbackRecorder(
                bufferDir, null, RecordingAudioSource.GameOnly,
                includeMicrophone: false,
                gameProcessId: () => child.Id,
                isGameInPlayniteTree: _ => null);
            sidecar = new AudioLoopbackRecorder(
                bufferDir, null, RecordingAudioSource.GameOnly,
                includeMicrophone: false,
                gameProcessId: () => child.Id,
                isGameInPlayniteTree: _ => null,
                capturePlayniteChimes: true);

            if (!main.Start() || !sidecar.Start())
            {
                Console.WriteLine("recorder failed to start — see one Warn above if a logger were attached");
                return 2;
            }

            Console.WriteLine($"main recorder ChimeCaptureMode = {main.ChimeCaptureMode}");
            Check(main.ChimeCaptureMode == PlayniteChimeCaptureMode.CancelGameReference,
                "GameOnly always uses CancelGameReference (tree probe returned unknown)",
                main.ChimeCaptureMode.ToString());

            Thread.Sleep(2000); // game-only lead-in

            Console.WriteLine("wave 1 chime (3 achievements -> one sound)...");
            var sound1Utc = CaptureTimelineClock.UtcNow;
            PlayTone(Wave1ChimeHz, 2.5, 0.006, 0);

            Thread.Sleep((int)((WaveGapSeconds - 2.5) * 1000));

            Console.WriteLine("wave 2 chime...");
            var sound2Utc = CaptureTimelineClock.UtcNow;
            PlayTone(Wave2ChimeHz, 2.5, 0.006, 0);

            Thread.Sleep(3500); // run past wave 2's slice end (sound2 + 4.5s)

            main.Stop();
            sidecar.Stop();
            main.Dispose();
            sidecar.Dispose();
            main = null;
            sidecar = null;

            Analyze(bufferDir, sound1Utc, sound2Utc);
        }
        finally
        {
            try { main?.Dispose(); } catch { }
            try { sidecar?.Dispose(); } catch { }
            try { if (!child.HasExited) { child.Kill(); } } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
        if (_failures == 0 && !keep)
        {
            try { Directory.Delete(bufferDir, true); } catch { }
        }
        else
        {
            Console.WriteLine("chunks kept at " + bufferDir);
        }

        return _failures;
    }

    // === Analysis ===

    private static void Analyze(string bufferDir, DateTime sound1Utc, DateTime sound2Utc)
    {
        var aud = LoadTrack(bufferDir, "aud_");
        var chm = LoadTrack(bufferDir, "chm_");
        var gam = LoadTrack(bufferDir, "gam_");
        Console.WriteLine();
        Console.WriteLine($"chunks: aud={aud.Count} chm={chm.Count} gam={gam.Count}");
        Check(aud.Count > 0, "main track wrote aud_ chunks", aud.Count.ToString());
        Check(chm.Count > 0, "sidecar wrote chm_ chunks", chm.Count.ToString());
        Check(gam.Count > 0, "timestamped game reference wrote gam_ chunks despite unknown tree probe", gam.Count.ToString());
        if (aud.Count == 0 || chm.Count == 0 || gam.Count == 0)
        {
            return;
        }

        CheckChunkTimeline("aud", aud);
        CheckChunkTimeline("chm", chm);
        CheckChunkTimeline("gam", gam);
        var mainReferenceAnchorFrames = RecordingPaths.AudioFrameAt(
            aud[0].StartUtc, gam[0].StartUtc, SampleRate);
        Check(Math.Abs(mainReferenceAnchorFrames) <= 1,
            "aud_ and gam_ share their source packet's timeline anchor",
            $"delta {mainReferenceAnchorFrames} frame(s)");

        var sliceSeconds = Math.Min(ToastDurationSeconds, ChimeMaxSliceSeconds) + ChimeTailBeyondToastSeconds;
        var waves = new[]
        {
            new Wave { Name = "wave 1", SoundUtc = sound1Utc, OwnHz = Wave1ChimeHz, OtherHz = Wave2ChimeHz },
            new Wave { Name = "wave 2", SoundUtc = sound2Utc, OwnHz = Wave2ChimeHz, OtherHz = Wave1ChimeHz },
        };

        foreach (var wave in waves)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {wave.Name}: slice {wave.SoundUtc:HH:mm:ss.fff} +{sliceSeconds:0.0}s ---");
            var sliceEnd = wave.SoundUtc.AddSeconds(sliceSeconds);
            var chmSlice = ReadWindow(chm, wave.SoundUtc, sliceEnd);
            var gamSlice = ReadWindow(gam, wave.SoundUtc, sliceEnd);
            var audSlice = ReadWindow(aud, wave.SoundUtc, sliceEnd);
            var frames = chmSlice.Length / 4;

            // Steady middle of this wave's chime, and an equal-length window after it ends, for
            // presence checks. The game signal is broadband noise, so absolute power in a chime
            // bin is dominated by the noise floor — chime leakage is the DIFFERENCE between the
            // during-chime and after-chime windows, not the absolute level.
            var p0 = (int)(0.4 * SampleRate);
            var p1 = (int)(2.1 * SampleRate);
            var a0 = (int)(2.7 * SampleRate);
            var a1 = (int)(4.4 * SampleRate);

            var audOwnDuring = GoertzelDb(audSlice, p0, p1, wave.OwnHz);
            var audOwnAfter = GoertzelDb(audSlice, a0, a1, wave.OwnHz);
            var audGame = GoertzelDb(audSlice, p0, p1, GameToneHz);
            var chmOtherWhole = GoertzelDb(chmSlice, 0, frames, wave.OtherHz);
            var chmOwnWhole = GoertzelDb(chmSlice, 0, frames, wave.OwnHz);

            // A single Goertzel bin over noise has ~5-6 dB run-to-run spread (2 degrees of
            // freedom), so only a double-digit rise reads as leakage rather than variance.
            Check(audOwnDuring - audOwnAfter <= 10,
                "aud_ main track shows no chime leak (chime-band power flat across the chime window)",
                $"during {audOwnDuring:0.0} vs after {audOwnAfter:0.0} dB");
            Check(audGame > audOwnAfter + 15,
                "aud_ main track carries the game marker tone",
                $"marker {audGame:0.0} vs noise floor {audOwnAfter:0.0} dB");
            Check(chmOwnWhole > chmOtherWhole + 15,
                $"chm_ slice holds its own chime only (the other wave's is {WaveGapSeconds:0.0}s away, cap is {sliceSeconds:0.0}s)",
                $"own {chmOwnWhole:0.0} vs other-wave floor {chmOtherWhole:0.0} dB");

            var before = (byte[])chmSlice.Clone();
            var outcome = PcmAudio.CancelCorrelated(
                chmSlice, gamSlice, out var d,
                preferEarlyAlignmentWindow: true,
                verificationLagRadiusFrames: 480);
            Console.WriteLine($"cancellation: outcome={outcome} lag={d.StartLagMs:0.000}->{d.EndLagMs:0.000}ms gain={d.Gain:0.00} corr={d.Correlation:0.000} supp={d.SuppressionDb:0.0}dB");
            Check(outcome == PcmCancellationOutcome.CancelledVerified, "cancellation verified", outcome.ToString());
            if (outcome == PcmCancellationOutcome.CancelledVerified)
            {
                // Whole-slice windows: the quartile verification legitimately tolerates one
                // imperfect block around a pump tear, so a short fixed window that happens to
                // straddle the tear under-reads a pass that is clean everywhere else.
                var gameSuppression = GoertzelDb(before, 0, frames, GameToneHz) - GoertzelDb(chmSlice, 0, frames, GameToneHz);
                var chimeLoss = GoertzelDb(before, p0, p1, wave.OwnHz) - GoertzelDb(chmSlice, p0, p1, wave.OwnHz);
                Check(gameSuppression >= 10, "game tone suppressed >= 10dB in the cancelled slice", $"{gameSuppression:0.0}dB");
                Check(Math.Abs(chimeLoss) <= 3, "wave's own chime survives within 3dB", $"lost {chimeLoss:0.0}dB");
            }
        }
    }

    private static void CheckChunkTimeline(string name, List<Chunk> chunks)
    {
        long worstDeltaFrames = 0;
        for (var index = 1; index < chunks.Count; index++)
        {
            var deltaFrames = RecordingPaths.AudioFrameAt(
                chunks[index - 1].EndUtc, chunks[index].StartUtc, SampleRate);
            if (Math.Abs(deltaFrames) > Math.Abs(worstDeltaFrames))
            {
                worstDeltaFrames = deltaFrames;
            }
        }

        Check(Math.Abs(worstDeltaFrames) <= 1,
            $"{name}_ chunk timestamps are sample-contiguous",
            $"worst boundary delta {worstDeltaFrames} frame(s)");
    }

    private sealed class Wave
    {
        public string Name;
        public DateTime SoundUtc;
        public double OwnHz;
        public double OtherHz;
    }

    private static void Check(bool condition, string what, string detail)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what + " (" + detail + ")");
        if (!condition)
        {
            _failures++;
        }
    }

    /// <summary>Normalized Goertzel power at one frequency over [startFrame, endFrame), in dB.</summary>
    private static double GoertzelDb(byte[] pcm16Stereo, int startFrame, int endFrame, double frequency)
    {
        var n = endFrame - startFrame;
        var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * frequency / SampleRate);
        double s1 = 0, s2 = 0;
        for (var frame = startFrame; frame < endFrame; frame++)
        {
            var left = (short)(pcm16Stereo[frame * 4] | (pcm16Stereo[frame * 4 + 1] << 8));
            var right = (short)(pcm16Stereo[frame * 4 + 2] | (pcm16Stereo[frame * 4 + 3] << 8));
            var s0 = (left + right) * 0.5 + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1 + s2 * s2 - coefficient * s1 * s2) / ((double)n * n);
        return 10.0 * Math.Log10(Math.Max(power, 1e-12));
    }

    // === Chunk loading (float32/int16 RIFF, placed by filename UTC; same as ChimeCancelProbe) ===

    private sealed class Chunk
    {
        public DateTime StartUtc;
        public byte[] Pcm;
        public DateTime EndUtc => RecordingPaths.AudioFrameUtc(
            StartUtc, Pcm.Length / PcmAudio.BlockAlign, SampleRate);
    }

    private static List<Chunk> LoadTrack(string dir, string prefix)
    {
        var chunks = new List<Chunk>();
        foreach (var path in Directory.EnumerateFiles(dir, prefix + "*.wav"))
        {
            var body = Path.GetFileNameWithoutExtension(path).Substring(prefix.Length);
            if (body.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                body = body.Substring(0, body.Length - 1);
            }

            if (!DateTime.TryParseExact(
                    body,
                    new[] { "yyyyMMdd-HHmmssfffffff", "yyyyMMdd-HHmmssfff", "yyyyMMdd-HHmmss" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startUtc))
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

    private static byte[] ReadWavAsPcm16(string path)
    {
        byte[] file;
        try
        {
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

        int formatTag = 0, channels = 0, rate = 0, bits = 0;
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
                rate = BitConverter.ToInt32(file, body + 4);
                bits = BitConverter.ToUInt16(file, body + 14);
                if (formatTag == 0xFFFE && body + 26 <= file.Length)
                {
                    formatTag = BitConverter.ToUInt16(file, body + 24);
                }
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = size <= 0 || body + size > file.Length ? file.Length - body : size;
                break;
            }

            if (size < 0)
            {
                break;
            }

            pos = body + size + (size & 1);
        }

        if (dataOffset < 0 || channels != 2 || rate != SampleRate)
        {
            return null;
        }

        if (formatTag == 1 && bits == 16)
        {
            var pcm = new byte[dataLength & ~3];
            Buffer.BlockCopy(file, dataOffset, pcm, 0, pcm.Length);
            return pcm;
        }

        if (formatTag == 3 && bits == 32)
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

    // === Tone rendering ===

    private static void PlayTone(double frequency, double seconds, double amplitude, double amHz, double noiseAmplitude = 0)
    {
        using (var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200))
        {
            var provider = new ToneProvider(frequency, amplitude, amHz, seconds, noiseAmplitude);
            output.Init(provider);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed class ToneProvider : ISampleProvider
    {
        private readonly double _frequency;
        private readonly double _amplitude;
        private readonly double _amHz;
        private readonly double _noiseAmplitude;
        private readonly Random _random = new Random(1234);
        private double _noiseState;
        private long _remainingSamples;
        private long _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public ToneProvider(double frequency, double amplitude, double amHz, double seconds, double noiseAmplitude)
        {
            _frequency = frequency;
            _amplitude = amplitude;
            _amHz = amHz;
            _noiseAmplitude = noiseAmplitude;
            _remainingSamples = (long)(seconds * SampleRate) * 2;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var samples = (int)Math.Min(count, _remainingSamples);
            for (var i = 0; i < samples; i += 2)
            {
                var t = _position / (double)SampleRate;
                var envelope = _amHz > 0 ? 0.6 + 0.4 * Math.Sin(2.0 * Math.PI * _amHz * t) : 1.0;
                var value = _amplitude * envelope * Math.Sin(2.0 * Math.PI * _frequency * t);
                if (_noiseAmplitude > 0)
                {
                    // One-pole lowpassed white noise: broadband enough for a unique correlation
                    // peak, band-limited enough that sub-frame misalignment stays a small residual.
                    _noiseState += 0.25 * ((_random.NextDouble() * 2.0 - 1.0) - _noiseState);
                    value += _noiseAmplitude * _noiseState * 4.0;
                }

                var sample = (float)Math.Max(-1.0, Math.Min(1.0, value));
                buffer[offset + i] = sample;
                if (i + 1 < samples)
                {
                    buffer[offset + i + 1] = sample;
                }

                _position++;
            }

            _remainingSamples -= samples;
            return samples;
        }
    }
}
