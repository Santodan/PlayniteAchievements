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
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.Recording;

internal static class HapticProbe
{
    private const int SampleRate = 48000;
    private const double GameToneHz = 1320;  // the audio that must survive
    private const double HapticToneHz = 180; // stands in for the pad's rumble waveform

    private static int _failures;

    private static int Main(string[] args)
    {
        var devices = ListDevices();
        if (args.Length == 0)
        {
            return 0;
        }

        if ((args[0] != "--measure" && args[0] != "--check") || args.Length < 2)
        {
            Console.WriteLine("usage: HapticProbe.exe [--check <index>] [--measure <index>]");
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

        return args[0] == "--check" ? Check(devices[index]) : Measure(devices[index]);
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
        var haptic = new Thread(() => PlayTone(hapticDevice, HapticToneHz, 8, 0.05, 0)) { IsBackground = true };

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

        var outcome = PcmAudio.CancelCorrelated(
            mixturePcm, referencePcm, out var d, muteUnverifiedBlocks: false);
        Console.WriteLine();
        Console.WriteLine($"cancellation: outcome={outcome} lag={d.StartLagMs:0.000}->{d.EndLagMs:0.000}ms " +
                          $"gain={d.Gain:0.00} corr={d.Correlation:0.000} supp={d.SuppressionDb:0.0}dB muted={d.MutedBlocks}");
        Check(outcome == PcmCancellationOutcome.CancelledVerified, "cancellation verified", outcome.ToString());
        Check(d.MutedBlocks == 0, "no block was silenced (the clip's own audio is never punched out)", d.MutedBlocks.ToString());

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
    private static void PlayTone(MMDevice device, double frequency, double seconds, double amplitude, double amHz)
    {
        var output = device == null
            ? new WasapiOut(AudioClientShareMode.Shared, 200)
            : new WasapiOut(device, AudioClientShareMode.Shared, false, 200);
        using (output)
        {
            output.Init(new ToneProvider(frequency, amplitude, amHz, seconds));
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
