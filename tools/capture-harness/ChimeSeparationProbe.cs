// End-to-end proof that Playnite-chime vs emulator audio separation works on REAL audio sessions,
// without Playnite. The probe recreates the exact process topology of a Playnite-launched emulator:
//
//   this process  ("Playnite")  — plays a 440 Hz chime tone via WASAPI (UniPlaySong's role)
//   child process ("emulator")  — plays an AM-warbled 1320 Hz game tone (RetroArch's role)
//
// and captures three streams with the plugin's real ProcessLoopbackCapture (compiled in from
// source, same for PcmAudio):
//
//   game    = include-tree on the CHILD pid    (GameOnly main track)     -> game tone only
//   sidecar = include-tree on OUR OWN pid      (chm_ chime sidecar)      -> both tones (child is in our tree)
//   outside = exclude-tree on our own pid      (FullSystem main track)   -> neither tone
//
// It then runs PcmAudio.CancelCorrelated(sidecar, game) — two INDEPENDENT loopback clients, so the
// real inter-client clock offset/drift is exercised — and asserts by Goertzel power that the game
// tone is suppressed while the chime tone survives.
//
//   ChimeSeparationProbe.exe                       run the probe (needs a default render device)
//   ChimeSeparationProbe.exe --tone f s [amp] [am] child mode: play a tone and exit
//
// Turn other audio down while it runs; the two include-tree captures are immune to other apps by
// construction, but the exclude-tree check is informational when something else is playing.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using NAudio.Wave;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.Recording;

internal static class ChimeSeparationProbe
{
    private const int SampleRate = 48000;
    private const double GameToneHz = 1320;
    private const double ChimeToneHz = 440;

    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--tone")
        {
            var frequency = double.Parse(args[1], CultureInfo.InvariantCulture);
            var seconds = double.Parse(args[2], CultureInfo.InvariantCulture);
            var amplitude = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0.25;
            var amHz = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 0;
            PlayTone(frequency, seconds, amplitude, amHz);
            return 0;
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            Console.WriteLine("process loopback unsupported on this OS (needs Win10 19041+)");
            return 2;
        }

        var exe = Process.GetCurrentProcess().MainModule.FileName;
        Console.WriteLine("spawning child \"emulator\" playing the game tone...");
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Format(CultureInfo.InvariantCulture, "--tone {0} 16 0.3 3", GameToneHz),
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        try
        {
            Thread.Sleep(1500); // let the child's render stream start

            var game = new Collector(new ProcessLoopbackCapture(child.Id, includeProcessTree: true), "game    (include child tree)");
            var sidecar = new Collector(new ProcessLoopbackCapture(Process.GetCurrentProcess().Id, includeProcessTree: true), "sidecar (include own tree)  ");
            var outside = new Collector(new ProcessLoopbackCapture(Process.GetCurrentProcess().Id, includeProcessTree: false), "outside (exclude own tree)  ");

            game.Start();
            sidecar.Start();
            outside.Start();
            if (!WaitForFirstPackets(game, sidecar, outside))
            {
                Console.WriteLine("no audio packets arrived within 5s — is a default render device active?");
                return 2;
            }

            Thread.Sleep(1500); // game-tone-only lead-in

            Console.WriteLine("playing the chime tone from this process...");
            var chimeStartUtc = DateTime.UtcNow;
            PlayTone(ChimeToneHz, 2.5, 0.25, 0);
            var chimeEndUtc = DateTime.UtcNow;

            Thread.Sleep(1000);
            game.Stop();
            sidecar.Stop();
            outside.Stop();

            Analyze(game, sidecar, outside, chimeStartUtc, chimeEndUtc);
        }
        finally
        {
            try { if (!child.HasExited) { child.Kill(); } } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
        return _failures;
    }

    // === Analysis ===

