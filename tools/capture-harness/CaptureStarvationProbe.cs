// CaptureStarvationProbe — proves what capture-thread priority costs under CPU load.
//
//   CaptureStarvationProbe.exe                 A/B BelowNormal vs AboveNormal under load
//   CaptureStarvationProbe.exe --seconds 20    longer runs
//   CaptureStarvationProbe.exe --no-load       control: same runs with no CPU load
//
// A WASAPI capture client is initialised with a 200 ms buffer. If its poll thread does not get
// scheduled inside that window, the engine overwrites the ring: the audio is gone, the device
// position jumps, and ProcessLoopbackCapture pads the hole with silence that never played. That
// padding is what a listener hears as stutter.
//
// The recorder runs up to four of these clients at once (endpoint, game reference, non-game, chime
// sidecar). This probe stands all four up, renders a tone so there is something to capture, pins
// every core with busy work the way an emulator does, and reports the padding each priority
// produces. PaddedGapFrames is the real currency here: it is exactly the silence the clip carries.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PlayniteAchievements.Services.Recording;

internal static class CaptureStarvationProbe
{
    private const int SampleRate = 48000;
    private const double ToneHz = 440.0;

    private static int Main(string[] args)
    {
        var seconds = ArgValue(args, "--seconds", 15);
        var withLoad = !args.Contains("--no-load");

        if (!ProcessLoopbackCapture.IsSupported)
        {
            Console.WriteLine("process loopback unsupported on this OS (needs Windows 10 19041+)");
            Console.WriteLine("NOTE: an unmanifested exe reports 6.2 — build via build.ps1 so the manifest is applied.");
            return 2;
        }

        Console.WriteLine(
            $"{seconds}s per run, cpu load {(withLoad ? Environment.ProcessorCount + " busy threads" : "off")}, " +
            $"{Environment.ProcessorCount} cores");
        Console.WriteLine();
        Console.WriteLine("priority       padded    discarded  delivered/elapsed  verdict");
        Console.WriteLine("------------  --------  ----------  -----------------  -------");

        var results = new Dictionary<ThreadPriority, double>();
        foreach (var priority in new[] { ThreadPriority.BelowNormal, ThreadPriority.AboveNormal })
        {
            results[priority] = RunOnce(priority, seconds, withLoad);
        }

        Console.WriteLine();
        var before = results[ThreadPriority.BelowNormal];
        var after = results[ThreadPriority.AboveNormal];
        Console.WriteLine(
            $"BelowNormal padded {before:0.###}s; AboveNormal padded {after:0.###}s.");
        if (before <= 0.001 && after <= 0.001)
        {
            Console.WriteLine("Neither starved — rerun with more load, or on the machine that stutters.");
            return 0;
        }

        Console.WriteLine(after < before
            ? $"AboveNormal cut the padded silence by {(1 - (after / Math.Max(1e-9, before))) * 100:0.#}%."
            : "AboveNormal did NOT help here — the stutter is not poll-thread starvation.");
        return 0;
    }

