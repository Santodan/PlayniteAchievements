// Answers the question the storyboard slide was made for: how close to the monitor's refresh rate does
// the notification's motion actually run, and which mechanism gets closest.
//
// The metric is the composition rate sustained DURING the motion, not the animation's own value changes.
// A WPF timeline advances once per composed frame by construction, so counting value changes only ever
// re-measures the render loop. What can actually go wrong is the render loop itself slowing down,
// because each frame of this motion costs real work: the toast is a per-pixel-alpha layered window, so
// every frame is a full surface update to the OS, and the old mechanism additionally issued a
// cross-process SetWindowPos per frame.
//
// Variants are measured against the display's own period read from the OS, never against the run's own
// mean -- deriving the target from the run being judged hands a starved run a lenient target.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

internal static class SlideCadenceProbe
{
    private const int SlideDurationMs = 240;
    private const double CardWidthDip = 442d;
    private const double CardHeightDip = 138d;
    private const double TravelPaddingDip = 40d;

    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    private enum Mechanism
    {
        /// <summary>The pre-storyboard slide: move the layered HWND once per composed frame.</summary>
        WindowMove,

        /// <summary>What ships now: a storyboard on the card host's translate, window stationary.</summary>
        Transform,

        /// <summary>As shipped, plus a bitmap cache on the card so a frame is a blit, not a re-raster.</summary>
        TransformCached,

        /// <summary>
        /// Stationary window sized to the card only. The card clips, so this is not a usable mode -- it
        /// isolates how much of the per-frame cost is the padded window's larger layered surface.
        /// </summary>
        TransformNoPadding,

        /// <summary>
        /// As shipped, plus the overlay-track sampling a recording-enabled wave really does on the UI
        /// thread during the slide: a RenderTargetBitmap of the card, a memcmp against the previous
        /// frame and an XOR, paced at the recording rate. This is the only thing in the running app
        /// that competes with the slide for the render loop.
        /// </summary>
        TransformWithSampling,
    }

    /// <summary>Recording rate the sampling variant paces itself at, mirroring RecordingFps.</summary>
    private const int RecordingFps = 60;

    private sealed class Result
    {
        public Mechanism Mechanism;
        public int Frames;
        public double SpanMs;
        public double MedianMs;
        public double MaxGapMs;
    }

    private static double _displayPeriodMs = 1000d / 60d;

