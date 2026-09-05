// Does process loopback preserve channel identity when asked for a multichannel capture format?
//
// The question behind it: a DualSense on USB exposes a 4-channel endpoint (FL, FR, left actuator,
// right actuator; mask 0x33) and games render their haptics to channels 2/3 of it. A stereo
// process-loopback capture folds those into L/R, which is how haptics get into a clip whose
// track excludes the sound host. If the engine keeps the channels apart when the capture asks for
// a 4-channel (quad) format, the recorder can exclude the host by process AND drop the actuators
// by channel in one stream, with no cancellation anywhere.
//
// A child renders a tone on ONE channel of a 4-channel stream to a chosen endpoint (the controller
// when one is connected, else the default output). The parent captures include-tree on the child
// twice: once stereo (today's format) and once 4-channel, and reports where the tone landed.
//
//   ChannelMapProbe.exe [--endpoint <index>] [--channels 4|6|8] [--tone-channel 2] [--hz 180] [--source-channels 4|8]
//   ChannelMapProbe.exe --pid <processId>        capture a RUNNING process (a game) at 2, 4 and 8 channels for 5 s
//   ChannelMapProbe.exe --tone <hz> <seconds> <channels> <activeChannel> <endpointId> [amplitude]   child mode (amplitude 0 = an open, silent stream)
//
// --source-channels sets how many channels the child's stream has (a game on a 7.1 endpoint renders
// 8). --pid skips the child and reports per-channel RMS of whatever the process is rendering, so a
// real game's stream can be checked against the capture formats the recorder uses.
//
// Conclusive with a DualSense connected (its 4-channel endpoint is the real case). Against a
// stereo endpoint the engine may already have downmixed the child's stream at the endpoint, so a
// fold there does not settle the question; a preserved channel there is a strong positive.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PlayniteAchievements.Services.Recording;

internal static class ChannelMapProbe
{
    private const int SampleRate = 48000;

