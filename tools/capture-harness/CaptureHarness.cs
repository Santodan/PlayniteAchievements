// End-to-end harness for the clip pipeline, on .NET Framework so the recorder's WinRT capture types
// load. Every painted frame of the test window carries its own sequence number as a binary barcode, so
// each frame of the recorded output can be identified exactly - which turns "wrong frames" from a
// judgement call into a list of duplicates, regressions and gaps.
//
// Phases: paint + record -> report each segment's timing -> export a clip -> decode the clip's frames.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpDX.MediaFoundation;

internal static class CaptureHarness
{
    // Barcode geometry, in client pixels at the window's native size. Bit 0 is leftmost.
    private const int CellSize = 24;
    private const int BitCount = 16;
    private const int BarcodeY = 0;
    private const int SyncX = 0;                       // always white
    private const int BitsX = CellSize * 2;            // bits start after sync + black reference

    private static double FreezeAtSeconds;
    private static double FreezeForSeconds;

    private const int ClientW = 1280;
    private const int ClientH = 720;

    private static string _pluginDir;
    private static bool _softwareEncoder;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 2 && string.Equals(args[0], "--reencode", StringComparison.OrdinalIgnoreCase))
        {
            // --software anywhere in the arguments keeps hardware transforms off the encoding sinks,
            // so the passes run on Microsoft's software H.264 encoder — the universal fall-back path.
            _softwareEncoder = args.Any(a => string.Equals(a, "--software", StringComparison.OrdinalIgnoreCase));
            args = args.Where(a => !string.Equals(a, "--software", StringComparison.OrdinalIgnoreCase)).ToArray();
            // Re-run only the composition phase over an existing base clip, so a clip the full run
            // produced (a stalled one, say) can be worked on without recording again:
            //   CaptureHarness.exe --reencode <clip.mp4> <fps> [toastStartSeconds] [trimLeadSeconds] [pluginDir]
            var here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _pluginDir = args.Length > 5 ? args[5] : Path.GetFullPath(Path.Combine(here, @"..\..\..\source\bin\Debug"));
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            var loaded = Assembly.LoadFrom(Path.Combine(_pluginDir, "PlayniteAchievements.dll"));
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            _videoLeadSeconds = args.Length > 4 ? double.Parse(args[4], invariant) : 0;
            _paints = new List<Tuple<int, double>> { Tuple.Create(0, 0.0) };
            CompareTimestamps(args[1]);
            MeasureComposition(
                loaded, args[1], Path.Combine(here, "reencode_composited.mp4"), int.Parse(args[2]),
                args.Length > 3 ? double.Parse(args[3], invariant) : (double?)null);
            return;
        }

        var stress = args.Length > 0 && string.Equals(args[0], "--stress", StringComparison.OrdinalIgnoreCase);
        var mfStress = args.Length > 0 && string.Equals(args[0], "--mf-stress", StringComparison.OrdinalIgnoreCase);
        var diagnosticStress = stress || mfStress;
        var seconds = diagnosticStress
            ? (args.Length > 1 ? int.Parse(args[1]) : 300)
            : (args.Length > 0 ? int.Parse(args[0]) : 20);
        var fps = diagnosticStress
            ? (args.Length > 2 ? int.Parse(args[2]) : 60)
            : (args.Length > 1 ? int.Parse(args[1]) : 30);
        FreezeAtSeconds = !diagnosticStress && args.Length > 3 ? double.Parse(args[3]) : 0;
        FreezeForSeconds = !diagnosticStress && args.Length > 4 ? double.Parse(args[4]) : 0;
        var scratch = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _pluginDir = diagnosticStress && args.Length > 3
            ? args[3]
            : !diagnosticStress && args.Length > 2
                ? args[2]
            : Path.GetFullPath(Path.Combine(scratch, @"..\..\..\source\bin\Debug"));

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        var plugin = Assembly.LoadFrom(Path.Combine(_pluginDir, "PlayniteAchievements.dll"));

        if (diagnosticStress)
        {
            var exportEverySeconds = args.Length > 4 ? int.Parse(args[4]) : 20;
            var teardownCycles = args.Length > 5 ? int.Parse(args[5]) : 5;
            var width = args.Length > 6 ? int.Parse(args[6]) : 1920;
            var height = args.Length > 7 ? int.Parse(args[7]) : 1080;
            if (mfStress)
            {
                RunMediaFoundationStress(plugin, scratch, seconds, fps, exportEverySeconds, width, height);
            }
            else
            {
                RunStress(plugin, scratch, seconds, fps, exportEverySeconds, teardownCycles, width, height);
            }

            return;
        }

        var buffer = Path.Combine(scratch, "harness_buffer");
        if (Directory.Exists(buffer))
        {
            foreach (var file in Directory.GetFiles(buffer))
            {
                File.Delete(file);
            }
        }

        Directory.CreateDirectory(buffer);
        Console.WriteLine("buffer: " + buffer);

        EncoderDurationTest(plugin, Path.Combine(scratch, "encoder_probe.mp4"), fps);

        var paints = Record(plugin, buffer, seconds, fps);
        File.WriteAllLines(
            Path.Combine(scratch, "paints.csv"),
            // Snapshot first: the window keeps painting (and appending) while this enumerates.
            new[] { "counter,elapsedMs" }.Concat(paints.ToArray().Select(p => p.Item1 + "," + p.Item2.ToString("0.000"))));
        Console.WriteLine("painted " + paints.Count + " frames");

        ReportSegments(buffer);
        MeasureScreenshotAlignment(plugin);

        var clip = Path.Combine(scratch, "harness_clip.mp4");
        if (File.Exists(clip))
        {
            File.Delete(clip);
        }

        if (ExportClip(plugin, buffer, clip))
        {
            // Composition, with the window still painting, so its interval spread can be compared
            // against the recording baseline printed above.
            var composited = Path.Combine(scratch, "harness_composited.mp4");
            if (File.Exists(composited))
            {
                File.Delete(composited);
            }

            MeasureComposition(plugin, clip, composited, fps);
            CompareParameterSets(buffer, composited);

            Console.WriteLine();
            Console.WriteLine("=== clip timing");
            foreach (var line in Mp4.Describe(clip))
            {
                Console.WriteLine("  " + line);
            }

            Console.WriteLine();
            Console.WriteLine("=== decoded frame identities (barcode per output frame)");
            DecodeAndReport(clip, -_videoLeadSeconds, "base clip");
        }

        // The regression case: ask for a window reaching further back than the buffer holds, which is
        // what a young session or a pruned buffer produces. The clip then begins later than the window,
        // and anything positioned from the window lands early by the difference.
        Console.WriteLine();
        Console.WriteLine("###### short-buffer case ######");
        var shortClip = Path.Combine(scratch, "harness_clip_shortbuffer.mp4");
        if (File.Exists(shortClip))
        {
            File.Delete(shortClip);
        }

        if (ExportClip(plugin, buffer, shortClip, reachBackSeconds: 30))
        {
            Console.WriteLine();
            Console.WriteLine("=== decoded frame identities (short-buffer clip)");
            DecodeAndReport(shortClip, -_videoLeadSeconds, "short buffer");
        }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        var name = e.Name.Split(',')[0];
        var candidate = Path.Combine(_pluginDir, name + ".dll");
        if (File.Exists(candidate))
        {
            return Assembly.LoadFrom(candidate);
        }

        // Playnite.SDK ships in the package folder, not the plugin output.
        var packages = Path.GetFullPath(Path.Combine(_pluginDir, @"..\..\packages"));
        if (Directory.Exists(packages))
        {
            var hit = Directory.GetFiles(packages, name + ".dll", SearchOption.AllDirectories)
                .FirstOrDefault(p => p.IndexOf("net46", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? Directory.GetFiles(packages, name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null)
            {
                return Assembly.LoadFrom(hit);
            }
        }

        return null;
    }

    // === phase 1: paint a self-identifying window and record it ===

    private static List<Tuple<int, double>> Record(Assembly plugin, string buffer, int seconds, int fps)
    {
        var paints = new List<Tuple<int, double>>();
        MarkerForm form = null;
        var ready = new ManualResetEventSlim(false);

        var ui = new Thread(() =>
        {
            form = new MarkerForm(paints);
            form.Shown += (s, e) => ready.Set();
            Application.Run(form);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("marker window did not appear");
        }

        var hwnd = IntPtr.Zero;
        form.Invoke((Action)(() => hwnd = form.Handle));
        Console.WriteLine("marker window hwnd 0x" + hwnd.ToInt64().ToString("X") + " client " + ClientW + "x" + ClientH);

        var recorder = NewRecorder(plugin, hwnd, buffer, fps);
        var started = (bool)recorder.GetType().GetMethod("Start", Flags).Invoke(recorder, null);
        Console.WriteLine("recorder Start() -> " + started);
        if (!started)
        {
            throw new InvalidOperationException("recorder refused to start");
        }

        Console.WriteLine("recording " + seconds + "s at " + fps + " fps...");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        recorder.GetType().GetMethod("Stop", Flags).Invoke(recorder, null);
        recorder.GetType().GetMethod("Dispose", Flags).Invoke(recorder, null);
        Console.WriteLine("recorder stopped");

        // The window keeps painting: later phases measure their own effect on it.
        _form = form;
        _paints = paints;
        _clockStartUtc = form.StartedUtc;
        BuildPaintIndex();
        Report("while recording", 0, paints[paints.Count - 1].Item2);
        return paints;
    }

    // === page-heap stress mode ===

    // Keeps the real recorder active while exports and overlay re-encodes consume already-finalized
    // segments. At the end it disposes the recorder without a separate Stop call, then repeats short
    // live-teardown cycles around the next-writer preparation boundary. This is intentionally a
    // separate mode: its job is native lifetime/heap pressure, not the frame-alignment report above.
    private static void RunStress(
        Assembly plugin, string scratch, int seconds, int fps, int exportEverySeconds, int teardownCycles,
        int width, int height)
    {
        var root = Path.Combine(scratch, "stress_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var buffer = Path.Combine(root, "buffer");
        var exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(buffer);
        Directory.CreateDirectory(exports);

        Console.WriteLine("stress root: " + root);
        Console.WriteLine("process: " + (Environment.Is64BitProcess ? "x64" : "x86") +
            ", duration=" + seconds + "s fps=" + fps +
            " exportEvery=" + exportEverySeconds + "s teardownCycles=" + teardownCycles +
            " surface=" + width + "x" + height);

        var paints = new List<Tuple<int, double>>();
        MarkerForm form = null;
        var ready = new ManualResetEventSlim(false);
        var ui = new Thread(() =>
        {
            form = new MarkerForm(paints, width, height);
            form.Shown += (s, e) => ready.Set();
            Application.Run(form);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("stress marker window did not appear");
        }

        var hwnd = IntPtr.Zero;
        form.Invoke((Action)(() => hwnd = form.Handle));
        _form = form;
        _paints = paints;
        _clockStartUtc = form.StartedUtc;

        // Borderless access requires a packaged-app manifest capability. Playnite is unpackaged,
        // and full page heap exposes an out-of-bounds read in GraphicsCapture.dll while that
        // unsupported access request marshals its result. The production fix removes the request;
        // set its one-time guard here so a stale pre-fix plugin binary can reach the rest of the soak.
        var border = plugin.GetType("PlayniteAchievements.Services.Capture.WgcCaptureBorder");
        border?.GetField("_accessRequested", Flags)?.SetValue(null, 1);
        Console.WriteLine("[stress] skipped optional borderless-access request (unpackaged host)");

        object recorder = null;
        Task<bool> exportTask = null;
        var exportIndex = 0;
        var exportFailures = 0;
        try
        {
            recorder = NewRecorder(plugin, hwnd, buffer, fps);
            if (!(bool)recorder.GetType().GetMethod("Start", Flags).Invoke(recorder, null))
            {
                throw new InvalidOperationException("stress recorder refused to start");
            }

            var timer = Stopwatch.StartNew();
            var nextExport = TimeSpan.FromSeconds(Math.Max(10, exportEverySeconds));
            while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                Thread.Sleep(200);

                if (exportTask != null && exportTask.IsCompleted)
                {
                    if (!CompleteStressExport(exportTask, exportIndex))
                    {
                        exportFailures++;
                    }

                    exportTask = null;
                }

                if (exportTask == null && timer.Elapsed >= nextExport && CountFinalizedSegments(buffer) >= 4)
                {
                    exportIndex++;
                    var current = exportIndex;
                    var baseClip = Path.Combine(exports, "base_" + current.ToString("D4") + ".mp4");
                    var composited = Path.Combine(exports, "composited_" + current.ToString("D4") + ".mp4");
                    Console.WriteLine("[stress " + timer.Elapsed.ToString(@"mm\:ss") + "] starting overlapping export #" + current);
                    exportTask = Task.Run(() =>
                    {
                        if (!ExportClip(plugin, buffer, baseClip))
                        {
                            return false;
                        }

                        return ReencodeStressClip(plugin, baseClip, composited, fps);
                    });
                    nextExport = timer.Elapsed + TimeSpan.FromSeconds(Math.Max(10, exportEverySeconds));
                }

                if (((int)timer.Elapsed.TotalSeconds % 30) == 0 && timer.ElapsedMilliseconds % 1000 < 250)
                {
                    Console.WriteLine("[stress " + timer.Elapsed.ToString(@"mm\:ss") + "] finalized=" +
                        CountFinalizedSegments(buffer) + " total=" + Directory.GetFiles(buffer, "seg_*.mp4").Length);
                }
            }

            // No Stop first: this intentionally asks Dispose to coordinate with an actively pumping
            // encoder, the lifetime boundary that bounded joins used to make unsafe.
            Console.WriteLine("[stress] forcing live recorder teardown");
            var teardown = Stopwatch.StartNew();
            ((IDisposable)recorder).Dispose();
            recorder = null;
            Console.WriteLine("[stress] live recorder teardown returned in " + teardown.ElapsedMilliseconds + "ms");

            if (exportTask != null)
            {
                if (!CompleteStressExport(exportTask, exportIndex))
                {
                    exportFailures++;
                }

                exportTask = null;
            }

            for (var cycle = 1; cycle <= Math.Max(0, teardownCycles); cycle++)
            {
                var cycleBuffer = Path.Combine(root, "teardown_" + cycle.ToString("D3"));
                Directory.CreateDirectory(cycleBuffer);
                var shortRun = NewRecorder(plugin, hwnd, cycleBuffer, fps);
                if (!(bool)shortRun.GetType().GetMethod("Start", Flags).Invoke(shortRun, null))
                {
                    throw new InvalidOperationException("teardown-cycle recorder refused to start");
                }

                // Next-writer preparation begins 750 ms before the five-second boundary.
                Thread.Sleep(4350);
                ((IDisposable)shortRun).Dispose();
                Console.WriteLine("[stress] teardown cycle " + cycle + "/" + teardownCycles + " complete");
            }
        }
        finally
        {
            try { (recorder as IDisposable)?.Dispose(); } catch { }
            try
            {
                if (form != null && !form.IsDisposed)
                {
                    form.BeginInvoke((Action)(() => form.Close()));
                }
            }
            catch { }
            try { ui.Join(TimeSpan.FromSeconds(5)); } catch { }
        }

        Console.WriteLine("[stress] exports=" + exportIndex + " failures=" + exportFailures +
            " finalizedSegments=" + CountFinalizedSegments(buffer));
        if (exportIndex == 0 || exportFailures != 0)
        {
            throw new InvalidOperationException(
                "stress run did not complete cleanly (exports=" + exportIndex + ", failures=" + exportFailures + ")");
        }
    }

    private static bool CompleteStressExport(Task<bool> task, int index)
    {
        try
        {
            var ok = task.GetAwaiter().GetResult();
            Console.WriteLine("[stress] overlapping export #" + index + " -> " + ok);
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[stress] overlapping export #" + index + " failed: " + (ex.InnerException ?? ex));
            return false;
        }
    }

    // Runs the MF/D3D/export overlap without Windows.Graphics.Capture. This is both a focused lease
    // regression and a useful fallback on test sessions where the per-user CaptureService is stopped.
    // The primary --stress mode remains the only one that covers WGC pump teardown itself.
    private static void RunMediaFoundationStress(
        Assembly plugin, string scratch, int seconds, int fps, int exportEverySeconds, int width, int height)
    {
        var root = Path.Combine(scratch, "mf_stress_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var buffer = Path.Combine(root, "buffer");
        var exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(buffer);
        Directory.CreateDirectory(exports);

        Console.WriteLine("MF stress root: " + root);
        Console.WriteLine("process: " + (Environment.Is64BitProcess ? "x64" : "x86") +
            ", duration=" + seconds + "s fps=" + fps + " exportEvery=" + exportEverySeconds +
            "s surface=" + width + "x" + height);

        var runtimeType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationRuntime");
        if (runtimeType == null)
        {
            throw new InvalidOperationException("plugin does not contain the shared MediaFoundationRuntime lease");
        }

        var acquire = runtimeType.GetMethod("Acquire", Flags);
        var deviceType = Type.GetType("SharpDX.Direct3D11.Device, SharpDX.Direct3D11");
        var textureType = Type.GetType("SharpDX.Direct3D11.Texture2D, SharpDX.Direct3D11");
        var encoderType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationH264Encoder");
        var recordingPaths = plugin.GetType("PlayniteAchievements.Services.Recording.RecordingPaths");
        var buildName = recordingPaths.GetMethod("BuildSegmentFileName", Flags);
        var write = encoderType.GetMethod("WriteFrame", Flags);
        var segmentSeconds = 3;
        var frameDuration = TimeSpan.TicksPerSecond / Math.Max(1, fps);
        var sessionStart = DateTime.UtcNow;
        var totalFrames = (long)Math.Max(segmentSeconds * 4, seconds) * fps;
        var nextExportAt = TimeSpan.FromSeconds(Math.Max(10, exportEverySeconds));
        var timer = Stopwatch.StartNew();
        var exportIndex = 0;
        var exportFailures = 0;
        var finalized = 0;
        Task<bool> exportTask = null;
        object encoder = null;
        object device = null;
        object texture = null;
        IDisposable lifetime = null;

        try
        {
            lifetime = (IDisposable)acquire.Invoke(null, null);
            device = Activator.CreateInstance(
                deviceType,
                Type.GetType("SharpDX.Direct3D.DriverType, SharpDX").GetField("Hardware").GetValue(null),
                Enum.ToObject(Type.GetType("SharpDX.Direct3D11.DeviceCreationFlags, SharpDX.Direct3D11"), 0x20 | 0x800));
            texture = MakeTexture(deviceType, textureType, device, width, height);

            for (var frame = 0L; frame < totalFrames; frame++)
            {
                var segmentIndex = frame / (segmentSeconds * (long)fps);
                var segmentFrame = frame % (segmentSeconds * (long)fps);
                if (segmentFrame == 0)
                {
                    if (encoder != null)
                    {
                        ((IDisposable)encoder).Dispose();
                        encoder = null;
                        finalized++;
                    }

                    var startUtc = sessionStart.AddSeconds(segmentIndex * segmentSeconds);
                    var name = (string)buildName.Invoke(null, new object[] { startUtc, width, height });
                    encoder = Activator.CreateInstance(
                        encoderType, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public, null,
                        new object[] { device, Path.Combine(buffer, name), width, height, fps, 4_000_000 }, null);
                }

                write.Invoke(encoder, new object[] { texture, segmentFrame * frameDuration, frameDuration });

                if (exportTask != null && exportTask.IsCompleted)
                {
                    if (!CompleteStressExport(exportTask, exportIndex))
                    {
                        exportFailures++;
                    }

                    exportTask = null;
                }

                if (exportTask == null && finalized >= 3 && timer.Elapsed >= nextExportAt)
                {
                    exportIndex++;
                    var current = exportIndex;
                    var baseClip = Path.Combine(exports, "base_" + current.ToString("D4") + ".mp4");
                    var composited = Path.Combine(exports, "composited_" + current.ToString("D4") + ".mp4");
                    Console.WriteLine("[MF stress " + timer.Elapsed.ToString(@"mm\:ss") + "] overlapping export #" + current);
                    exportTask = Task.Run(() =>
                    {
                        if (!ExportClip(plugin, buffer, baseClip))
                        {
                            return false;
                        }

                        return ReencodeStressClip(plugin, baseClip, composited, fps);
                    });
                    nextExportAt = timer.Elapsed + TimeSpan.FromSeconds(Math.Max(10, exportEverySeconds));
                }

                var target = TimeSpan.FromTicks((frame + 1) * TimeSpan.TicksPerSecond / Math.Max(1, fps));
                var delay = target - timer.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }
            }

            ((IDisposable)encoder)?.Dispose();
            encoder = null;
            finalized++;
            if (exportTask != null)
            {
                if (!CompleteStressExport(exportTask, exportIndex))
                {
                    exportFailures++;
                }

                exportTask = null;
            }
        }
        finally
        {
            try { (encoder as IDisposable)?.Dispose(); } catch { }
            try { (texture as IDisposable)?.Dispose(); } catch { }
            try { (device as IDisposable)?.Dispose(); } catch { }
            try { lifetime?.Dispose(); } catch { }
        }

        Console.WriteLine("[MF stress] exports=" + exportIndex + " failures=" + exportFailures +
            " finalizedSegments=" + finalized);
        if (exportIndex == 0 || exportFailures != 0)
        {
            throw new InvalidOperationException(
                "MF stress did not complete cleanly (exports=" + exportIndex + ", failures=" + exportFailures + ")");
        }
    }

    private static int CountFinalizedSegments(string buffer)
    {
        return Directory.GetFiles(buffer, "seg_*.mp4").Count(Mp4.HasMoov);
    }

    private static bool ReencodeStressClip(Assembly plugin, string baseClip, string outputPath, int fps)
    {
        var track = BuildTrack(plugin);
        var reencoderType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationOverlayReencoder");
        var reencoder = Activator.CreateInstance(
            reencoderType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { ConsoleLogger.Create(reencoderType.GetConstructors(Flags)[0].GetParameters()[0].ParameterType) },
            null);
        var qualityType = plugin.GetType("PlayniteAchievements.Models.Settings.RecordingQuality");
        var quality = Enum.Parse(qualityType, Enum.GetNames(qualityType)[0]);
        var clipSeconds = Mp4.VideoTiming(baseClip).Seconds;
        return (bool)reencoderType.GetMethod("Export", Flags).Invoke(reencoder, new object[]
        {
            baseClip, track,
            1.0, Math.Min(4.0, clipSeconds), _videoLeadSeconds,
            clipSeconds, null, 0d,
            outputPath, fps, quality,
        });
    }

    private static MarkerForm _form;
    private static List<Tuple<int, double>> _paints;

    // Everything needed to ask the only question that matters: does output time t show the frame that
    // was on screen at windowStart + t? Relative checks (ordering, durations, segment lengths) all pass
    // with a whole-clip shift, which is exactly the defect being chased.
    private static DateTime _clockStartUtc;
    private static DateTime _requestedWindowStartUtc;

    // Where the clip really begins — the reference every position inside it must be measured from.
    private static DateTime _windowStartUtc;
    private static double _videoLeadSeconds;
    private static Dictionary<int, DateTime> _paintUtcByCounter;

    private static void BuildPaintIndex()
    {
        _paintUtcByCounter = new Dictionary<int, DateTime>();
        foreach (var paint in _paints.ToArray())
        {
            _paintUtcByCounter[paint.Item1] = _clockStartUtc.AddMilliseconds(paint.Item2);
        }
    }

    /// <summary>
    /// Compares when each output frame was actually painted against when the clip's own timeline claims
    /// it should have been. <paramref name="shiftSeconds"/> is where output zero sits relative to the
    /// requested window start: the base clip starts a keyframe lead early, the composited clip starts
    /// exactly on the window.
    /// </summary>
    private static void ReportAlignment(List<Tuple<double, int>> identities, double shiftSeconds, string label)
    {
        if (_paintUtcByCounter == null || _windowStartUtc == default(DateTime))
        {
            Console.WriteLine("  alignment: no reference (window or paint index missing)");
            return;
        }

        // The window keeps painting after recording stops, so refresh before comparing.
        BuildPaintIndex();

        var offsets = new List<double>();
        var perFrame = new List<string> { "outputSeconds,counter,offsetSeconds" };
        foreach (var entry in identities)
        {
            DateTime painted;
            if (entry.Item2 < 0 || !_paintUtcByCounter.TryGetValue(entry.Item2, out painted))
            {
                continue;
            }

            var expected = _windowStartUtc.AddSeconds(shiftSeconds + entry.Item1);
            var offset = (painted - expected).TotalSeconds;
            offsets.Add(offset);
            perFrame.Add(
                entry.Item1.ToString("0.000") + "," + entry.Item2 + "," + offset.ToString("0.000"));
        }

        try
        {
            File.WriteAllLines(
                Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "alignment_" + label.Replace(" ", "_") + ".csv"),
                perFrame);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  (could not write the per-frame alignment csv: " + ex.Message + ")");
        }

        // Where the stale frames sit matters more than how many: a uniform shift and an isolated run of
        // stale frames are different defects with the same median.
        var stale = 0;
        var firstStaleAt = -1.0;
        var worstAt = -1.0;
        var worst = 0.0;
        foreach (var entry in identities)
        {
            DateTime painted;
            if (entry.Item2 < 0 || !_paintUtcByCounter.TryGetValue(entry.Item2, out painted))
            {
                continue;
            }

            var offset = (painted - _windowStartUtc.AddSeconds(shiftSeconds + entry.Item1)).TotalSeconds;
            if (Math.Abs(offset) <= 0.25)
            {
                continue;
            }

            stale++;
            if (firstStaleAt < 0)
            {
                firstStaleAt = entry.Item1;
            }

            if (Math.Abs(offset) > Math.Abs(worst))
            {
                worst = offset;
                worstAt = entry.Item1;
            }
        }

        Console.WriteLine(
            "  frames off by more than 0.25s: " + stale + " of " + identities.Count +
            (stale > 0
                ? "   first at " + firstStaleAt.ToString("0.000") + "s, worst " +
                  worst.ToString("+0.000;-0.000") + "s at " + worstAt.ToString("0.000") + "s"
                : string.Empty));

        if (offsets.Count < 5)
        {
            Console.WriteLine("  alignment " + label + ": too few identified frames (" + offsets.Count + ")");
            return;
        }

        offsets.Sort();
        var median = offsets[offsets.Count / 2];
        Console.WriteLine(
            "  ALIGNMENT " + label.PadRight(12) +
            " median offset " + median.ToString("+0.000;-0.000") + "s" +
            "  (min " + offsets[0].ToString("+0.000;-0.000") +
            ", max " + offsets[offsets.Count - 1].ToString("+0.000;-0.000") +
            ", n=" + offsets.Count + ")");
        Console.WriteLine(
            Math.Abs(median) <= 0.15
                ? "  => footage sits where the timeline says: content and clock agree"
                : "  => OFF BY " + median.ToString("+0.000;-0.000") + "s: every frame is " +
                  (median > 0 ? "LATER" : "EARLIER") + " than the timeline claims, so a card placed by " +
                  "clock lands " + Math.Abs(median).ToString("0.00") + "s wrong against the picture");
    }

    // Interval spread of the captured window's paints over a window of the run, which is what a game
    // would feel as smoothness.
    private static void Report(string label, double fromMs, double toMs)
    {
        var gaps = new List<double>();
        lock (_paints ?? new List<Tuple<int, double>>())
        {
        }

        var snapshot = _paints.ToArray();
        for (var i = 1; i < snapshot.Length; i++)
        {
            var at = snapshot[i].Item2;
            if (at < fromMs || at > toMs)
            {
                continue;
            }

            gaps.Add(at - snapshot[i - 1].Item2);
        }

        if (gaps.Count < 5)
        {
            Console.WriteLine("  paints " + label + ": too few samples (" + gaps.Count + ")");
            return;
        }

        gaps.Sort();
        Console.WriteLine(
            "  paints " + label.PadRight(22) +
            " n=" + gaps.Count.ToString().PadLeft(5) +
            " median=" + gaps[gaps.Count / 2].ToString("0.00") + "ms" +
            " p95=" + gaps[(int)(gaps.Count * 0.95)].ToString("0.00") + "ms" +
            " p99=" + gaps[(int)(gaps.Count * 0.99)].ToString("0.00") + "ms" +
            " max=" + gaps[gaps.Count - 1].ToString("0.00") + "ms");
    }

    // === phase 5: what compositing costs the captured window ===

    private const double ToastMaxSeconds = 4.0;

    // The composited "chime": one second of a 1 kHz tone, 48 kHz stereo 16-bit like the plugin's,
    // mixed in a little ahead of the card. The synthetic game audio is 440 Hz, so each is
    // measurable on its own.
    private const double ChimeSeconds = 1.0;
    private const double ChimeLeadSeconds = 0.3;
    private const int ChimeHz = 1000;
    private const int GameToneHz = 440;

    private sealed class CompositionResult
    {
        public long Milliseconds;
        public List<Tuple<double, int>> Identities;
    }

    /// <summary>
    /// Runs the overlay re-encode three ways over the same base clip. The spliced pass and the
    /// whole-clip pass get a production-shaped card — the clip ends half a second after the card
    /// fades, so the card sits at the end and most of the clip is the lead-in the splice copies —
    /// plus a chime mixed into the audio, and must produce the same frame sequence with the card
    /// over the same frames; the third run puts the card inside the second GOP with no chime, so
    /// the audio passes through as AAC and the plan has to copy after the card instead of before.
    /// </summary>
    private static void MeasureComposition(
        Assembly plugin, string baseClip, string outputPath, int fps, double? toastStartOverride = null)
    {
        var clipSeconds = Mp4.VideoTiming(baseClip).Seconds;
        var lateToast = toastStartOverride ?? clipSeconds - ToastMaxSeconds - 0.5;
        var outDir = Path.GetDirectoryName(outputPath);
        var stem = Path.GetFileNameWithoutExtension(outputPath);

        var spliced = RunComposition(plugin, baseClip, outputPath, fps, lateToast, true, true, "spliced");
        var whole = RunComposition(
            plugin, baseClip, Path.Combine(outDir, stem + "_whole.mp4"), fps, lateToast, false, true, "whole-clip");
        RunComposition(
            plugin, baseClip, Path.Combine(outDir, stem + "_early.mp4"), fps, 2.0, true, false, "early card, audio passthrough");
        Report("while recording", 0, 22000);

        Console.WriteLine();
        Console.WriteLine("=== spliced vs whole-clip");
        if (spliced == null || whole == null)
        {
            Console.WriteLine("  one of the passes failed; nothing to compare");
            return;
        }

        Console.WriteLine("  spliced " + spliced.Milliseconds + "ms vs whole-clip " + whole.Milliseconds + "ms => " +
            (100.0 * spliced.Milliseconds / Math.Max(1, whole.Milliseconds)).ToString("0") + "% of the whole-clip time");
        CompareIdentities(spliced.Identities, whole.Identities);
    }

    private static CompositionResult RunComposition(
        Assembly plugin, string baseClip, string outputPath, int fps, double toastStartSeconds,
        bool splice, bool chime, string label)
    {
        Console.WriteLine();
        Console.WriteLine("=== composition (overlay re-encode, " + label + ") with the window still painting");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        try
        {
            var track = BuildTrack(plugin);
            var reencoderType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationOverlayReencoder");
            var reencoder = Activator.CreateInstance(
                reencoderType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new object[] { ConsoleLogger.Create(reencoderType.GetConstructors(Flags)[0].GetParameters()[0].ParameterType) },
                null);
            // Older builds of the plugin (a baseline for comparison) have neither switch.
            reencoderType.GetProperty("SpliceEnabled", Flags)?.SetValue(reencoder, splice);
            reencoderType.GetProperty("PreferSoftwareEncoder", Flags)?.SetValue(reencoder, _softwareEncoder);

            var qualityType = plugin.GetType("PlayniteAchievements.Models.Settings.RecordingQuality");
            var quality = Enum.Parse(qualityType, Enum.GetNames(qualityType)[0]);

            var clipSeconds = Mp4.VideoTiming(baseClip).Seconds;
            var chimeStart = toastStartSeconds - ChimeLeadSeconds;
            var startedAt = _paints[_paints.Count - 1].Item2;
            var timer = Stopwatch.StartNew();

            var ok = (bool)reencoderType.GetMethod("Export", Flags).Invoke(reencoder, new object[]
            {
                baseClip, track,
                toastStartSeconds,      // toastStartSeconds, on the base clip's timeline
                ToastMaxSeconds,        // toastMaxSeconds
                _videoLeadSeconds,      // trimLeadSeconds: the keyframe lead the export reported
                clipSeconds,            // endSeconds
                chime ? Tone(ChimeHz, ChimeSeconds, 0.5) : null,
                chimeStart,
                outputPath, fps, quality,
            });

            timer.Stop();
            var endedAt = _paints[_paints.Count - 1].Item2;
            Console.WriteLine("  Export=" + ok + " in " + timer.ElapsedMilliseconds + "ms for a " +
                clipSeconds.ToString("0.0") + "s clip (card at " + toastStartSeconds.ToString("0.00") + "s" +
                (chime ? ", chime at " + chimeStart.ToString("0.00") + "s" : ", no chime") + ")");
            Report("while compositing", startedAt, endedAt);
            if (!ok)
            {
                return null;
            }

            foreach (var line in Mp4.Describe(outputPath))
            {
                Console.WriteLine("  composited " + line);
            }

            // The reported defect is wrong frames in the *composited* output, so read its identities
            // back too rather than trusting that the base clip being right means this one is.
            Console.WriteLine();
            Console.WriteLine("=== decoded frame identities of the COMPOSITED clip (" + label + ")");
            var identities = DecodeAndReport(outputPath, 0.0, "composited");
            // The card covers base [toastStart, toastStart + ToastMax]; the output drops the lead.
            ReportCardWindow(
                outputPath, toastStartSeconds - _videoLeadSeconds,
                toastStartSeconds - _videoLeadSeconds + ToastMaxSeconds, fps);
            ReportAudio(outputPath, clipSeconds - _videoLeadSeconds, chime ? chimeStart - _videoLeadSeconds : -1);
            return new CompositionResult { Milliseconds = timer.ElapsedMilliseconds, Identities = identities };
        }
        catch (Exception ex)
        {
            Console.WriteLine("  composition failed: " + (ex.InnerException ?? ex));
            return null;
        }
    }

    /// <summary>
    /// Both passes decode the same base frames and must put the same source frame at the same
    /// output time; a spliced clip that is merely well-ordered could still be shifted or missing
    /// frames at a splice point.
    /// </summary>
    private static void CompareIdentities(List<Tuple<double, int>> spliced, List<Tuple<double, int>> whole)
    {
        Console.WriteLine("  frames: spliced=" + spliced.Count + " whole=" + whole.Count);
        var differences = 0;
        var count = Math.Min(spliced.Count, whole.Count);
        for (var i = 0; i < count; i++)
        {
            var a = spliced[i];
            var b = whole[i];
            if (a.Item2 != b.Item2 || Math.Abs(a.Item1 - b.Item1) > 0.001)
            {
                differences++;
                if (differences <= 10)
                {
                    Console.WriteLine("  DIFF at index " + i + ": spliced " + a.Item1.ToString("0.000") + "s->" + a.Item2 +
                        "  whole " + b.Item1.ToString("0.000") + "s->" + b.Item2);
                }
            }
        }

        differences += Math.Abs(spliced.Count - whole.Count);
        Console.WriteLine(differences == 0
            ? "  SPLICE == WHOLE: identical frame sequence and timing"
            : "  SPLICE != WHOLE: " + differences + " differing frames   <-- splice defect");
    }

    /// <summary>
    /// Reads which output frames carry the card, by chroma at the card's centre (the harness card is
    /// a translucent purple the window never paints), and checks that set against the window the
    /// card was asked to cover. Catches a splice that drops the compositing or lands it on the wrong
    /// frames, which frame identities alone cannot see.
    /// </summary>
    private static void ReportCardWindow(string clip, double expectedStart, double expectedEnd, int fps)
    {
        var carded = new List<Tuple<double, bool>>();
        DecodeNv12(clip, (time, frame, stride, w, h) =>
        {
            // BuildTrack: bottom-left, 24 DIP gap at scale 1, 420x130 card in a 1920x1080 client.
            var cx = (int)((24 + 420 / 2.0) * w / 1920.0);
            var cy = (int)((1080 - 24 - 130 / 2.0) * h / 1080.0);
            carded.Add(Tuple.Create(time, IsCardPurpleNv12(frame, stride, h, cx, cy)));
        });

        var tolerance = 1.5 / fps;
        var cardedCount = 0;
        var outsideWindow = 0;
        var missingInside = 0;
        double first = -1, last = -1;
        foreach (var entry in carded)
        {
            var inside = entry.Item1 >= expectedStart - tolerance && entry.Item1 <= expectedEnd + tolerance;
            var strictlyInside = entry.Item1 >= expectedStart + tolerance && entry.Item1 <= expectedEnd - tolerance;
            if (entry.Item2)
            {
                cardedCount++;
                if (first < 0) { first = entry.Item1; }
                last = entry.Item1;
                if (!inside) { outsideWindow++; }
            }
            else if (strictlyInside)
            {
                missingInside++;
            }
        }

        Console.WriteLine("  card frames: " + cardedCount + " of " + carded.Count +
            (cardedCount > 0 ? " from " + first.ToString("0.000") + "s to " + last.ToString("0.000") + "s" : string.Empty) +
            "; expected window " + expectedStart.ToString("0.000") + "s to " + expectedEnd.ToString("0.000") + "s");
        Console.WriteLine("  carded frames outside the window: " + outsideWindow +
            "   uncarded frames inside it: " + missingInside);
        Console.WriteLine(cardedCount > 0 && outsideWindow == 0 && missingInside == 0
            ? "  CARD WINDOW OK"
            : "  CARD WINDOW MISMATCH   <-- the card is missing or on the wrong frames");
    }

    // The harness card is 0x90/0x18/0x40 (R/G/B) at alpha 0xC0 over a dark window whose only other
    // colours are white, gold text and an orange-red bar. In BT.709 limited Y'CbCr the card lands
    // near Cb 134 / Cr 179 over the dark ground and Cb 121 / Cr 200 over the bar; the bar alone is
    // Cb 79 / Cr 212, gold text Cb 30 / Cr 154, and grey ground or white text sit at 128 / 128.
    // Cr well above neutral together with Cb not far below it is therefore the card and nothing else.
    //
    // The Cr floor is 165 rather than a value just clear of neutral, because the sweeping bar
    // crosses this very sample point: it is painted across [ClientH-120, ClientH-40), and the card's
    // centre sits at ClientH-89 in the same units. A frame caught mid-sweep samples part bar and
    // part ground, and that blend reached Cb 115 / Cr 152, which a 150 floor accepted as a card on
    // frames seconds away from the toast. Mixing the bar (Cb 79 / Cr 212) with the ground
    // (Cb 131 / Cr 128) by any fraction t gives Cb = 131 - 52t and Cr = 128 + 84t, so Cr above 165
    // forces t above 0.44 and therefore Cb below 108 — under the Cb floor. No mixture of the two can
    // satisfy both bounds, while the card clears them either way it is composited.
    private static bool IsCardPurpleNv12(byte[] frame, int stride, int height, int cx, int cy)
    {
        long cb = 0, cr = 0, n = 0;
        var chromaPlane = stride * height;
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var offset = chromaPlane + (((cy / 2) + dy) * stride) + (((cx / 2) + dx) * 2);
                if (offset < 0 || offset + 1 >= frame.Length)
                {
                    continue;
                }

                cb += frame[offset];
                cr += frame[offset + 1];
                n++;
            }
        }

        if (n == 0)
        {
            return false;
        }

        cb /= n; cr /= n;
        return cr > 165 && cb > 110;
    }

    /// <summary>
    /// Decodes the clip's audio and checks that it survived the pass: the track runs the length of
    /// the video, the synthetic game tone is present throughout, and — when a chime was mixed in —
    /// the chime tone appears only inside its second. Exercises the remux's audio interleave in
    /// both modes: AAC passthrough (no chime) and PCM decode, mix, re-encode (chime).
    /// </summary>
    private static void ReportAudio(string clip, double expectedSeconds, double chimeStartOut)
    {
        var pcm = DecodeAudio(clip);
        if (pcm == null)
        {
            Console.WriteLine("  AUDIO MISSING   <-- the clip has no decodable audio track");
            return;
        }

        var seconds = pcm.Length / (4.0 * 48000);
        var durationOk = Math.Abs(seconds - expectedSeconds) < 0.25;
        var gameBefore = ToneDb(pcm, GameToneHz, 0.5, 1.5);
        var gameAfter = ToneDb(pcm, GameToneHz, seconds - 1.5, seconds - 0.5);
        Console.WriteLine("  audio: " + seconds.ToString("0.000") + "s (video " + expectedSeconds.ToString("0.000") + "s); " +
            GameToneHz + "Hz game tone " + gameBefore.ToString("0.0") + "dB at the start, " +
            gameAfter.ToString("0.0") + "dB at the end");
        var gameOk = gameBefore > -40 && gameAfter > -40;

        var chimeOk = true;
        if (chimeStartOut >= 0)
        {
            var inside = ToneDb(pcm, ChimeHz, chimeStartOut + 0.1, chimeStartOut + ChimeSeconds - 0.1);
            var before = ToneDb(pcm, ChimeHz, chimeStartOut - 1.2, chimeStartOut - 0.2);
            var after = ToneDb(pcm, ChimeHz, chimeStartOut + ChimeSeconds + 0.2, chimeStartOut + ChimeSeconds + 1.2);
            Console.WriteLine("  chime " + ChimeHz + "Hz: " + inside.ToString("0.0") + "dB inside its second, " +
                before.ToString("0.0") + "dB before, " + after.ToString("0.0") + "dB after");
            chimeOk = inside > before + 20 && inside > after + 20;
        }

        Console.WriteLine(durationOk && gameOk && chimeOk
            ? "  AUDIO OK"
            : "  AUDIO MISMATCH   <-- " + (!durationOk ? "length " : string.Empty) +
              (!gameOk ? "game-tone " : string.Empty) + (!chimeOk ? "chime " : string.Empty));
    }

    /// <summary>The clip's first audio stream as 48 kHz stereo 16-bit PCM, or null.</summary>
    private static byte[] DecodeAudio(string clip)
    {
        MediaManager.Startup();
        try
        {
            using (var reader = new SourceReader(clip))
            {
                reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((int)SourceReaderIndex.FirstAudioStream, true);
                using (var pcmType = new MediaType())
                {
                    pcmType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    pcmType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    pcmType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000);
                    pcmType.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
                    pcmType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
                    pcmType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4);
                    pcmType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 48000 * 4);
                    reader.SetCurrentMediaType((int)SourceReaderIndex.FirstAudioStream, pcmType);
                }

                using (var output = new MemoryStream())
                {
                    while (true)
                    {
                        var sample = reader.ReadSample(
                            (int)SourceReaderIndex.FirstAudioStream, SourceReaderControlFlags.None,
                            out _, out var flags, out _);
                        if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                        {
                            sample?.Dispose();
                            break;
                        }

                        using (sample)
                        using (var buffer = sample.ConvertToContiguousBuffer())
                        {
                            var ptr = buffer.Lock(out _, out var length);
                            try
                            {
                                var bytes = new byte[length];
                                Marshal.Copy(ptr, bytes, 0, length);
                                output.Write(bytes, 0, length);
                            }
                            finally
                            {
                                buffer.Unlock();
                            }
                        }
                    }

                    return output.Length > 0 ? output.ToArray() : null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  audio decode failed: " + ex.Message);
            return null;
        }
        finally
        {
            try { MediaManager.Shutdown(); } catch { }
        }
    }

    /// <summary>Goertzel power of one tone in the left channel over [from, to] seconds, in dBFS.</summary>
    private static double ToneDb(byte[] pcm, int hz, double from, double to)
    {
        var start = Math.Max(0, (int)(from * 48000));
        var end = Math.Min(pcm.Length / 4, (int)(to * 48000));
        var n = end - start;
        if (n < 480)
        {
            return double.NegativeInfinity;
        }

        var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * hz / 48000.0);
        double s0 = 0, s1 = 0, s2 = 0;
        for (var i = start; i < end; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 4) / 32768.0;
            s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1 + s2 * s2 - coefficient * s1 * s2) / (n * (double)n) * 4.0;
        return 10.0 * Math.Log10(Math.Max(1e-12, power));
    }

    /// <summary>A stereo 48 kHz 16-bit tone of the given length and amplitude, phase starting at zero.</summary>
    private static byte[] Tone(int hz, double seconds, double amplitude)
    {
        var frames = (int)(seconds * 48000);
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var value = (short)(Math.Sin(2.0 * Math.PI * hz * i / 48000.0) * amplitude * short.MaxValue);
            bytes[i * 4] = (byte)value;
            bytes[i * 4 + 1] = (byte)(value >> 8);
            bytes[i * 4 + 2] = (byte)value;
            bytes[i * 4 + 3] = (byte)(value >> 8);
        }

        return bytes;
    }

    // A one-keyframe track: a translucent card, held for the whole toast interval.
    private static object BuildTrack(Assembly plugin)
    {
        var trackType = plugin.GetType("PlayniteAchievements.Services.Capture.ToastOverlayTrack");
        var frameType = trackType.GetNestedType("Frame", BindingFlags.Public | BindingFlags.NonPublic);
        var sampleType = trackType.GetNestedType("Sample", BindingFlags.Public | BindingFlags.NonPublic);
        var track = Activator.CreateInstance(trackType);

        const int CardW = 420;
        const int CardH = 130;
        var pixels = new byte[CardW * CardH * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;      // B, premultiplied against alpha 0xC0
            pixels[i + 1] = 0x18;
            pixels[i + 2] = 0x90;
            pixels[i + 3] = 0xC0;
        }

        byte[] deflated;
        using (var memory = new MemoryStream())
        {
            using (var deflate = new System.IO.Compression.DeflateStream(
                memory, System.IO.Compression.CompressionMode.Compress, true))
            {
                deflate.Write(pixels, 0, pixels.Length);
            }

            deflated = memory.ToArray();
        }

        var frame = Activator.CreateInstance(frameType);
        frameType.GetProperty("Width").SetValue(frame, CardW);
        frameType.GetProperty("Height").SetValue(frame, CardH);
        frameType.GetProperty("Deflated").SetValue(frame, deflated);
        frameType.GetProperty("IsDelta").SetValue(frame, false);
        var frames = trackType.GetProperty("Frames").GetValue(track);
        frames.GetType().GetMethod("Add").Invoke(frames, new[] { frame });

        var samples = trackType.GetProperty("Samples").GetValue(track);
        var add = samples.GetType().GetMethod("Add");
        for (var ms = 0; ms <= 4000; ms += 33)
        {
            var sample = Activator.CreateInstance(sampleType);
            sampleType.GetField("ElapsedMs").SetValue(sample, ms);
            sampleType.GetField("FrameIndex").SetValue(sample, 0);
            sampleType.GetField("SlideXPhys").SetValue(sample, 0.0);
            sampleType.GetField("SlideYPhys").SetValue(sample, 0.0);
            sampleType.GetField("GlowScale").SetValue(sample, 1.0);
            sampleType.GetField("CardWPhys").SetValue(sample, CardW);
            sampleType.GetField("CardHPhys").SetValue(sample, CardH);
            sampleType.GetField("HostOpacity").SetValue(sample, 1.0);
            sampleType.GetField("ClientW").SetValue(sample, 1920);
            sampleType.GetField("ClientH").SetValue(sample, 1080);
            add.Invoke(samples, new[] { sample });
        }

        trackType.GetProperty("DurationSeconds").SetValue(track, 4.0);
        trackType.GetProperty("AlignRight").SetValue(track, false);
        trackType.GetProperty("AlignBottom").SetValue(track, true);
        trackType.GetProperty("GapDip").SetValue(track, 24.0);
        trackType.GetProperty("MonitorScale").SetValue(track, 1.0);
        trackType.GetProperty("AchievementName").SetValue(track, "Harness");
        trackType.GetProperty("ProviderKey").SetValue(track, "harness");
        return track;
    }

    // === phase 6: could part of the clip be copied instead of re-encoded? ===

    private static void CompareParameterSets(string buffer, string composited)
    {
        Console.WriteLine();
        Console.WriteLine("=== can a re-encoded span be spliced into copied video? (avcC must match)");
        try
        {
            var segment = Directory.GetFiles(buffer, "seg_*.mp4").OrderBy(p => p).First();
            var fromSegment = Mp4.AvcC(segment);
            var fromRecode = Mp4.AvcC(composited);
            Console.WriteLine("  segment  avcC: " + Describe(fromSegment));
            Console.WriteLine("  recoded  avcC: " + Describe(fromRecode));
            var same = fromSegment.Length == fromRecode.Length;
            if (same)
            {
                for (var i = 0; i < fromSegment.Length; i++)
                {
                    if (fromSegment[i] != fromRecode[i]) { same = false; break; }
                }
            }

            Console.WriteLine(same
                ? "  => IDENTICAL: copied and re-encoded GOPs can share one track"
                : "  => DIFFERENT: a single track cannot hold both without re-signalling");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  comparison failed: " + ex.Message);
        }
    }

    private static string Describe(byte[] avcC)
    {
        var hex = string.Join("", avcC.Take(16).Select(b => b.ToString("x2")));
        return avcC.Length + " bytes, profile=" + avcC[1] + " level=" + avcC[3] + ", first16=" + hex;
    }

    private const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object NewRecorder(Assembly plugin, IntPtr hwnd, string buffer, int fps)
    {
        var type = plugin.GetType("PlayniteAchievements.Services.Capture.WgcVideoRecorder");
        var ctor = type.GetConstructors(Flags).OrderByDescending(c => c.GetParameters().Length).First();
        Console.WriteLine("ctor: " + string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)));

        Func<IntPtr> resolve = () => hwnd;
        var args = new List<object>();
        foreach (var parameter in ctor.GetParameters())
        {
            var t = parameter.ParameterType;
            if (t == typeof(Func<IntPtr>))
            {
                args.Add(resolve);
            }
            else if (t == typeof(string))
            {
                args.Add(buffer);
            }
            else if (t == typeof(int))
            {
                // fps then segmentSeconds, by name.
                args.Add(parameter.Name.IndexOf("fps", StringComparison.OrdinalIgnoreCase) >= 0 ? fps : 5);
            }
            else if (t.IsEnum)
            {
                // Native resolution keeps the barcode at 1:1; the first quality value is fine.
                var names = Enum.GetNames(t);
                var pick = names.FirstOrDefault(n => n.Equals("Native", StringComparison.OrdinalIgnoreCase)) ?? names[0];
                Console.WriteLine("  " + t.Name + " -> " + pick + "   (options: " + string.Join("/", names) + ")");
                args.Add(Enum.Parse(t, pick));
            }
            else
            {
                // A real logger, not null: the pump swallows its own exceptions into the logger, so a
                // null one hides the reason capture stops.
                args.Add(ConsoleLogger.Create(t));
            }
        }

        return ctor.Invoke(args.ToArray());
    }

    // Feeds the plugin's own encoder deliberately uneven durations through the same call the recorder
    // uses, to see whether it is the encoder that flattens them.
    private static void EncoderDurationTest(Assembly plugin, string outputPath, int fps)
    {
        Console.WriteLine();
        Console.WriteLine("=== encoder duration test (MediaFoundationH264Encoder, uneven durations in)");
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            // The encoder does not start Media Foundation itself — whoever owns a run of encoders
            // holds one MediaFoundationRuntime lease around all of them. Every call site inside the
            // plugin was given one when that became the contract; this standalone test was not, and
            // had been failing on its first write ever since with "Shutdown() has been called".
            var runtimeType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationRuntime");
            using ((IDisposable)runtimeType.GetMethod("Acquire", Flags).Invoke(null, null))
            {
                var deviceType = Type.GetType("SharpDX.Direct3D11.Device, SharpDX.Direct3D11");
                var device = Activator.CreateInstance(
                    deviceType,
                    Type.GetType("SharpDX.Direct3D.DriverType, SharpDX").GetField("Hardware").GetValue(null),
                    Enum.ToObject(Type.GetType("SharpDX.Direct3D11.DeviceCreationFlags, SharpDX.Direct3D11"), 0x20 | 0x800));

                var encoderType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationH264Encoder");
                var encoder = Activator.CreateInstance(
                    encoderType, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public, null,
                    new object[] { device, outputPath, 640, 360, fps, 4_000_000 }, null);

                var textureType = Type.GetType("SharpDX.Direct3D11.Texture2D, SharpDX.Direct3D11");
                var write = encoderType.GetMethod("WriteFrame", Flags);
                var time = 0L;
                for (var i = 0; i < 60; i++)
                {
                    var duration = i % 2 == 0 ? 100_000L : 566_666L; // 10 ms / 56.67 ms
                    var texture = MakeTexture(deviceType, textureType, device, 640, 360);
                    using ((IDisposable)texture)
                    {
                        write.Invoke(encoder, new object[] { texture, time, duration });
                    }

                    time += duration;
                }

                ((IDisposable)encoder).Dispose();
                ((IDisposable)device).Dispose();

                var info = Mp4.VideoTiming(outputPath);
                Console.WriteLine(
                    "  wrote 60 frames summing to 2.000s; result frames=" + info.Samples +
                    " sttsEntries=" + info.SttsEntries + " media=" + info.Seconds.ToString("0.000") + "s");
                Console.WriteLine(info.SttsEntries > 1
                    ? "  => encoder PRESERVES per-frame durations"
                    : "  => encoder FLATTENS durations to a uniform grid  <-- this is the drift cause");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  test failed: " + (ex.InnerException ?? ex).Message);
        }
    }

    private static object MakeTexture(Type deviceType, Type textureType, object device, int w, int h)
    {
        var descriptionType = Type.GetType("SharpDX.Direct3D11.Texture2DDescription, SharpDX.Direct3D11");
        var description = Activator.CreateInstance(descriptionType);
        void Set(string name, object value) => descriptionType.GetField(name).SetValue(description, value);
        Set("Width", w);
        Set("Height", h);
        Set("MipLevels", 1);
        Set("ArraySize", 1);
        Set("Format", Enum.ToObject(Type.GetType("SharpDX.DXGI.Format, SharpDX.DXGI"), 87)); // B8G8R8A8_UNorm
        Set("SampleDescription", Activator.CreateInstance(
            Type.GetType("SharpDX.DXGI.SampleDescription, SharpDX.DXGI"), 1, 0));
        Set("Usage", Enum.ToObject(Type.GetType("SharpDX.Direct3D11.ResourceUsage, SharpDX.Direct3D11"), 0));
        Set("BindFlags", Enum.ToObject(Type.GetType("SharpDX.Direct3D11.BindFlags, SharpDX.Direct3D11"), 0x20 | 0x8));
        Set("CpuAccessFlags", Enum.ToObject(Type.GetType("SharpDX.Direct3D11.CpuAccessFlags, SharpDX.Direct3D11"), 0));
        Set("OptionFlags", Enum.ToObject(Type.GetType("SharpDX.Direct3D11.ResourceOptionFlags, SharpDX.Direct3D11"), 0));
        return Activator.CreateInstance(textureType, device, description);
    }

    // === phase 2: what did the recorder write ===

    private static void ReportSegments(string buffer)
    {
        var segments = Directory.GetFiles(buffer, "seg_*.mp4").OrderBy(p => p).ToList();
        Console.WriteLine();
        Console.WriteLine("=== segments written: " + segments.Count);
        DateTime? previous = null;
        foreach (var path in segments)
        {
            var name = Path.GetFileName(path);
            var info = TimingWhenReadable(path);
            var stamp = ParseStamp(name);
            var gap = previous.HasValue && stamp.HasValue
                ? (stamp.Value - previous.Value).TotalSeconds.ToString("0.000") + "s"
                : "-";
            previous = stamp ?? previous;
            Console.WriteLine(
                "  " + name + "  frames=" + info.Samples + " sttsEntries=" + info.SttsEntries +
                " media=" + info.Seconds.ToString("0.000") + "s  wallGapToNext=" + gap);
        }

        var variable = segments.Count > 0 && TimingWhenReadable(segments[0]).SttsEntries > 1;
        Console.WriteLine(
            "  => segment durations are " + (variable ? "VARIABLE (real per-frame timing preserved)" : "UNIFORM (nominal grid)"));
    }

    // Segments finish writing on a background thread, so a just-rotated one can still be locked.
    private static Mp4.Timing TimingWhenReadable(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                return Mp4.VideoTiming(path);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }

        Console.WriteLine("  (still locked: " + Path.GetFileName(path) + ")");
        return new Mp4.Timing();
    }

    private static DateTime? ParseStamp(string name)
    {
        // Current: seg_yyyyMMdd-HHmmssfffffffZ_WxH.mp4. Older millisecond and
        // second-resolution names remain readable by the harness too.
        var body = name.Substring(4);
        var separator = body.IndexOf('_');
        var stamp = separator >= 0 ? body.Substring(0, separator) : body;
        if (stamp.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            stamp = stamp.Substring(0, stamp.Length - 1);
        }

        DateTime parsed;
        if (DateTime.TryParseExact(
            stamp,
            new[] { "yyyyMMdd-HHmmssfffffff", "yyyyMMdd-HHmmssfff", "yyyyMMdd-HHmmss" },
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// The screenshot path in the same units as the clip: capture the window live the way the toast
    /// pipeline does, read the frame's own number out of the result, and compare when that frame was
    /// painted against when the grab happened. A live grab cannot rewind, so landing within a frame or
    /// two of "now" is the reference the clip's alignment is judged against.
    /// </summary>
    private static void MeasureScreenshotAlignment(Assembly plugin)
    {
        Console.WriteLine();
        Console.WriteLine("=== screenshot alignment (live grab vs the clock)");
        try
        {
            var serviceType = plugin.GetType("PlayniteAchievements.Services.UI.UnlockScreenshotService");
            var service = Activator.CreateInstance(
                serviceType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new object[]
                {
                    ConsoleLogger.Create(serviceType.GetConstructors(Flags)[0].GetParameters()[0].ParameterType),
                },
                null);

            var capture = serviceType.GetMethod(
                "CaptureGameWindow", Flags, null, new[] { typeof(IntPtr), typeof(int?), typeof(int) }, null);

            var hwnd = IntPtr.Zero;
            _form.Invoke((Action)(() => hwnd = _form.Handle));

            // Grab first, index after: the window is still painting, so a frame captured now is only in
            // the paint list once it has been painted.
            var grabs = new List<Tuple<int, DateTime, string>>();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var takenUtc = DateTime.UtcNow;
                var bitmap = (Bitmap)capture.Invoke(service, new object[] { hwnd, null, 0 });
                if (bitmap == null)
                {
                    Console.WriteLine("  grab " + attempt + ": capture returned null");
                    continue;
                }

                using (bitmap)
                {
                    grabs.Add(Tuple.Create(
                        BarcodeFromBitmap(bitmap), takenUtc, bitmap.Width + "x" + bitmap.Height));
                }

                Thread.Sleep(200);
            }

            BuildPaintIndex();
            var attemptNumber = 0;
            foreach (var grab in grabs)
            {
                attemptNumber++;
                DateTime painted;
                if (grab.Item1 < 0 || !_paintUtcByCounter.TryGetValue(grab.Item1, out painted))
                {
                    Console.WriteLine("  grab " + attemptNumber + ": frame " + grab.Item1 + " not in the paint index");
                    continue;
                }

                Console.WriteLine(
                    "  grab " + attemptNumber + ": frame " + grab.Item1 + " was painted " +
                    (painted - grab.Item2).TotalSeconds.ToString("+0.000;-0.000") + "s relative to the grab (" +
                    grab.Item3 + ")");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  screenshot alignment failed: " + (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>Reads the painted frame number out of a captured bitmap, or -1 when unreadable.</summary>
    private static int BarcodeFromBitmap(Bitmap bitmap)
    {
        var stride = bitmap.Width * 4;
        var pixels = new byte[stride * bitmap.Height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, Math.Min(stride, data.Stride));
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return Barcode.Read(pixels, stride, bitmap.Height, false, bitmap.Width);
    }

    // === phase 3: export a clip from those segments ===

    /// <param name="reachBackSeconds">
    /// How far before the chosen segment to ask the window to start. Anything beyond what the buffer
    /// holds makes PlanClip begin the clip at its oldest usable segment instead, which is the case that
    /// used to place the toast card early: positions were measured from the window the caller asked for
    /// rather than from where the clip actually begins.
    /// </param>
    private static bool ExportClip(Assembly plugin, string buffer, string outputPath, double reachBackSeconds = 0)
    {
        var timeline = plugin.GetType("PlayniteAchievements.Services.Recording.SegmentTimeline");
        var parse = timeline.GetMethod("ParseSegments", Flags);
        var files = Directory.GetFiles(buffer, "seg_*.mp4")
            .OrderBy(p => p)
            // During --stress the current writer, the prepared next writer and any backlogged
            // finalizers coexist in this directory. Only a top-level moov marks a closed MP4.
            .Where(Mp4.HasMoov)
            .Select(p => new ValueTuple<string, long>(p, new FileInfo(p).Length))
            .ToList();
        if (files.Count < 3)
        {
            Console.WriteLine("not enough segments to export");
            return false;
        }

        var segments = parse.Invoke(null, new object[] { files, TimeZoneInfo.Local, "seg_", ".mp4" });
        var count = (int)segments.GetType().GetProperty("Count").GetValue(segments);
        var item = segments.GetType().GetProperty("Item");
        // The last few segments: drift, if any, is largest at the end of a session, and this keeps the
        // export short however long the recording ran.
        // Normally sample the last few segments, keeping the export short however long the recording ran.
        // For the short-buffer case, anchor to the OLDEST segment and ask for a start before it — that is
        // the only way to guarantee the window reaches past what the buffer holds.
        var first = item.GetValue(segments, new object[] { 0 });
        var firstStartUtc = (DateTime)first.GetType().GetProperty("StartUtc").GetValue(first);
        var last = item.GetValue(segments, new object[] { count - 1 });
        var endUtc = (DateTime)last.GetType().GetProperty("StartUtc").GetValue(last);
        // A fixed window ending at the last segment's start, so the clip keeps its production shape
        // (a long lead-in ahead of the card) however irregularly the segments rotated. 9.7 s rather
        // than 10 so the start falls between keyframes and the export reports a lead to trim.
        var startUtc = reachBackSeconds > 0
            ? firstStartUtc.AddSeconds(-reachBackSeconds)
            : endUtc.AddSeconds(-9.7) > firstStartUtc.AddSeconds(0.5) ? endUtc.AddSeconds(-9.7) : firstStartUtc.AddSeconds(0.5);

        Console.WriteLine();
        Console.WriteLine("=== export window " + startUtc.ToString("HH:mm:ss.fff") + " -> " + endUtc.ToString("HH:mm:ss.fff") +
            (reachBackSeconds > 0 ? "   (asking " + reachBackSeconds.ToString("0.0") + "s further back than the buffer holds)" : string.Empty));
        var plan = timeline.GetMethod("PlanClip", Flags)
            .Invoke(null, new object[] { segments, startUtc, endUtc, 5, null });
        if (plan == null)
        {
            Console.WriteLine("PlanClip returned null");
            return false;
        }

        Console.WriteLine("  plan files=" + plan.GetType().GetProperty("Segments").GetValue(plan).GetType()
            .GetProperty("Count").GetValue(plan.GetType().GetProperty("Segments").GetValue(plan)) +
            " startOffset=" + ((double)plan.GetType().GetProperty("StartOffsetSeconds").GetValue(plan)).ToString("0.000") +
            "s duration=" + ((double)plan.GetType().GetProperty("DurationSeconds").GetValue(plan)).ToString("0.000") + "s");

        var exporterType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationClipExporter");
        var exporter = Activator.CreateInstance(
            exporterType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new object[] { ConsoleLogger.Create(exporterType.GetConstructors(Flags)[0].GetParameters()[0].ParameterType) }, null);
        // Two different instants, and conflating them is the defect this case exists to catch: the window
        // the caller asked for, and where the clip actually begins once the plan clamps to real footage.
        _requestedWindowStartUtc = startUtc;
        var planSegments = plan.GetType().GetProperty("Segments").GetValue(plan);
        var planFirst = planSegments.GetType().GetProperty("Item").GetValue(planSegments, new object[] { 0 });
        _windowStartUtc = ((DateTime)planFirst.GetType().GetProperty("StartUtc").GetValue(planFirst))
            .AddSeconds((double)plan.GetType().GetProperty("StartOffsetSeconds").GetValue(plan));

        var shortfall = (_windowStartUtc - _requestedWindowStartUtc).TotalSeconds;
        Console.WriteLine(
            "  clip actually begins " + _windowStartUtc.ToString("HH:mm:ss.fff") +
            "  (" + shortfall.ToString("+0.000;-0.000") + "s vs the window asked for)");
        if (shortfall > 0.25)
        {
            Console.WriteLine(
                "  => a position measured from the window instead of the clip start would be " +
                shortfall.ToString("0.00") + "s early — the toast-placement defect");
        }

        var audioPlan = PlanSyntheticAudio(plugin, buffer, timeline, parse, startUtc, plan);
        var callArgs = new object[] { plan, audioPlan, outputPath, 0d };
        var ok = (bool)exporterType.GetMethod("Export", Flags).Invoke(exporter, callArgs);
        _videoLeadSeconds = (double)callArgs[3];
        Console.WriteLine("  Export=" + ok + " videoLead=" + _videoLeadSeconds.ToString("0.000") + "s");
        return ok && File.Exists(outputPath);
    }

    /// <summary>
    /// A synthetic loopback track for the base clip: one WAV chunk per video segment, named and
    /// timed like the audio recorder's (aud_ + the segment's UTC stamp), each running exactly to
    /// the next segment's start so the exporter's concatenation meets no gap or overlap, carrying
    /// a continuous 440 Hz tone. The harness records no real audio, and without an audio track the
    /// overlay pass's audio interleave and chime mix never run.
    /// </summary>
    private static object PlanSyntheticAudio(
        Assembly plugin, string buffer, Type timeline, MethodInfo parse, DateTime startUtc, object videoPlan)
    {
        var stamps = Directory.GetFiles(buffer, "seg_*.mp4")
            .Select(Path.GetFileName)
            .Select(name => name.Substring(4, 23))
            .Select(stamp => DateTime.ParseExact(
                stamp, "yyyyMMdd-HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal))
            .OrderBy(t => t)
            .ToList();
        if (stamps.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < stamps.Count; i++)
        {
            var path = Path.Combine(buffer, "aud_" + stamps[i].ToString("yyyyMMdd-HHmmssfffffff'Z'") + ".wav");
            if (File.Exists(path))
            {
                continue;
            }

            var chunkSeconds = i + 1 < stamps.Count ? (stamps[i + 1] - stamps[i]).TotalSeconds : 6.0;
            var firstFrame = (long)Math.Round((stamps[i] - stamps[0]).TotalSeconds * 48000);
            var frames = (int)Math.Round(chunkSeconds * 48000);
            using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48000, 16, 2)))
            {
                var bytes = new byte[frames * 4];
                for (var f = 0; f < frames; f++)
                {
                    var value = (short)(Math.Sin(2.0 * Math.PI * GameToneHz * (firstFrame + f) / 48000.0) * 0.3 * short.MaxValue);
                    bytes[f * 4] = (byte)value;
                    bytes[f * 4 + 1] = (byte)(value >> 8);
                    bytes[f * 4 + 2] = (byte)value;
                    bytes[f * 4 + 3] = (byte)(value >> 8);
                }

                writer.Write(bytes, 0, bytes.Length);
            }
        }

        var files = Directory.GetFiles(buffer, "aud_*.wav")
            .OrderBy(p => p)
            .Select(p => new ValueTuple<string, long>(p, new FileInfo(p).Length))
            .ToList();
        var chunks = parse.Invoke(null, new object[] { files, TimeZoneInfo.Local, "aud_", ".wav" });
        var planEndUtc = (DateTime)videoPlan.GetType().GetProperty("EndUtc").GetValue(videoPlan);
        var audioPlan = timeline.GetMethod("PlanClip", Flags)
            .Invoke(null, new object[] { chunks, startUtc, planEndUtc, 5, null });
        Console.WriteLine("  synthetic audio: " + files.Count + " chunk(s), plan " + (audioPlan == null ? "null" : "ok"));
        return audioPlan;
    }

    /// <summary>
    /// Compares the video's compressed sample times with the times the decoding reader hands back
    /// under each processing mode. Advanced video processing includes frame-rate conversion, which
    /// re-times decoded frames onto the type's declared (average) frame rate; on a clip with a
    /// capture stall that average is below the capture rate, and every decoded timestamp drifts
    /// from the compressed one it came from.
    /// </summary>
    private static void CompareTimestamps(string clip)
    {
        Console.WriteLine();
        Console.WriteLine("=== compressed vs decoded sample times");
        var compressed = ReadTimes(clip, null, false);
        Console.WriteLine("  compressed: " + compressed.Count + " samples, last at " + Last(compressed));
        foreach (var mode in new[] { "advanced", "basic", "none" })
        {
            try
            {
                var decoded = ReadTimes(clip, mode, mode != "none");
                var pairs = Math.Min(compressed.Count, decoded.Count);
                var worst = 0.0;
                var worstAt = -1;
                for (var i = 0; i < pairs; i++)
                {
                    var delta = Math.Abs(decoded[i] - compressed[i]);
                    if (delta > worst)
                    {
                        worst = delta;
                        worstAt = i;
                    }
                }

                Console.WriteLine("  " + mode.PadRight(9) + decoded.Count + " samples, last at " + Last(decoded) +
                    ", worst |decoded - compressed| = " + (worst * 1000).ToString("0.0") + "ms at index " + worstAt +
                    (worst > 0.002 ? "   <-- decoded times are not the compressed times" : string.Empty));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  " + mode.PadRight(9) + "failed: " + ex.Message);
            }
        }
    }

    private static string Last(List<double> times)
    {
        return times.Count == 0 ? "-" : times[times.Count - 1].ToString("0.000") + "s";
    }

    /// <param name="processing">null: native compressed samples; "advanced"/"basic": RGB32 via that reader flag; "none": the decoder's own output type.</param>
    private static List<double> ReadTimes(string clip, string processing, bool rgb)
    {
        var times = new List<double>();
        MediaManager.Startup();
        try
        {
            MediaAttributes attributes = null;
            if (processing == "advanced" || processing == "basic")
            {
                attributes = new MediaAttributes(1);
                if (processing == "advanced")
                {
                    attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
                }
                else
                {
                    attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1);
                }
            }

            using (attributes)
            using (var reader = attributes == null ? new SourceReader(clip) : new SourceReader(clip, attributes))
            {
                reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                if (processing != null)
                {
                    using (var request = new MediaType())
                    {
                        request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        request.Set(MediaTypeAttributeKeys.Subtype, rgb ? VideoFormatGuids.Rgb32 : VideoFormatGuids.NV12);
                        reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                    }
                }

                while (true)
                {
                    var sample = reader.ReadSample(
                        (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlags.None,
                        out _, out var flags, out _);
                    if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                    {
                        sample?.Dispose();
                        break;
                    }

                    using (sample)
                    {
                        times.Add(sample.SampleTime / 10_000_000.0);
                    }
                }
            }
        }
        finally
        {
            try { MediaManager.Shutdown(); } catch { }
        }

        return times;
    }

    // === phase 4: read each output frame's identity back ===

    private static List<Tuple<double, int>> DecodeAndReport(string clip, double shiftSeconds, string label)
    {
        var identities = new List<Tuple<double, int>>();
        DecodeNv12(clip, (time, frame, stride, w, h) =>
            identities.Add(Tuple.Create(time, Barcode.ReadNv12(frame, stride, w))));

        Analyse(identities);
        ReportAlignment(identities, shiftSeconds, label);
        return identities;
    }

    /// <summary>
    /// Decodes a clip's video to the decoder's own NV12 and hands each frame to
    /// <paramref name="onFrame"/> as (seconds, planes, luma stride, width, height): the chroma plane
    /// follows the luma plane at the same stride. No video-processing attribute is set on the
    /// reader, on purpose: advanced processing re-times frames onto the average frame rate (see
    /// <see cref="CompareTimestamps"/>), and the checks here are about where frames really sit.
    /// </summary>
    private static void DecodeNv12(string clip, Action<double, byte[], int, int, int> onFrame)
    {
        MediaManager.Startup();
        try
        {
            using (var reader = new SourceReader(clip))
            {
                reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                using (var request = new MediaType())
                {
                    request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                    reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                }

                int w, h, typeStride;
                using (var decoded = reader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream))
                {
                    var size = decoded.Get(MediaTypeAttributeKeys.FrameSize);
                    w = (int)(size >> 32);
                    h = (int)(size & 0xffffffff);
                    try { typeStride = Math.Abs(decoded.Get(MediaTypeAttributeKeys.DefaultStride)); }
                    catch { typeStride = w; }
                }

                byte[] frame = null;
                while (true)
                {
                    var sample = reader.ReadSample(
                        (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlags.None,
                        out _, out var flags, out _);
                    if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
                    {
                        sample?.Dispose();
                        break;
                    }

                    using (sample)
                    using (var buffer = sample.ConvertToContiguousBuffer())
                    {
                        var ptr = buffer.Lock(out _, out var length);
                        try
                        {
                            // A contiguous NV12 buffer packs its rows; derive the stride from the
                            // length when it divides evenly, else trust the type.
                            var stride = length % (h * 3 / 2) == 0 ? length / (h * 3 / 2) : typeStride;
                            if (frame == null || frame.Length < length)
                            {
                                frame = new byte[length];
                            }

                            Marshal.Copy(ptr, frame, 0, length);
                            onFrame(sample.SampleTime / 10_000_000.0, frame, stride, w, h);
                        }
                        finally
                        {
                            buffer.Unlock();
                        }
                    }
                }
            }
        }
        finally
        {
            try { MediaManager.Shutdown(); } catch { }
        }
    }

    private static void Analyse(List<Tuple<double, int>> identities)
    {
        Console.WriteLine("  output frames: " + identities.Count);
        var unreadable = identities.Count(i => i.Item2 < 0);
        var duplicates = 0;
        var regressions = 0;
        var biggestJump = 0;

        for (var i = 1; i < identities.Count; i++)
        {
            var previous = identities[i - 1].Item2;
            var current = identities[i].Item2;
            if (previous < 0 || current < 0)
            {
                continue;
            }

            if (current == previous)
            {
                duplicates++;
                Console.WriteLine(
                    "  repeat at " + identities[i].Item1.ToString("0.000") + "s (source frame " + current + ")");
            }
            else if (current < previous)
            {
                regressions++;
                Console.WriteLine(
                    "  REGRESSION at " + identities[i].Item1.ToString("0.000") + "s: frame " +
                    previous + " -> " + current + " (went back " + (previous - current) + ")");
            }
            else if (current - previous > biggestJump)
            {
                biggestJump = current - previous;
            }
        }

        Console.WriteLine("  unreadable barcodes: " + unreadable);
        Console.WriteLine("  repeated identities:  " + duplicates + "   (a static or stalled source repeats legitimately)");
        Console.WriteLine("  ORDER REGRESSIONS:    " + regressions + "   <-- any of these is a wrong-frame defect");
        Console.WriteLine("  largest forward jump: " + biggestJump + " source frames");
        Console.WriteLine();
        Console.WriteLine("  first 40 (outputTime -> sourceFrame):");
        foreach (var entry in identities.Take(40))
        {
            Console.Write("   " + entry.Item1.ToString("0.000") + "->" + entry.Item2);
        }

        Console.WriteLine();
    }

    // === the self-identifying window ===

    private sealed class MarkerForm : Form
    {
        private readonly List<Tuple<int, double>> _paints;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        private readonly Font _font = new Font(FontFamily.GenericMonospace, 48, FontStyle.Bold);
        private int _counter;

        public MarkerForm(List<Tuple<int, double>> paints, int width = ClientW, int height = ClientH)
        {
            _paints = paints;
            Text = "PA capture harness";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(width, height);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 20, 28);

            // Repaint faster than the capture rate so every captured frame should differ — except during
            // the deliberate freeze below, which is what a game window that stops presenting looks like
            // to the capture: WGC hands back nothing new and the pump repeats the frame it holds.
            var timer = new System.Windows.Forms.Timer { Interval = 8 };
            timer.Tick += (s, e) =>
            {
                var elapsed = _clock.Elapsed.TotalSeconds;
                if (FreezeAtSeconds > 0 &&
                    elapsed >= FreezeAtSeconds && elapsed < FreezeAtSeconds + FreezeForSeconds)
                {
                    return;
                }

                Invalidate();
            };
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var n = ++_counter;
            _paints.Add(Tuple.Create(n, _clock.Elapsed.TotalMilliseconds));
            var g = e.Graphics;
            g.Clear(BackColor);

            // Barcode: sync cell, black reference, then 16 bits of the counter, LSB first.
            g.FillRectangle(Brushes.White, SyncX, BarcodeY, CellSize, CellSize);
            g.FillRectangle(Brushes.Black, SyncX + CellSize, BarcodeY, CellSize, CellSize);
            for (var bit = 0; bit < BitCount; bit++)
            {
                var set = ((n >> bit) & 1) == 1;
                g.FillRectangle(
                    set ? Brushes.White : Brushes.Black,
                    BitsX + bit * CellSize, BarcodeY, CellSize, CellSize);
            }

            // Human-readable counter, plus a sweeping bar so motion is obvious to the eye.
            g.DrawString(n.ToString("00000"), _font, Brushes.Gold, 20, CellSize + 20);
            var x = (n * 13) % Math.Max(1, ClientW - 80);
            g.FillRectangle(Brushes.OrangeRed, x, ClientH - 120, 80, 80);
            g.DrawString(
                "elapsed " + (_clock.Elapsed.TotalSeconds).ToString("0.00") + "s",
                SystemFonts.DefaultFont, Brushes.White, 20, ClientH - 30);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _font.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // Playnite's ILogger, forwarded to the console so plugin diagnostics are visible. Built with
    // Reflection.Emit so the harness needs no compile-time reference to the SDK.
    private static class ConsoleLogger
    {
        public static object Create(Type loggerInterface)
        {
            var assemblyName = new System.Reflection.AssemblyName("HarnessLogger");
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                assemblyName, System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType(
                "HarnessLoggerImpl", TypeAttributes.Public | TypeAttributes.Class, typeof(object),
                new[] { loggerInterface });

            var writeLine = typeof(Console).GetMethod("WriteLine", new[] { typeof(string) });
            var concat = typeof(string).GetMethod("Concat", new[] { typeof(object[]) });

            foreach (var method in loggerInterface.GetMethods())
            {
                var parameters = method.GetParameters();
                var impl = type.DefineMethod(
                    method.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType,
                    parameters.Select(p => p.ParameterType).ToArray());
                var il = impl.GetILGenerator();

                // Console.WriteLine(string.Concat(new object[] { "[plugin] ", name, " ", arg0, " ", arg1... }))
                var slots = 1 + parameters.Length * 2;
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, slots);
                il.Emit(System.Reflection.Emit.OpCodes.Newarr, typeof(object));
                il.Emit(System.Reflection.Emit.OpCodes.Dup);
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "[plugin " + method.Name + "] ");
                il.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);

                for (var i = 0; i < parameters.Length; i++)
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Dup);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 1 + i * 2);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg, i + 1);
                    if (parameters[i].ParameterType.IsValueType)
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Box, parameters[i].ParameterType);
                    }

                    il.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);

                    il.Emit(System.Reflection.Emit.OpCodes.Dup);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 2 + i * 2);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldstr, " | ");
                    il.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);
                }

                il.Emit(System.Reflection.Emit.OpCodes.Call, concat);
                il.Emit(System.Reflection.Emit.OpCodes.Call, writeLine);

                if (method.ReturnType != typeof(void))
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Ldnull);
                }

                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                type.DefineMethodOverride(impl, method);
            }

            return Activator.CreateInstance(type.CreateType());
        }
    }

    private static class Barcode
    {
        // Returns the encoded counter, or -1 when the sync/reference cells do not look right.
        // The window is painted in logical units but recorded in physical pixels, so the barcode's
        // geometry is scaled by whatever the display's DPI factor was; derive it from the frame width
        // rather than assuming 1:1.
        public static int Read(byte[] frame, int stride, int height, bool bottomUp, int frameWidth)
        {
            var scale = frameWidth / (double)ClientW;
            var cell = CellSize * scale;
            var y = (int)((BarcodeY + CellSize / 2.0) * scale);

            var sync = Luma(frame, stride, height, bottomUp, (int)(cell * 0.5), y);
            var dark = Luma(frame, stride, height, bottomUp, (int)(cell * 1.5), y);
            if (sync < 140 || dark > 110 || sync - dark < 60)
            {
                return -1;
            }

            var mid = (sync + dark) / 2;
            var value = 0;
            for (var bit = 0; bit < BitCount; bit++)
            {
                var luma = Luma(
                    frame, stride, height, bottomUp, (int)(cell * (2.5 + bit)), y);
                if (luma > mid)
                {
                    value |= 1 << bit;
                }
            }

            return value;
        }

        /// <summary>
        /// The counter from an NV12 frame's luma plane: the same cells as <see cref="Read"/>, sampled
        /// as Y' directly. Limited-range white (235) and black (16) clear the same thresholds.
        /// </summary>
        public static int ReadNv12(byte[] frame, int stride, int frameWidth)
        {
            var scale = frameWidth / (double)ClientW;
            var cell = CellSize * scale;
            var y = (int)((BarcodeY + CellSize / 2.0) * scale);

            var sync = LumaNv12(frame, stride, (int)(cell * 0.5), y);
            var dark = LumaNv12(frame, stride, (int)(cell * 1.5), y);
            if (sync < 140 || dark > 110 || sync - dark < 60)
            {
                return -1;
            }

            var mid = (sync + dark) / 2;
            var value = 0;
            for (var bit = 0; bit < BitCount; bit++)
            {
                if (LumaNv12(frame, stride, (int)(cell * (2.5 + bit)), y) > mid)
                {
                    value |= 1 << bit;
                }
            }

            return value;
        }

        private static int LumaNv12(byte[] frame, int stride, int x, int y)
        {
            var offset = (y * stride) + x;
            return offset < 0 || offset >= frame.Length ? 0 : frame[offset];
        }

        private static int Luma(byte[] frame, int stride, int height, bool bottomUp, int x, int y)
        {
            var row = bottomUp ? height - 1 - y : y;
            var offset = row * stride + x * 4;
            if (offset < 0 || offset + 2 >= frame.Length)
            {
                return 0;
            }

            return (int)(0.114 * frame[offset] + 0.587 * frame[offset + 1] + 0.299 * frame[offset + 2]);
        }
    }

    // Minimal MP4 reader for the boxes this harness reports on.
    private static class Mp4
    {
        public struct Timing
        {
            public long Samples;
            public int SttsEntries;
            public double Seconds;
        }

        public static Timing VideoTiming(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var moov = Find(bytes, 0, bytes.Length, "moov");
            foreach (var trak in All(bytes, moov.Item1, moov.Item2, "trak"))
            {
                var mdia = Find(bytes, trak.Item1, trak.Item2, "mdia");
                var hdlr = Find(bytes, mdia.Item1, mdia.Item2, "hdlr");
                if (Type(bytes, hdlr.Item1 + 8) != "vide")
                {
                    continue;
                }

                var mdhd = Find(bytes, mdia.Item1, mdia.Item2, "mdhd");
                var timescale = U32(bytes, mdhd.Item1 + 12);
                var minf = Find(bytes, mdia.Item1, mdia.Item2, "minf");
                var stbl = Find(bytes, minf.Item1, minf.Item2, "stbl");
                var stts = Find(bytes, stbl.Item1, stbl.Item2, "stts");
                var entries = (int)U32(bytes, stts.Item1 + 4);
                long samples = 0, total = 0;
                for (var i = 0; i < entries; i++)
                {
                    var c = U32(bytes, stts.Item1 + 8 + i * 8);
                    var d = U32(bytes, stts.Item1 + 12 + i * 8);
                    samples += c;
                    total += c * d;
                }

                return new Timing { Samples = samples, SttsEntries = entries, Seconds = total / (double)timescale };
            }

            return new Timing();
        }

        // A finalized MP4 has a complete top-level moov box. Scan only box headers so an active
        // multi-megabyte mdat is never copied into the harness heap just to reject the file.
        public static bool HasMoov(string path)
        {
            try
            {
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    while (stream.Position + 8 <= stream.Length)
                    {
                        var boxStart = stream.Position;
                        var size32 = ReadU32(reader);
                        var type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                        long size = size32;
                        var header = 8L;
                        if (size == 1)
                        {
                            if (stream.Position + 8 > stream.Length)
                            {
                                return false;
                            }

                            size = ReadU64(reader);
                            header = 16;
                        }
                        else if (size == 0)
                        {
                            size = stream.Length - boxStart;
                        }

                        if (size < header || boxStart + size > stream.Length)
                        {
                            return false;
                        }

                        if (type == "moov")
                        {
                            return true;
                        }

                        stream.Position = boxStart + size;
                    }
                }
            }
            catch
            {
                // A writer may be extending the file while this snapshot is taken. It simply is not
                // eligible for this export; the next stress interval will see it after finalization.
            }

            return false;
        }

        private static uint ReadU32(BinaryReader reader)
        {
            var b = reader.ReadBytes(4);
            if (b.Length != 4)
            {
                throw new EndOfStreamException();
            }

            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static long ReadU64(BinaryReader reader)
        {
            var b = reader.ReadBytes(8);
            if (b.Length != 8)
            {
                throw new EndOfStreamException();
            }

            ulong value = 0;
            for (var i = 0; i < b.Length; i++)
            {
                value = (value << 8) | b[i];
            }

            if (value > long.MaxValue)
            {
                throw new InvalidDataException("MP4 box length exceeds Int64");
            }

            return (long)value;
        }

        // The track's H.264 parameter sets: moov > trak > mdia > minf > stbl > stsd > avc1 > avcC.
        public static byte[] AvcC(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var moov = Find(bytes, 0, bytes.Length, "moov");
            foreach (var trak in All(bytes, moov.Item1, moov.Item2, "trak"))
            {
                var mdia = Find(bytes, trak.Item1, trak.Item2, "mdia");
                var hdlr = Find(bytes, mdia.Item1, mdia.Item2, "hdlr");
                if (Type(bytes, hdlr.Item1 + 8) != "vide")
                {
                    continue;
                }

                var minf = Find(bytes, mdia.Item1, mdia.Item2, "minf");
                var stbl = Find(bytes, minf.Item1, minf.Item2, "stbl");
                var stsd = Find(bytes, stbl.Item1, stbl.Item2, "stsd");
                // stsd: 4 version/flags + 4 entry count, then the sample entry; avc1's own header is
                // 78 bytes before its child boxes.
                var avc1 = Find(bytes, stsd.Item1 + 8, stsd.Item2, "avc1");
                var avcC = Find(bytes, avc1.Item1 + 78, avc1.Item2, "avcC");
                var length = (int)(avcC.Item2 - avcC.Item1);
                var result = new byte[length];
                Array.Copy(bytes, (int)avcC.Item1, result, 0, length);
                return result;
            }

            throw new InvalidDataException("no video avcC");
        }

        /// <summary>The audio track's media duration in seconds, 0 when the file has none.</summary>
        public static double AudioSeconds(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var moov = Find(bytes, 0, bytes.Length, "moov");
            foreach (var trak in All(bytes, moov.Item1, moov.Item2, "trak"))
            {
                var mdia = Find(bytes, trak.Item1, trak.Item2, "mdia");
                var hdlr = Find(bytes, mdia.Item1, mdia.Item2, "hdlr");
                if (Type(bytes, hdlr.Item1 + 8) != "soun")
                {
                    continue;
                }

                var mdhd = Find(bytes, mdia.Item1, mdia.Item2, "mdhd");
                return U32(bytes, mdhd.Item1 + 16) / (double)U32(bytes, mdhd.Item1 + 12);
            }

            return 0;
        }

        public static IEnumerable<string> Describe(string path)
        {
            var timing = VideoTiming(path);
            yield return "video: frames=" + timing.Samples + " sttsEntries=" + timing.SttsEntries +
                " media=" + timing.Seconds.ToString("0.000") + "s";
            yield return "audio: media=" + AudioSeconds(path).ToString("0.000") + "s";
        }

        private static long U32(byte[] b, long offset)
        {
            var o = (int)offset;
            return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
        }

        private static string Type(byte[] b, long o)
        {
            return System.Text.Encoding.ASCII.GetString(b, (int)o, 4);
        }

        private static Tuple<long, long> Find(byte[] b, long start, long end, string type)
        {
            var o = start;
            while (o + 8 <= end)
            {
                var size = U32(b, (int)o);
                var t = Type(b, o + 4);
                long header = 8;
                if (size == 0) { size = end - o; }
                if (size == 1)
                {
                    size = 0;
                    for (var i = 0; i < 8; i++) { size = (size << 8) | b[o + 8 + i]; }
                    header = 16;
                }

                if (t == type) { return Tuple.Create(o + header, o + size); }
                o += size;
            }

            throw new InvalidDataException("box " + type + " not found");
        }

        private static IEnumerable<Tuple<long, long>> All(byte[] b, long start, long end, string type)
        {
            var o = start;
            while (o + 8 <= end)
            {
                var size = U32(b, (int)o);
                if (size <= 0) { break; }
                if (Type(b, o + 4) == type) { yield return Tuple.Create(o + 8, o + size); }
                o += size;
            }
        }
    }
}