    [STAThread]
    private static int Main(string[] args)
    {
        var repeats = 5;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repeats" && i + 1 < args.Length)
            {
                repeats = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture));
            }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var results = new List<Result>();

        app.Startup += async (s, e) =>
        {
            try
            {
                _displayPeriodMs = ResolveDisplayPeriodMs();
                Console.WriteLine(
                    "Display composes every {0:0.00} ms ({1:0.0} Hz). Slide is {2} ms.",
                    _displayPeriodMs, 1000d / _displayPeriodMs, SlideDurationMs);
                Console.WriteLine(
                    "Ideal frame count for a slide at that rate: {0:0}.", SlideDurationMs / _displayPeriodMs);
                Console.WriteLine();

                foreach (Mechanism mechanism in Enum.GetValues(typeof(Mechanism)))
                {
                    for (var run = 0; run < repeats; run++)
                    {
                        var result = await RunOne(mechanism);
                        if (result != null)
                        {
                            results.Add(result);
                        }
                    }
                }

                Report(results);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("probe failed: " + ex);
            }
            finally
            {
                app.Shutdown();
            }
        };

        app.Run();
        return 0;
    }

    private static async System.Threading.Tasks.Task<Result> RunOne(Mechanism mechanism)
    {
        var padded = mechanism != Mechanism.TransformNoPadding && mechanism != Mechanism.WindowMove;
        var sampling = mechanism == Mechanism.TransformWithSampling;
        var travel = CardHeightDip + TravelPaddingDip;

        var slide = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(slide);

        var card = BuildCard();
        if (mechanism == Mechanism.TransformCached)
        {
            // The card is static for the whole slide, so caching it turns each frame from a re-raster of
            // text and two shadow effects into a transformed blit of one texture.
            card.CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = false };
        }

        card.Margin = padded ? new Thickness(0, 0, 0, travel) : new Thickness(0);

        var host = new Grid
        {
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            RenderTransform = group,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        host.Children.Add(card);

        var window = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Left = 120,
            Top = 120,
            Opacity = 1,
            Content = host,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            // Let the window pay its first-paint cost before anything is timed, exactly as the plugin's
            // warm-frame wait does; otherwise every mechanism's first run measures window creation.
            await WaitFrames(3, 300);

            var ticks = new TickCounter();
            var finished = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            if (mechanism == Mechanism.WindowMove)
            {
                var scale = RenderScale(window);
                var distancePx = (int)Math.Round(travel * scale);
                var restY = 120 + distancePx;
                var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;

                EventHandler tick = null;
                tick = (s, e) =>
                {
                    if (!ticks.TryAdvance(e, out var elapsed))
                    {
                        return;
                    }

                    var t = Math.Min(1.0, elapsed / SlideDurationMs);
                    var y = (int)Math.Round((restY + distancePx) + ((restY - (restY + distancePx)) * ease.Ease(t)));
                    SetWindowPos(hwnd, IntPtr.Zero, 120, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    if (t >= 1.0)
                    {
                        CompositionTarget.Rendering -= tick;
                        finished.TrySetResult(true);
                    }
                };

                CompositionTarget.Rendering += tick;
                try
                {
                    await System.Threading.Tasks.Task.WhenAny(
                        finished.Task, System.Threading.Tasks.Task.Delay(SlideDurationMs + 2000));
                }
                finally
                {
                    CompositionTarget.Rendering -= tick;
                }
            }
            else
            {
                var animation = new DoubleAnimation
                {
                    From = travel,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(SlideDurationMs)),
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
                    FillBehavior = FillBehavior.HoldEnd,
                };
                Storyboard.SetTarget(animation, host);
                Storyboard.SetTargetProperty(
                    animation,
                    new PropertyPath(
                        "(0).(1)[1].(2)",
                        UIElement.RenderTransformProperty,
                        TransformGroup.ChildrenProperty,
                        TranslateTransform.YProperty));

                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);

                var sampleIntervalMs = 1000d / RecordingFps;
                var dueTolerance = _displayPeriodMs / 2d;
                var nextDueMs = sampleIntervalMs;
                byte[] previous = null;

                EventHandler tick = null;
                tick = (s, e) =>
                {
                    if (!ticks.TryAdvance(e, out var elapsed))
                    {
                        return;
                    }

                    if (sampling && elapsed >= nextDueMs - dueTolerance)
                    {
                        do
                        {
                            nextDueMs += sampleIntervalMs;
                        }
                        while (nextDueMs <= elapsed);

                        previous = SampleCard(card, previous);
                    }

                    if (elapsed >= SlideDurationMs)
                    {
                        CompositionTarget.Rendering -= tick;
                        finished.TrySetResult(true);
                    }
                };

                slide.Y = 0;
                CompositionTarget.Rendering += tick;
                storyboard.Begin(host, true);
                try
                {
                    await System.Threading.Tasks.Task.WhenAny(
                        finished.Task, System.Threading.Tasks.Task.Delay(SlideDurationMs + 2000));
                }
                finally
                {
                    CompositionTarget.Rendering -= tick;
                }
            }

            return new Result
            {
                Mechanism = mechanism,
                Frames = ticks.Frames,
                SpanMs = ticks.SpanMs,
                MedianMs = ticks.MedianIntervalMs,
                MaxGapMs = ticks.MaxIntervalMs,
            };
        }
        finally
        {
            try
            {
                window.Close();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// What ToastOverlayTrackRecorder costs the render loop per sampled frame: rasterise the card
    /// through a VisualBrush, copy the pixels out, compare against the previous frame and XOR it.
    /// Returns the frame just taken, to be diffed against next time.
    /// </summary>
    private static byte[] SampleCard(FrameworkElement card, byte[] previous)
    {
        try
        {
            var w = card.ActualWidth > 0 ? card.ActualWidth : CardWidthDip;
            var h = card.ActualHeight > 0 ? card.ActualHeight : CardHeightDip;
            var pw = Math.Max(1, (int)Math.Ceiling(w));
            var ph = Math.Max(1, (int)Math.Ceiling(h));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var offset = VisualTreeHelper.GetOffset(card);
                dc.DrawRectangle(
                    new VisualBrush(card)
                    {
                        Stretch = Stretch.Fill,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(offset.X, offset.Y, w, h),
                    },
                    null,
                    new Rect(0, 0, w, h));
            }

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                pw, ph, 96.0, 96.0, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var stride = pw * 4;
            var buffer = new byte[stride * ph];
            rtb.CopyPixels(buffer, stride, 0);

            if (previous != null && previous.Length == buffer.Length)
            {
                var same = true;
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i] != previous[i])
                    {
                        same = false;
                        break;
                    }
                }

                if (!same)
                {
                    var delta = new byte[buffer.Length];
                    for (var i = 0; i < buffer.Length; i++)
                    {
                        delta[i] = (byte)(buffer[i] ^ previous[i]);
                    }

                    GC.KeepAlive(delta);
                }
            }

            return buffer;
        }
        catch
        {
            return previous;
        }
    }

    /// <summary>A card shaped like the real one in the ways that cost per frame: shadows and text.</summary>
    private static FrameworkElement BuildCard()
    {
        var text = new StackPanel { Margin = new Thickness(18, 12, 18, 12) };
        text.Children.Add(new TextBlock
        {
            Text = "Achievement unlocked",
            FontSize = 18,
            Foreground = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 4, Opacity = 0.8 },
        });
        text.Children.Add(new TextBlock
        {
            Text = "A reasonably long achievement description line",
            FontSize = 13,
            Foreground = Brushes.LightGray,
            Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 3, Opacity = 0.7 },
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 64,
            Height = 64,
            Margin = new Thickness(12),
            Background = new LinearGradientBrush(Colors.SteelBlue, Colors.MidnightBlue, 45),
            CornerRadius = new CornerRadius(8),
        });
        row.Children.Add(text);

        return new Border
        {
            Width = CardWidthDip,
            MinHeight = CardHeightDip,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 80)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 },
            Child = row,
            IsHitTestVisible = false,
        };
    }

    private static async System.Threading.Tasks.Task<int> WaitFrames(int frames, int timeoutMs)
    {
        var ticks = new TickCounter();
        var reached = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler tick = null;
        tick = (s, e) =>
        {
            if (ticks.TryAdvance(e, out _) && ticks.Frames >= frames)
            {
                reached.TrySetResult(true);
            }
        };

        CompositionTarget.Rendering += tick;
        try
        {
            await System.Threading.Tasks.Task.WhenAny(
                reached.Task, System.Threading.Tasks.Task.Delay(timeoutMs));
        }
        finally
        {
            CompositionTarget.Rendering -= tick;
        }

        return ticks.Frames;
    }

    private static double RenderScale(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var m = source?.CompositionTarget?.TransformToDevice;
        return m.HasValue && m.Value.M11 > 0 ? m.Value.M11 : 1.0;
    }

    private static double ResolveDisplayPeriodMs()
    {
        try
        {
            var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode) && devMode.dmDisplayFrequency > 1)
            {
                return 1000d / devMode.dmDisplayFrequency;
            }
        }
        catch
        {
        }

        return 1000d / 60d;
    }

    private static void Report(List<Result> results)
    {
        Console.WriteLine(
            "{0,-20} {1,7} {2,9} {3,9} {4,9} {5,9}",
            "mechanism", "frames", "medianMs", "sustained", "% of max", "maxGapMs");

        foreach (Mechanism mechanism in Enum.GetValues(typeof(Mechanism)))
        {
            var runs = results.FindAll(r => r.Mechanism == mechanism);
            if (runs.Count == 0)
            {
                continue;
            }

            // Median across runs, so one scheduling hiccup does not decide the verdict.
            runs.Sort((a, b) => a.MedianMs.CompareTo(b.MedianMs));
            var mid = runs[runs.Count / 2];
            var sustained = mid.MedianMs > 0 ? 1000d / mid.MedianMs : 0d;

            Console.WriteLine(
                "{0,-20} {1,7} {2,9:0.00} {3,7:0.0}Hz {4,8:0}% {5,9:0.0}",
                mechanism,
                mid.Frames,
                mid.MedianMs,
                sustained,
                100d * sustained / (1000d / _displayPeriodMs),
                mid.MaxGapMs);
        }

        Console.WriteLine();
        Console.WriteLine("  sustained is the composition rate held during the motion; % of max is that");
        Console.WriteLine("  against the display's own rate. TransformNoPadding is not a usable mode -- it");
        Console.WriteLine("  clips the card -- and is here only to price the padded window's larger surface.");
    }

    /// <summary>Counts distinct composed frames by their own composition timestamp.</summary>
    private sealed class TickCounter
    {
        private readonly System.Diagnostics.Stopwatch _fallback = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<double> _intervals = new List<double>();
        private bool _chosen;
        private bool _useRenderingTime;
        private double _firstMs;
        private double _lastMs = double.NegativeInfinity;

        public int Frames { get; private set; }

        public double SpanMs => Frames > 1 ? _lastMs - _firstMs : 0d;

        public double MaxIntervalMs { get; private set; }

        public double MedianIntervalMs
        {
            get
            {
                if (_intervals.Count == 0)
                {
                    return 0d;
                }

                var sorted = new List<double>(_intervals);
                sorted.Sort();
                return sorted[sorted.Count / 2];
            }
        }

        public bool TryAdvance(EventArgs e, out double elapsedMs)
        {
            elapsedMs = 0d;
            var renderingTime = (e as RenderingEventArgs)?.RenderingTime;
            if (!_chosen)
            {
                _useRenderingTime = renderingTime.HasValue;
                _chosen = true;
            }

            var nowMs = _useRenderingTime && renderingTime.HasValue
                ? renderingTime.Value.TotalMilliseconds
                : _fallback.Elapsed.TotalMilliseconds;
            if (nowMs <= _lastMs)
            {
                return false;
            }

            if (Frames == 0)
            {
                _firstMs = nowMs;
            }
            else
            {
                var interval = nowMs - _lastMs;
                _intervals.Add(interval);
                if (interval > MaxIntervalMs)
                {
                    MaxIntervalMs = interval;
                }
            }

            _lastMs = nowMs;
            Frames++;
            elapsedMs = nowMs - _firstMs;
            return true;
        }
    }
}