    /// <summary>Returns seconds of silence padding across the four clients.</summary>
    private static double RunOnce(ThreadPriority priority, int seconds, bool withLoad)
    {
        ProcessLoopbackCapture.PollThreadPriority = priority;

        var stop = new ManualResetEventSlim(false);
        var loadThreads = withLoad ? StartCpuLoad(stop) : new List<Thread>();
        var tone = StartTone(stop);

        var captures = new List<ProcessLoopbackCapture>();
        long delivered = 0;
        var stopwatch = new Stopwatch();
        try
        {
            var self = Process.GetCurrentProcess().Id;
            var endpointId = AudioEndpointEnumerator.TryGetDefaultEndpointId(
                AudioDataFlow.Render, AudioEndpointRole.Console);

            // The recorder's four live clients.
            if (endpointId != null) { captures.Add(ProcessLoopbackCapture.ForEndpoint(endpointId)); }
            captures.Add(new ProcessLoopbackCapture(self, includeProcessTree: true));
            captures.Add(new ProcessLoopbackCapture(self, includeProcessTree: false));
            captures.Add(new ProcessLoopbackCapture(self, includeProcessTree: true));

            foreach (var capture in captures)
            {
                var local = capture;
                local.DataAvailable += (s, e) => Interlocked.Add(ref delivered, e.BytesRecorded);
                local.StartRecording();
            }

            stopwatch.Start();
            Thread.Sleep(seconds * 1000);
            stopwatch.Stop();
        }
        finally
        {
            foreach (var capture in captures)
            {
                try { capture.StopRecording(); } catch { }
            }

            stop.Set();
            try { tone?.Join(2000); } catch { }
            foreach (var thread in loadThreads) { try { thread.Join(2000); } catch { } }
        }

        var paddedFrames = captures.Sum(c => c.PaddedGapFrames);
        var paddedSeconds = paddedFrames / (double)SampleRate;

        // Each client delivers 48 kHz stereo float; four of them over the run.
        var expected = 4.0 * SampleRate * 8 * stopwatch.Elapsed.TotalSeconds;
        var ratio = expected <= 0 ? 0 : delivered / expected;

        foreach (var capture in captures) { try { capture.Dispose(); } catch { } }

        Console.WriteLine(
            $"{priority,-12}  {paddedSeconds,7:0.###}s  {"n/a",10}  {ratio,16:0.000}  " +
            (paddedSeconds > 0.05 ? "STARVED" : "ok"));
        return paddedSeconds;
    }

    /// <summary>Busy work on every core, as a CPU-bound emulator produces.</summary>
    private static List<Thread> StartCpuLoad(ManualResetEventSlim stop)
    {
        var threads = new List<Thread>();
        for (var i = 0; i < Environment.ProcessorCount; i++)
        {
            var thread = new Thread(() =>
            {
                var x = 1.0;
                while (!stop.IsSet)
                {
                    for (var n = 0; n < 200000; n++) { x = Math.Sqrt(x + n) + 1.0; }
                }

                GC.KeepAlive(x);
            })
            {
                IsBackground = true,
                Name = "PA-Probe-Load",
                // A game's own threads run at Normal. Anything below that loses to them.
                Priority = ThreadPriority.Normal,
            };
            thread.Start();
            threads.Add(thread);
        }

        return threads;
    }

    /// <summary>Renders a tone so the clients have something to capture; silence produces no packets.</summary>
    private static Thread StartTone(ManualResetEventSlim stop)
    {
        var thread = new Thread(() =>
        {
            try
            {
                // WaveOutEvent, not WasapiOut: WasapiOut needs an NAudio MMDevice, and NAudio gets
                // one by activating its own coclass for the MMDeviceEnumerator CLSID. This probe
                // compiles AudioEndpointEnumerator in, which activates that CLSID from the CLSID
                // itself first, and the CLR's map is process-wide first-writer-wins -- so NAudio's
                // cast then fails with "Unable to cast object of type System.__ComObject". The
                // legacy waveOut path needs no enumerator, and any output device will do here.
                using (var output = new WaveOutEvent { DesiredLatency = 200 })
                {
                    output.Init(new ToneProvider());
                    output.Play();
                    while (!stop.IsSet && output.PlaybackState == PlaybackState.Playing)
                    {
                        Thread.Sleep(50);
                    }

                    output.Stop();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("tone failed: " + ex.Message);
            }
        })
        { IsBackground = true, Name = "PA-Probe-Tone" };
        thread.Start();
        return thread;
    }

    private sealed class ToneProvider : ISampleProvider
    {
        private long _sample;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i += 2)
            {
                var t = _sample++ / (double)SampleRate;
                var v = (float)(0.05 * Math.Sin(2 * Math.PI * ToneHz * t));
                buffer[offset + i] = v;
                buffer[offset + i + 1] = v;
            }

            return count;
        }
    }

    private static int ArgValue(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }
}
