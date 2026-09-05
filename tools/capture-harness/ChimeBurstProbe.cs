// Burst test for the exclusion-based clip tracks: two toast waves of three achievements, on the
// REAL recorder plumbing. Unlike ChimeSeparationProbe (raw loopback clients), this drives two
// actual AudioLoopbackRecorder instances concurrently — one Game Only, one Full System — exactly
// as UnlockRecordingService wires them (game pid + sound-host pid delegates), so the mixer graph,
// direct packet timestamping, wall-clock main pump, gap padding, chunk rotation, the 8-channel
// process captures and their stereo reduction are all exercised. The process topology is
// production's:
//
//   this process   (orchestrator; stands in for Playnite, plays nothing during the waves)
//   child "game"   plays a continuous AM-warbled game tone (an emulator's role)
//   child "host"   plays the wave chimes on schedule (PlayniteAchievements.SoundHost's role)
//
// A wave of three achievements plays ONE chime (highest tier wins, ToastNotificationService), so
// two waves of three means two chimes at wave cadence: with the default 6 s toast, wave 2's chime
// fires ~7.5 s after wave 1's. Each wave's chime uses a distinct frequency (440 / 587 Hz) so a
// chime leaking into a slice is directly measurable.
//
// Per wave and per mode, the probe reads the toast-plus-tail slice and asserts by Goertzel power:
//   - the clip track (aud_) carries the game marker tone
//   - the clip track shows no rise at the chime frequency during the chime: the sound host is
//     excluded from the capture (Full System) or was never inside the game tree (Game Only), so
//     nothing is subtracted and nothing can leak
//   - Game Only: the exclude-host fallback track (alt_) exists and satisfies the same two checks;
//     Full System writes no fallback
//   - production's ChimeCompositeDecision adds the composited chime to both modes' clips
//
// When exactly one controller (haptic) endpoint is connected, the game child additionally renders
// a 180 Hz actuator tone to it for the whole run — the real game-with-haptics topology. The probe
// also runs a plain STEREO process capture of the game tree, which folds the actuator channels
// into L/R the way every recorder capture did before the 8-channel format; that capture's
// haptic-to-game ratio is the contamination reference, and every clip track is asserted to sit
// >= 30 dB below it. No cancellation is involved: the recorder drops channels 2/3.
//
//   ChimeBurstProbe.exe [--keep] [--no-haptics] [--cold]     ~35 s run, plays quiet tones
//   ChimeBurstProbe.exe --tone f s [amp] [amHz] [--haptics]  game-child mode
//   ChimeBurstProbe.exe --host leadInSeconds [--cold]        sound-host-child mode

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
    private const double HapticToneHz = 180;

    // Production timing being replicated: each slice is read through its toast plus a tail.
    private const double ToastDurationSeconds = 6.0;
    private const double WaveGapSeconds = 7.5;
    private const double ChimeSeconds = 2.5;
    private const double SliceTailSeconds = 0.5;

    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--tone")
        {
            return RunGameChild(args);
        }

        if (args.Length >= 2 && args[0] == "--host")
        {
            return RunHostChild(args);
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            Console.WriteLine("process loopback unsupported (needs Win10 19041+ and the win10.manifest build)");
            return 2;
        }

        var keep = args.Contains("--keep");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var gameOnlyDir = Path.Combine(Path.GetTempPath(), "chime-burst-" + stamp + "-go");
        var fullSystemDir = Path.Combine(Path.GetTempPath(), "chime-burst-" + stamp + "-fs");
        Directory.CreateDirectory(gameOnlyDir);
        Directory.CreateDirectory(fullSystemDir);
        Console.WriteLine("buffers: " + gameOnlyDir + " / " + fullSystemDir);

        var exe = Process.GetCurrentProcess().MainModule.FileName;
        string controllerName = null;
        var hapticsLayer = !args.Contains("--no-haptics") &&
            HasSingleControllerEndpoint(out controllerName);
        Console.WriteLine(hapticsLayer
            ? $"haptic layer: ON — the game child also renders {HapticToneHz} Hz to '{controllerName}'"
            : "haptic layer: off (no single controller endpoint connected)");

        // The game signal is band-limited noise with the marker tone embedded, so a chime bin's
        // leakage is measured as its during-vs-after rise above that noise rather than as an
        // absolute level.
        var game = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Format(CultureInfo.InvariantCulture, "--tone {0} 30 0.005 3 0.004", GameToneHz) +
                (hapticsLayer ? " --haptics" : string.Empty),
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        // The sound host is a separate long-lived process in production; here its stand-in lives
        // exactly as long as the two waves. Its pid must exist before the recorders start, as the
        // recorder reads it once at Start.
        var host = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--host 3.2" + (args.Contains("--cold") ? " --cold" : string.Empty),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });

        AudioLoopbackRecorder gameOnly = null;
        AudioLoopbackRecorder fullSystem = null;
        StereoCollector gameTreeStereo = null;
        try
        {
            Thread.Sleep(1500); // game render stream up

            // Wired exactly like UnlockRecordingService, once per mode, on the same pids.
            gameOnly = new AudioLoopbackRecorder(
                gameOnlyDir, null, RecordingAudioSource.GameOnly,
                includeMicrophone: false,
                gameProcessId: () => game.Id,
                soundHostProcessId: () => host.Id);
            fullSystem = new AudioLoopbackRecorder(
                fullSystemDir, null, RecordingAudioSource.FullSystem,
                includeMicrophone: false,
                gameProcessId: () => game.Id,
                soundHostProcessId: () => host.Id);

            if (!gameOnly.Start() || !fullSystem.Start())
            {
                Console.WriteLine("recorder failed to start — see one Warn above if a logger were attached");
                return 2;
            }

            Check(gameOnly.ClipTrack == ClipTrackKind.IncludeGame && gameOnly.HasFallbackTrack,
                "Game Only records the game tree with an exclude-host fallback track",
                $"{gameOnly.ClipTrack} fallback={gameOnly.HasFallbackTrack}");
            Check(fullSystem.ClipTrack == ClipTrackKind.ExcludeSoundHost && !fullSystem.HasFallbackTrack,
                "Full System records everything except the sound host, with no fallback",
                $"{fullSystem.ClipTrack} fallback={fullSystem.HasFallbackTrack}");
            Check(gameOnly.ExcludedSoundHostProcessId == host.Id && fullSystem.ExcludedSoundHostProcessId == host.Id,
                "both recorders keyed the exclusion off the sound host's pid",
                $"{gameOnly.ExcludedSoundHostProcessId}/{fullSystem.ExcludedSoundHostProcessId} vs {host.Id}");

            // Production's composite decision for these sessions while the host is still up:
            // the live sound never entered either clip track, so the composited copy is the only
            // chime a clip receives.
            var gameOnlyVerdict = ChimeCompositeDecision.Decide(
                true, gameOnly.ClipTrack, false, gameOnly.ExcludedSoundHostProcessId, host.Id);
            var fullSystemVerdict = ChimeCompositeDecision.Decide(
                true, fullSystem.ClipTrack, false, fullSystem.ExcludedSoundHostProcessId, host.Id);
            Check(ChimeCompositeDecision.AllowsComposite(gameOnlyVerdict) &&
                  ChimeCompositeDecision.AllowsComposite(fullSystemVerdict),
                "production adds the composited chime to both modes' clips",
                $"{gameOnlyVerdict} / {fullSystemVerdict}");

            if (hapticsLayer)
            {
                // The contamination reference: what a stereo capture of the game tree carries.
                gameTreeStereo = new StereoCollector(new ProcessLoopbackCapture(game.Id, includeProcessTree: true));
                gameTreeStereo.Start();
            }

            // The host child reports each chime's launch stamp (CaptureTimelineClock, the same
            // clock the recorder places packets on) the way SoundPlayedUtc does in production.
            var stamps = new List<DateTime>();
            string line;
            while (stamps.Count < 2 && (line = host.StandardOutput.ReadLine()) != null)
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && parts[0] == "chime" &&
                    long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                {
                    stamps.Add(new DateTime(ticks, DateTimeKind.Utc));
                    Console.WriteLine($"wave {stamps.Count} chime launched at {stamps[stamps.Count - 1]:HH:mm:ss.fff}");
                }
            }

            host.WaitForExit(20000);
            Check(stamps.Count == 2, "the sound-host child launched two chimes", stamps.Count.ToString());
            if (stamps.Count < 2)
            {
                return 2;
            }

            Thread.Sleep(3500); // run past wave 2's slice end

            gameOnly.Stop();
            fullSystem.Stop();
            gameOnly.Dispose();
            fullSystem.Dispose();
            gameOnly = null;
            fullSystem = null;
            var contamination = gameTreeStereo?.StopAndReadPcm16();
            gameTreeStereo = null;

            Analyze("Game Only", gameOnlyDir, ClipTrackKind.IncludeGame, stamps[0], stamps[1], contamination);
            Analyze("Full System", fullSystemDir, ClipTrackKind.ExcludeSoundHost, stamps[0], stamps[1], contamination);
        }
        finally
        {
            try { gameOnly?.Dispose(); } catch { }
            try { fullSystem?.Dispose(); } catch { }
            try { gameTreeStereo?.StopAndReadPcm16(); } catch { }
            try { if (!game.HasExited) { game.Kill(); } } catch { }
            try { if (!host.HasExited) { host.Kill(); } } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
        if (_failures == 0 && !keep)
        {
            try { Directory.Delete(gameOnlyDir, true); } catch { }
            try { Directory.Delete(fullSystemDir, true); } catch { }
        }
        else
        {
            Console.WriteLine("chunks kept at " + gameOnlyDir + " and " + fullSystemDir);
        }

        return _failures;
    }

    // === Child modes ===

    private static int RunGameChild(string[] args)
    {
        var seconds = double.Parse(args[2], CultureInfo.InvariantCulture);
        Thread haptics = null;
        if (args.Contains("--haptics"))
        {
            // The game process renders its haptic waveform to the controller's own endpoint
            // while its audio plays on the default output — the exact defect topology.
            haptics = new Thread(() => PlayHapticTone(seconds)) { IsBackground = true };
            haptics.Start();
        }

        PlayTone(
            double.Parse(args[1], CultureInfo.InvariantCulture),
            seconds,
            args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0.25,
            args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 0,
            args.Length > 5 ? double.Parse(args[5], CultureInfo.InvariantCulture) : 0);
        haptics?.Join();
        return 0;
    }

    /// <summary>
    /// Stands in for the sound host: plays the two wave chimes on the wave cadence and prints each
    /// launch stamp. Chimes carry their own band-limited noise (distinct seeds) for the same reason
    /// the game tone does: a pure sine's periodic autocorrelation lets the cancellation lag search
    /// lock onto any period multiple, a signal pathology real broadband chimes do not have.
    /// </summary>
    private static int RunHostChild(string[] args)
    {
        var leadIn = double.Parse(args[1], CultureInfo.InvariantCulture);
        if (!args.Contains("--cold"))
        {
            // The production host keeps its stream warm; a cold render stream starts on a ramping
            // engine client whose early samples are time-warped. --cold reproduces that state.
            PlayTone(220, 0.3, 0.0008, 0);
        }

        Thread.Sleep((int)(leadIn * 1000));
        Console.WriteLine("chime\t" + CaptureTimelineClock.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        PlayTone(Wave1ChimeHz, ChimeSeconds, 0.006, 0, 0.004, seed: 41);

        Thread.Sleep((int)((WaveGapSeconds - ChimeSeconds) * 1000));
        Console.WriteLine("chime\t" + CaptureTimelineClock.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        PlayTone(Wave2ChimeHz, ChimeSeconds, 0.006, 0, 0.004, seed: 42);
        return 0;
    }

    // === Analysis ===

    private static void Analyze(
        string mode,
        string bufferDir,
        ClipTrackKind clipTrack,
        DateTime sound1Utc,
        DateTime sound2Utc,
        byte[] gameTreeStereo)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {mode} ({clipTrack}) =====");
        var aud = LoadTrack(bufferDir, RecordingPaths.AudioChunkFilePrefix);
        var alt = LoadTrack(bufferDir, RecordingPaths.FallbackChunkFilePrefix);
        Console.WriteLine($"chunks: aud={aud.Count} alt={alt.Count}");
        Check(aud.Count > 0, $"{mode}: clip track wrote aud_ chunks", aud.Count.ToString());
        var expectsFallback = clipTrack == ClipTrackKind.IncludeGame;
        Check(alt.Count > 0 == expectsFallback,
            expectsFallback
                ? $"{mode}: exclude-host fallback wrote alt_ chunks"
                : $"{mode}: no fallback track (the clip track already excludes the host)",
            alt.Count.ToString());
        if (aud.Count == 0 || (expectsFallback && alt.Count == 0))
        {
            return;
        }

        CheckChunkTimeline("aud", aud);
        if (expectsFallback)
        {
            CheckChunkTimeline("alt", alt);
        }

        // A stereo capture of the game tree folds the actuator channels into L/R, which is what
        // every recorder capture did before the 8-channel format; its haptic-to-game ratio is the
        // contamination a clip track must sit well below. Ratios cancel the capture paths' volume
        // scaling. Both tones run for the whole session, so no slice alignment is needed.
        double contaminationRatioDb = 0;
        if (gameTreeStereo != null)
        {
            var frames = gameTreeStereo.Length / 4;
            var game = GoertzelDb(gameTreeStereo, 0, frames, GameToneHz);
            var haptic = GoertzelDb(gameTreeStereo, 0, frames, HapticToneHz);
            contaminationRatioDb = haptic - game;
            Console.WriteLine(
                $"stereo game-tree capture: haptic {haptic:0.0} dB, game {game:0.0} dB, " +
                $"ratio {contaminationRatioDb:0.0} dB (what a stereo capture carries)");
        }

        var sliceSeconds = ToastDurationSeconds + SliceTailSeconds;
        var waves = new[]
        {
            new Wave { Name = "wave 1", SoundUtc = sound1Utc, OwnHz = Wave1ChimeHz, OtherHz = Wave2ChimeHz },
            new Wave { Name = "wave 2", SoundUtc = sound2Utc, OwnHz = Wave2ChimeHz, OtherHz = Wave1ChimeHz },
        };

        foreach (var wave in waves)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {mode} {wave.Name}: slice {wave.SoundUtc:HH:mm:ss.fff} +{sliceSeconds:0.0}s ---");
            var sliceEnd = wave.SoundUtc.AddSeconds(sliceSeconds);
            AssertClipSlice(mode, wave, "aud_ (clip track)", ReadWindow(aud, wave.SoundUtc, sliceEnd),
                gameTreeStereo != null, contaminationRatioDb);
            if (expectsFallback)
            {
                AssertClipSlice(mode, wave, "alt_ (exclude-host fallback)", ReadWindow(alt, wave.SoundUtc, sliceEnd),
                    gameTreeStereo != null, contaminationRatioDb);
            }
        }
    }

    /// <summary>
    /// The three properties every clip track must have over a wave's slice: it carries the game,
    /// it shows no chime, and (with the haptic layer) it carries none of the actuator tone.
    /// </summary>
    private static void AssertClipSlice(
        string mode,
        Wave wave,
        string track,
        byte[] slice,
        bool hapticsLayer,
        double contaminationRatioDb)
    {
        // Steady middle of this wave's chime, and an equal-length window after it ends. The game
        // signal is broadband noise, so absolute power in a chime bin is dominated by the noise
        // floor — chime leakage is the DIFFERENCE between the during-chime and after-chime
        // windows, not the absolute level. A live chime in the endpoint mix measures as a rise of
        // 15 dB or more; a single-bin estimate of noise alone scatters by up to about 12 dB.
        var p0 = (int)(0.4 * SampleRate);
        var p1 = (int)(2.1 * SampleRate);
        var a0 = (int)(2.7 * SampleRate);
        var a1 = (int)(4.4 * SampleRate);

        var game = GoertzelDb(slice, p0, p1, GameToneHz);
        var ownDuring = GoertzelDb(slice, p0, p1, wave.OwnHz);
        var ownAfter = GoertzelDb(slice, a0, a1, wave.OwnHz);

        Check(game > ownAfter + 15,
            $"{mode} {wave.Name}: {track} carries the game marker tone",
            $"marker {game:0.0} vs noise floor {ownAfter:0.0} dB");
        Check(ownDuring - ownAfter <= 12,
            $"{mode} {wave.Name}: {track} shows no chime during the live chime",
            $"during {ownDuring:0.0} vs after {ownAfter:0.0} dB");
        if (hapticsLayer)
        {
            var haptic = GoertzelDb(slice, p0, p1, HapticToneHz);
            Check(contaminationRatioDb - (haptic - game) >= 30,
                $"{mode} {wave.Name}: {track} excludes the haptic tone by >= 30dB",
                $"stereo-capture ratio {contaminationRatioDb:0.0}dB vs track ratio {haptic - game:0.0}dB");
        }
    }

    /// <summary>A plain stereo process capture accumulated for the whole run and read as 16-bit PCM.</summary>
    private sealed class StereoCollector
    {
        private readonly ProcessLoopbackCapture _capture;
        private readonly MemoryStream _bytes = new MemoryStream();

        public StereoCollector(ProcessLoopbackCapture capture)
        {
            _capture = capture;
            _capture.DataAvailable += (s, e) =>
            {
                lock (_bytes)
                {
                    _bytes.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };
        }

        public void Start() => _capture.StartRecording();

        public byte[] StopAndReadPcm16()
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            byte[] raw;
            lock (_bytes)
            {
                raw = _bytes.ToArray();
            }

            var frames = raw.Length / 8; // float32 stereo
            var pcm = new byte[frames * 4];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var value = BitConverter.ToSingle(raw, frame * 8 + channel * 4);
                    var scaled = (int)Math.Round(Math.Max(-1f, Math.Min(1f, value)) * 32767f);
                    pcm[frame * 4 + channel * 2] = (byte)(scaled & 0xff);
                    pcm[frame * 4 + channel * 2 + 1] = (byte)((scaled >> 8) & 0xff);
                }
            }

            return pcm;
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

    // === Chunk loading (float32/int16 RIFF, placed by filename UTC) ===

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

    private static void PlayTone(
        double frequency,
        double seconds,
        double amplitude,
        double amHz,
        double noiseAmplitude = 0,
        int seed = 1234)
    {
        using (var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200))
        {
            var provider = new ToneProvider(
                frequency, amplitude, amHz, seconds, noiseAmplitude, seed: seed);
            output.Init(provider);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Whether exactly one controller (haptic) render endpoint is connected.</summary>
    private static bool HasSingleControllerEndpoint(out string name)
    {
        name = null;
        try
        {
            using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            {
                var controllers = enumerator
                    .EnumerateAudioEndPoints(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.DeviceState.Active)
                    .Where(d => HapticEndpointIds().Contains(d.ID))
                    .ToList();
                if (controllers.Count == 1)
                {
                    name = controllers[0].FriendlyName;
                }

                foreach (var device in controllers)
                {
                    try { device.Dispose(); } catch { }
                }

                return name != null;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Endpoint ids the recorder classifies as controller outputs. Classification goes through
    /// AudioEndpointEnumerator (the path the plugin uses); NAudio is still how this probe RENDERS
    /// its test tones, so the two are matched up by endpoint id.
    /// </summary>
    private static HashSet<string> HapticEndpointIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in AudioEndpointEnumerator.EnumerateActive(AudioDataFlow.Render))
        {
            if (RenderEndpointScan.IsHapticEndpoint(endpoint))
            {
                ids.Add(endpoint.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Game child helper: renders the haptic marker tone to the single controller endpoint's left
    /// actuator channel (native channel 2 on a DualSense) for the whole child tone duration.
    /// </summary>
    private static void PlayHapticTone(double seconds)
    {
        try
        {
            using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            {
                var controllers = enumerator
                    .EnumerateAudioEndPoints(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.DeviceState.Active)
                    .Where(d => HapticEndpointIds().Contains(d.ID))
                    .ToList();
                if (controllers.Count != 1)
                {
                    return;
                }

                var controller = controllers[0];
                int channels;
                using (var client = controller.AudioClient)
                {
                    channels = client.MixFormat.Channels;
                }

                if (channels < 3)
                {
                    return;
                }

                using (var output = new WasapiOut(
                    controller, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 200))
                {
                    // Band-limited noise rides on the rumble tone for the same reason as every
                    // other probe tone: a pure sine correlates equally at every half-period, which
                    // turns lag calibration into a comb of near-equal wrong answers (measured
                    // live: equal peaks every 2.7 ms). Real haptic streams are broadband.
                    output.Init(new ToneProvider(
                        HapticToneHz, 0.05, 7, seconds, 0.03, channels, activeChannel: 2, seed: 77));
                    output.Play();
                    while (output.PlaybackState == PlaybackState.Playing)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
        }
        catch
        {
            // A pad that disconnects mid-run just removes the haptic layer; the parent's
            // ratio checks fail loudly if the layer was promised but never rendered.
        }
    }

    private sealed class ToneProvider : ISampleProvider
    {
        private readonly double _frequency;
        private readonly double _amplitude;
        private readonly double _amHz;
        private readonly double _noiseAmplitude;
        private readonly int _channels;
        private readonly int _activeChannel;
        private readonly Random _random;
        private double _noiseState;
        private long _remainingSamples;
        private long _position;

        public WaveFormat WaveFormat { get; }

        public ToneProvider(
            double frequency,
            double amplitude,
            double amHz,
            double seconds,
            double noiseAmplitude,
            int channels = 2,
            int activeChannel = -1,
            int seed = 1234)
        {
            _frequency = frequency;
            _amplitude = amplitude;
            _amHz = amHz;
            _noiseAmplitude = noiseAmplitude;
            _channels = channels;
            _activeChannel = activeChannel;
            _random = new Random(seed);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);
            _remainingSamples = (long)(seconds * SampleRate) * channels;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var samples = (int)Math.Min(count, _remainingSamples);
            samples -= samples % _channels;
            for (var i = 0; i < samples; i += _channels)
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
                for (var channel = 0; channel < _channels; channel++)
                {
                    buffer[offset + i + channel] =
                        _activeChannel < 0 || channel == _activeChannel ? sample : 0f;
                }

                _position++;
            }

            _remainingSamples -= samples;
            return samples;
        }
    }
}