    private static void Analyze(Collector game, Collector sidecar, Collector outside, DateTime chimeStartUtc, DateTime chimeEndUtc)
    {
        // Align all three independent clients onto a shared timeline via their first-packet UTC.
        var commonStart = Max(game.FirstPacketUtc, Max(sidecar.FirstPacketUtc, outside.FirstPacketUtc));
        var gamePcm = game.AlignedPcm16(commonStart);
        var sidecarPcm = sidecar.AlignedPcm16(commonStart);
        var outsidePcm = outside.AlignedPcm16(commonStart);
        var frames = Math.Min(gamePcm.Length, Math.Min(sidecarPcm.Length, outsidePcm.Length)) / 4;
        Console.WriteLine();
        Console.WriteLine($"aligned timeline: {frames / (double)SampleRate:0.00}s from {commonStart:HH:mm:ss.fff} UTC");

        // Analysis window: the steady middle of the chime.
        var w0 = (int)((chimeStartUtc - commonStart).TotalSeconds * SampleRate) + SampleRate * 4 / 10;
        var w1 = (int)((chimeEndUtc - commonStart).TotalSeconds * SampleRate) - SampleRate * 4 / 10;
        if (w0 < 0 || w1 <= w0 || w1 > frames)
        {
            Console.WriteLine($"FAIL analysis window [{w0}..{w1}] does not fit the capture");
            _failures++;
            return;
        }

        Console.WriteLine();
        Console.WriteLine("stream                        chime 440Hz   game 1320Hz   (Goertzel power, dB rel. sidecar's own)");
        var sidecarChime = GoertzelDb(sidecarPcm, w0, w1, ChimeToneHz);
        var sidecarGame = GoertzelDb(sidecarPcm, w0, w1, GameToneHz);
        Report(sidecar.Name, 0, 0); // the sidecar is its own 0 dB reference

        var gameChime = GoertzelDb(gamePcm, w0, w1, ChimeToneHz) - sidecarChime;
        var gameGame = GoertzelDb(gamePcm, w0, w1, GameToneHz) - sidecarGame;
        Report(game.Name, gameChime, gameGame);
        var outsideChime = GoertzelDb(outsidePcm, w0, w1, ChimeToneHz) - sidecarChime;
        var outsideGame = GoertzelDb(outsidePcm, w0, w1, GameToneHz) - sidecarGame;
        Report(outside.Name, outsideChime, outsideGame);

        Console.WriteLine();
        Check(gameGame > -6, "game capture carries the game tone", $"{gameGame:0.0}dB");
        Check(gameChime < -30, "game capture excludes the parent's chime (GameOnly never records UniPlaySong)", $"{gameChime:0.0}dB");
        Check(outsideChime < -30 && outsideGame < -30,
            "excluded capture carries neither tone (FullSystem main track; informational if other audio was playing)",
            $"chime {outsideChime:0.0}dB game {outsideGame:0.0}dB");

        // The production question: can the game tone be removed from the sidecar while the chime
        // survives, across two independent loopback clients?
        var before = (byte[])sidecarPcm.Clone();
        var outcome = PcmAudio.CancelCorrelated(sidecarPcm, gamePcm, out var d);
        Console.WriteLine();
        Console.WriteLine($"cancellation: outcome={outcome} lag={d.StartLagMs:0.000}->{d.EndLagMs:0.000}ms gain={d.Gain:0.00} corr={d.Correlation:0.000} supp={d.SuppressionDb:0.0}dB");
        Check(outcome == PcmCancellationOutcome.CancelledVerified, "cancellation verified", outcome.ToString());
        if (outcome == PcmCancellationOutcome.CancelledVerified)
        {
            var gameSuppression = GoertzelDb(before, w0, w1, GameToneHz) - GoertzelDb(sidecarPcm, w0, w1, GameToneHz);
            var chimeLoss = GoertzelDb(before, w0, w1, ChimeToneHz) - GoertzelDb(sidecarPcm, w0, w1, ChimeToneHz);
            Check(gameSuppression >= 10, "game tone suppressed >= 10dB in the cancelled sidecar", $"{gameSuppression:0.0}dB");
            Check(Math.Abs(chimeLoss) <= 3, "chime tone survives within 3dB", $"lost {chimeLoss:0.0}dB");
        }
    }

    private static void Report(string name, double chimeDb, double gameDb)
    {
        Console.WriteLine($"{name}      {chimeDb,8:0.0}      {gameDb,8:0.0}");
    }

