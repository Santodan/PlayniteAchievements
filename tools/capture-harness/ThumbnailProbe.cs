// Checks whether DWM can present a cloaked WPF notification window through a thumbnail in a second,
// surfaceless window, pixel for pixel, so motion can be done by DWM instead of by WPF frames.
//
//   ThumbnailProbe.exe [--presenter noredirect|layered] [--dump <dir>]
//
// A card (translucent fill, blurred glow, shadowed text) is shown in a DWM-composited WPF window, as
// the plugin shows a notification, over a solid backdrop, and the screen is captured. The window is
// then cloaked and a presenter window at the same rectangle shows its DWM thumbnail; the screen is
// captured again and compared. Further captures move the thumbnail and halve its opacity.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using WF = System.Windows.Forms;

public static class ThumbnailProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectI { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public int Flags;
        public RectI Destination;
        public RectI Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    private const int TnpRectDestination = 1, TnpRectSource = 2, TnpOpacity = 4, TnpVisible = 8, TnpSourceClientAreaOnly = 16;

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbnailProperties props);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    private sealed class Presenter : WF.Form
    {
        private readonly bool _layered;

        public Presenter(bool layered)
        {
            _layered = layered;
            FormBorderStyle = WF.FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = WF.FormStartPosition.Manual;
            TopMost = true;
            BackColor = System.Drawing.Color.Black;
        }

        protected override bool ShowWithoutActivation => true;

        protected override WF.CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // TOPMOST | TRANSPARENT | TOOLWINDOW | NOACTIVATE, plus LAYERED or NOREDIRECTIONBITMAP.
                p.ExStyle |= 0x8 | 0x20 | 0x80 | 0x08000000 | (_layered ? 0x80000 : 0x00200000);
                return p;
            }
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var layered = false;
        string dump = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--presenter") layered = args[++i] == "layered";
            else if (args[i] == "--dump") dump = args[++i];
        }

        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var code = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) =>
        {
            try { await RunAsync(layered, dump); }
            catch (Exception ex) { Console.WriteLine("FAILED: " + ex); code = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return code;
    }

    private static async Task RunAsync(bool layered, string dump)
    {
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 30, 160)),
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 80, Top = 80, Width = 900, Height = 600,
        };
        backdrop.Show();

        var source = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, AllowsTransparency = false,
            Background = System.Windows.Media.Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 140, Top = 140, Width = 640, Height = 320,
            Content = BuildCard(),
        };
        WindowChrome.SetWindowChrome(source, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1), CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        source.SourceInitialized += (s, e) =>
        {
            var h = new WindowInteropHelper(source).Handle;
            HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(h, ref m);
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000);
            var corner = 1;
            DwmSetWindowAttribute(h, 33, ref corner, 4);
            var border = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(h, 34, ref border, 4);
        };
        source.Show();
        await Settle();

        var hwnd = new WindowInteropHelper(source).Handle;
        GetWindowRect(hwnd, out var rect);
        var capture = new RectI { Left = rect.Left - 40, Top = rect.Top - 40, Right = rect.Right + 120, Bottom = rect.Bottom + 120 };
        var live = Capture(capture);

        var cloak = 1;
        DwmSetWindowAttribute(hwnd, 13, ref cloak, 4);
        await Settle();
        var cloaked = Capture(capture);
        Report("cloaked source vs live (expect a difference: the card is gone)", live, cloaked);

        var presenter = new Presenter(layered);
        presenter.Bounds = new Rectangle(capture.Left, capture.Top, capture.Right - capture.Left, capture.Bottom - capture.Top);
        presenter.Show();
        if (layered)
        {
            // Colour key on the form's black fill: only the thumbnail remains.
            Console.WriteLine("layered attrs: " + SetLayeredWindowAttributes(presenter.Handle, 0, 0, 1));
        }

        var hr = DwmRegisterThumbnail(presenter.Handle, hwnd, out var thumb);
        Console.WriteLine("presenter {0}, register 0x{1:X}", layered ? "layered" : "noredirect", hr);
        var w = rect.Right - rect.Left;
        var h2 = rect.Bottom - rect.Top;
        Func<int, int, byte, int> place = (dx, dy, opacity) =>
        {
            var p = new ThumbnailProperties
            {
                Flags = TnpRectDestination | TnpOpacity | TnpVisible | TnpSourceClientAreaOnly,
                Destination = new RectI { Left = 40 + dx, Top = 40 + dy, Right = 40 + dx + w, Bottom = 40 + dy + h2 },
                Opacity = opacity, Visible = true, SourceClientAreaOnly = true,
            };
            return DwmUpdateThumbnailProperties(thumb, ref p);
        };

        Console.WriteLine("update 0x{0:X}", place(0, 0, 255));
        await Settle();
        var thumbAtRest = Capture(capture);
        Report("thumbnail at rest vs live", live, thumbAtRest);

        place(37, 53, 255);
        await Settle();
        var moved = Capture(capture);
        Report("thumbnail moved (37,53) vs live shifted", Shift(live, 37, 53), moved, 37, 53);

        place(0, 0, 128);
        await Settle();
        var faded = Capture(capture);
        Report("thumbnail at opacity 128 vs live (informational)", live, faded);

        if (dump != null)
        {
            Directory.CreateDirectory(dump);
            live.Save(Path.Combine(dump, "live.png"));
            cloaked.Save(Path.Combine(dump, "cloaked.png"));
            thumbAtRest.Save(Path.Combine(dump, "thumb.png"));
            moved.Save(Path.Combine(dump, "moved.png"));
            faded.Save(Path.Combine(dump, "faded.png"));
        }

        DwmUnregisterThumbnail(thumb);
        presenter.Close();
        source.Close();
        backdrop.Close();
    }

    private static UIElement BuildCard()
    {
        var text = new TextBlock
        {
            Text = "Achievement unlocked - The Richest Broke Person",
            FontSize = 22, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(24, 20, 24, 0),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Color = Colors.Black, Opacity = 0.8 },
        };
        var sub = new TextBlock
        {
            Text = "Rare - 4.2% of players", FontSize = 15, Margin = new Thickness(24, 60, 24, 0),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 255, 220, 120)),
        };
        var fill = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new LinearGradientBrush(System.Windows.Media.Color.FromArgb(150, 20, 30, 60), System.Windows.Media.Color.FromArgb(235, 60, 20, 80), 30),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 120, 200, 255)), BorderThickness = new Thickness(2),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Color = Colors.Cyan, Opacity = 0.9 },
        };
        var grid = new Grid { Margin = new Thickness(60) };
        grid.Children.Add(fill);
        grid.Children.Add(text);
        grid.Children.Add(sub);
        return grid;
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            DwmFlush();
        }

        await Task.Delay(250);
    }

    private static Bitmap Capture(RectI r)
    {
        var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        }

        return bmp;
    }

    private static Bitmap Shift(Bitmap src, int dx, int dy)
    {
        var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImageUnscaled(src, dx, dy);
        }

        return bmp;
    }

    // Compares inside the region both images cover (skipX/skipY exclude the strip a shift leaves empty).
    private static void Report(string label, Bitmap a, Bitmap b, int skipX = 0, int skipY = 0)
    {
        int differing = 0, max = 0, total = 0;
        for (var y = skipY; y < a.Height - 80; y++)
        {
            for (var x = skipX; x < a.Width - 80; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                var d = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                total++;
                if (d > 0) differing++;
                if (d > max) max = d;
            }
        }

        Console.WriteLine("{0}: {1} of {2} px differ, max {3} levels", label, differing, total, max);
    }
}
