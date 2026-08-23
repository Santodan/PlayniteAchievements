// Measures the controller-haptics removal that the recorder does for real clips, without needing a
// DualSense attached. Two things are demonstrated:
//
//   1. the defect — process loopback is endpoint-agnostic, so audio this process renders to a
//      SECOND output device lands in the capture of this process anyway, exactly as a game's
//      haptic waveform does;
//   2. the fix — capturing that endpoint separately (ProcessLoopbackCapture.ForEndpoint) and
//      running PcmAudio.CancelCorrelated removes it, and the report says what it cost the audio
//      that was supposed to survive.
//
// Both tones are rendered by THIS process to two different endpoints, which is the topology of a
// game playing sound to the speakers and haptics to the pad.
//
//   HapticProbe.exe                      list active render endpoints and their classification
//   HapticProbe.exe --measure <index>    run the measurement against that endpoint (index from the list)
//
// Turn other audio down while it runs, and pick an endpoint you can leave playing a tone for a few
// seconds (an idle HDMI output is ideal).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Playnite.SDK;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.Recording;

internal static class HapticProbe
{
    private const int SampleRate = 48000;
    private const double GameToneHz = 1320;  // the audio that must survive
    private const double HapticToneHz = 180; // stands in for the pad's rumble waveform (AM, so the
                                             // correlation peak is unique rather than one of many)
    private const int HapticBlockFrames = 2400; // production's 50 ms transient-sized blocks

    private static int _failures;