    private static void Check(bool condition, string what, string detail)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what + " (" + detail + ")");
        if (!condition)
        {
            _failures++;
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>Normalized Goertzel power at one frequency over [startFrame, endFrame), in dB.</summary>
    private static double GoertzelDb(byte[] pcm16Stereo, int startFrame, int endFrame, double frequency)
    {
        var n = endFrame - startFrame;
        var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * frequency / SampleRate);
        double s0 = 0, s1 = 0, s2 = 0;
        for (var frame = startFrame; frame < endFrame; frame++)
        {
            var left = (short)(pcm16Stereo[frame * 4] | (pcm16Stereo[frame * 4 + 1] << 8));
            var right = (short)(pcm16Stereo[frame * 4 + 2] | (pcm16Stereo[frame * 4 + 3] << 8));
            s0 = (left + right) * 0.5 + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1 + s2 * s2 - coefficient * s1 * s2) / ((double)n * n);
        return 10.0 * Math.Log10(Math.Max(power, 1e-12));
    }

    // === Capture collection ===

    private sealed class Collector
    {
        private readonly ProcessLoopbackCapture _capture;
        private readonly MemoryStream _bytes = new MemoryStream();

        public string Name { get; }

        public DateTime FirstPacketUtc => _capture.FirstPacketCaptureUtc ?? DateTime.MaxValue;

        public bool HasPackets => _capture.FirstPacketCaptureUtc.HasValue;

        public Collector(ProcessLoopbackCapture capture, string name)
        {
            _capture = capture;
            Name = name;
            _capture.DataAvailable += (s, e) =>
            {
                lock (_bytes)
                {
                    _bytes.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };
        }

        public void Start() => _capture.StartRecording();

        public void Stop()
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
        }

        /// <summary>Float32 stream converted to 16-bit PCM, trimmed to start at commonStartUtc.</summary>
        public byte[] AlignedPcm16(DateTime commonStartUtc)
        {
            byte[] raw;
            lock (_bytes)
            {
                raw = _bytes.ToArray();
            }

            var skipFrames = Math.Max(0, (int)Math.Round((commonStartUtc - FirstPacketUtc).TotalSeconds * SampleRate));
            var totalFrames = raw.Length / 8; // float32 stereo
            var frames = Math.Max(0, totalFrames - skipFrames);
            var pcm = new byte[frames * 4];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var value = BitConverter.ToSingle(raw, (skipFrames + frame) * 8 + channel * 4);
                    var scaled = (int)Math.Round(Math.Max(-1f, Math.Min(1f, value)) * 32767f);
                    pcm[frame * 4 + channel * 2] = (byte)(scaled & 0xff);
                    pcm[frame * 4 + channel * 2 + 1] = (byte)((scaled >> 8) & 0xff);
                }
            }

            return pcm;
        }
    }

    private static bool WaitForFirstPackets(params Collector[] collectors)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var ready = true;
            foreach (var collector in collectors)
            {
                ready &= collector.HasPackets;
            }

            if (ready)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    // === Tone rendering (both the child "game" and this process's "chime") ===

    private static void PlayTone(double frequency, double seconds, double amplitude, double amHz)
    {
        using (var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200))
        {
            var provider = new ToneProvider(frequency, amplitude, amHz, seconds);
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
        private long _remainingSamples;
        private long _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public ToneProvider(double frequency, double amplitude, double amHz, double seconds)
        {
            _frequency = frequency;
            _amplitude = amplitude;
            _amHz = amHz;
            _remainingSamples = (long)(seconds * SampleRate) * 2;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var samples = (int)Math.Min(count, _remainingSamples);
            for (var i = 0; i < samples; i += 2)
            {
                var t = _position / (double)SampleRate;
                var envelope = _amHz > 0 ? 0.6 + 0.4 * Math.Sin(2.0 * Math.PI * _amHz * t) : 1.0;
                var value = (float)(_amplitude * envelope * Math.Sin(2.0 * Math.PI * _frequency * t));
                buffer[offset + i] = value;
                if (i + 1 < samples)
                {
                    buffer[offset + i + 1] = value;
                }

                _position++;
            }

            _remainingSamples -= samples;
            return samples;
        }
    }
}
