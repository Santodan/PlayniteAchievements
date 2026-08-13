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

    [STAThread]
    private static void Main(string[] args)
    {
        var seconds = args.Length > 0 ? int.Parse(args[0]) : 20;
        var fps = args.Length > 1 ? int.Parse(args[1]) : 30;
        FreezeAtSeconds = args.Length > 3 ? double.Parse(args[3]) : 0;
        FreezeForSeconds = args.Length > 4 ? double.Parse(args[4]) : 0;
        var scratch = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _pluginDir = args.Length > 2
            ? args[2]
            : Path.GetFullPath(Path.Combine(scratch, @"..\..\..\source\bin\Debug"));

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        var plugin = Assembly.LoadFrom(Path.Combine(_pluginDir, "PlayniteAchievements.dll"));

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
            new[] { "counter,elapsedMs" }.Concat(paints.Select(p => p.Item1 + "," + p.Item2.ToString("0.000"))));
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

    private static void MeasureComposition(Assembly plugin, string baseClip, string outputPath, int fps)
    {
        Console.WriteLine();
        Console.WriteLine("=== composition (overlay re-encode) with the window still painting");
        try
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
            var startedAt = _paints[_paints.Count - 1].Item2;
            var timer = Stopwatch.StartNew();

            var ok = (bool)reencoderType.GetMethod("Export", Flags).Invoke(reencoder, new object[]
            {
                baseClip, track,
                2.0,                    // toastStartSeconds
                4.0,                    // toastMaxSeconds
                0.5,                    // trimLeadSeconds (the keyframe lead the export reported)
                clipSeconds,            // endSeconds
                null, 0d,               // no chime
                outputPath, fps, quality,
            });

            timer.Stop();
            var endedAt = _paints[_paints.Count - 1].Item2;
            Console.WriteLine("  Export=" + ok + " in " + timer.ElapsedMilliseconds + "ms for a " +
                clipSeconds.ToString("0.0") + "s clip");
            Report("while compositing", startedAt, endedAt);
            Report("while recording", 0, 22000);
            if (ok)
            {
                foreach (var line in Mp4.Describe(outputPath))
                {
                    Console.WriteLine("  composited " + line);
                }

                // The reported defect is wrong frames in the *composited* output, so read its identities
                // back too rather than trusting that the base clip being right means this one is.
                Console.WriteLine();
                Console.WriteLine("=== decoded frame identities of the COMPOSITED clip");
                DecodeAndReport(outputPath, 0.0, "composited");

            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  composition failed: " + (ex.InnerException ?? ex));
        }
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
            sampleType.GetField("RelX").SetValue(sample, 60);
            sampleType.GetField("RelY").SetValue(sample, 820);
            sampleType.GetField("ClientW").SetValue(sample, 1920);
            sampleType.GetField("ClientH").SetValue(sample, 1080);
            add.Invoke(samples, new[] { sample });
        }

        trackType.GetProperty("DurationSeconds").SetValue(track, 4.0);
        trackType.GetProperty("OffsetX").SetValue(track, 0);
        trackType.GetProperty("OffsetY").SetValue(track, 0);
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
        // seg_yyyyMMdd-HHmmssfff_WxH.mp4
        var body = name.Substring(4);
        var stamp = body.Substring(0, Math.Min(18, body.Length));
        DateTime parsed;
        if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmssfff", null,
            System.Globalization.DateTimeStyles.None, out parsed))
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
        var first = item.GetValue(
            segments, new object[] { reachBackSeconds > 0 ? 0 : Math.Max(0, count - 3) });
        var startUtc = reachBackSeconds > 0
            ? ((DateTime)first.GetType().GetProperty("StartUtc").GetValue(first)).AddSeconds(-reachBackSeconds)
            : ((DateTime)first.GetType().GetProperty("StartUtc").GetValue(first)).AddSeconds(0.5);
        var last = item.GetValue(segments, new object[] { count - 1 });
        var endUtc = (DateTime)last.GetType().GetProperty("StartUtc").GetValue(last);

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

        var callArgs = new object[] { plan, null, outputPath, 0d };
        var ok = (bool)exporterType.GetMethod("Export", Flags).Invoke(exporter, callArgs);
        _videoLeadSeconds = (double)callArgs[3];
        Console.WriteLine("  Export=" + ok + " videoLead=" + _videoLeadSeconds.ToString("0.000") + "s");
        return ok && File.Exists(outputPath);
    }

    // === phase 4: read each output frame's identity back ===

    private static void DecodeAndReport(string clip, double shiftSeconds, string label)
    {
        var identities = new List<Tuple<double, int>>();
        MediaManager.Startup();
        try
        {
            using (var attributes = new MediaAttributes(1))
            {
                attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
                using (var reader = new SourceReader(clip, attributes))
                {
                    reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                    using (var request = new MediaType())
                    {
                        request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                        reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                    }

                    int w, h, stride;
                    using (var decoded = reader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream))
                    {
                        var size = decoded.Get(MediaTypeAttributeKeys.FrameSize);
                        w = (int)(size >> 32);
                        h = (int)(size & 0xffffffff);
                        try { stride = decoded.Get(MediaTypeAttributeKeys.DefaultStride); }
                        catch { stride = w * 4; }
                    }

                    var absStride = Math.Abs(stride);
                    var bottomUp = stride < 0;
                    var frame = new byte[absStride * h];
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
                            using (var buffer = sample.ConvertToContiguousBuffer())
                            {
                                var ptr = buffer.Lock(out _, out var length);
                                try { Marshal.Copy(ptr, frame, 0, Math.Min(length, frame.Length)); }
                                finally { buffer.Unlock(); }
                            }

                            identities.Add(Tuple.Create(
                                sample.SampleTime / 10_000_000.0,
                                Barcode.Read(frame, absStride, h, bottomUp, w)));
                        }
                    }
                }
            }
        }
        finally
        {
            try { MediaManager.Shutdown(); } catch { }
        }

        Analyse(identities);
        ReportAlignment(identities, shiftSeconds, label);
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

        public MarkerForm(List<Tuple<int, double>> paints)
        {
            _paints = paints;
            Text = "PA capture harness";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(ClientW, ClientH);
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
                var avcC = Find(bytes, avc1.Item1 + 78 - 8, avc1.Item2, "avcC");
                var length = (int)(avcC.Item2 - avcC.Item1);
                var result = new byte[length];
                Array.Copy(bytes, (int)avcC.Item1, result, 0, length);
                return result;
            }

            throw new InvalidDataException("no video avcC");
        }

        public static IEnumerable<string> Describe(string path)
        {
            var timing = VideoTiming(path);
            yield return "video: frames=" + timing.Samples + " sttsEntries=" + timing.SttsEntries +
                " media=" + timing.Seconds.ToString("0.000") + "s";
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