    /// <summary>
    /// Runs the probe, keeping everything it printed. Double-clicking a console app closes the
    /// window the moment it exits, which reads as a crash, so the output is also written next to
    /// the exe as HapticProbe-report.txt and the window is held open when this process owns it.
    /// </summary>
    private static int Main(string[] args)
    {
        var transcript = new StringWriter();
        var console = Console.Out;
        Console.SetOut(new TeeWriter(console, transcript));
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex);
            return 1;
        }
        finally
        {
            Console.SetOut(console);
            SaveReport(transcript.ToString());
            HoldWindowOpen();
        }
    }

    private static void SaveReport(string text)
    {
        try
        {
            var path = Path.Combine(
                Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) ?? ".",
                "HapticProbe-report.txt");
            File.WriteAllText(path, text);
            Console.WriteLine();
            Console.WriteLine("Saved this report to " + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("(could not save a report file: " + ex.Message + ")");
        }
    }

    /// <summary>
    /// Waits for a key only when this process is the console's only client, i.e. it was launched by
    /// double-click rather than from a shell that will keep the window.
    /// </summary>
    private static void HoldWindowOpen()
    {
        try
        {
            var clients = new uint[4];
            if (GetConsoleProcessList(clients, (uint)clients.Length) > 1)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to close...");
            Console.ReadKey(true);
        }
        catch
        {
            // No console, or input is redirected: nothing to hold open.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    private static int Run(string[] args)
    {
        var devices = ListDevices();
        ListMicrophones();
        if (args.Length == 0)
        {
            // Double-click path: whoever runs this is answering "why were my clips not cleaned",
            // so also prove whether the endpoint the recorder picked can actually be captured.
            // Silent — nothing is rendered.
            var selected = RenderEndpointScan.FindHapticEndpoints(null);
            if (selected.Count > 0)
            {
                Console.WriteLine();
                for (var i = 0; i < devices.Count; i++)
                {
                    if (devices[i].ID == selected[0].DeviceId)
                    {
                        Check(devices[i]);
                        break;
                    }
                }
            }

            return 0;
        }

        if ((args[0] != "--measure" && args[0] != "--check" && args[0] != "--stamps") || args.Length < 2)
        {
            Console.WriteLine("usage: HapticProbe.exe [--check <index>] [--stamps <index>] [--measure <index>]");
            return 2;
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            Console.WriteLine("process loopback unsupported on this OS (needs Win10 19041+)");
            return 2;
        }

        var index = int.Parse(args[1], CultureInfo.InvariantCulture);
        if (index < 0 || index >= devices.Count)
        {
            Console.WriteLine("no such endpoint index");
            return 2;
        }

        if (args[0] == "--check")
        {
            return Check(devices[index]);
        }

        return args[0] == "--stamps" ? Stamps(devices[index]) : Measure(devices[index]);
    }

    /// <summary>
    /// Checks that the per-packet capture stamps the recorder now writes by are self-consistent: for
    /// each packet, where its own stamp puts it versus where the frames counted so far put it. Those
    /// two agreeing is the whole basis of stamp-placed reference writes — a steadily growing gap
    /// would be the clock drift that pump-paced writing used to hide, and a jumping one would mean
    /// the stamps cannot be trusted for placement.
    /// <para>
    /// Renders a quiet tone to the chosen endpoint so there is something to capture. Pick a virtual
    /// endpoint (a Steam streaming device) to keep it inaudible.
    /// </para>
    /// </summary>
    private static int Stamps(MMDevice device)
    {
        Console.WriteLine($"capturing '{device.FriendlyName}' and checking packet stamps...");
        var capture = ProcessLoopbackCapture.ForEndpoint(device.ID);
        var packets = 0;
        var stamped = 0;
        long frames = 0;
        DateTime? first = null;
        var worstDeviationMs = 0.0;
        var lastDeviationMs = 0.0;

        capture.StampedDataAvailable += (s, e) =>
        {
            packets++;
            if (e.CaptureUtc.HasValue)
            {
                stamped++;
                if (first == null)
                {
                    first = e.CaptureUtc;
                }
                else
                {
                    var byStamp = (e.CaptureUtc.Value - first.Value).TotalSeconds;
                    var byFrames = frames / (double)SampleRate;
                    lastDeviationMs = (byStamp - byFrames) * 1000.0;
                    worstDeviationMs = Math.Max(worstDeviationMs, Math.Abs(lastDeviationMs));
                }
            }

            frames += e.Bytes / 8; // float32 stereo
        };

        var tone = new Thread(() => PlayTone(device, HapticToneHz, 4, 0.02, 7)) { IsBackground = true };
        try
        {
            capture.StartRecording();
            tone.Start();
            tone.Join();
            Thread.Sleep(300);
        }
        finally
        {
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }

        Console.WriteLine($"packets={packets} stamped={stamped} frames={frames} ({frames / (double)SampleRate:0.00}s)");
        Console.WriteLine($"stamp vs frame position: worst {worstDeviationMs:0.00}ms, final {lastDeviationMs:0.00}ms");
        Check(packets > 0, "the endpoint delivered packets", packets.ToString());
        Check(stamped == packets, "every packet carried a usable stamp", $"{stamped}/{packets}");
        Check(
            worstDeviationMs < 20,
            "stamps agree with the frame count (placement will not fight the stream)",
            $"{worstDeviationMs:0.00}ms");
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
        return _failures;
    }

    /// <summary>
    /// Activates the endpoint capture and reports what it delivered, without rendering anything.
    /// Answers the only question the recorder cares about on an unfamiliar machine: whether this
    /// endpoint can be captured at all, in the 48 kHz stereo float the reference track needs.
    /// </summary>
    private static int Check(MMDevice device)
    {
        Console.WriteLine($"activating endpoint loopback on '{device.FriendlyName}'...");
        ProcessLoopbackCapture capture;
        try
        {
            capture = ProcessLoopbackCapture.ForEndpoint(device.ID);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL activation: " + ex.Message);
            return 1;
        }

        long bytes = 0;
        capture.DataAvailable += (s, e) => bytes += e.BytesRecorded;
        try
        {
            capture.StartRecording();
            Thread.Sleep(2000);
        }
        finally
        {
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }

        Console.WriteLine($"PASS activated as {capture.WaveFormat}");
        Console.WriteLine(bytes > 0
            ? $"     delivered {bytes / 8 / (double)SampleRate:0.00}s of audio"
            : "     delivered no packets (the endpoint was silent, which is expected when nothing plays to it)");
        return 0;
    }

    /// <summary>
    /// Prints every active render endpoint with the identity the classifier reads, and marks the
    /// ones the recorder would capture as a haptic reference. Run this on a machine reporting
    /// haptics in its clips to see whether the pad's endpoint is recognised.
    /// </summary>
    private static List<MMDevice> ListDevices()
    {
        var devices = new List<MMDevice>();
        var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID
            : null;

        Console.WriteLine("active render endpoints:");
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(device);
            var identities = Identities(device);
            var haptic = HapticEndpointClassifier.IsHapticEndpoint(
                identities, TryRead(() => device.FriendlyName), TryRead(() => device.DeviceFriendlyName));
            var marks = (device.ID == defaultId ? " [default]" : string.Empty) +
                        (haptic ? " [haptic]" : string.Empty);
            Console.WriteLine($"  {devices.Count - 1,2}  {TryRead(() => device.FriendlyName)}{marks}");
            Console.WriteLine($"      identity {(identities.Count == 0 ? "(none published)" : identities[0])}");
        }

        var selected = RenderEndpointScan.FindHapticEndpoints(null);
        Console.WriteLine();
        Console.WriteLine(selected.Count == 0
            ? "the recorder would capture no haptic reference on this machine."
            : "the recorder would capture: " + string.Join(", ", Describe(selected)));
        Console.WriteLine();
        return devices;
    }

    /// <summary>
    /// Shows which input device the recorder would mix in when "include microphone" is on, and why.
    /// A DualSense makes Windows switch the default recording device to the pad's own microphone,
    /// which records the haptics acoustically — the one copy no render-side cancellation can reach.
    /// Nothing is recorded here; the selection only enumerates.
    /// </summary>
    private static void ListMicrophones()
    {
        Console.WriteLine("microphone the recorder would use:");
        var chosen = MicrophoneSelector.TryChoose(new ProbeLogger());
        Console.WriteLine("  -> " + (chosen == null ? "omitted (no verified safe input)" : "'" + chosen.FriendlyName + "'"));
        Console.WriteLine();
    }

    /// <summary>Prints what the plugin would log, so probe output and log lines read the same.</summary>
    private sealed class ProbeLogger : ILogger
    {
        public void Info(string message) => Console.WriteLine("  " + message);
        public void Info(Exception ex, string message) => Console.WriteLine("  " + message + " :: " + ex.Message);
        public void Debug(string message) => Console.WriteLine("  " + message);
        public void Debug(Exception ex, string message) => Console.WriteLine("  " + message + " :: " + ex.Message);
        public void Warn(string message) => Console.WriteLine("  " + message);
        public void Warn(Exception ex, string message) => Console.WriteLine("  " + message + " :: " + ex.Message);
        public void Error(string message) => Console.WriteLine("  " + message);
        public void Error(Exception ex, string message) => Console.WriteLine("  " + message + " :: " + ex.Message);
        public void Trace(string message) => Console.WriteLine("  " + message);
        public void Trace(Exception ex, string message) => Console.WriteLine("  " + message + " :: " + ex.Message);
    }

    /// <summary>The vendor-carrying strings this endpoint publishes, the same sweep the scan does.</summary>
    private static List<string> Identities(MMDevice device)
    {
        var identities = new List<string>();
        try
        {
            var properties = device.Properties;
            for (var i = 0; i < properties.Count; i++)
            {
                try
                {
                    if (properties.GetValue(i).Value is string text &&
                        (text.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         text.IndexOf("VID&", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        identities.Add(text);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return identities;
    }

    private static string[] Describe(IReadOnlyList<HapticEndpointInfo> endpoints)
    {
        var names = new string[endpoints.Count];
        for (var i = 0; i < endpoints.Count; i++)
        {
            names[i] = endpoints[i].Name;
        }

        return names;
    }

    private static int Measure(MMDevice hapticDevice)
    {
        Console.WriteLine($"rendering the game tone to the default device and the haptic tone to '{hapticDevice.FriendlyName}'...");

        var mixture = new Collector(
            new ProcessLoopbackCapture(Process.GetCurrentProcess().Id, includeProcessTree: true),
            "mixture  (process loopback, this pid)");
        var reference = new Collector(
            ProcessLoopbackCapture.ForEndpoint(hapticDevice.ID),
            "reference(endpoint loopback)         ");

        var game = new Thread(() => PlayTone(null, GameToneHz, 8, 0.02, 3)) { IsBackground = true };
        // Real controller haptics are impulses, not a laboratory tone held for eight seconds. The
        // old continuous signal made every half-second fit trivially and missed exactly the weak
        // transient blocks reported in the field. Sixty milliseconds on / 290 ms off exercises the
        // production 50 ms block fit and its restore-on-uncertainty fallback.
        var haptic = new Thread(
            () => PlayTone(hapticDevice, HapticToneHz, 8, 0.05, 7, burstPeriodSeconds: 0.35, burstOnSeconds: 0.06))
        { IsBackground = true };

        mixture.Start();
        reference.Start();
        game.Start();
        haptic.Start();

        if (!WaitForFirstPackets(mixture, reference))
        {
            Console.WriteLine("no audio packets arrived within 5s on both captures");
            return 2;
        }

        game.Join();
        haptic.Join();
        Thread.Sleep(500);
        mixture.Stop();
        reference.Stop();

        var commonStart = Max(mixture.FirstPacketUtc, reference.FirstPacketUtc);
        var mixturePcm = mixture.AlignedPcm16(commonStart);
        var referencePcm = reference.AlignedPcm16(commonStart);
        var frames = Math.Min(mixturePcm.Length, referencePcm.Length) / 4;
        if (frames < SampleRate * 3)
        {
            Console.WriteLine($"captured only {frames / (double)SampleRate:0.00}s; needs at least 3s");
            return 2;
        }

        // Steady middle of the two tones, away from the start and stop transients.
        var w0 = SampleRate;
        var w1 = frames - SampleRate;

        var beforeHaptic = GoertzelDb(mixturePcm, w0, w1, HapticToneHz);
        var beforeGame = GoertzelDb(mixturePcm, w0, w1, GameToneHz);
        var referenceHaptic = GoertzelDb(referencePcm, w0, w1, HapticToneHz);
        var referenceGame = GoertzelDb(referencePcm, w0, w1, GameToneHz);

        Console.WriteLine();
        Console.WriteLine("stream                                 haptic 180Hz   game 1320Hz   (Goertzel power dB)");
        Console.WriteLine($"{mixture.Name}    {beforeHaptic,8:0.0}      {beforeGame,8:0.0}");
        Console.WriteLine($"{reference.Name}    {referenceHaptic,8:0.0}      {referenceGame,8:0.0}");
        Console.WriteLine();

        Check(beforeHaptic - referenceGame > 20,
            "process loopback swept up the other endpoint's audio (this is the defect being fixed)",
            $"{beforeHaptic:0.0}dB");
        Check(referenceGame < referenceHaptic - 20,
            "the endpoint capture carries the haptic tone and not the game tone",
            $"haptic {referenceHaptic:0.0}dB vs game {referenceGame:0.0}dB");

        // Same policy and search width the recorder uses, so the probe measures production.
        var originalMixture = (byte[])mixturePcm.Clone();
        var outcome = PcmAudio.CancelCorrelated(
            mixturePcm,
            referencePcm,
            out var d,
            muteUnverifiedBlocks: false,
            maxLagFrames: 12000,
            minimumGain: 0.005,
            maximumGain: 20.0,
            blockGainFloor: 0.005,
            keepBlockSuppressionDb: 10,
            cancellationBlockFrames: HapticBlockFrames,
            maximumResidualCorrelation: 0.35,
            commitVerifiedBlocksOnWeakPass: true,
            minimumCorrelation: 0.15,
            attemptVerifiedBlocksWhenGloballyClean: true);
        Console.WriteLine();
        Console.WriteLine($"cancellation: outcome={outcome} lag={d.StartLagMs:0.000}->{d.EndLagMs:0.000}ms " +
                          $"gain={d.Gain:0.00} corr={d.Correlation:0.000} supp={d.SuppressionDb:0.0}dB " +
                           $"blocks={d.SubtractedBlocks}/{d.TotalBlocks} quiet={d.QuietBlocks} " +
                           $"fixed={d.FixedFitBlocks} " +
                           $"gated={d.MutedBlocks} " +
                           $"partial={d.PartialCommit} " +
                           $"weakest={d.WeakestBlockSuppressionDb:0.0}dB residual={d.ResidualCorrelation:0.000}");
        Check(outcome == PcmCancellationOutcome.CancelledVerified, "cancellation verified", outcome.ToString());
        Check(d.MutedBlocks == 0, "production cleanup never gates clip audio", d.MutedBlocks.ToString());

        if (outcome != PcmCancellationOutcome.CancelledVerified)
        {
            Check(
                originalMixture.SequenceEqual(mixturePcm),
                "a rejected cleanup leaves the recorded audio byte-for-byte intact",
                outcome.ToString());
        }

        if (outcome == PcmCancellationOutcome.CancelledVerified)
        {
            var suppression = beforeHaptic - GoertzelDb(mixturePcm, w0, w1, HapticToneHz);
            var gameLoss = beforeGame - GoertzelDb(mixturePcm, w0, w1, GameToneHz);
            Check(suppression >= 10, "haptic tone suppressed >= 10dB", $"{suppression:0.0}dB");
            Check(Math.Abs(gameLoss) <= 3, "game tone survives within 3dB", $"lost {gameLoss:0.0}dB");
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
        return _failures;
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

    private static string TryRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

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

    /// <summary>Plays a tone to one endpoint, or to the default device when it is null.</summary>
    private static void PlayTone(
        MMDevice device,
        double frequency,
        double seconds,
        double amplitude,
        double amHz,
        double burstPeriodSeconds = 0,
        double burstOnSeconds = 0)
    {
        var output = device == null
            ? new WasapiOut(AudioClientShareMode.Shared, 200)
            : new WasapiOut(device, AudioClientShareMode.Shared, false, 200);
        using (output)
        {
            output.Init(new ToneProvider(
                frequency, amplitude, amHz, seconds, burstPeriodSeconds, burstOnSeconds));
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Writes to the console and to the saved report at once.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _copy;

        public TeeWriter(TextWriter console, TextWriter copy)
        {
            _console = console;
            _copy = copy;
        }

        public override System.Text.Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            _console.Write(value);
            _copy.Write(value);
        }

        public override void Write(string value)
        {
            _console.Write(value);
            _copy.Write(value);
        }

        public override void WriteLine(string value)
        {
            _console.WriteLine(value);
            _copy.WriteLine(value);
        }
    }

    private sealed class ToneProvider : ISampleProvider
    {
        private readonly double _frequency;
        private readonly double _amplitude;
        private readonly double _amHz;
        private readonly double _burstPeriodSeconds;
        private readonly double _burstOnSeconds;
        private long _remainingSamples;
        private long _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public ToneProvider(
            double frequency,
            double amplitude,
            double amHz,
            double seconds,
            double burstPeriodSeconds = 0,
            double burstOnSeconds = 0)
        {
            _frequency = frequency;
            _amplitude = amplitude;
            _amHz = amHz;
            _burstPeriodSeconds = burstPeriodSeconds;
            _burstOnSeconds = burstOnSeconds;
            _remainingSamples = (long)(seconds * SampleRate) * 2;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var samples = (int)Math.Min(count, _remainingSamples);
            for (var i = 0; i < samples; i += 2)
            {
                var t = _position / (double)SampleRate;
                var envelope = _amHz > 0 ? 0.6 + 0.4 * Math.Sin(2.0 * Math.PI * _amHz * t) : 1.0;
                if (_burstPeriodSeconds > 0 &&
                    t % _burstPeriodSeconds >= _burstOnSeconds)
                {
                    envelope = 0;
                }
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
