// Hardware proof for the recorder's endpoint-isolation design.
//
// A game-like process renders a normal tone to the default output and a rumble-like tone to the
// DualSense actuator channels. Process loopback hears both endpoints; the recorder's real endpoint
// capture must keep the game tone while rejecting the actuator tone.
//
//   HapticProbe.exe                   list active outputs
//   HapticProbe.exe --auto            test both actuators on the detected controller
//   HapticProbe.exe --measure <index> test both actuators on one listed output

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
using PlayniteAchievements.Services.Recording;

internal static class HapticProbe
{
    private const int SampleRate = 48000;
    private const double GameToneHz = 1320;
    private const double HapticToneHz = 180;
    private static int _failures;

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

    private static int Run(string[] args)
    {
        var devices = ListDevices();
        try
        {
            ListMicrophones();
            if (args.Length == 0)
            {
                Console.WriteLine("Run --auto to prove haptic exclusion on the connected controller.");
                return 0;
            }

            if (!ProcessLoopbackCapture.IsSupported)
            {
                Console.WriteLine("process loopback unsupported on this OS (needs Windows 10 19041+)");
                return 2;
            }

            MMDevice controller;
            if (args[0] == "--auto")
            {
                var controllers = devices.Where(RenderEndpointScan.IsHapticEndpoint).ToList();
                if (controllers.Count != 1)
                {
                    Console.WriteLine(
                        $"--auto requires exactly one detected controller output; found {controllers.Count}.");
                    return 2;
                }

                controller = controllers[0];
            }
            else if (args[0] == "--measure" && args.Length == 2)
            {
                if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= devices.Count)
                {
                    Console.WriteLine("no such endpoint index");
                    return 2;
                }

                controller = devices[index];
                if (!RenderEndpointScan.IsHapticEndpoint(controller))
                {
                    Console.WriteLine("the selected endpoint is not classified as a controller output");
                    return 2;
                }
            }
            else
            {
                Console.WriteLine("usage: HapticProbe.exe [--auto] [--measure <index>]");
                return 2;
            }

            Console.WriteLine($"testing '{controller.FriendlyName}': left actuator, then right actuator");
            Measure(controller, 2);
            Measure(controller, 3);
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
            return _failures;
        }
        finally
        {
            foreach (var device in devices)
            {
                try { device.Dispose(); } catch { }
            }
        }
    }

    private static List<MMDevice> ListDevices()
    {
        var devices = new List<MMDevice>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            var defaultId = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID
                : null;

            Console.WriteLine("active render endpoints:");
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(device);
                var marks =
                    (string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)
                        ? " [default]" : string.Empty) +
                    (RenderEndpointScan.IsHapticEndpoint(device) ? " [controller]" : string.Empty);
                Console.WriteLine($"  {devices.Count - 1,2}  {device.FriendlyName}{marks}");
            }
        }

        Console.WriteLine();
        return devices;
    }

    private static void ListMicrophones()
    {
        Console.WriteLine("microphone the recorder would use:");
        var chosen = MicrophoneSelector.TryChoose(new ProbeLogger());
        Console.WriteLine(
            "  -> " + (chosen == null
                ? "omitted (no verified safe input)"
                : "'" + chosen.FriendlyName + "'"));
        try { chosen?.Dispose(); } catch { }
        Console.WriteLine();
    }

    private static int Measure(MMDevice controller, int actuatorChannel)
    {
        WaveFormat nativeFormat;
        using (var client = controller.AudioClient)
        {
            nativeFormat = client.MixFormat;
        }

        if (!ProcessLoopbackCapture.IsDualSenseActuatorFormat(nativeFormat))
        {
            Console.WriteLine("FAIL controller does not expose the proven 48 kHz 4-channel layout");
            _failures++;
            return 2;
        }

        using (var enumerator = new MMDeviceEnumerator())
        using (var output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console))
        {
            var outputIsController = RenderEndpointScan.IsHapticEndpoint(output);
            ProcessLoopbackCapture endpointCapture;
            var keepProgramChannels = false;
            if (outputIsController)
            {
                endpointCapture = ProcessLoopbackCapture.ForEndpointNative(output.ID);
                keepProgramChannels =
                    ProcessLoopbackCapture.IsDualSenseActuatorFormat(endpointCapture.WaveFormat);
                if (!keepProgramChannels)
                {
                    endpointCapture.Dispose();
                    endpointCapture = ProcessLoopbackCapture.ForEndpoint(output.ID);
                }
            }
            else
            {
                endpointCapture = ProcessLoopbackCapture.ForEndpoint(output.ID);
            }

            using (var process = new FloatCollector(
                new ProcessLoopbackCapture(Process.GetCurrentProcess().Id, includeProcessTree: true),
                keepProgramChannels: false))
            using (var speaker = new FloatCollector(endpointCapture, keepProgramChannels))
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"channel {actuatorChannel}: game -> '{output.FriendlyName}', " +
                    $"haptics -> '{controller.FriendlyName}'");

                process.Start();
                speaker.Start();
                Thread.Sleep(500);

                var game = new Thread(
                    () => PlayTone(output, GameToneHz, 6, 0.02, 3, activeChannel: -1, channels: 2))
                { IsBackground = true };
                var haptic = new Thread(
                    () => PlayTone(
                        controller,
                        HapticToneHz,
                        6,
                        0.05,
                        7,
                        activeChannel: actuatorChannel,
                        channels: nativeFormat.Channels))
                { IsBackground = true };
                game.Start();
                haptic.Start();
                game.Join();
                haptic.Join();
                Thread.Sleep(500);
                process.Stop();
                speaker.Stop();

                if (!process.HasPackets || !speaker.HasPackets)
                {
                    Check(false, "both captures delivered audio", "a capture had no packets");
                    return 2;
                }

                var commonStart = Max(process.FirstPacketUtc, speaker.FirstPacketUtc);
                var processPcm = process.AlignedPcm16(commonStart);
                var speakerPcm = speaker.AlignedPcm16(commonStart);
                var frames = Math.Min(processPcm.Length, speakerPcm.Length) / 4;
                var start = SampleRate;
                var end = frames - SampleRate;
                if (end <= start)
                {
                    Check(false, "enough aligned audio was captured", frames + " frames");
                    return 2;
                }

                var processHaptic = GoertzelDb(processPcm, start, end, HapticToneHz);
                var speakerHaptic = GoertzelDb(speakerPcm, start, end, HapticToneHz);
                var processGame = GoertzelDb(processPcm, start, end, GameToneHz);
                var speakerGame = GoertzelDb(speakerPcm, start, end, GameToneHz);
                var exclusion = processHaptic - speakerHaptic;

                Console.WriteLine(
                    $"  process: haptic {processHaptic:0.0}dB, game {processGame:0.0}dB");
                Console.WriteLine(
                    $"  endpoint: haptic {speakerHaptic:0.0}dB, game {speakerGame:0.0}dB");
                Check(
                    exclusion >= 30,
                    "controller audio is excluded by at least 30dB",
                    $"{exclusion:0.0}dB");
                Check(
                    Math.Abs(processGame - speakerGame) <= 3,
                    "program audio survives within 3dB",
                    $"process {processGame:0.0}dB vs endpoint {speakerGame:0.0}dB");
            }
        }

        return _failures;
    }

    private sealed class FloatCollector : IDisposable
    {
        private readonly ProcessLoopbackCapture _capture;
        private readonly bool _keepProgramChannels;
        private readonly MemoryStream _bytes = new MemoryStream();
        private bool _stopped;

        public FloatCollector(ProcessLoopbackCapture capture, bool keepProgramChannels)
        {
            _capture = capture;
            _keepProgramChannels = keepProgramChannels;
            _capture.DataAvailable += OnData;
        }

        public DateTime FirstPacketUtc => _capture.FirstPacketCaptureUtc ?? DateTime.MaxValue;
        public bool HasPackets => FirstPacketUtc != DateTime.MaxValue;

        public void Start() => _capture.StartRecording();

        public void Stop()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            try { _capture.StopRecording(); } catch { }
        }

        public byte[] AlignedPcm16(DateTime startUtc)
        {
            byte[] raw;
            lock (_bytes)
            {
                raw = _bytes.ToArray();
            }

            var skipFrames = Math.Max(
                0,
                (int)Math.Round((startUtc - FirstPacketUtc).TotalSeconds * SampleRate));
            var skipBytes = Math.Min(raw.Length, skipFrames * 8);
            var frames = (raw.Length - skipBytes) / 8;
            var pcm = new byte[frames * 4];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var sample = BitConverter.ToSingle(
                        raw,
                        skipBytes + frame * 8 + channel * sizeof(float));
                    var value = (short)Math.Round(
                        Math.Max(-1f, Math.Min(1f, sample)) * short.MaxValue);
                    pcm[frame * 4 + channel * 2] = (byte)(value & 0xff);
                    pcm[frame * 4 + channel * 2 + 1] = (byte)((value >> 8) & 0xff);
                }
            }

            return pcm;
        }

        private void OnData(object sender, WaveInEventArgs packet)
        {
            var buffer = packet.Buffer;
            var bytes = packet.BytesRecorded;
            if (_keepProgramChannels)
            {
                buffer = ProcessLoopbackCapture.ExtractDualSenseProgramAudio(
                    packet.Buffer,
                    packet.BytesRecorded,
                    _capture.WaveFormat);
                bytes = buffer?.Length ?? 0;
            }

            if (bytes <= 0)
            {
                return;
            }

            lock (_bytes)
            {
                _bytes.Write(buffer, 0, bytes);
            }
        }

        public void Dispose()
        {
            Stop();
            _capture.Dispose();
            _bytes.Dispose();
        }
    }

    private static void PlayTone(
        MMDevice device,
        double frequency,
        double seconds,
        double amplitude,
        double amHz,
        int activeChannel,
        int channels)
    {
        using (var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 200))
        {
            output.Init(new ToneProvider(
                frequency,
                amplitude,
                amHz,
                seconds,
                activeChannel,
                SampleRate,
                channels));
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static double GoertzelDb(
        byte[] pcm16Stereo,
        int startFrame,
        int endFrame,
        double frequency)
    {
        var count = endFrame - startFrame;
        var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * frequency / SampleRate);
        double current = 0, previous = 0, beforePrevious = 0;
        for (var frame = startFrame; frame < endFrame; frame++)
        {
            var offset = frame * 4;
            var left = (short)(pcm16Stereo[offset] | (pcm16Stereo[offset + 1] << 8));
            var right = (short)(pcm16Stereo[offset + 2] | (pcm16Stereo[offset + 3] << 8));
            current = (left + right) * 0.5 + coefficient * previous - beforePrevious;
            beforePrevious = previous;
            previous = current;
        }

        var power =
            (previous * previous + beforePrevious * beforePrevious -
             coefficient * previous * beforePrevious) /
            ((double)count * count);
        return 10.0 * Math.Log10(Math.Max(power, 1e-12));
    }

    private static void Check(bool condition, string what, string detail)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what + " (" + detail + ")");
        if (!condition)
        {
            _failures++;
        }
    }

    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;

    private sealed class ToneProvider : ISampleProvider
    {
        private readonly double _frequency;
        private readonly double _amplitude;
        private readonly double _amHz;
        private readonly int _activeChannel;
        private readonly int _sampleRate;
        private readonly int _channels;
        private long _remainingSamples;
        private long _position;

        public ToneProvider(
            double frequency,
            double amplitude,
            double amHz,
            double seconds,
            int activeChannel,
            int sampleRate,
            int channels)
        {
            _frequency = frequency;
            _amplitude = amplitude;
            _amHz = amHz;
            _activeChannel = activeChannel;
            _sampleRate = sampleRate;
            _channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _remainingSamples = (long)(seconds * sampleRate) * channels;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var samples = (int)Math.Min(count, _remainingSamples);
            samples -= samples % _channels;
            for (var i = 0; i < samples; i += _channels)
            {
                var seconds = _position / (double)_sampleRate;
                var envelope = 0.6 + 0.4 * Math.Sin(2 * Math.PI * _amHz * seconds);
                var value = (float)(
                    _amplitude * envelope * Math.Sin(2 * Math.PI * _frequency * seconds));
                for (var channel = 0; channel < _channels; channel++)
                {
                    buffer[offset + i + channel] =
                        _activeChannel < 0 || channel == _activeChannel ? value : 0;
                }

                _position++;
            }

            _remainingSamples -= samples;
            return samples;
        }
    }

    private sealed class ProbeLogger : ILogger
    {
        public void Info(string message) => Console.WriteLine("  " + message);
        public void Info(Exception ex, string message) => Info(message + " :: " + ex.Message);
        public void Debug(string message) => Console.WriteLine("  " + message);
        public void Debug(Exception ex, string message) => Debug(message + " :: " + ex.Message);
        public void Warn(string message) => Console.WriteLine("  " + message);
        public void Warn(Exception ex, string message) => Warn(message + " :: " + ex.Message);
        public void Error(string message) => Console.WriteLine("  " + message);
        public void Error(Exception ex, string message) => Error(message + " :: " + ex.Message);
        public void Trace(string message) => Console.WriteLine("  " + message);
        public void Trace(Exception ex, string message) => Trace(message + " :: " + ex.Message);
    }

    private static void SaveReport(string text)
    {
        try
        {
            var executable = Process.GetCurrentProcess().MainModule.FileName;
            var path = Path.Combine(
                Path.GetDirectoryName(executable) ?? ".",
                "HapticProbe-report.txt");
            File.WriteAllText(path, text);
            Console.WriteLine();
            Console.WriteLine("Saved this report to " + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("(could not save report: " + ex.Message + ")");
        }
    }

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
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

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
        public override void Write(char value) { _console.Write(value); _copy.Write(value); }
        public override void Write(string value) { _console.Write(value); _copy.Write(value); }
        public override void WriteLine(string value)
        {
            _console.WriteLine(value);
            _copy.WriteLine(value);
        }
    }
}
