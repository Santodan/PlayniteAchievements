// Benchmarks and compares the real notification card: the user's own settings (config.json, with its
// background, glow, sizes and shadows), the bundled template or a template file, built and animated by
// the plugin's own code driven through reflection, in a DWM-composited window as the plugin shows it.
//
//   TemplateBench.exe [--template <AchievementToast.xaml>] [--cycles 3] [--hold 4] [--load N]
//                     [--compare <other AchievementToast.xaml>] [--kind rare] [--config <config.json>]
//
// Bench mode runs complete notifications - slide in, hold with the countdown, glow and background all
// running, slide out - and samples the card's screen rectangle through desktop duplication. Reports
// acquired desktop updates and missed updates separately. This is not a hardware scanout/VRR probe.
// Use --no-capture for game-impact measurements: desktop duplication adds its own GPU workload.
//
// Compare mode shows the template (A) and the --compare template (B) at once, so both cards share one
// background player and show the same frame, pauses every animation, and compares the two on screen.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

public static class TemplateBench
{
    private const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static string _repo;
    private static string _pluginDir;
    private static string _templatePath;
    private static string _comparePath;
    private static string _configPath;
    private static string _kind = "rare";
    private static int _cycles = 3;
    private static double _holdSeconds = 4;
    private static int _load;
    private static bool _software;
    private static int _gameWork;
    private static bool _noPulse, _noRays, _noCountdown, _stillBackground, _stillPulse;
    private static string _overlayPath;
    private static Card _overlay;
    private static readonly List<GameSample> _gameFrames = new List<GameSample>();
    private static bool _noCapture, _inProcessCapture;
    private static int _recorderPriority = 2;
    private static int _frameRate;

    // Every CompositionTarget.Rendering: the frames WPF prepared, presented or not.
    private static readonly List<long> _renders = new List<long>();
    private static double _gameBaseline;
    private static int _gpuPriority = -1;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    private static bool _freezeMotion;
    private static bool _liveMotion;
    private static bool _thumbnail;
    private static bool _presenter;
    private static bool _holdLayers, _layersCompare, _earlyLayers, _layersMidSlide;

