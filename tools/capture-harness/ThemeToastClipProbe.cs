// Runs a theme's AchievementToast.xaml through the plugin's real clip path and reports how faithfully
// the composited clip reproduces the live card's motion.
//
//   ThemeToastClipProbe.exe <toast.xaml> [--theme <themeDir>] [--base <clip.mp4>] [--fps 60]
//                           [--out <dir>] [--label <name>] [--plugin <pluginDir>]
//
// Everything that decides what lands in the clip is the plugin's own code, driven by reflection on an
// uninitialized ToastNotificationService: ToastSurfaceFactory builds the surface and slide host,
// CaptureWaveShadowLayers / PrimeWaveCardPixels / SampleWaveTracks / RunSlideStoryboard /
// StopActiveSlide run as shipped, ToastOverlayTrackRecorder accumulates the track, and
// MediaFoundationOverlayReencoder composites it into a base clip. The probe supplies only the wave's
// timeline (warm frames, capture delay, hold, slide-out), mirrored from the wave loop, and the
// sampling cadence, mirrored from its onTrackSample handler.
//
// Alongside each sample it records where the card's root element truly is on screen (host slide plus
// any transform inside the template) and afterwards reads where the track says it is (slide offset
// plus the card's opaque left edge in the stored frame). The difference is what the clip gets wrong.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

public static class ThemeToastClipProbe
{
    private const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static string _repo;
    private static string _pluginDir;
    private static string _toastXaml;
    private static string _themeDir;
    private static string _baseClip;
    private static string _outDir;
    private static string _label;
    private static int _fps = 60;
    private static int _exitCode;

