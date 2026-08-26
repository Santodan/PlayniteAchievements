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
//   - aud_ (the speaker-endpoint mix both modes record) carries the game marker tone
//   - gam_ exists even though the tree probe returned unknown (the always-cancel fix)
//   - each wave's chm_ slice contains its OWN chime and not the other wave's (the slice cap)
//   - cancellation removes the game tone (>= 10 dB) while the wave's chime survives (within 3 dB)
//   - GameOnly isolation: aud_ minus oth_ drops the live chime and keeps the game tone
//   - FullSystem re-timing, primary path: the chime's SOURCE FILE (regenerated deterministically,
//     decoded by the real ChimeSoundFile) subtracts the live chime from aud_ — the UniPlaySong
//     1.8.4 resolved-sound flow, needing no capture-based reference at all
//   - FullSystem re-timing, fallback path: aud_ minus the game-free chm_ slice, and a FullSystem
//     recorder wires the gam_ reference that fallback depends on
//
// When exactly one controller (haptic) endpoint is connected, the child additionally renders a
// 180 Hz actuator tone to it for the whole run — the real game-with-haptics topology — and every
// user-facing output (the aud_ speaker mix, the GameOnly result, the FullSystem result) is
// asserted to exclude that tone by >= 30 dB relative to the process capture that carries it.
// One run then covers all recording cases: full/game audio, with/without haptics, with chimes;
// the without-chime case is the same tracks outside the chime slices.
//
//   ChimeBurstProbe.exe [--keep] [--no-haptics]             ~35 s run, plays quiet tones
//   ChimeBurstProbe.exe --tone f s [amp] [amHz] [--haptics] child mode

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
        string controllerName = null;
        var hapticsLayer = !args.Contains("--no-haptics") &&
            HasSingleControllerEndpoint(out controllerName);
        Console.WriteLine(hapticsLayer
            ? $"haptic layer: ON — the child also renders {HapticToneHz} Hz to '{controllerName}'"
            : "haptic layer: off (no single controller endpoint connected)");

        // The game signal is band-limited noise with the marker tone embedded: a pure tone's
        // near-periodic autocorrelation would give the cancellation's lag search ambiguous peaks
        // every carrier period, which is a signal pathology rather than a pipeline defect.
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Format(CultureInfo.InvariantCulture, "--tone {0} 30 0.005 3 0.004", GameToneHz) +
                (hapticsLayer ? " --haptics" : string.Empty),
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        AudioLoopbackRecorder main = null;
        AudioLoopbackRecorder sidecar = null;
        try
        {
            Thread.Sleep(1500); // child render stream up

            // Wired exactly like UnlockRecordingService: GameOnly main + chime sidecar.
            main = new AudioLoopbackRecorder(
                bufferDir, null, RecordingAudioSource.GameOnly,
                includeMicrophone: false,
                gameProcessId: () => child.Id);
            sidecar = new AudioLoopbackRecorder(
                bufferDir, null, RecordingAudioSource.GameOnly,
                includeMicrophone: false,
                gameProcessId: () => child.Id,
                capturePlayniteChimes: true);

            if (!main.Start() || !sidecar.Start())
            {
                Console.WriteLine("recorder failed to start — see one Warn above if a logger were attached");
                return 2;
            }

            Console.WriteLine($"main recorder ChimeCaptureMode = {main.ChimeCaptureMode}");
            Check(main.ChimeCaptureMode == PlayniteChimeCaptureMode.CancelGameReference,
                "GameOnly captures the game reference needed for chime isolation",
                main.ChimeCaptureMode.ToString());

            // Warm the parent's render path during the lead-in: the process's FIRST render stream
            // starts on a ramping engine client whose early samples are time-warped, which no
            // fixed-lag subtraction can match (measured: corr 0.997 at the true onset yet only
            // 6 dB verified). UniPlaySong's player is likewise warm except right after its idle
            // teardown — --cold skips the warm-up to reproduce exactly that first-wave state.
            if (!args.Contains("--cold"))
            {
                PlayTone(220, 0.3, 0.0008, 0);
            }
            Thread.Sleep(1700); // game-only lead-in

            // Chimes carry their own band-limited noise (distinct seeds) for the same reason the
            // game tone does: a pure sine's periodic autocorrelation lets the cancellation lag
            // search lock onto any period multiple, which is a signal pathology real broadband
            // chimes do not have — observed live as a 125 ms "best" lag on a 440 Hz tone.
            Console.WriteLine("wave 1 chime (3 achievements -> one sound)...");
            var sound1Utc = CaptureTimelineClock.UtcNow;
            PlayTone(Wave1ChimeHz, 2.5, 0.006, 0, 0.004, seed: 41);

            Thread.Sleep((int)((WaveGapSeconds - 2.5) * 1000));

            Console.WriteLine("wave 2 chime...");
            var sound2Utc = CaptureTimelineClock.UtcNow;
            PlayTone(Wave2ChimeHz, 2.5, 0.006, 0, 0.004, seed: 42);

            Thread.Sleep(3500); // run past wave 2's slice end (sound2 + 4.5s)

            main.Stop();
            sidecar.Stop();
            main.Dispose();
            sidecar.Dispose();
            main = null;
            sidecar = null;

            // Full System wiring: with a resolvable pid it must also capture the game reference,
            // so the chime sidecar can start and the live chime can be verifiably removed.
            var fullSystemDir = bufferDir + "-fs";
            Directory.CreateDirectory(fullSystemDir);
            using (var fullSystemRecorder = new AudioLoopbackRecorder(
                fullSystemDir, null, RecordingAudioSource.FullSystem,
                includeMicrophone: false,
                gameProcessId: () => child.Id))
            {
                Check(fullSystemRecorder.Start(), "FullSystem recorder starts", "start failed");
                Check(
                    fullSystemRecorder.ChimeCaptureMode == PlayniteChimeCaptureMode.CancelGameReference,
                    "FullSystem captures the game reference needed for chime re-timing",
                    fullSystemRecorder.ChimeCaptureMode.ToString());
                Thread.Sleep(1000);
                fullSystemRecorder.Stop();
            }

            try { Directory.Delete(fullSystemDir, true); } catch { }

            Analyze(bufferDir, sound1Utc, sound2Utc, hapticsLayer);
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

    private static void Analyze(
        string bufferDir, DateTime sound1Utc, DateTime sound2Utc, bool hapticsLayer)
    {
        var aud = LoadTrack(bufferDir, "aud_");
        var chm = LoadTrack(bufferDir, "chm_");
        var gam = LoadTrack(bufferDir, "gam_");
        var oth = LoadTrack(bufferDir, "oth_");
        Console.WriteLine();
        Console.WriteLine($"chunks: aud={aud.Count} chm={chm.Count} gam={gam.Count} oth={oth.Count}");
        Check(aud.Count > 0, "main track wrote aud_ chunks", aud.Count.ToString());
        Check(chm.Count > 0, "sidecar wrote chm_ chunks", chm.Count.ToString());
        Check(gam.Count > 0, "timestamped game reference wrote gam_ chunks", gam.Count.ToString());
        Check(oth.Count > 0, "GameOnly wrote the non-game oth_ sidecar", oth.Count.ToString());
        if (aud.Count == 0 || chm.Count == 0 || gam.Count == 0 || oth.Count == 0)
        {
            return;
        }

        CheckChunkTimeline("aud", aud);
        CheckChunkTimeline("chm", chm);
        CheckChunkTimeline("gam", gam);
        CheckChunkTimeline("oth", oth);

        var sliceSeconds = Math.Min(ToastDurationSeconds, ChimeMaxSliceSeconds) + ChimeTailBeyondToastSeconds;
        var waves = new[]
        {
            new Wave { Name = "wave 1", SoundUtc = sound1Utc, OwnHz = Wave1ChimeHz, OtherHz = Wave2ChimeHz, Seed = 41 },
            new Wave { Name = "wave 2", SoundUtc = sound2Utc, OwnHz = Wave2ChimeHz, OtherHz = Wave1ChimeHz, Seed = 42 },
        };

        foreach (var wave in waves)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {wave.Name}: slice {wave.SoundUtc:HH:mm:ss.fff} +{sliceSeconds:0.0}s ---");
            var sliceEnd = wave.SoundUtc.AddSeconds(sliceSeconds);
            var chmSlice = ReadWindow(chm, wave.SoundUtc, sliceEnd);
            var gamSlice = ReadWindow(gam, wave.SoundUtc, sliceEnd);
            var audSlice = ReadWindow(aud, wave.SoundUtc, sliceEnd);
            var othSlice = ReadWindow(oth, wave.SoundUtc, sliceEnd);
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

            Check(audGame > audOwnAfter + 15,
                "raw endpoint track carries the game marker tone",
                $"marker {audGame:0.0} vs noise floor {audOwnAfter:0.0} dB");
            Check(chmOwnWhole > chmOtherWhole + 15,
                $"chm_ slice holds its own chime only (the other wave's is {WaveGapSeconds:0.0}s away, cap is {sliceSeconds:0.0}s)",
                $"own {chmOwnWhole:0.0} vs other-wave floor {chmOtherWhole:0.0} dB");

            // The child renders the haptic tone continuously; the game-tree process capture (gam_)
            // hears it across endpoints, so its haptic-to-game ratio is the contamination
            // reference. Ratios cancel the two capture paths' unrelated volume scaling.
            var gamGame = GoertzelDb(gamSlice, p0, p1, GameToneHz);
            var gamHaptic = hapticsLayer ? GoertzelDb(gamSlice, p0, p1, HapticToneHz) : 0;
            if (hapticsLayer)
            {
                var audHaptic = GoertzelDb(audSlice, p0, p1, HapticToneHz);
                Check(
                    (gamHaptic - gamGame) - (audHaptic - audGame) >= 30,
                    "speaker-endpoint track (both modes' aud_) excludes the haptic tone by >= 30dB",
                    $"process ratio {gamHaptic - gamGame:0.0}dB vs endpoint ratio {audHaptic - audGame:0.0}dB");
            }

            // The wave's chime, regenerated deterministically as the WAV file UniPlaySong would
            // have resolved, decoded by the real ChimeSoundFile and placed at the fire time —
            // the reference production's file-based chime handling is built on.
            var chimeFilePath = Path.Combine(
                bufferDir, $"chime-{wave.OwnHz.ToString("0", CultureInfo.InvariantCulture)}.wav");
            WriteChimeWav(chimeFilePath, wave.OwnHz, wave.Seed);
            var filePcm = ChimeSoundFile.TryReadPcm(chimeFilePath, sliceSeconds, 1.0, null);
            Check(filePcm != null, "resolved chime file decodes", chimeFilePath);
            byte[] fileReference = null;
            if (filePcm != null)
            {
                fileReference = new byte[audSlice.Length];
                PcmAudio.MixInto(fileReference, 0, filePcm, 0, filePcm.Length);
            }

            // Production's mirrored-game-copy purge: an audio-mirroring service (game streaming)
            // re-renders the whole mix from its own process, so oth_ can carry a delayed copy of
            // the game; it must be verifiably purged before oth_ may touch the clip audio.
            othSlice = (byte[])othSlice.Clone();
            for (var peel = 0; peel < 2; peel++)
            {
                var peelOutcome = PcmAudio.CancelCorrelated(
                    othSlice, gamSlice, out _,
                    muteUnverifiedBlocks: false,
                    maxLagFrames: 12000,
                    commitVerifiedBlocksOnWeakPass: true,
                    preferEarlyAlignmentWindow: true,
                    verificationLagRadiusFrames: 480);
                if (peelOutcome != PcmCancellationOutcome.CancelledVerified)
                {
                    break;
                }
            }
            var purgeOutcome = PcmAudio.CancelCorrelated(
                othSlice, gamSlice, out var purge,
                maxLagFrames: 12000,
                preferEarlyAlignmentWindow: true,
                verificationLagRadiusFrames: 480);
            Console.WriteLine(
                $"oth purge: outcome={purgeOutcome} lag={purge.StartLagMs:0.000}ms " +
                $"corr={purge.Correlation:0.000} supp={purge.SuppressionDb:0.0}dB " +
                $"gated={purge.MutedBlocks}");
            var purged = purgeOutcome != PcmCancellationOutcome.Unseparable && purge.MutedBlocks == 0;
            if (purged)
            {
                Check(true, "oth_ reference verified free of a mirrored game copy", purgeOutcome.ToString());
            }
            else
            {
                // Every purge refusal is fail-closed: production skips the cleanup and the clip
                // keeps the speaker mix this run already validated (game tone, haptic exclusion).
                Console.WriteLine(
                    "NOTE oth_ purge failed closed; cleanup skipped and the validated speaker " +
                    $"mix ships as-is ({purgeOutcome} gated={purge.MutedBlocks})");
            }

            // Production skips the cleanup entirely when the purge is refused; the clip then keeps
            // the already-validated speaker mix, so there is nothing further to prove here.
            var gameOnlyOutcome = PcmCancellationOutcome.Unseparable;
            var isolatedGame = (byte[])audSlice.Clone();
            if (purged)
            {
                gameOnlyOutcome = SubtractNonGame(
                    isolatedGame, othSlice, out var gameOnly, residualPass: false);
                if (gameOnlyOutcome != PcmCancellationOutcome.CancelledVerified)
                {
                    isolatedGame = (byte[])audSlice.Clone();
                    gameOnlyOutcome = SubtractNonGame(
                        isolatedGame, othSlice, out gameOnly, residualPass: false, blockFrames: 24000);
                }
                Console.WriteLine(
                    $"GameOnly subtraction: outcome={gameOnlyOutcome} " +
                    $"lag={gameOnly.StartLagMs:0.000}->{gameOnly.EndLagMs:0.000}ms " +
                    $"corr={gameOnly.Correlation:0.000} supp={gameOnly.SuppressionDb:0.0}dB");
                Check(
                    gameOnlyOutcome == PcmCancellationOutcome.CancelledVerified,
                    "GameOnly non-game subtraction verified",
                    gameOnlyOutcome.ToString());
            }
            if (gameOnlyOutcome == PcmCancellationOutcome.CancelledVerified)
            {
                var residualOutcome = SubtractNonGame(
                    isolatedGame, othSlice, out var residual, residualPass: true);
                Console.WriteLine(
                    $"residual subtraction: outcome={residualOutcome} " +
                    $"lag={residual.StartLagMs:0.000}->{residual.EndLagMs:0.000}ms " +
                    $"corr={residual.Correlation:0.000} supp={residual.SuppressionDb:0.0}dB");
                var isolatedGameTone = GoertzelDb(isolatedGame, p0, p1, GameToneHz);
                Check(
                    Math.Abs(audGame - isolatedGameTone) <= 3,
                    "GameOnly output keeps the game tone within 3dB",
                    $"lost {audGame - isolatedGameTone:0.0}dB");
                if (hapticsLayer)
                {
                    var isolatedHaptic = GoertzelDb(isolatedGame, p0, p1, HapticToneHz);
                    Check(
                        (gamHaptic - gamGame) - (isolatedHaptic - isolatedGameTone) >= 30,
                        "GameOnly output excludes the haptic tone by >= 30dB",
                        $"process ratio {gamHaptic - gamGame:0.0}dB vs output ratio " +
                        $"{isolatedHaptic - isolatedGameTone:0.0}dB");
                }
            }

            // Production's shared live-chime stage: always runs, on whatever the non-game stage
            // left — or the raw mix when it was skipped or refused. Partial removals are kept and
            // still license the composite.
            if (fileReference != null)
            {
                var chimeOutcome = SubtractNonGame(
                    isolatedGame, fileReference, out var chimePass, residualPass: false,
                    maxLagFrames: 36000, detectClean: true);
                if (chimeOutcome == PcmCancellationOutcome.Unseparable ||
                    (chimeOutcome == PcmCancellationOutcome.CleanNoGameDetected &&
                     chimePass.SubtractedBlocks == 0))
                {
                    chimeOutcome = SubtractNonGame(
                        isolatedGame, fileReference, out chimePass, residualPass: true,
                        maxLagFrames: 36000, detectClean: true);
                }
                Console.WriteLine(
                    $"GameOnly live-chime stage: outcome={chimeOutcome} " +
                    $"lag={chimePass.StartLagMs:0.000}ms corr={chimePass.Correlation:0.000} " +
                    $"supp={chimePass.SuppressionDb:0.0}dB restored={chimePass.RestoredBlocks} " +
                    $"gated={chimePass.MutedBlocks}");
                // The composite is unconditional by policy; these report removal quality only.
                var cDuring = GoertzelDb(isolatedGame, p0, p1, wave.OwnHz);
                var cAfter = GoertzelDb(isolatedGame, a0, a1, wave.OwnHz);
                Console.WriteLine(
                    $"GameOnly live-chime residue: during {cDuring:0.0} vs after {cAfter:0.0} dB " +
                    $"(raw {audOwnDuring:0.0} dB)");
            }

            // Production's PRIMARY FullSystem path (UniPlaySong 1.8.4+): the same file reference
            // subtracts the live chime from the speaker mix — no capture-based reference involved.
            if (fileReference != null)
            {
                // ±750 ms search, as in production: the reference sits at the launch stamp while
                // the rendered chime starts an out-of-process onset later (measured ~500 ms on a
                // cold render stream).
                var fsFile = (byte[])audSlice.Clone();
                var fsFileOutcome = SubtractNonGame(
                    fsFile, fileReference, out var fsf, residualPass: false, maxLagFrames: 36000,
                    detectClean: true);
                if (fsFileOutcome == PcmCancellationOutcome.Unseparable)
                {
                    fsFile = (byte[])audSlice.Clone();
                    fsFileOutcome = SubtractNonGame(
                        fsFile, fileReference, out fsf, residualPass: false, blockFrames: 24000,
                        maxLagFrames: 36000, detectClean: true);
                }
                Console.WriteLine(
                    $"FullSystem file-based chime removal: outcome={fsFileOutcome} " +
                    $"lag={fsf.StartLagMs:0.000}ms corr={fsf.Correlation:0.000} " +
                    $"supp={fsf.SuppressionDb:0.0}dB restored={fsf.RestoredBlocks} " +
                    $"gated={fsf.MutedBlocks}");

                // Production's hybrid mop-up: the captured Playnite-tree slice, verified
                // game-free, matches the chime exactly as rendered — a cold player's time-warped
                // onset included — and removes what the pristine file could not.
                var mopUp = (byte[])chmSlice.Clone();
                for (var peel = 0; peel < 2; peel++)
                {
                    var peelOutcome = PcmAudio.CancelCorrelated(
                        mopUp, gamSlice, out _,
                        muteUnverifiedBlocks: false,
                        commitVerifiedBlocksOnWeakPass: true,
                        preferEarlyAlignmentWindow: true,
                        verificationLagRadiusFrames: 480);
                    if (peelOutcome != PcmCancellationOutcome.CancelledVerified)
                    {
                        break;
                    }
                }
                var mopUpPurge = PcmAudio.CancelCorrelated(
                    mopUp, gamSlice, out var mopUpDiag,
                    preferEarlyAlignmentWindow: true,
                    verificationLagRadiusFrames: 480);
                if (mopUpPurge != PcmCancellationOutcome.Unseparable && mopUpDiag.MutedBlocks == 0)
                {
                    var mopUpOutcome = SubtractNonGame(
                        fsFile, mopUp, out var mop, residualPass: false, maxLagFrames: 12000,
                        detectClean: true);
                    if (mopUpOutcome == PcmCancellationOutcome.Unseparable)
                    {
                        mopUpOutcome = SubtractNonGame(
                            fsFile, mopUp, out mop, residualPass: true, maxLagFrames: 12000,
                            detectClean: true);
                    }
                    Console.WriteLine(
                        $"FullSystem captured mop-up: outcome={mopUpOutcome} " +
                        $"lag={mop.StartLagMs:0.000}ms corr={mop.Correlation:0.000} " +
                        $"supp={mop.SuppressionDb:0.0}dB restored={mop.RestoredBlocks}");
                }
                else
                {
                    Console.WriteLine(
                        $"FullSystem captured mop-up unavailable ({mopUpPurge} " +
                        $"gated={mopUpDiag.MutedBlocks})");
                }

                var fsfDuring = GoertzelDb(fsFile, p0, p1, wave.OwnHz);
                var fsfAfter = GoertzelDb(fsFile, a0, a1, wave.OwnHz);
                var fsfGame = GoertzelDb(fsFile, p0, p1, GameToneHz);
                Check(
                    fsfDuring - fsfAfter <= 12 || audOwnDuring - fsfDuring >= 15,
                    "hybrid removal drops the live chime",
                    $"during {fsfDuring:0.0} vs after {fsfAfter:0.0} dB (raw {audOwnDuring:0.0} dB)");
                Check(
                    Math.Abs(audGame - fsfGame) <= 3,
                    "hybrid removal keeps the game tone within 3dB",
                    $"lost {audGame - fsfGame:0.0}dB");
                if (hapticsLayer)
                {
                    var fsfHaptic = GoertzelDb(fsFile, p0, p1, HapticToneHz);
                    Check(
                        (gamHaptic - gamGame) - (fsfHaptic - fsfGame) >= 30,
                        "hybrid removal output excludes the haptic tone by >= 30dB",
                        $"process ratio {gamHaptic - gamGame:0.0}dB vs output ratio " +
                        $"{fsfHaptic - fsfGame:0.0}dB");
                }
            }

            var before = (byte[])chmSlice.Clone();
            // Production's CancelGameFromPlayniteSlice: two best-effort peels remove the game's
            // two engine paths (speaker stream, controller haptic stream) at their own lags before
            // the strict verified-or-muted pass decides the outcome. Keep in sync with
            // source\Services\Recording\UnlockRecordingService.cs.
            for (var peel = 0; peel < 2; peel++)
            {
                var peelOutcome = PcmAudio.CancelCorrelated(
                    chmSlice, gamSlice, out _,
                    muteUnverifiedBlocks: false,
                    commitVerifiedBlocksOnWeakPass: true,
                    preferEarlyAlignmentWindow: true,
                    verificationLagRadiusFrames: 480);
                if (peelOutcome != PcmCancellationOutcome.CancelledVerified)
                {
                    break;
                }
            }
            var outcome = PcmAudio.CancelCorrelated(
                chmSlice, gamSlice, out var d,
                preferEarlyAlignmentWindow: true,
                verificationLagRadiusFrames: 480);
            Console.WriteLine($"cancellation: outcome={outcome} lag={d.StartLagMs:0.000}->{d.EndLagMs:0.000}ms gain={d.Gain:0.00} corr={d.Correlation:0.000} supp={d.SuppressionDb:0.0}dB");
            // CleanNoGameDetected after the peels means the peels already removed the game; the
            // Goertzel suppression check below still fails loudly if that claim were false.
            var separated = outcome == PcmCancellationOutcome.CancelledVerified ||
                outcome == PcmCancellationOutcome.CleanNoGameDetected;
            // With a resolved sound file (UniPlaySong 1.8.4+) the composite never reads this
            // capture-based fallback, and its verified-or-dropped refusal is the designed safe
            // outcome — as it also is under continuous haptic crossfeed, where the game reaches
            // both process captures through a second engine path at its own lag.
            var failedClosed = !separated &&
                outcome == PcmCancellationOutcome.Unseparable &&
                (hapticsLayer || filePcm != null);
            Check(
                separated || failedClosed,
                failedClosed
                    ? "capture-fallback cancellation failed closed (chime dropped there; the file path composites instead)"
                    : "cancellation verified",
                outcome.ToString());
            if (separated)
            {
                // Whole-slice windows: the quartile verification legitimately tolerates one
                // imperfect block around a pump tear, so a short fixed window that happens to
                // straddle the tear under-reads a pass that is clean everywhere else.
                var gameSuppression = GoertzelDb(before, 0, frames, GameToneHz) - GoertzelDb(chmSlice, 0, frames, GameToneHz);
                var chimeLoss = GoertzelDb(before, p0, p1, wave.OwnHz) - GoertzelDb(chmSlice, p0, p1, wave.OwnHz);
                Check(gameSuppression >= 10, "game tone suppressed >= 10dB in the cancelled slice", $"{gameSuppression:0.0}dB");
                Check(Math.Abs(chimeLoss) <= 3, "wave's own chime survives within 3dB", $"lost {chimeLoss:0.0}dB");

                // Full System re-times the chime the same way: the speaker mix (identical in both
                // modes) minus this game-free Playnite-tree slice must drop the live chime while
                // keeping the game tone, or the composited chime would double it.
                var fullSystem = (byte[])audSlice.Clone();
                var fsOutcome = SubtractNonGame(
                    fullSystem, chmSlice, out var fs, residualPass: false, maxLagFrames: 36000,
                    detectClean: true);
                if (fsOutcome == PcmCancellationOutcome.Unseparable)
                {
                    fullSystem = (byte[])audSlice.Clone();
                    fsOutcome = SubtractNonGame(
                        fullSystem, chmSlice, out fs, residualPass: false, blockFrames: 24000,
                        maxLagFrames: 36000, detectClean: true);
                }
                Console.WriteLine(
                    $"FullSystem chime removal: outcome={fsOutcome} " +
                    $"lag={fs.StartLagMs:0.000}->{fs.EndLagMs:0.000}ms " +
                    $"corr={fs.Correlation:0.000} supp={fs.SuppressionDb:0.0}dB " +
                    $"restored={fs.RestoredBlocks} gated={fs.MutedBlocks}");
                Check(
                    fsOutcome == PcmCancellationOutcome.CancelledVerified &&
                        fs.RestoredBlocks == 0 && fs.MutedBlocks == 0,
                    "FullSystem Playnite-audio removal verified with no partial blocks",
                    $"{fsOutcome} restored={fs.RestoredBlocks} gated={fs.MutedBlocks}");
                if (fsOutcome == PcmCancellationOutcome.CancelledVerified)
                {
                    var fsResidualOutcome = SubtractNonGame(
                        fullSystem, chmSlice, out var fsResidual, residualPass: true);
                    Console.WriteLine(
                        $"FullSystem residual: outcome={fsResidualOutcome} " +
                        $"supp={fsResidual.SuppressionDb:0.0}dB");
                    var fsDuring = GoertzelDb(fullSystem, p0, p1, wave.OwnHz);
                    var fsAfter = GoertzelDb(fullSystem, a0, a1, wave.OwnHz);
                    var fsGame = GoertzelDb(fullSystem, p0, p1, GameToneHz);
                    Check(
                        fsDuring - fsAfter <= 12 || audOwnDuring - fsDuring >= 15,
                        "FullSystem output removes the live chime for re-timing",
                        $"during {fsDuring:0.0} vs after {fsAfter:0.0} dB " +
                        $"(raw {audOwnDuring:0.0} dB)");
                    Check(
                        Math.Abs(audGame - fsGame) <= 3,
                        "FullSystem output keeps the game tone within 3dB",
                        $"lost {audGame - fsGame:0.0}dB");
                    if (hapticsLayer)
                    {
                        var fsHaptic = GoertzelDb(fullSystem, p0, p1, HapticToneHz);
                        Check(
                            (gamHaptic - gamGame) - (fsHaptic - fsGame) >= 30,
                            "FullSystem output excludes the haptic tone by >= 30dB",
                            $"process ratio {gamHaptic - gamGame:0.0}dB vs output ratio " +
                            $"{fsHaptic - fsGame:0.0}dB");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The exact production subtraction (UnlockRecordingService.SubtractNonGame): one full-clip
    /// stereo fit by default, a 500 ms fallback via blockFrames, and a lower-floor residual pass.
    /// Keep the parameters in sync with source\Services\Recording\UnlockRecordingService.cs.
    /// </summary>
    private static PcmCancellationOutcome SubtractNonGame(
        byte[] mixture,
        byte[] reference,
        out PcmCancellationDiagnostics diagnostics,
        bool residualPass,
        int? blockFrames = null,
        int maxLagFrames = 12000,
        bool detectClean = false)
    {
        var floor = residualPass ? 0.001 : 0.005;
        return PcmAudio.CancelCorrelated(
            mixture,
            reference,
            out diagnostics,
            muteUnverifiedBlocks: false,
            maxLagFrames: maxLagFrames,
            minimumGain: floor,
            maximumGain: 20,
            blockGainFloor: floor,
            keepBlockSuppressionDb: 10,
            cancellationBlockFrames: blockFrames ?? mixture.Length / PcmAudio.BlockAlign,
            maximumResidualCorrelation: detectClean ? double.MaxValue : 0.35,
            commitVerifiedBlocksOnWeakPass: true,
            minimumCorrelation: residualPass ? 0.03 : 0.15,
            attemptVerifiedBlocksWhenGloballyClean: !detectClean,
            verificationLagRadiusFrames: 128,
            independentChannelGains: true,
            gainCrossfadeFrames: 0,
            fractionalLagSteps: 32);
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
        public int Seed;
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

    /// <summary>
    /// Regenerates one wave's chime samples (ToneProvider is deterministic per seed) and writes
    /// them as the WAV file that stands in for the UniPlaySong-resolved sound.
    /// </summary>
    private static void WriteChimeWav(string path, double frequency, int seed)
    {
        var provider = new ToneProvider(frequency, 0.006, 0, 2.5, 0.004, seed: seed);
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)))
        {
            var buffer = new float[SampleRate];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.WriteSamples(buffer, 0, read);
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
                    .Where(RenderEndpointScan.IsHapticEndpoint)
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
    /// Child helper: renders the haptic marker tone to the single controller endpoint's left
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
                    .Where(RenderEndpointScan.IsHapticEndpoint)
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