    // The card as the clip sampler renders it (effects stripped, rays hidden), through the
    // service's own TryRenderToastItemBytes: from the card window, or with layers from the
    // background layer's container with the content layer's drawn over it.
    private static byte[] SamplerRender(Type svcType, Card card, object layers, out int width, out int height)
    {
        width = height = 0;
        var container = (FrameworkElement)card.Surface.ItemContainerGenerator.ContainerFromIndex(0);
        FrameworkElement under = null, over = null;
        if (layers != null)
        {
            var find = layers.GetType().GetMethod("CardContainer");
            under = (FrameworkElement)find.Invoke(layers, new object[] { layers.GetType().GetProperty("Background").GetValue(layers), 0 });
            over = (FrameworkElement)find.Invoke(layers, new object[] { layers.GetType().GetProperty("Content").GetValue(layers), 0 });
        }

        var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(FrameworkElement), typeof(System.Windows.Media.Effects.Effect));
        var effects = Activator.CreateInstance(typeof(List<>).MakeGenericType(pairType));
        var burstType = _plugin.GetType("PlayniteAchievements.Views.Controls.RarityRayBurst");
        var bursts = Activator.CreateInstance(typeof(List<>).MakeGenericType(burstType));
        var collect = svcType.GetMethod("CollectEffects", Flags);
        collect.Invoke(null, new object[] { container, effects, bursts });
        if (under != null) collect.Invoke(null, new object[] { under, effects, null });
        if (over != null) collect.Invoke(null, new object[] { over, effects, null });
        svcType.GetMethod("StripEffects", Flags).Invoke(null, new object[] { effects });
        svcType.GetMethod("HideRayBursts", Flags).Invoke(null, new object[] { bursts });
        try
        {
            Func<int, byte[]> take = length => new byte[length];
            var call = new object[] { card.Window, container, null, take, false, 1d, null, null, 0, 0, 0, 0, false, under, over };
            var ok = (bool)svcType.GetMethod("TryRenderToastItemBytes", Flags).Invoke(card.Service, call);
            if (!ok) return null;
            width = (int)call[8];
            height = (int)call[9];
            return (byte[])call[7];
        }
        finally
        {
            svcType.GetMethod("RestoreRayBursts", Flags).Invoke(null, new object[] { bursts });
            svcType.GetMethod("RestoreEffects", Flags).Invoke(null, new object[] { effects });
        }
    }

    private static void SavePixels(byte[] pixels, int width, int height, string path)
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
    }

    private static object _layersToDrop;

    // What the end of a wave does to its layers: the rays go back to the card's own window and
    // the layer windows close.
    private static void DropLayers(Type svcType, object service)
    {
        if (_layersToDrop == null) return;
        svcType.GetMethod("DropRaysAhead", Flags).Invoke(service, null);
        ((IDisposable)_layersToDrop).Dispose();
        _layersToDrop = null;
    }

    private static object PrepareLayers(Type svcType, dynamic card, bool report, string when)
    {
        DropLayers(svcType, (object)card.Service);
        var build = Stopwatch.StartNew();
        object prepared = svcType.GetMethod("PrepareHoldLayers", Flags).Invoke(
            (object)card.Service, new object[] { (object)card.Window, (object)card.Surface, (object)card.Items, (object)card.Template, false, when == "before" });
        if (report) Console.WriteLine("hold layers built {0} the entrance in {1} ms: {2}", when, build.ElapsedMilliseconds, prepared != null);
        return prepared;
    }
    private static bool _tinyAnimation;
    // Which bursts --no-rays hides: all, the card's own (ToastLayer.Role Rays) or the icon's.
    private static string _hideRays = "all";
    // Bursts draw one geometry per copy, whatever their template asks.
    private static bool _rayGeometry;
    // Cards in the wave, stacked as the service stacks a wave of several notifications.
    private static int _cards = 1;
    // The content layer's effects are removed once it is shown, to measure what they cost there.
    private static bool _contentNoEffects;
    // The glow pulses inside the content layer are stopped once it is shown.
    private static bool _contentNoPulse;

    private static int StopPulses(DependencyObject root)
    {
        var pulse = _plugin.GetType("PlayniteAchievements.Views.Helpers.RarityGlowPulse");
        var get = pulse.GetMethod("GetIsActive", BindingFlags.Public | BindingFlags.Static);
        var set = pulse.GetMethod("SetIsActive", BindingFlags.Public | BindingFlags.Static);
        var stopped = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var which = Environment.GetEnvironmentVariable("BENCH_PULSES") ?? "all";
            if (child is FrameworkElement pulsing && (bool)get.Invoke(null, new object[] { child }) &&
                (which == "all" || (which == "hidden") == !pulsing.IsVisible))
            {
                set.Invoke(null, new object[] { child, false });
                stopped++;
            }

            stopped += StopPulses(child);
        }

        return stopped;
    }
    // The background's decoding Image is collapsed in the content and rays layers.
    private static bool _layersStopDecode;

    private static int CollapseNamed(DependencyObject root, string name)
    {
        var done = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                element.Visibility = Visibility.Collapsed;
                done++;
            }

            done += CollapseNamed(child, name);
        }

        return done;
    }

    private static int RemoveEffects(DependencyObject root)
    {
        var removed = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement element && element.Effect != null)
            {
                element.Effect = null;
                removed++;
            }

            removed += RemoveEffects(child);
        }

        return removed;
    }

    private static void UseRayGeometry(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child.GetType().Name == "RarityRayBurst")
            {
                child.GetType().GetProperty("IsRasterized").SetValue(child, false);
            }

            UseRayGeometry(child);
        }
    }
    // The ray driver's invalidations per second when above its own 30, through SetSamplingFps.
    private static double _rayFps;
    private static string _snapshot;
    private static string _decodePath;
    // Glow pulse and rays off and no countdown: only the background animates in the hold.
    private static bool _backgroundOnly;
    // The plugin's own content scale (fit and DPI compensation) on the card surface: 2 renders the
    // card at the device size of a 200% display.
    private static double _cardScale = 1;
    private static Assembly _plugin;
    private static object _logger;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--loadwindow") >= 0)
        {
            return 0;
        }

        var record = Array.IndexOf(args, "--record");
        if (record >= 0)
        {
            return RecordChild(args[record + 1]);
        }

        var here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _repo = Path.GetFullPath(Path.Combine(here, "..", "..", ".."));
        _pluginDir = Path.Combine(_repo, "source", "bin", "Debug");
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Playnite", "ExtensionsData", "e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b", "config.json");
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--template": _templatePath = args[++i]; break;
                case "--compare": _comparePath = args[++i]; break;
                case "--config": _configPath = args[++i]; break;
                case "--kind": _kind = args[++i]; break;
                case "--cycles": _cycles = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--hold": _holdSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--load": _load = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--plugin": _pluginDir = args[++i]; break;
                case "--snapshot": _snapshot = args[++i]; break;
                case "--decode": _decodePath = args[++i]; break;
                case "--background-only": _backgroundOnly = true; break;
                case "--software": _software = true; break;
                // A GameLoad at this shader work under the card; its frame rate is reported per phase.
                case "--game": _gameWork = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--no-pulse": _noPulse = true; break;
                case "--no-rays": _noRays = true; break;
                case "--ray-geometry": _rayGeometry = true; break;
                case "--cards": _cards = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--content-no-effects": _contentNoEffects = true; break;
                case "--content-no-pulse": _contentNoPulse = true; break;
                case "--layers-stop-decode": _layersStopDecode = true; break;
                case "--no-icon-rays": _noRays = true; _hideRays = "icon"; break;
                case "--no-card-rays": _noRays = true; _hideRays = "card"; break;
                case "--no-countdown": _noCountdown = true; break;
                case "--still-background": _stillBackground = true; break;
                // Pauses the card and icon glow pulses only; --no-pulse also stops the rays.
                case "--still-pulse": _stillPulse = true; break;
                // A second card from this template in its own window over the first, shown only while
                // the card rests: the content layer split from the card for the hold.
                case "--overlay": _overlayPath = args[++i]; break;
                // Capture runs in a child process at normal GPU priority unless one of these is given.
                case "--no-capture": _noCapture = true; break;
                case "--in-process-capture": _inProcessCapture = true; break;
                // The capture child's GPU scheduling class (default 2, normal). At 5 it is not
                // starved by the game and misses few updates, but takes GPU time itself: read
                // notification cadence from such a run and game fps from a --no-capture one.
                case "--recorder-priority": _recorderPriority = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--gpu-priority": _gpuPriority = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--card-scale": _cardScale = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                // The plugin's animation frame rate (AnimationFrameRate.Current) instead of the display's.
                case "--frame-rate": _frameRate = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                // Leave the quiet scope's stand-downs out, so the background and glow keep animating
                // through the slides.
                case "--animate-motion": _freezeMotion = false; break;
                case "--live-motion": _liveMotion = true; break;
                // The card window stays cloaked and a surfaceless presenter window shows its DWM
                // thumbnail; entrance and exit move the thumbnail from a pacing thread, so the
                // motion needs no WPF frames.
                case "--thumbnail": _thumbnail = true; break;
                // The plugin's own presenter (ToastThumbnailPresenter): the card window stays
                // cloaked, the slide storyboards run on the service's motion stand-in, and the
                // service mirrors it onto the thumbnail.
                case "--presenter": _presenter = true; break;
                // With --presenter: while the card rests, the plugin shows its background and its
                // content from layer windows (ToastHoldLayers) over the card window, which stands still.
                case "--hold-layers": _holdLayers = true; break;
                // With --hold-layers: builds the layers before the entrance, as the wave does,
                // so the hold only shows them.
                case "--early-layers": _earlyLayers = true; break;
                // With --early-layers: builds them once the slide is under way instead.
                case "--layers-mid-slide": _layersMidSlide = true; break;
                // With --hold-layers: captures the card on screen before and after the split and
                // counts differing pixels. Use with --still-background --no-pulse --no-rays.
                case "--layers-compare": _layersCompare = true; break;
                // A 6 px square in the window corner, away from the card, animating forever: a
                // frame every composition that redraws none of the card.
                case "--tiny-animation": _tinyAnimation = true; break;
                case "--ray-fps": _rayFps = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
            }
        }

        _freezeMotion = Array.IndexOf(args, "--animate-motion") < 0;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        var code = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // The popup brushes a Playnite theme supplies; without them a card with no background image
        // has no surface. The plugin's neutral preview theme's values.
        app.Resources["PlayAch.Brush.PopupSurface"] = Frozen(Color.FromRgb(0x23, 0x29, 0x33));
        app.Resources["PlayAch.Brush.PopupBorder"] = Frozen(Color.FromRgb(0x34, 0x3B, 0x46));
        var loads = new List<Process>();
        if (_gpuPriority >= 0)
        {
            // 0 idle .. 2 normal .. 4 high, 5 realtime (privileged).
            Console.WriteLine("gpu priority {0}: status 0x{1:X}", _gpuPriority,
                D3DKMTSetProcessSchedulingPriorityClass(Process.GetCurrentProcess().Handle, _gpuPriority));
        }

        app.Startup += async (s, e) =>
        {
            try
            {
                if (_load > 0)
                {
                    var exe = Path.Combine(here, "SlideCadenceProbe.exe");
                    for (var i = 0; i < _load; i++)
                    {
                        loads.Add(Process.Start(new ProcessStartInfo(exe, "--loadwindow") { UseShellExecute = false }));
                    }

                    await Task.Delay(2000);
                }

                if (_gameWork > 0)
                {
                    var game = Process.Start(new ProcessStartInfo(Path.Combine(here, "GameLoad.exe"), "--report --work " + _gameWork)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                    });
                    loads.Add(game);
                    game.OutputDataReceived += (o, d) =>
                    {
                        var parts = d.Data?.Split(' ');
                        if (parts != null && parts.Length == 4 && parts[0] == "f")
                        {
                            lock (_gameFrames)
                            {
                                _gameFrames.Add(new GameSample
                                {
                                    Presented = long.Parse(parts[1], CultureInfo.InvariantCulture),
                                    Time = long.Parse(parts[2], CultureInfo.InvariantCulture),
                                    MaxFrameTicks = long.Parse(parts[3], CultureInfo.InvariantCulture),
                                });
                            }
                        }
                    };
                    game.BeginOutputReadLine();
                    await Task.Delay(3000);
                    var now = Stopwatch.GetTimestamp();
                    _gameBaseline = GameFps(now - (2 * Stopwatch.Frequency), now);
                    Console.WriteLine("game alone {0:0.0} fps", _gameBaseline);
                }

                await RunAsync(app);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + (ex.InnerException ?? ex));
                code = 1;
            }
            finally
            {
                foreach (var p in loads)
                {
                    try { p.Kill(); } catch { }
                }

                app.Shutdown();
            }
        };
        app.Run();
        return code;
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        var name = new AssemblyName(e.Name).Name + ".dll";
        var direct = Path.Combine(_pluginDir, name);
        if (File.Exists(direct))
        {
            return Assembly.LoadFrom(direct);
        }

        var packages = Path.Combine(_repo, "source", "packages");
        var hit = Directory.GetFiles(packages, name, SearchOption.AllDirectories)
            .FirstOrDefault(p => p.IndexOf("\\net4", StringComparison.OrdinalIgnoreCase) >= 0);
        return hit != null ? Assembly.LoadFrom(hit) : null;
    }

    private static async Task RunAsync(Application app)
    {
        _plugin = Assembly.LoadFrom(Path.Combine(_pluginDir, "PlayniteAchievements.dll"));
        var sdk = _plugin.GetReferencedAssemblies().Select(Assembly.Load).First(a => a.GetName().Name == "Playnite.SDK");
        _logger = ConsoleLogger.Create(sdk.GetType("Playnite.SDK.ILogger"));
        InstallLocalization(sdk, app);

        if (_decodePath != null)
        {
            DecodeTiming(_decodePath);
            return;
        }

        if (_frameRate > 0)
        {
            _plugin.GetType("PlayniteAchievements.Views.Helpers.AnimationFrameRate").GetField("_cached", Flags).SetValue(null, _frameRate);
        }

        var persisted = LoadPersisted();
        InstallPluginInstance(persisted);
        if (_comparePath != null)
        {
            // Animations off, so both cards hold one state; the background is paused below.
            persisted.GetType().GetProperty("AnimateRarityGlows").SetValue(persisted, false);
            await CompareAsync(persisted);
            return;
        }

        if (_backgroundOnly || _noPulse)
        {
            persisted.GetType().GetProperty("AnimateRarityGlows").SetValue(persisted, false);
        }

        var card = await BuildCardAsync(persisted, _templatePath, left: true);
        if (_overlayPath != null)
        {
            _overlay = await BuildCardAsync(persisted, _overlayPath, left: true, backdropUnder: false);
            Cloak(_overlay, true);
        }

        Console.WriteLine("card window {0}x{1} px, background animating, glow pulse {2}",
            card.Rect.Right - card.Rect.Left, card.Rect.Bottom - card.Rect.Top,
            persisted.GetType().GetProperty("AnimateRarityGlows").GetValue(persisted));
        if (_snapshot != null)
        {
            SaveCapture(card.Rect, _snapshot);
            Console.WriteLine("snapshot saved to " + _snapshot);
        }

        await BenchAsync(card);
        _overlay?.Window.Close();
        card.Window.Close();
    }

    /// <summary>
    /// A window with no surface of its own that shows the cloaked card window through a DWM
    /// thumbnail and moves it, one step per DWM composition, from its own thread.
    /// </summary>
    private sealed class ThumbnailPresenter
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Props
        {
            public int Flags;
            public Rect32 Destination;
            public Rect32 Source;
            public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref Props props);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        private sealed class Surfaceless : System.Windows.Forms.Form
        {
            protected override bool ShowWithoutActivation => true;

            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get
                {
                    var p = base.CreateParams;
                    // TOPMOST | TRANSPARENT | TOOLWINDOW | NOACTIVATE | NOREDIRECTIONBITMAP
                    p.ExStyle |= 0x8 | 0x20 | 0x80 | 0x08000000 | 0x00200000;
                    return p;
                }
            }
        }

        private readonly IntPtr _thumb;
        private readonly int _width, _height, _travel;
        private readonly Surfaceless _form;

        public ThumbnailPresenter(IntPtr source, Rect32 rect, int travelPx)
        {
            _width = rect.Right - rect.Left;
            _height = rect.Bottom - rect.Top;
            _travel = travelPx;
            _form = new Surfaceless
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                TopMost = true,
                Bounds = new System.Drawing.Rectangle(rect.Left, rect.Top, _width, _height),
            };
            _form.Show();
            var hr = DwmRegisterThumbnail(_form.Handle, source, out _thumb);
            if (hr != 0) throw new InvalidOperationException("DwmRegisterThumbnail 0x" + hr.ToString("X"));
            Place(_travel, false);
        }

        private void Place(int offsetY, bool visible)
        {
            var p = new Props
            {
                Flags = 1 | 4 | 8 | 16,
                Destination = new Rect32 { Left = 0, Top = offsetY, Right = _width, Bottom = offsetY + _height },
                Opacity = 255,
                Visible = visible,
                SourceClientAreaOnly = true,
            };
            DwmUpdateThumbnailProperties(_thumb, ref p);
        }

        /// <summary>Slides in (from the travel offset to rest) or out, sine eased, and returns at once.</summary>
        public void Slide(bool entering, double durationMs)
        {
            var thread = new Thread(() =>
            {
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    var p = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / durationMs);
                    var shown = entering ? Math.Sin(p * Math.PI / 2) : Math.Cos(p * Math.PI / 2);
                    Place((int)Math.Round(_travel * (1 - shown)), entering || p < 1.0);
                    if (p >= 1.0) break;
                    DwmFlush();
                }
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }
    }

    private static void Cloak(Card card, bool cloaked)
    {
        _plugin.GetType("PlayniteAchievements.Services.UI.ToastWindowSurface").GetMethod("SetCloaked")
            .Invoke(null, new object[] { card.Window, cloaked });
    }

    private sealed class Card
    {
        public Window Window;
        public Grid Host;
        public ItemsControl Surface;
        public TranslateTransform Slide;
        public object Service;
        public Rect32 Rect;
        public double TravelDip;
        public object Items;
        public DataTemplate Template;
    }

    private static object _settings;

    private static object LoadPersisted()
    {
        var settingsType = _plugin.GetType("PlayniteAchievements.Models.PlayniteAchievementsSettings");
        var json = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert");
        var deserialize = json.GetMethods().First(m => m.Name == "DeserializeObject" && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType == typeof(Type));
        _settings = deserialize.Invoke(null, new object[] { File.ReadAllText(_configPath), settingsType });
        return settingsType.GetProperty("Persisted").GetValue(_settings);
    }

    /// <summary>
    /// A plugin instance carrying only what the card's image loading reaches for: the disk and
    /// memory image services over the user's plugin data folder, the ray track service, and the
    /// settings. Constructed as the plugin's own startup does, without Playnite.
    /// </summary>
    private static void InstallPluginInstance(object persisted)
    {
        var pluginType = _plugin.GetType("PlayniteAchievements.PlayniteAchievementsPlugin");
        var instance = FormatterServices.GetUninitializedObject(pluginType);
        var dataDir = Path.GetDirectoryName(_configPath);
        var disk = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Services.Images.DiskImageService"), _logger, dataDir, 8, null);
        var memory = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Services.Images.MemoryImageService"), _logger, disk, 512);
        var rays = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Services.Images.RayTrackService"), _logger, memory, 1024);
        Set(instance, "_diskImageService", disk);
        Set(instance, "_imageService", memory);
        Set(instance, "_rayTrackService", rays);

        var settingsVmType = pluginType.GetField("_settingsViewModel", Flags).FieldType;
        var settingsVm = FormatterServices.GetUninitializedObject(settingsVmType);
        var settingsField = settingsVmType.GetFields(Flags).First(f => f.FieldType == _settings.GetType());
        settingsField.SetValue(settingsVm, _settings);
        Set(instance, "_settingsViewModel", settingsVm);
        pluginType.GetField("<Instance>k__BackingField", Flags).SetValue(null, instance);
    }

    private static DataTemplate LoadTemplate(string path)
    {
        var resolverType = _plugin.GetType("PlayniteAchievements.Services.UI.AchievementToastTemplateResolver");
        var resolver = Activator.CreateInstance(resolverType, new object[] { null, _logger, null, null, null });
        if (path == null)
        {
            return (DataTemplate)resolverType.GetMethod("ResolveBundledDefaultTemplate").Invoke(resolver, new object[] { false });
        }

        var call = new object[] { File.ReadAllText(path), false, null, null };
        var ok = (bool)resolverType.GetMethod("TryLoadTemplateFromXaml").Invoke(resolver, call);
        if (!ok || call[2] == null)
        {
            throw new InvalidOperationException("template did not load: " + call[3]);
        }

        return (DataTemplate)call[2];
    }

    private static async Task<Card> BuildCardAsync(object persisted, string templatePath, bool left, bool backdropUnder = true)
    {
        var template = LoadTemplate(templatePath);
        var argsFactory = _plugin.GetType("PlayniteAchievements.Services.UI.ToastPreviewFactory");
        var unlock = argsFactory.GetMethod("BuildPreviewArgs", Flags).Invoke(null, new object[] { _kind, null, null });
        var vmType = _plugin.GetType("PlayniteAchievements.ViewModels.AchievementToastViewModel");
        var ctor = vmType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var values = ctor.GetParameters().Select(p =>
            p.ParameterType == unlock.GetType() ? unlock
            : p.ParameterType == persisted.GetType() ? persisted
            : p.ParameterType == typeof(bool) ? (object)false
            : null).ToArray();
        var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(vmType));
        for (var n = 0; n < Math.Max(1, _cards); n++)
        {
            var vm = ctor.Invoke(values);
            await (Task)vmType.GetMethod("PrepareImagesAsync").Invoke(vm, null);
            items.Add(vm);
        }
        var factory = _plugin.GetType("PlayniteAchievements.Services.UI.ToastSurfaceFactory");
        var surface = (ItemsControl)factory.GetMethod("BuildToastSurface", Flags).Invoke(null, new object[] { items, template });
        if (Math.Abs(_cardScale - 1) > 1e-6)
        {
            surface.LayoutTransform = new ScaleTransform(_cardScale, _cardScale);
        }

        var hostArgs = new object[] { surface, null };
        var host = (Grid)factory.GetMethod("BuildSlideHost", Flags).Invoke(null, hostArgs);
        var slide = (TranslateTransform)hostArgs[1];

        var provider = _plugin.GetType("PlayniteAchievements.Views.Helpers.PlayniteUiProvider");
        var window = (Window)provider.GetMethod("CreateBorderlessTopmostWindow").Invoke(null, new object[] { null, "TemplateBench" });
        var surfaceType = _plugin.GetType("PlayniteAchievements.Services.UI.ToastWindowSurface");
        surfaceType.GetMethod("UseDwmComposition").Invoke(null, new object[] { window });
        Grid motionHost = null;
        if (_presenter)
        {
            var motionArgs = new object[] { null, null };
            motionHost = (Grid)factory.GetMethod("BuildSlideHost", Flags).Invoke(null, motionArgs);
            slide = (TranslateTransform)motionArgs[1];
            var root = new Grid { IsHitTestVisible = false };
            root.Children.Add(motionHost);
            root.Children.Add(host);
            window.Content = root;
        }
        else
        {
            window.Content = host;
        }

        window.Left = left ? 60 : 60;
        window.Top = left ? 60 : 760;

        var svcType = _plugin.GetType("PlayniteAchievements.Services.UI.ToastNotificationService");
        var svc = FormatterServices.GetUninitializedObject(svcType);
        Set(svc, "_logger", _logger);
        Set(svc, "_activeWindow", window);
        Set(svc, "_activeSlideHost", host);
        Set(svc, "_activeSlideTransform", slide);
        Set(svc, "_activeCardSurface", surface);
        Set(svc, "_activeDwmComposed", true);
        if (_presenter) Set(svc, "_activeMotionHost", motionHost);
        Set(svc, "_activeMonitorScale", 1.0);
        Set(svc, "_activeSlideInTravels", true);
        Set(svc, "_activeSlideOutTravels", true);
        var corner = _plugin.GetType("PlayniteAchievements.Models.Settings.ToastScreenCorner");
        Set(svc, "_activePosition", Enum.Parse(corner, "BottomRight"));

        // A still, solid backdrop under the card's transparent margins: whatever else is on screen
        // there (a blinking caret) would otherwise count as the card changing.
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20)),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = window.Left - 20,
            Top = window.Top - 20,
            Width = 1600,
            Height = 700,
        };
        // GameLoad already supplies a still backdrop. Adding another top-level window would
        // change the presentation workload being attributed to the notification.
        if (_gameWork == 0 && backdropUnder) backdrop.Show();
        window.Closed += (s, e) => backdrop.Close();

        window.Show();
        if (_presenter)
        {
            surfaceType.GetMethod("SetCloaked").Invoke(null, new object[] { window, true });
            svcType.GetMethod("StartPresenter", Flags).Invoke(svc, new object[] { window, false });
            var made = svcType.GetField("_activePresenter", Flags).GetValue(svc);
            if (made == null) throw new InvalidOperationException("presenter was not created");
            made.GetType().GetMethod("Reveal").Invoke(made, null);
        }

        if (_software)
        {
            // The card's window drawn on the CPU, out of the GPU queue a game keeps full.
            ((HwndSource)PresentationSource.FromVisual(window)).CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        }

        factory.GetMethod("ApplyMeasuredCardGaps", Flags)?.Invoke(null, new object[] { surface });
        svcType.GetMethod("ReserveSlideTravel", Flags).Invoke(svc, new object[] { window, surface });
        window.UpdateLayout();
        backdrop.Width = window.ActualWidth + 40;
        backdrop.Height = window.ActualHeight + 40;

        // Let the background player load and start.
        await Task.Delay(2500);
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        var travel = (double)svcType.GetMethod("SlideDistanceDip", Flags).Invoke(svc, new object[] { window });
        return new Card { Window = window, Host = host, Surface = surface, Slide = slide, Service = svc, Rect = rect, TravelDip = travel, Items = items, Template = template };
    }

    private static async Task BenchAsync(Card card)
    {
        var svcType = card.Service.GetType();
        var easeIn = svcType.GetField("DefaultSlideInEase", Flags).GetValue(null);
        var easeOut = svcType.GetField("DefaultSlideOutEase", Flags).GetValue(null);
        var run = svcType.GetMethod("RunSlideStoryboard", Flags);
        var stop = svcType.GetMethod("StopActiveSlide", Flags);
        var engage = svcType.GetMethod("EngageMotionCache", Flags);
        var countdown = svcType.GetMethod("AnimateCountdownBars", Flags);
        var stopCountdown = svcType.GetMethod("StopCountdownBars", Flags);
        const double inMs = 300, outMs = 250;

        var phases = new Dictionary<string, List<PhaseStats>>
        {
            ["entrance"] = new List<PhaseStats>(),
            ["hold"] = new List<PhaseStats>(),
            ["exit"] = new List<PhaseStats>(),
        };

        if (_noRays)
        {
            HideRays(card.Host);
            if (_overlay != null) HideRays(_overlay.Host);
        }

        if (_rayGeometry)
        {
            UseRayGeometry(card.Host);
        }

        StillBackground(card.Host);

        if (_rayFps > 0)
        {
            _plugin.GetType("PlayniteAchievements.Views.Helpers.RayAnimationDriver")
                .GetMethod("SetSamplingFps", Flags | BindingFlags.Public).Invoke(null, new object[] { _rayFps });
        }

        if (_tinyAnimation)
        {
            var dot = new System.Windows.Shapes.Rectangle
            {
                Width = 6, Height = 6, Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            card.Host.Children.Add(dot);
            dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(1))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }

        ThumbnailPresenter presenter = null;
        if (_thumbnail)
        {
            var scale = PresentationSource.FromVisual(card.Window).CompositionTarget.TransformToDevice.M22;
            presenter = new ThumbnailPresenter(new WindowInteropHelper(card.Window).Handle, card.Rect,
                (int)Math.Round(card.TravelDip * scale));
            Cloak(card, true);
            Console.WriteLine("thumbnail presenter: travel {0} px", (int)Math.Round(card.TravelDip * scale));
        }

        IRecorder recorder = _noCapture ? new NullRecorder()
            : _inProcessCapture ? new RegionRecorder(card.Rect)
            : (IRecorder)new RemoteRecorder(card.Rect);
        var lastRenderingTime = TimeSpan.MinValue;
        EventHandler countRenders = (s, e) =>
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == lastRenderingTime) return;
            lastRenderingTime = time;
            lock (_renders)
            {
                _renders.Add(Stopwatch.GetTimestamp());
            }
        };
        CompositionTarget.Rendering += countRenders;
        using (recorder)
        {
            recorder.Start();
            if (_gameWork > 0)
            {
                // The baseline with everything but the card in place: the backdrop, the recorder,
                // and the card's window shown but cloaked.
                var surfaceType = _plugin.GetType("PlayniteAchievements.Services.UI.ToastWindowSurface");
                surfaceType.GetMethod("SetCloaked").Invoke(null, new object[] { card.Window, true });
                await Task.Delay(1000);
                var b0 = Stopwatch.GetTimestamp();
                await Task.Delay(3000);
                var b1 = Stopwatch.GetTimestamp();
                if (!_thumbnail && !_presenter) surfaceType.GetMethod("SetCloaked").Invoke(null, new object[] { card.Window, false });
                Console.WriteLine("game with card hidden {0:0.0} fps, worst frame {1:0.0} ms", GameFps(b0, b1), GameMaxMs(b0, b1));
                await Task.Delay(500);
            }

            for (var c = 0; c < _cycles; c++)
            {
                // Entrance, as the wave runs it: cache realized a frame ahead, then the slide.
                if (_thumbnail)
                {
                    card.Slide.Y = 0;
                }
                else
                {
                    card.Slide.Y = card.TravelDip;
                    if (!_presenter) engage.Invoke(null, new object[] { card.Host, card.Window });
                }

                object prepared = null;
                if (_holdLayers && _earlyLayers && !_layersMidSlide)
                {
                    prepared = PrepareLayers(svcType, card, c == 0, "before");
                }

                await Frames(3);
                var t0 = Stopwatch.GetTimestamp();
                if (_thumbnail)
                {
                    presenter.Slide(true, inMs);
                }
                else
                {
                    run.Invoke(card.Service, new object[] { null, card.TravelDip, 0d, easeIn, inMs, true, "in", false });
                }

                if (_holdLayers && _earlyLayers && _layersMidSlide)
                {
                    prepared = PrepareLayers(svcType, card, c == 0, "during");
                }

                if (_thumbnail)
                {
                }
                else if (_liveMotion)
                {
                    svcType.GetMethod("DisposeSlideQuiet", Flags).Invoke(card.Service, null);
                }
                else if (!_freezeMotion)
                {
                    ResumeAnimations(card.Host);
                }

                await Task.Delay((int)inMs + 20);
                var t1 = Stopwatch.GetTimestamp();
                if (!_thumbnail) stop.Invoke(card.Service, null);
                StillBackground(card.Host);

                // As the wave does at the settle: hits, and DWM composition, limited to the card.
                var surfaceType = _plugin.GetType("PlayniteAchievements.Services.UI.ToastWindowSurface");
                var region = surfaceType.GetMethod("SetHitRegion");
                region.Invoke(null, new object[] { card.Window, card.Surface, 16d });
                if (_overlay != null)
                {
                    Cloak(_overlay, false);
                }

                object layers = null;
                if (_holdLayers)
                {
                    int sampledW = 0, sampledH = 0;
                    var sampledOne = _layersCompare ? SamplerRender(svcType, card, null, out sampledW, out sampledH) : null;
                    var before = _layersCompare ? Capture(card.Rect) : null;
                    if (_layersCompare && _snapshot != null) SaveCapture(card.Rect, _snapshot + "-one.png");
                    var begun = (Task)svcType.GetMethod("BeginHoldLayersAsync", Flags).Invoke(
                        card.Service, new object[] { card.Window, card.Surface, card.Items, card.Template, false, prepared });
                    await begun;
                    layers = begun.GetType().GetProperty("Result").GetValue(begun);
                    if (c == 0) Console.WriteLine("hold layers: " + (layers != null ? "shown" : "none"));
                    if (layers != null && _layersStopDecode)
                    {
                        foreach (var name in new[] { "Rays", "Content" })
                        {
                            var layerWindow = (Window)layers.GetType().GetProperty(name).GetValue(layers);
                            if (layerWindow != null)
                            {
                                var done = CollapseNamed((DependencyObject)layerWindow.Content, "ToastBackgroundSourceHost");
                                if (c == 0) Console.WriteLine(name + " layer decode hosts collapsed: " + done);
                            }
                        }
                    }

                    if (layers != null && _contentNoPulse)
                    {
                        var contentWindow = (Window)layers.GetType().GetProperty("Content").GetValue(layers);
                        var stopped = StopPulses((DependencyObject)contentWindow.Content);
                        if (c == 0) Console.WriteLine("content layer pulses stopped: " + stopped);
                    }

                    if (layers != null && _contentNoEffects)
                    {
                        var contentWindow = (Window)layers.GetType().GetProperty("Content").GetValue(layers);
                        var removed = RemoveEffects((DependencyObject)contentWindow.Content);
                        if (c == 0) Console.WriteLine("content layer effects removed: " + removed);
                    }

                    if (layers != null && _rayGeometry)
                    {
                        foreach (var name in new[] { "Rays", "Background", "Content" })
                        {
                            var layerWindow = (Window)layers.GetType().GetProperty(name).GetValue(layers);
                            if (layerWindow != null) UseRayGeometry((DependencyObject)layerWindow.Content);
                        }
                    }

                    if (layers != null && _noRays)
                    {
                        foreach (var name in new[] { "Background", "Content" })
                        {
                            var layerWindow = (Window)layers.GetType().GetProperty(name).GetValue(layers);
                            HideRays((Grid)layerWindow.Content);
                        }
                    }

                    if (before != null && layers != null)
                    {
                        // The compositions right after the layers were shown: what a layer that had
                        // not presented yet would leave missing while the card under it is hidden.
                        int missing = 0, worst = 0, worstFrame = 0;
                        for (var frame = 1; frame <= 30; frame++)
                        {
                            await Frames(1);
                            var atOnce = Capture(card.Rect);
                            var count = 0;
                            for (var i = 0; i < before.Length; i++)
                            {
                                var a = before[i];
                                var b = atOnce[i];
                                var d = Math.Max(Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)),
                                    Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
                                if (d > 16) count++;
                                if (d > worst) worst = d;
                            }

                            if (count > missing)
                            {
                                missing = count;
                                worstFrame = frame;
                                if (_snapshot != null) SaveCapture(card.Rect, _snapshot + "-atonce.png");
                            }
                        }

                        Console.WriteLine(
                            "layers over 30 frames vs one window: at most {0} px differ by more than 16 levels (frame {1}), max {2}",
                            missing, worstFrame, worst);
                        await Task.Delay(500);
                        var after = Capture(card.Rect);
                        if (_snapshot != null)
                        {
                            SaveCapture(card.Rect, _snapshot + "-layers.png");
                            // Each layer's own tree, rendered offscreen: what it holds, not how it is shown.
                            foreach (var name in new[] { "Background", "Content" })
                            {
                                var layerWindow = (Window)layers.GetType().GetProperty(name).GetValue(layers);
                                var visual = (FrameworkElement)layerWindow.Content;
                                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                    (int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                                rtb.Render(visual);
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                                using (var file = File.Create(_snapshot + "-" + name + ".png")) encoder.Save(file);
                            }
                        }
                        int differing = 0, max = 0;
                        for (var i = 0; i < before.Length; i++)
                        {
                            var a = before[i];
                            var b = after[i];
                            var d = Math.Max(Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)),
                                Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
                            if (d > 0) differing++;
                            if (d > max) max = d;
                        }

                        Console.WriteLine("layers vs one window: {0} of {1} px differ, max {2} levels", differing, before.Length, max);

                        // The clip sampler's render of the card, from its window before the split
                        // and from the layers now.
                        var sampledLayers = SamplerRender(svcType, card, layers, out var layeredW, out var layeredH);
                        if (sampledOne == null || sampledLayers == null || layeredW != sampledW || layeredH != sampledH)
                        {
                            Console.WriteLine("sampler render: failed or sizes differ ({0}x{1} vs {2}x{3})", sampledW, sampledH, layeredW, layeredH);
                        }
                        else
                        {
                            int off = 0, most = 0;
                            for (var i = 0; i < sampledOne.Length; i++)
                            {
                                var d = Math.Abs(sampledOne[i] - sampledLayers[i]);
                                if (d > 2 && i % 4 == 0) off++;
                                if (d > most) most = d;
                            }

                            Console.WriteLine(
                                "sampler render, layers vs one window: {0}x{1}, {2} px differ by more than 2 levels in blue, max {3} in any channel",
                                sampledW, sampledH, off, most);
                            if (_snapshot != null)
                            {
                                SavePixels(sampledOne, sampledW, sampledH, _snapshot + "-sampler-one.png");
                                SavePixels(sampledLayers, sampledW, sampledH, _snapshot + "-sampler-layers.png");
                            }
                        }
                    }

                    // The hold is measured from the split.
                    t1 = Stopwatch.GetTimestamp();
                }

                if (!_backgroundOnly && !_noCountdown)
                {
                    // Split, the countdown is in the front window.
                    var bar = _overlay ?? card;
                    countdown.Invoke(bar.Service, new object[] { bar.Window, (int)(_holdSeconds * 1000), 0d });
                }
                await Task.Delay(TimeSpan.FromSeconds(_holdSeconds));
                var t2 = Stopwatch.GetTimestamp();
                if (layers != null)
                {
                    await (Task)svcType.GetMethod("EndHoldLayersAsync", Flags).Invoke(
                        card.Service, new object[] { card.Window, layers, 250 });
                    // The wave disposes its layers when it ends, after the exit; here the card
                    // lives on, so they go before the next cycle builds its own.
                    _layersToDrop = layers;
                }

                if (!_backgroundOnly && !_noCountdown)
                {
                    var bar = _overlay ?? card;
                    stopCountdown.Invoke(bar.Service, new object[] { bar.Window });
                }

                region.Invoke(null, new object[] { card.Window, null, 0d });
                if (_overlay != null)
                {
                    Cloak(_overlay, true);
                }
                if (!_thumbnail && !_presenter) engage.Invoke(null, new object[] { card.Host, card.Window });
                await Frames(1);
                var t3 = Stopwatch.GetTimestamp();
                if (_thumbnail)
                {
                    presenter.Slide(false, outMs);
                }
                else
                {
                    run.Invoke(card.Service, new object[] { null, 0d, card.TravelDip, easeOut, outMs, true, "out", false });
                }

                if (_thumbnail)
                {
                }
                else if (_liveMotion)
                {
                    svcType.GetMethod("DisposeSlideQuiet", Flags).Invoke(card.Service, null);
                }
                else if (!_freezeMotion)
                {
                    ResumeAnimations(card.Host);
                }

                await Task.Delay((int)outMs + 20);
                var t4 = Stopwatch.GetTimestamp();
                if (!_thumbnail) stop.Invoke(card.Service, null);
                StillBackground(card.Host);
                await Task.Delay(300);

                phases["entrance"].Add(WithGame(recorder.Stats(t0, t1), t0, t1));
                phases["hold"].Add(WithGame(recorder.Stats(t1, t2), t1, t2));
                phases["exit"].Add(WithGame(recorder.Stats(t3, t4), t3, t4));
            }
        }

        CompositionTarget.Rendering -= countRenders;

        var hz = 1000.0 / DisplayPeriodMs();
        Console.WriteLine("display {0:0.0} Hz; per phase, medians of {1} cycles:", hz, _cycles);
        Console.WriteLine("{0,-9} {1,10} {2,11} {3,12} {4,10} {5,7} {6,9} {7,10} {8,8}",
            "phase", "changed/s", "% of display", "longest gap", "presents/s", "missed", "game fps", "game worst", "wpf/s");
        foreach (var phase in phases)
        {
            var mid = phase.Value.OrderBy(p => p.ChangedPerSecond).ElementAt(phase.Value.Count / 2);
            Console.WriteLine("{0,-9} {1,10} {2,11} {3,12} {4,10} {5,7} {6,9:0.0} {7,8:0.0}ms {8,8:0.0}",
                phase.Key,
                _noCapture ? "n/a" : mid.ChangedPerSecond.ToString("0.0", CultureInfo.InvariantCulture),
                _noCapture ? "n/a" : (100 * mid.ChangedPerSecond / hz).ToString("0", CultureInfo.InvariantCulture) + "%",
                _noCapture ? "n/a" : mid.LongestGapMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms",
                _noCapture ? "n/a" : mid.PresentsPerSecond.ToString("0.0", CultureInfo.InvariantCulture),
                _noCapture ? "n/a" : phase.Value.Sum(p => p.Missed).ToString(CultureInfo.InvariantCulture),
                phase.Value.Select(p => p.GameFps).OrderBy(f => f).ElementAt(phase.Value.Count / 2),
                phase.Value.Max(p => p.GameMaxMs),
                phase.Value.Select(p => p.RendersPerSecond).OrderBy(f => f).ElementAt(phase.Value.Count / 2));
        }

        Console.WriteLine("  missed: desktop updates the recorder did not acquire, over all cycles. game worst:");
        Console.WriteLine("  the game's longest frame in any cycle.");

        Console.WriteLine("  changed/s: acquired desktop updates in which sampled card pixels changed. Missed updates");
        Console.WriteLine("  make this a lower bound. wpf/s counts callbacks, not displayed frames or VRR scanouts.");
        if (_noCapture) Console.WriteLine("  Capture disabled: n/a is unmeasured, not zero notification FPS.");
    }

    /// <summary>
    /// Undoes the quiet scope's stand-downs for --animate-motion: the background and glow keep
    /// animating while the card slides.
    /// </summary>
    private static void ResumeAnimations(Grid host)
    {
        _plugin.GetType("PlayniteAchievements.Views.Helpers.AsyncImage").GetMethod("ResumeGifAnimationsUnder", Flags)?.Invoke(null, new object[] { host });
        _plugin.GetType("PlayniteAchievements.Views.Helpers.RarityGlowPulse").GetMethod("ResumeUnder", Flags)?.Invoke(null, new object[] { host });
    }

    private static async Task CompareAsync(object persisted)
    {
        var a = await BuildCardAsync(persisted, _templatePath, left: true);
        var b = await BuildCardAsync(persisted, _comparePath, left: false);

        // --overlay with --compare: B is the split card, its front window stacked over it.
        var front = _overlayPath != null ? await BuildCardAsync(persisted, _overlayPath, left: false, backdropUnder: false) : null;
        var asyncImage = _plugin.GetType("PlayniteAchievements.Views.Helpers.AsyncImage");
        var pause = asyncImage.GetMethod("PauseGifAnimationsUnder", Flags);
        pause.Invoke(null, new object[] { a.Host });
        pause.Invoke(null, new object[] { b.Host });

        // The ray burst runs on its own clock, so two cards never show it at the same phase; it is
        // the same element in both templates and is hidden for the comparison.
        HideRays(a.Host);
        HideRays(b.Host);
        await Frames(10);
        await Task.Delay(500);

        var pa = Capture(a.Rect);
        var pb = Capture(b.Rect);
        if (_snapshot != null)
        {
            SaveCapture(a.Rect, _snapshot + ".a.png");
            SaveCapture(b.Rect, _snapshot + ".b.png");
        }
        if (pa.Length != pb.Length)
        {
            Console.WriteLine("window sizes differ: A {0}x{1}, B {2}x{3}",
                a.Rect.Right - a.Rect.Left, a.Rect.Bottom - a.Rect.Top, b.Rect.Right - b.Rect.Left, b.Rect.Bottom - b.Rect.Top);
        }
        else
        {
            var differing = 0;
            var over2 = 0;
            var max = 0;
            for (var i = 0; i < pa.Length; i++)
            {
                if (pa[i] == pb[i])
                {
                    continue;
                }

                differing++;
                var d = 0;
                for (var s = 0; s < 24; s += 8)
                {
                    d = Math.Max(d, Math.Abs(((pa[i] >> s) & 255) - ((pb[i] >> s) & 255)));
                }

                max = Math.Max(max, d);
                if (d > 2)
                {
                    over2++;
                }
            }

            Console.WriteLine("B vs A on screen: {0} of {1} pixels differ, {2} by more than 2 levels, max {3} levels",
                differing, pa.Length, over2, max);
        }

        a.Window.Close();
        b.Window.Close();
    }

    /// <summary>
    /// Times every frame of a GIF or WebM through the plugin's own canvas (the work GifPlayer does
    /// on the thread pool per frame), three passes, against each frame's own display duration.
    /// </summary>
    private static void DecodeTiming(string path)
    {
        var payload = File.ReadAllBytes(path);
        var webm = _plugin.GetType("PlayniteAchievements.Services.Images.Webm.WebmContainer");
        var isWebm = (bool)webm.GetMethod("HasSignature", Flags).Invoke(null, new object[] { payload });
        var parse = Stopwatch.StartNew();
        object canvas;
        if (isWebm)
        {
            var image = webm.GetMethod("Parse", Flags).Invoke(null, new object[] { payload });
            canvas = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Views.Helpers.Gif.WebmCanvas"), Flags, null, new[] { image }, null);
        }
        else
        {
            var image = _plugin.GetType("PlayniteAchievements.Views.Helpers.Gif.GifImage").GetMethod("Parse", Flags).Invoke(null, new object[] { payload });
            canvas = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Views.Helpers.Gif.GifCanvas"), Flags, null, new[] { image }, null);
        }

        var parseMs = parse.Elapsed.TotalMilliseconds;
        var type = canvas.GetType();
        var render = type.GetMethod("Render", Flags);
        var delay = type.GetMethod("GetDelayMs", Flags);
        var count = (int)type.GetProperty("FrameCount", Flags).GetValue(canvas);
        var width = (int)type.GetProperty("Width", Flags).GetValue(canvas);
        var height = (int)type.GetProperty("Height", Flags).GetValue(canvas);
        var first = Stopwatch.StartNew();
        render.Invoke(canvas, new object[] { 0 });
        var firstMs = first.Elapsed.TotalMilliseconds;

        var times = new List<double>();
        var misses = 0;
        var totalDelay = 0d;
        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = 0; i < count; i++)
            {
                var sw = Stopwatch.StartNew();
                render.Invoke(canvas, new object[] { i });
                var ms = sw.Elapsed.TotalMilliseconds;
                times.Add(ms);
                var due = (int)delay.Invoke(canvas, new object[] { i });
                totalDelay += due;
                if (ms > due)
                {
                    misses++;
                }
            }
        }

        times.Sort();
        Func<double, double> pct = q => times[Math.Min(times.Count - 1, (int)(q * times.Count))];
        Console.WriteLine("{0}: {1}x{2}, {3} frames, {4:0.0} fps nominal, parse {5:0.0} ms, first frame {6:0.0} ms",
            isWebm ? "WebM" : "GIF", width, height, count, 1000.0 * count * 3 / totalDelay, parseMs, firstMs);
        Console.WriteLine("per-frame decode: mean {0:0.00} ms, p50 {1:0.00}, p95 {2:0.00}, p99 {3:0.00}, max {4:0.00}; {5} of {6} exceeded their frame's duration",
            times.Average(), pct(0.5), pct(0.95), pct(0.99), times[times.Count - 1], misses, times.Count);
        ((IDisposable)canvas).Dispose();
    }

    private static void SaveCapture(Rect32 r, string path)
    {
        using (var bitmap = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
        {
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
            }

            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    /// <summary>
    /// For --still-background: pauses the background again, since ending a slide's quiet scope
    /// resumes it.
    /// </summary>
    private static void StillBackground(Grid host)
    {
        if (_stillBackground)
        {
            _plugin.GetType("PlayniteAchievements.Views.Helpers.AsyncImage").GetMethod("PauseGifAnimationsUnder", Flags).Invoke(null, new object[] { host });
        }

        if (_stillPulse)
        {
            var pause = _plugin.GetType("PlayniteAchievements.Views.Helpers.RarityGlowPulse").GetMethod("PauseUnder", Flags);
            pause.Invoke(null, new object[] { host });
            if (_overlay != null) pause.Invoke(null, new object[] { _overlay.Host });
        }
    }

    private static bool IsCardBurst(DependencyObject burst)
    {
        var role = _plugin.GetType("PlayniteAchievements.Views.Helpers.ToastLayer")
            .GetMethod("GetRole", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { burst });
        return role.ToString() == "Rays";
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void HideRays(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child.GetType().Name == "RarityRayBurst" && child is UIElement element &&
                (_hideRays == "all" || (_hideRays == "card") == IsCardBurst(child)))
            {
                element.Visibility = Visibility.Hidden;
            }

            HideRays(child);
        }
    }

    private static int[] Capture(Rect32 r)
    {
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        using (var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
            }

            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new int[w * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bitmap.UnlockBits(data);
            return px;
        }
    }

    private static void InstallLocalization(Assembly sdk, Application app)
    {
        var enUs = Path.Combine(_repo, "source", "Localization", "en_US.xaml");
        var dictionary = (ResourceDictionary)XamlReader.Load(File.OpenRead(enUs));
        app.Resources.MergedDictionaries.Add(dictionary);
        var strings = new Dictionary<string, string>();
        foreach (var key in dictionary.Keys)
        {
            if (key is string k && dictionary[k] is string v)
            {
                strings[k] = v;
            }
        }

        var providerType = sdk.GetType("Playnite.SDK.ResourceProvider");
        var set = providerType.GetMethod("SetGlobalProvider", BindingFlags.Static | BindingFlags.NonPublic);
        set?.Invoke(null, new[] { StringProvider.Create(sdk.GetType("Playnite.SDK.IResourceProvider"), strings) });
    }

    private static double DisplayPeriodMs()
    {
        var info = new DwmTimingInfo { cbSize = (uint)Marshal.SizeOf(typeof(DwmTimingInfo)) };
        return DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0 && info.rateRefreshNum > 0
            ? 1000.0 * info.rateRefreshDen / info.rateRefreshNum
            : 1000.0 / 60;
    }

    private static Task Frames(int n)
    {
        var tcs = new TaskCompletionSource<bool>();
        var count = 0;
        EventHandler h = null;
        h = (s, e) =>
        {
            if (++count >= n)
            {
                CompositionTarget.Rendering -= h;
                tcs.TrySetResult(true);
            }
        };
        CompositionTarget.Rendering += h;
        return tcs.Task;
    }

    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name);
        field.SetValue(target, value);
    }

    private struct PhaseStats
    {
        public double ChangedPerSecond;
        public double PresentsPerSecond;
        public double LongestGapMs;
        public double GameFps;
        public double GameMaxMs;
        public int Missed;
        public double RendersPerSecond;
    }

    /// <summary>One GameLoad report: when, frames presented so far, and its longest frame since the last.</summary>
    private struct GameSample
    {
        public long Time;
        public long Presented;
        public long MaxFrameTicks;
    }

    private static PhaseStats WithGame(PhaseStats stats, long from, long to)
    {
        stats.GameFps = GameFps(from, to);
        stats.GameMaxMs = GameMaxMs(from, to);
        lock (_renders)
        {
            stats.RendersPerSecond = _renders.Count(t => t >= from && t <= to) * (double)Stopwatch.Frequency / (to - from);
        }
        return stats;
    }

    /// <summary>The game's presents per second over [from, to], from its reports; 0 without a game.</summary>
    private static double GameFps(long from, long to)
    {
        lock (_gameFrames)
        {
            var span = _gameFrames.Where(f => f.Time >= from && f.Time <= to).ToList();
            if (span.Count < 2)
            {
                return 0;
            }

            var first = span[0];
            var last = span[span.Count - 1];
            return (last.Presented - first.Presented) * (double)Stopwatch.Frequency / (last.Time - first.Time);
        }
    }

    /// <summary>The game's longest frame, present to present, over [from, to].</summary>
    private static double GameMaxMs(long from, long to)
    {
        lock (_gameFrames)
        {
            var span = _gameFrames.Where(f => f.Time > from && f.Time <= to).ToList();
            return span.Count == 0 ? 0 : span.Max(f => f.MaxFrameTicks) * 1000.0 / Stopwatch.Frequency;
        }
    }

    private struct Frame
    {
        public long Time;
        public bool Changed;

        // Desktop updates since the last acquired frame; above 1, the recorder missed some.
        public int Accumulated;
    }

    private interface IRecorder : IDisposable
    {
        void Start();

        PhaseStats Stats(long from, long to);
    }

    private static PhaseStats Summarize(List<Frame> all, long from, long to)
    {
        var frames = all.Where(f => f.Time >= from && f.Time <= to).ToList();
        var seconds = (to - from) / (double)Stopwatch.Frequency;
        var changes = frames.Where(f => f.Changed).Select(f => f.Time).ToList();
        var gap = 0d;
        var last = from;
        foreach (var t in changes)
        {
            gap = Math.Max(gap, (t - last) * 1000.0 / Stopwatch.Frequency);
            last = t;
        }

        return new PhaseStats
        {
            ChangedPerSecond = changes.Count / seconds,
            PresentsPerSecond = frames.Count / seconds,
            LongestGapMs = Math.Max(gap, (to - last) * 1000.0 / Stopwatch.Frequency),
            Missed = frames.Sum(f => Math.Max(0, f.Accumulated - 1)),
        };
    }

    /// <summary>
    /// For --no-capture: records nothing, so the game's frame rate is measured with no capture
    /// work on the GPU at all.
    /// </summary>
    private sealed class NullRecorder : IRecorder
    {
        public void Start()
        {
        }

        public PhaseStats Stats(long from, long to) => new PhaseStats();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A <see cref="RegionRecorder"/> in a child process (this tool with --record) at normal GPU
    /// priority, so the bench's --gpu-priority raises only the card's rendering, not the capture's
    /// copies and readbacks. Frames arrive over the child's standard output.
    /// </summary>
    private sealed class RemoteRecorder : IRecorder
    {
        private readonly List<Frame> _frames = new List<Frame>();
        private readonly Process _process;

        public RemoteRecorder(Rect32 r)
        {
            var exe = Assembly.GetExecutingAssembly().Location;
            _process = Process.Start(new ProcessStartInfo(exe, string.Format(CultureInfo.InvariantCulture,
                "--record {0},{1},{2},{3},{4}", r.Left, r.Top, r.Right, r.Bottom, _recorderPriority))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            _process.OutputDataReceived += (o, d) =>
            {
                var parts = d.Data?.Split(' ');
                if (parts != null && parts.Length == 4 && parts[0] == "r")
                {
                    lock (_frames)
                    {
                        _frames.Add(new Frame
                        {
                            Time = long.Parse(parts[1], CultureInfo.InvariantCulture),
                            Changed = parts[2] == "1",
                            Accumulated = int.Parse(parts[3], CultureInfo.InvariantCulture),
                        });
                    }
                }
            };
            _process.BeginOutputReadLine();
        }

        public void Start()
        {
            // The child starts capturing at once; wait for its first frame.
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 3000)
            {
                lock (_frames)
                {
                    if (_frames.Count > 0)
                    {
                        return;
                    }
                }

                Thread.Sleep(20);
            }
        }

        public PhaseStats Stats(long from, long to)
        {
            // Frames for the span may still be in the pipe.
            Thread.Sleep(100);
            lock (_frames)
            {
                return Summarize(_frames, from, to);
            }
        }

        public void Dispose()
        {
            try { _process.Kill(); } catch { }
        }
    }

    /// <summary>The --record child: streams "r time changed accumulated" per frame until killed.</summary>
    private static int RecordChild(string spec)
    {
        var p = spec.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        D3DKMTSetProcessSchedulingPriorityClass(Process.GetCurrentProcess().Handle, p.Length > 4 ? p[4] : 2);
        var recorder = new RegionRecorder(new Rect32 { Left = p[0], Top = p[1], Right = p[2], Bottom = p[3] });
        var output = Console.Out;
        recorder.Sink = f =>
        {
            output.WriteLine("r {0} {1} {2}", f.Time, f.Changed ? 1 : 0, f.Accumulated);
            output.Flush();
        };
        recorder.Start();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>
    /// Desktop duplication of the card's screen rectangle. For every presented frame it records the
    /// present time and whether the rectangle's pixels changed (sampled on a grid).
    /// </summary>
    private sealed class RegionRecorder : IRecorder
    {
        private readonly Rect32 _rect;
        private readonly List<Frame> _frames = new List<Frame>();

        /// <summary>Receives every frame on the capture thread, for --record to stream out.</summary>
        public Action<Frame> Sink;
        private DXGI.Factory1 _factory;
        private DXGI.Adapter1 _adapter;
        private D3D11.Device _device;
        private DXGI.OutputDuplication _duplication;
        private D3D11.Texture2D _staging;
        private SharpDX.Mathematics.Interop.RawRectangle _bounds;
        private Thread _thread;
        private volatile bool _stop;

        public RegionRecorder(Rect32 rect)
        {
            _rect = rect;
            _factory = new DXGI.Factory1();
            _adapter = _factory.GetAdapter1(0);
            for (var o = 0; o < _adapter.GetOutputCount(); o++)
            {
                using (var output = _adapter.GetOutput(o))
                {
                    var b = output.Description.DesktopBounds;
                    if (rect.Left < b.Left || rect.Left >= b.Right || rect.Top < b.Top || rect.Top >= b.Bottom)
                    {
                        continue;
                    }

                    _bounds = b;
                    _device = new D3D11.Device(_adapter);
                    using (var output1 = output.QueryInterface<DXGI.Output1>())
                    {
                        _duplication = output1.DuplicateOutput(_device);
                    }

                    var w = Math.Min(rect.Right, b.Right) - rect.Left;
                    var h = Math.Min(rect.Bottom, b.Bottom) - rect.Top;
                    _staging = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
                    {
                        Width = w,
                        Height = h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new DXGI.SampleDescription(1, 0),
                        Usage = D3D11.ResourceUsage.Staging,
                        CpuAccessFlags = D3D11.CpuAccessFlags.Read,
                    });
                    return;
                }
            }

            throw new InvalidOperationException("no output contains the card");
        }

        public void Start()
        {
            _thread = new Thread(Pump) { IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        private void Pump()
        {
            var context = _device.ImmediateContext;
            var w = _staging.Description.Width;
            var h = _staging.Description.Height;
            var region = new D3D11.ResourceRegion(_rect.Left - _bounds.Left, _rect.Top - _bounds.Top, 0,
                _rect.Left - _bounds.Left + w, _rect.Top - _bounds.Top + h, 1);
            long previous = 0;
            while (!_stop)
            {
                var hr = _duplication.TryAcquireNextFrame(50, out var info, out var resource);
                if (hr.Failure)
                {
                    continue;
                }

                try
                {
                    if (info.LastPresentTime == 0)
                    {
                        continue;
                    }

                    using (var texture = resource.QueryInterface<D3D11.Texture2D>())
                    {
                        context.CopySubresourceRegion(texture, 0, region, _staging, 0);
                    }

                    var box = context.MapSubresource(_staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                    long hash = 17;
                    try
                    {
                        for (var y = 0; y < h; y += 3)
                        {
                            var row = box.DataPointer + (y * box.RowPitch);
                            for (var x = 0; x < w; x += 3)
                            {
                                hash = (hash * 31) ^ SharpDX.Utilities.Read<int>(row + (x * 4));
                            }
                        }
                    }
                    finally
                    {
                        context.UnmapSubresource(_staging, 0);
                    }

                    var frame = new Frame { Time = info.LastPresentTime, Changed = hash != previous, Accumulated = info.AccumulatedFrames };
                    lock (_frames)
                    {
                        _frames.Add(frame);
                    }

                    Sink?.Invoke(frame);

                    previous = hash;
                }
                finally
                {
                    resource?.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
        }

        public PhaseStats Stats(long from, long to)
        {
            lock (_frames)
            {
                return Summarize(_frames, from, to);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(2000);
            _staging?.Dispose();
            _duplication?.Dispose();
            _device?.Dispose();
            _adapter?.Dispose();
            _factory?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DwmTimingInfo
    {
        public uint cbSize;
        public uint rateRefreshNum, rateRefreshDen;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNum, rateComposeDen;
        public ulong qpcVBlank, cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose, cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete;
        public ulong cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending;
        public ulong cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed, cRefreshNextPresented;
        public ulong cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo info);

    /// <summary>An IResourceProvider over the English strings, built at runtime against the SDK.</summary>
    public static class StringProvider
    {
        private static Dictionary<string, string> _strings;

        public static object Create(Type providerInterface, Dictionary<string, string> strings)
        {
            _strings = strings;
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("BenchStrings"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("main").DefineType(
                "BenchStringsImpl", TypeAttributes.Public | TypeAttributes.Class, typeof(object), new[] { providerInterface });
            foreach (var method in providerInterface.GetMethods())
            {
                var impl = type.DefineMethod(method.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray());
                var il = impl.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(StringProvider).GetMethod(
                    method.ReturnType == typeof(string) ? "GetString" : "GetResource"));
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                type.DefineMethodOverride(impl, method);
            }

            return Activator.CreateInstance(type.CreateType());
        }

        public static string GetString(string key) => _strings.TryGetValue(key, out var v) ? v : "<!" + key + "!>";

        public static object GetResource(string key) => _strings.TryGetValue(key, out var v) ? v : null;
    }

    public static class ConsoleLogger
    {
        public static object Create(Type loggerInterface)
        {
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("BenchLogger"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("main").DefineType(
                "BenchLoggerImpl", TypeAttributes.Public | TypeAttributes.Class, typeof(object), new[] { loggerInterface });
            var write = typeof(ConsoleLogger).GetMethod("Write");
            foreach (var method in loggerInterface.GetMethods())
            {
                var parameters = method.GetParameters();
                var impl = type.DefineMethod(method.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType, parameters.Select(p => p.ParameterType).ToArray());
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

            Console.WriteLine("[plugin " + level + "] " + string.Join(" ", args.Where(a => a != null && !(a is Exception))));
        }
    }
}