    private sealed class Truth
    {
        public double ElapsedMs;
        public double TrueXPhys;
        public double TrueOpacity;
        public bool SlideRunning;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _repo = Path.GetFullPath(Path.Combine(here, "..", "..", ".."));
        _pluginDir = Path.Combine(_repo, "source", "bin", "Debug");
        _themeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Playnite", "Themes", "Fullscreen", "PS5-Experience_saVantCZ");
        _baseClip = Path.Combine(here, "harness_clip.mp4");
        _outDir = Path.Combine(here, "theme_toast");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--theme": _themeDir = args[++i]; break;
                case "--base": _baseClip = args[++i]; break;
                case "--fps": _fps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": _outDir = args[++i]; break;
                case "--label": _label = args[++i]; break;
                case "--plugin": _pluginDir = args[++i]; break;
                default: _toastXaml = args[i]; break;
            }
        }

        if (_toastXaml == null || !File.Exists(_toastXaml))
        {
            Console.WriteLine("usage: ThemeToastClipProbe.exe <toast.xaml> [--theme dir] [--base clip.mp4] [--fps 60] [--out dir] [--label name]");
            return 2;
        }

        _label = _label ?? Path.GetFileNameWithoutExtension(_toastXaml).Replace('.', '_');
        Directory.CreateDirectory(_outDir);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginAndPackages;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) =>
        {
            try
            {
                await RunAsync(app);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + (ex.InnerException ?? ex));
                _exitCode = 1;
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
        return _exitCode;
    }

    private static Assembly ResolveFromPluginAndPackages(object sender, ResolveEventArgs e)
    {
        var name = new AssemblyName(e.Name).Name + ".dll";
        var direct = Path.Combine(_pluginDir, name);
        if (File.Exists(direct))
        {
            return Assembly.LoadFrom(direct);
        }

        var packages = Path.Combine(_repo, "source", "packages");
        if (Directory.Exists(packages))
        {
            var hit = Directory.GetFiles(packages, name, SearchOption.AllDirectories)
                .FirstOrDefault(p => p.IndexOf("\\net4", StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit != null)
            {
                return Assembly.LoadFrom(hit);
            }
        }

        return null;
    }

    private static async Task RunAsync(Application app)
    {
        var plugin = Assembly.LoadFrom(Path.Combine(_pluginDir, "PlayniteAchievements.dll"));
        var loggerType = plugin.GetReferencedAssemblies()
            .Select(Assembly.Load).First(a => a.GetName().Name == "Playnite.SDK")
            .GetType("Playnite.SDK.ILogger");
        var logger = ConsoleLogger.Create(loggerType);

        InstallThemeResources(app);
        var dictionary = LoadToastDictionary(_toastXaml);
        var template = (DataTemplate)dictionary["PlayAch.Template.AchievementToast"];
        var slideInAuthored = dictionary.Contains("PlayAch.Storyboard.ToastSlideIn")
            ? (Storyboard)dictionary["PlayAch.Storyboard.ToastSlideIn"] : null;
        var slideOutAuthored = dictionary.Contains("PlayAch.Storyboard.ToastSlideOut")
            ? (Storyboard)dictionary["PlayAch.Storyboard.ToastSlideOut"] : null;
        var durationSeconds = dictionary.Contains("PlayAch.Toast.DurationSeconds")
            ? double.Parse((string)dictionary["PlayAch.Toast.DurationSeconds"], CultureInfo.InvariantCulture)
            : 4d;

        var svcType = plugin.GetType("PlayniteAchievements.Services.UI.ToastNotificationService");
        var vmType = plugin.GetType("PlayniteAchievements.ViewModels.AchievementToastViewModel");
        var factory = plugin.GetType("PlayniteAchievements.Services.UI.ToastSurfaceFactory");
        var recorderType = plugin.GetType("PlayniteAchievements.Services.UI.ToastOverlayTrackRecorder");

        var vm = CreateViewModel(plugin, vmType);
        var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(vmType));
        items.Add(vm);

        // The "game": a plain window whose client rect the anchor reads, sized to the base clip in
        // physical pixels so the export maps positions 1:1.
        var game = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = 0,
            Top = 0,
            Width = 1280,
            Height = 720,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1c)),
            Title = "ThemeToastClipProbe game",
        };
        game.Show();
        var dpi = PresentationSource.FromVisual(game).CompositionTarget.TransformToDevice.M11;
        game.Width = 1280 / dpi;
        game.Height = 720 / dpi;
        game.UpdateLayout();
        var gameHwnd = new WindowInteropHelper(game).Handle;

        var surface = (ItemsControl)factory.GetMethod("BuildToastSurface", Flags).Invoke(null, new object[] { items, template });
        var hostArgs = new object[] { surface, null };
        var host = (Grid)factory.GetMethod("BuildSlideHost", Flags).Invoke(null, hostArgs);
        var slide = (TranslateTransform)hostArgs[1];

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            Left = 1280 / dpi - 1100,
            Top = 20,
            Content = host,
            Title = "ThemeToastClipProbe toast",
        };

        var svc = FormatterServices.GetUninitializedObject(svcType);
        Set(svc, "_logger", logger);
        Set(svc, "_activeCardSurface", surface);
        Set(svc, "_activeSlideHost", host);
        Set(svc, "_activeSlideTransform", slide);
        Set(svc, "_activeIsGame", true);
        Set(svc, "_activeReferenceHwnd", gameHwnd);
        Set(svc, "_activeMonitorScale", dpi);
        Set(svc, "_screenshotService", FormatterServices.GetUninitializedObject(
            plugin.GetType("PlayniteAchievements.Services.UI.UnlockScreenshotService")));
        var scratchField = svcType.GetField("_trackRenderScratch", Flags);
        scratchField.SetValue(svc, Activator.CreateInstance(scratchField.FieldType));

        window.Show();
        factory.GetMethod("ApplyMeasuredCardGaps", Flags)?.Invoke(null, new object[] { surface });
        window.UpdateLayout();

        // Warm frames, then the recording setup, each followed by a composed frame: the wave loop's
        // order. The card's Loaded has already fired, so a Loaded-triggered template animation runs
        // from here on, exactly as it does in the plugin.
        var framePeriodMs = await MeasureFramePeriodAsync(2);
        Console.WriteLine("monitor frame period " + framePeriodMs.ToString("0.00") + " ms, dpi scale " + dpi.ToString("0.##"));

        var recorder = Activator.CreateInstance(
            recorderType, Flags, null,
            new object[] { logger, 1000d / _fps, true, false, 24d, 24d, dpi }, null);
        Invoke(svc, "CaptureWaveShadowLayers", recorder, window, items);
        await MeasureFramePeriodAsync(1);
        Invoke(svc, "PrimeWaveCardPixels", recorder, window, items);
        await MeasureFramePeriodAsync(1);

        var container = (FrameworkElement)surface.ItemContainerGenerator.ContainerFromIndex(0);
        var root = VisualTreeHelper.GetChildrenCount(container) > 0
            ? VisualTreeHelper.GetChild(container, 0) as FrameworkElement
            : container;
        var windowPhysPerDip = PhysPerDip(window);
        var restLeftDip = container.TransformToAncestor(window).TransformBounds(new Rect(container.RenderSize)).Left
            - slide.X;

        var inMs = 0d;
        var inTravels = true;
        var outMs = 0d;
        var outTravels = true;
        var slideIn = ResolveSlide(svcType, slideInAuthored, 240, out inMs, out inTravels);
        var slideOut = ResolveSlide(svcType, slideOutAuthored, 200, out outMs, out outTravels);
        Console.WriteLine("slide in " + inMs + " ms travels=" + inTravels + ", slide out " + outMs + " ms travels=" + outTravels +
            ", display " + durationSeconds + " s");

        // Sampling: the wave loop's onTrackSample, one sample per recording frame on the composed tick
        // nearest each due instant.
        var truths = new List<Truth>();
        var sampleInterval = 1000d / _fps;
        var dueTolerance = framePeriodMs / 2d;
        var nextDue = 0d;
        var sampleCount = 0;
        var firstMs = double.NaN;
        var lastMs = double.NegativeInfinity;
        var sampleMethod = svcType.GetMethod("SampleWaveTracks", Flags);
        var runningField = svcType.GetField("_runningSlideStoryboard", Flags);
        EventHandler onSample = (s, e) =>
        {
            var now = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
            if (now <= lastMs)
            {
                return;
            }

            lastMs = now;
            if (double.IsNaN(firstMs))
            {
                firstMs = now;
            }

            var elapsed = now - firstMs;
            if (elapsed < nextDue - dueTolerance)
            {
                return;
            }

            do
            {
                nextDue += sampleInterval;
            }
            while (nextDue <= elapsed);

            sampleCount++;
            try
            {
                sampleMethod.Invoke(svc, new object[] { recorder, window, items, elapsed, sampleCount });
            }
            catch (Exception ex)
            {
                Console.WriteLine("sample failed: " + (ex.InnerException ?? ex).Message);
            }

            var bounds = root.TransformToAncestor(window).TransformBounds(new Rect(root.RenderSize));
            var containerOffset = container.TransformToAncestor(window).TransformBounds(new Rect(container.RenderSize)).Left
                - slide.X - restLeftDip;
            truths.Add(new Truth
            {
                ElapsedMs = elapsed,
                // Relative to the container's resting left edge, in physical pixels.
                TrueXPhys = (bounds.Left - restLeftDip - containerOffset) * windowPhysPerDip,
                TrueOpacity = host.Opacity * root.Opacity,
                SlideRunning = runningField.GetValue(svc) != null,
            });
        };

        var slideInEase = svcType.GetField("DefaultSlideInEase", Flags).GetValue(null);
        var slideOutEase = svcType.GetField("DefaultSlideOutEase", Flags).GetValue(null);
        var run = svcType.GetMethod("RunSlideStoryboard", Flags);

        run.Invoke(svc, new object[] { slideIn, 0d, 0d, slideInEase, inMs, inTravels, "in" });
        CompositionTarget.Rendering += onSample;

        var captureDelayMs = Math.Max(300, (int)Math.Round(inMs) + 20);
        await Task.Delay(captureDelayMs);
        Invoke(svc, "StopActiveSlide");
        var remainingMs = Math.Max(0, durationSeconds * 1000 - captureDelayMs);
        await Task.Delay((int)remainingMs);

        run.Invoke(svc, new object[] { slideOut, 0d, 0d, slideOutEase, outMs, outTravels, "out" });
        await Task.Delay((int)Math.Round(outMs) + 10);
        CompositionTarget.Rendering -= onSample;
        Invoke(svc, "StopActiveSlide");

        var completeTask = (Task)recorderType.GetMethod("CompleteAsync", Flags).Invoke(recorder, null);
        await completeTask;
        var tracks = (IEnumerable)completeTask.GetType().GetProperty("Result").GetValue(completeTask);
        var track = tracks.Cast<object>().FirstOrDefault();
        window.Close();
        game.Close();
        if (track == null)
        {
            throw new InvalidOperationException("the recorder produced no track");
        }

        Compare(track, truths);
        Export(plugin, logger, track);
    }

    private static void Compare(object track, List<Truth> truths)
    {
        var trackType = track.GetType();
        var samples = ((IEnumerable)trackType.GetProperty("Samples").GetValue(track)).Cast<object>().ToList();
        var frames = ((IEnumerable)trackType.GetProperty("Frames").GetValue(track)).Cast<object>().ToList();
        var reconstruct = trackType.GetMethod("TryReconstructFrame");
        var sampleType = samples[0].GetType();
        Func<object, string, object> field = (o, n) => sampleType.GetField(n).GetValue(o);

        byte[] buffer = null;
        var bufferIndex = -1;
        var leftEdgeByFrame = new Dictionary<int, double>();
        var csv = new StringBuilder("elapsedMs,trueXPhys,recordedXPhys,errorPhys,trueOpacity,recordedOpacity,frameIndex,slideRunning\n");
        var count = Math.Min(samples.Count, truths.Count);
        double maxErr = 0, sumSq = 0;
        var stale = 0;
        var motionSamples = 0;
        var worstAt = 0d;
        for (var i = 0; i < count; i++)
        {
            var sample = samples[i];
            var frameIndex = (int)field(sample, "FrameIndex");
            if (!leftEdgeByFrame.TryGetValue(frameIndex, out var leftPhys))
            {
                var args = new object[] { frameIndex, buffer, bufferIndex };
                if ((bool)reconstruct.Invoke(track, args))
                {
                    buffer = (byte[])args[1];
                    bufferIndex = (int)args[2];
                    var frame = frames[frameIndex];
                    var w = (int)frame.GetType().GetProperty("Width").GetValue(frame);
                    var h = (int)frame.GetType().GetProperty("Height").GetValue(frame);
                    var cardW = (int)field(sample, "CardWPhys");
                    leftPhys = OpaqueLeftEdge(buffer, w, h) * (cardW / (double)w);
                }
                else
                {
                    leftPhys = double.NaN;
                }

                leftEdgeByFrame[frameIndex] = leftPhys;
            }

            var recordedX = (double)field(sample, "SlideXPhys") + leftPhys;
            var truth = truths[i];
            var err = recordedX - truth.TrueXPhys;
            var moving = i > 0 && Math.Abs(truths[i].TrueXPhys - truths[i - 1].TrueXPhys) > 0.5;
            if (moving)
            {
                motionSamples++;
                if (i > 0 && Math.Abs(recordedX - ((double)field(samples[i - 1], "SlideXPhys") +
                        leftEdgeByFrame[(int)field(samples[i - 1], "FrameIndex")])) < 0.5)
                {
                    stale++;
                }
            }

            if (!double.IsNaN(err) && truth.TrueOpacity > 0.02)
            {
                sumSq += err * err;
                if (Math.Abs(err) > Math.Abs(maxErr))
                {
                    maxErr = err;
                    worstAt = truth.ElapsedMs;
                }
            }

            csv.AppendLine(string.Join(",",
                truth.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture),
                truth.TrueXPhys.ToString("0.0", CultureInfo.InvariantCulture),
                recordedX.ToString("0.0", CultureInfo.InvariantCulture),
                err.ToString("0.0", CultureInfo.InvariantCulture),
                truth.TrueOpacity.ToString("0.00", CultureInfo.InvariantCulture),
                ((double)field(sample, "HostOpacity")).ToString("0.00", CultureInfo.InvariantCulture),
                frameIndex,
                truth.SlideRunning ? 1 : 0));
        }

        var csvPath = Path.Combine(_outDir, _label + "_motion.csv");
        File.WriteAllText(csvPath, csv.ToString());
        Console.WriteLine();
        Console.WriteLine("=== motion fidelity (" + _label + "): live card position vs what the track will composite");
        Console.WriteLine("  samples " + count + ", unique frames stored " + frames.Count);
        Console.WriteLine("  samples where the live card moved: " + motionSamples +
            ", of which the recorded position did not move: " + stale);
        Console.WriteLine("  position error while visible: rms " + Math.Sqrt(sumSq / Math.Max(1, count)).ToString("0.0") +
            " px, worst " + maxErr.ToString("0.0") + " px at " + worstAt.ToString("0") + " ms");
        Console.WriteLine("  per-sample table: " + csvPath);
    }

    // Leftmost column with a mostly opaque pixel on the card's middle row, in frame pixels.
    private static double OpaqueLeftEdge(byte[] premulBgra, int width, int height)
    {
        var row = height / 2;
        for (var x = 0; x < width; x++)
        {
            if (premulBgra[(row * width + x) * 4 + 3] >= 128)
            {
                return x;
            }
        }

        return double.NaN;
    }

    private static void Export(Assembly plugin, object logger, object track)
    {
        var reencoderType = plugin.GetType("PlayniteAchievements.Services.Capture.MediaFoundationOverlayReencoder");
        var reencoder = Activator.CreateInstance(reencoderType, Flags, null, new[] { logger }, null);
        var qualityType = plugin.GetType("PlayniteAchievements.Models.Settings.RecordingQuality");
        var quality = Enum.Parse(qualityType, Enum.GetNames(qualityType)[0]);
        var output = Path.Combine(_outDir, _label + ".mp4");
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        const double toastStart = 1.0;
        var duration = (double)track.GetType().GetProperty("DurationSeconds").GetValue(track);
        var ok = (bool)reencoderType.GetMethod("Export", Flags).Invoke(reencoder, new object[]
        {
            _baseClip, track, toastStart, duration, 0d, Math.Min(10d, toastStart + duration + 1.0),
            null, 0d, output, _fps, quality,
        });
        Console.WriteLine();
        Console.WriteLine("=== export: " + (ok ? output : "FAILED") + " (card from " + toastStart + " s, " +
            duration.ToString("0.00") + " s long)");
        if (!ok)
        {
            _exitCode = 1;
        }
    }

    // ResolveSlideStoryboard's rules, with the plugin's own AnimatesSlide and BuildSlidePath.
    private static Storyboard ResolveSlide(Type svcType, Storyboard authored, double fallbackMs, out double ms, out bool travels)
    {
        ms = fallbackMs;
        travels = true;
        if (authored == null)
        {
            return null;
        }

        var animatesSlide = svcType.GetMethod("AnimatesSlide", Flags);
        var buildPath = svcType.GetMethod("BuildSlidePath", Flags);
        var storyboard = authored.Clone();
        var resolved = 0d;
        var moves = false;
        foreach (var child in storyboard.Children)
        {
            if (Storyboard.GetTargetName(child) != null || Storyboard.GetTarget(child) != null)
            {
                continue;
            }

            if ((bool)animatesSlide.Invoke(null, new object[] { child }))
            {
                Storyboard.SetTargetProperty(child, (PropertyPath)buildPath.Invoke(null, null));
                moves = true;
            }

            if (child.Duration.HasTimeSpan)
            {
                resolved = Math.Max(resolved, child.Duration.TimeSpan.TotalMilliseconds);
            }
        }

        if (resolved <= 0)
        {
            return null;
        }

        ms = resolved;
        travels = moves;
        return storyboard;
    }

    private static object CreateViewModel(Assembly plugin, Type vmType)
    {
        var argsType = plugin.GetType("PlayniteAchievements.Models.AchievementUnlockedEventArgs");
        var unlock = Activator.CreateInstance(argsType);
        argsType.GetProperty("DisplayName").SetValue(unlock, "Master of the Probe");
        argsType.GetProperty("ProviderKey").SetValue(unlock, "Steam");
        argsType.GetProperty("RarityTier").SetValue(unlock, "Rare");
        var ctor = vmType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var values = ctor.GetParameters().Select(p => p.ParameterType == argsType ? unlock
            : p.ParameterType == typeof(bool) ? (object)false : null).ToArray();
        return ctor.Invoke(values);
    }

    // The theme's own brush, the strings this file references, and nothing else.
    private static void InstallThemeResources(Application app)
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#30343a"), 0));
        gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1a1d24"), 0.6));
        gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1a1d24"), 1));
        app.Resources["PS5BasicGradient"] = gradient;
        app.Resources["LOCPS5TrophyEarned"] = "Trophy Earned!";
        app.Resources["LOCPS5Complete100"] = "100% Complete";
        app.Resources["LOCPS5AllAchievementsUnlocked"] = "You've unlocked all achievements!";
        app.Resources["LOCPS5Progress"] = "Progress";
    }

    // Playnite's ThemeFile extension is not available outside Playnite; substitute the file path it
    // resolves to.
    private static ResourceDictionary LoadToastDictionary(string path)
    {
        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"\{ThemeFile\s+'([^']+)'\}", m =>
            new Uri(Path.Combine(_themeDir, m.Groups[1].Value.Replace('/', '\\'))).AbsoluteUri);
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static async Task<double> MeasureFramePeriodAsync(int frames)
    {
        var tcs = new TaskCompletionSource<double>();
        var seen = 0;
        var first = double.NaN;
        var last = double.NegativeInfinity;
        EventHandler handler = null;
        handler = (s, e) =>
        {
            var now = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
            if (now <= last)
            {
                return;
            }

            last = now;
            if (double.IsNaN(first))
            {
                first = now;
            }

            if (++seen > frames)
            {
                CompositionTarget.Rendering -= handler;
                tcs.TrySetResult((last - first) / frames);
            }
        };
        CompositionTarget.Rendering += handler;
        return await tcs.Task;
    }

    private static double PhysPerDip(Window window)
    {
        return PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice.M11;
    }

    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Flags);
        if (field == null)
        {
            throw new MissingFieldException(target.GetType().Name, name);
        }

        field.SetValue(target, value);
    }

    private static object Invoke(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, Flags);
        if (method == null)
        {
            throw new MissingMethodException(target.GetType().Name, name);
        }

        return method.Invoke(target, args);
    }

    public static class ConsoleLogger
    {
        public static object Create(Type loggerInterface)
        {
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("ProbeLogger"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("main").DefineType(
                "ProbeLoggerImpl", TypeAttributes.Public | TypeAttributes.Class, typeof(object), new[] { loggerInterface });
            var write = typeof(ConsoleLogger).GetMethod("Write", BindingFlags.Public | BindingFlags.Static);
            foreach (var method in loggerInterface.GetMethods())
            {
                var parameters = method.GetParameters();
                var impl = type.DefineMethod(
                    method.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType,
                    parameters.Select(p => p.ParameterType).ToArray());
                var il = impl.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldstr, method.Name);
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, parameters.Length);
                il.Emit(System.Reflection.Emit.OpCodes.Newarr, typeof(object));
                for (var i = 0; i < parameters.Length; i++)
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Dup);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, i);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg, i + 1);
                    if (parameters[i].ParameterType.IsValueType)
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Box, parameters[i].ParameterType);
                    }

                    il.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);
                }

                il.Emit(System.Reflection.Emit.OpCodes.Call, write);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                type.DefineMethodOverride(impl, method);
            }

            return Activator.CreateInstance(type.CreateType());
        }

        public static void Write(string level, object[] args)
        {
            if (level == "Trace" || level == "Debug")
            {
                return;
            }

            var text = string.Join(" ", args.Where(a => a != null && !(a is Exception)));
            var ex = args.OfType<Exception>().FirstOrDefault();
            Console.WriteLine("[plugin " + level + "] " + text + (ex != null ? " :: " + ex.Message : string.Empty));
        }
    }
}