    private static int Main(string[] args)
    {
        if (args.Length >= 6 && args[0] == "--tone")
        {
            PlayTone(
                RenderDeviceFor(args[5]),
                double.Parse(args[1], CultureInfo.InvariantCulture),
                double.Parse(args[2], CultureInfo.InvariantCulture),
                int.Parse(args[3], CultureInfo.InvariantCulture),
                int.Parse(args[4], CultureInfo.InvariantCulture),
                args.Length > 6 ? double.Parse(args[6], CultureInfo.InvariantCulture) : 0.05);
            return 0;
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            Console.WriteLine("process loopback unsupported (needs Win10 19041+ and the win10.manifest build)");
            return 2;
        }

        var pid = Option(args, "--pid", -1);
        if (pid > 0)
        {
            return CaptureExisting(pid);
        }

        var channels = Option(args, "--channels", 4);
        var sourceChannels = Option(args, "--source-channels", 4);
        var toneChannel = Option(args, "--tone-channel", 2);
        var hz = Option(args, "--hz", 180);
        var endpointIndex = Option(args, "--endpoint", -1);

        var devices = AudioEndpointEnumerator.EnumerateActive(AudioDataFlow.Render);
        var defaultId = AudioEndpointEnumerator.TryGetDefaultEndpointId(AudioDataFlow.Render, AudioEndpointRole.Console);
        Console.WriteLine("active render endpoints:");
        EndpointIdentity target = null;
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var isDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase);
            var isHaptic = RenderEndpointScan.IsHapticEndpoint(device);
            Console.WriteLine($"  {i,2}  {device.FriendlyName}{(isDefault ? " [default]" : string.Empty)}{(isHaptic ? " [controller]" : string.Empty)}");
            if (endpointIndex == i || (endpointIndex < 0 && target == null && isHaptic))
            {
                target = device;
            }
        }

        if (target == null)
        {
            target = devices.FirstOrDefault(d => string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault();
        }

        if (target == null)
        {
            Console.WriteLine("no render endpoint");
            return 2;
        }

        var targetIsHaptic = RenderEndpointScan.IsHapticEndpoint(target);
        Console.WriteLine();
        Console.WriteLine($"rendering a {hz} Hz tone on channel {toneChannel} of a {sourceChannels}-channel stream to '{target.FriendlyName}'" +
            (targetIsHaptic ? " (controller endpoint: conclusive)" : " (not a controller endpoint: a fold here is inconclusive)"));

        var exe = Process.GetCurrentProcess().MainModule.FileName;
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Format(
                CultureInfo.InvariantCulture, "--tone {0} 8 {1} {2} \"{3}\"", hz, sourceChannels, toneChannel, target.Id),
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        var failures = 0;
        try
        {
            Thread.Sleep(1500);

            Collector stereo = null, multi = null;
            try
            {
                stereo = new Collector(new ProcessLoopbackCapture(child.Id, includeProcessTree: true), 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine("stereo include-tree capture failed: " + ex.Message);
                return 2;
            }

            try
            {
                var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);
                var capture = new ProcessLoopbackCapture(child.Id, includeProcessTree: true, captureFormat: format);
                Console.WriteLine($"{channels}-channel process loopback accepted: {capture.WaveFormat}");
                multi = new Collector(capture, channels);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL the engine rejected a {channels}-channel process-loopback format: {ex.Message}");
                Console.WriteLine("verdict: channel-preserving exclusion is not available; haptics need the endpoint track.");
                stereo.Stop();
                return 1;
            }

            stereo.Start();
            multi.Start();
            Thread.Sleep(4000);
            stereo.Stop();
            multi.Stop();

            Console.WriteLine();
            Console.WriteLine($"tone power per capture channel (dB, {hz} Hz, steady middle of the capture):");
            var stereoLevels = stereo.LevelsDb(hz);
            var multiLevels = multi.LevelsDb(hz);
            Console.WriteLine("  stereo capture (today):  " + Format(stereoLevels));
            Console.WriteLine($"  {channels}-channel capture:      " + Format(multiLevels));

            if (multiLevels.Length <= toneChannel)
            {
                Console.WriteLine("FAIL the capture has fewer channels than the tone channel");
                return 1;
            }

            var front = Math.Max(multiLevels[0], multiLevels[1]);
            var onTarget = multiLevels[toneChannel];
            var preserved = onTarget - front >= 20;
            var folded = front - onTarget >= 10;
            Console.WriteLine();
            if (preserved)
            {
                Console.WriteLine($"PASS channel identity preserved: channel {toneChannel} carries the tone {onTarget - front:0.0} dB above the front channels");
                Console.WriteLine("verdict: exclude-host capture at 4 channels can drop actuator channels structurally" +
                    (targetIsHaptic ? "." : " (rerun with a controller connected to confirm on its real endpoint)."));
            }
            else if (folded)
            {
                failures++;
                Console.WriteLine($"FAIL the tone was folded into the front channels ({front:0.0} dB front vs {onTarget:0.0} dB on channel {toneChannel})");
                Console.WriteLine(targetIsHaptic
                    ? "verdict: the engine mixes contributing streams before the capture format; actuators cannot be separated by channel."
                    : "verdict: inconclusive on a stereo endpoint (the endpoint itself downmixed the stream); rerun with a controller connected.");
            }
            else
            {
                failures++;
                Console.WriteLine($"FAIL ambiguous: front {front:0.0} dB vs channel {toneChannel} {onTarget:0.0} dB");
            }
        }
        finally
        {
            try { if (!child.HasExited) { child.Kill(); } } catch { }
        }

        return failures;
    }

    /// <summary>
    /// Captures a running process's tree at 2, 4 and 8 channels at once and reports per-channel
    /// RMS, so a real game's render (whatever its stream format) can be checked against every
    /// capture format the recorder might ask for. Play sound in the process while it runs.
    /// </summary>
    private static int CaptureExisting(int pid)
    {
        string name;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch (Exception ex) { Console.WriteLine($"pid {pid}: {ex.Message}"); return 2; }

        Console.WriteLine($"capturing pid {pid} ({name}) include-tree at 2, 4 and 8 channels for 5 s; keep sound playing in it...");
        var collectors = new List<Collector>();
        foreach (var channels in new[] { 2, 4, 8 })
        {
            try
            {
                var capture = channels == 2
                    ? new ProcessLoopbackCapture(pid, includeProcessTree: true)
                    : new ProcessLoopbackCapture(pid, includeProcessTree: true,
                        captureFormat: WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels));
                collectors.Add(new Collector(capture, channels));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {channels}-channel capture could not be created: {ex.Message}");
            }
        }

        foreach (var collector in collectors) { collector.Start(); }
        Thread.Sleep(5000);
        foreach (var collector in collectors) { collector.Stop(); }

        Console.WriteLine();
        Console.WriteLine("RMS per capture channel (dBFS over the whole capture):");
        var loudest = new Dictionary<int, double>();
        foreach (var collector in collectors)
        {
            var rms = collector.RmsDb();
            loudest[collector.Channels] = rms.Length == 0 ? -200 : rms.Max();
            Console.WriteLine($"  {collector.Channels}-channel  frames={collector.Frames,7}  " + Format(rms));
        }

        Console.WriteLine();
        if (!loudest.ContainsKey(2) || loudest[2] < -60)
        {
            Console.WriteLine("the stereo capture heard nothing: the process was not rendering (or renders from another process)");
            return 1;
        }

        var failures = 0;
        foreach (var channels in new[] { 4, 8 })
        {
            if (!loudest.ContainsKey(channels)) { continue; }
            var gap = loudest[2] - loudest[channels];
            var ok = gap <= 6;
            if (!ok) { failures++; }
            Console.WriteLine((ok ? "PASS " : "FAIL ") +
                $"{channels}-channel capture carries the process's audio (loudest channel {loudest[channels]:0.0} dBFS vs stereo {loudest[2]:0.0} dBFS)");
        }

        return failures;
    }

    private static int Option(string[] args, string name, int fallback)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string Format(double[] levels)
    {
        return string.Join("  ", levels.Select((level, index) => $"ch{index}={level,6:0.0}"));
    }

    private static MMDevice RenderDeviceFor(string endpointId)
    {
        return new MMDeviceEnumerator().GetDevice(endpointId);
    }

    private static void PlayTone(MMDevice device, double hz, double seconds, int channels, int activeChannel, double amplitude)
    {
        using (var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 200))
        {
            output.Init(new ToneProvider(hz, amplitude, seconds, activeChannel, channels));
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Collects one capture's float samples and measures per-channel tone power.</summary>
    private sealed class Collector
    {
        private readonly ProcessLoopbackCapture _capture;
        private readonly MemoryStream _bytes = new MemoryStream();
        private readonly int _channels;

        public Collector(ProcessLoopbackCapture capture, int channels)
        {
            _capture = capture;
            _channels = channels;
            _capture.DataAvailable += (s, e) =>
            {
                lock (_bytes)
                {
                    _bytes.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };
        }

        public int Channels => _channels;

        public long Frames
        {
            get
            {
                lock (_bytes)
                {
                    return _bytes.Length / (4 * _channels);
                }
            }
        }

        public void Start() => _capture.StartRecording();

        /// <summary>Per-channel RMS in dBFS over everything captured.</summary>
        public double[] RmsDb()
        {
            byte[] raw;
            lock (_bytes)
            {
                raw = _bytes.ToArray();
            }

            var frames = raw.Length / (4 * _channels);
            var levels = new double[_channels];
            for (var channel = 0; channel < _channels; channel++)
            {
                double sum = 0;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sample = BitConverter.ToSingle(raw, (frame * _channels + channel) * 4);
                    sum += sample * sample;
                }

                var rms = frames == 0 ? 0 : Math.Sqrt(sum / frames);
                levels[channel] = rms > 0 ? 20.0 * Math.Log10(rms) : -200;
            }

            return levels;
        }

        public void Stop()
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
        }

        public double[] LevelsDb(double hz)
        {
            byte[] raw;
            lock (_bytes)
            {
                raw = _bytes.ToArray();
            }

            var frames = raw.Length / (4 * _channels);
            var start = Math.Min(frames, SampleRate);           // skip the first second
            var end = Math.Min(frames, start + 2 * SampleRate); // two seconds of steady tone
            var levels = new double[_channels];
            for (var channel = 0; channel < _channels; channel++)
            {
                var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * hz / SampleRate);
                double s1 = 0, s2 = 0;
                for (var frame = start; frame < end; frame++)
                {
                    var sample = BitConverter.ToSingle(raw, (frame * _channels + channel) * 4);
                    var s0 = sample + coefficient * s1 - s2;
                    s2 = s1;
                    s1 = s0;
                }

                var n = Math.Max(1, end - start);
                var power = (s1 * s1 + s2 * s2 - coefficient * s1 * s2) / ((double)n * n);
                levels[channel] = 10.0 * Math.Log10(Math.Max(power, 1e-14));
            }

            return levels;
        }
    }

    private sealed class ToneProvider : ISampleProvider
    {
        private readonly double _hz;
        private readonly double _amplitude;
        private readonly int _activeChannel;
        private readonly int _channels;
        private long _remaining;
        private long _position;

        public ToneProvider(double hz, double amplitude, double seconds, int activeChannel, int channels)
        {
            _hz = hz;
            _amplitude = amplitude;
            _activeChannel = activeChannel;
            _channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);
            _remaining = (long)(seconds * SampleRate) * channels;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var samples = (int)Math.Min(count, _remaining);
            samples -= samples % _channels;
            for (var i = 0; i < samples; i += _channels)
            {
                var value = (float)(_amplitude * Math.Sin(2 * Math.PI * _hz * _position / SampleRate));
                for (var channel = 0; channel < _channels; channel++)
                {
                    buffer[offset + i + channel] = channel == _activeChannel ? value : 0f;
                }

                _position++;
            }

            _remaining -= samples;
            return samples;
        }
    }
}
