using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// One-line-per-event diagnostics for the toast placement path, used to chase DPI/scaling
    /// clipping bugs that only reproduce on remote users' mixed-DPI or high-scale setups. Every
    /// method is wrapped so diagnostics can never throw into the toast pipeline, and every caller
    /// is gated behind <see cref="PerfScope.PerfTracingEnabled"/> so there is zero cost (and zero
    /// log noise) unless the compile-time tracing flag is on.
    ///
    /// The Playnite process is system-DPI-aware (its exe manifest declares no per-monitor
    /// awareness), so <c>GetDpiForMonitor</c> returns the system DPI for every monitor and cannot
    /// by itself reveal a mixed-DPI topology. The truth-teller logged here is each monitor's true
    /// physical resolution (EnumDisplaySettings dmPelsWidth/Height) compared against its
    /// virtualized <see cref="Screen.Bounds"/>.
    /// </summary>
    internal static class ToastPlacementDiagnostics
    {
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("shcore.dll")]
        private static extern int GetProcessDpiAwareness(IntPtr hprocess, out int awareness);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        /// <summary>
        /// One line per wave describing the display environment: Playnite mode, the process DPI
        /// awareness, and the topology of every monitor (virtualized bounds vs true physical
        /// resolution, so a mixed-DPI setup is visible).
        /// </summary>
        internal static string DescribeEnvironment(IPlayniteAPI api)
        {
            try
            {
                var mode = "unknown";
                try
                {
                    mode = api?.ApplicationInfo?.Mode.ToString() ?? "unknown";
                }
                catch
                {
                    mode = "unknown";
                }

                var awareness = DescribeAwareness();
                var sysScale = ResolveSystemScale().ToString("0.00", CultureInfo.InvariantCulture);

                var sb = new StringBuilder();
                sb.Append($"Toast env: mode={mode} awareness={awareness} sysScale={sysScale}");

                var index = 0;
                foreach (var screen in Screen.AllScreens)
                {
                    var bounds = screen.Bounds;
                    var deviceName = screen.DeviceName ?? "?";
                    var dpiForMon = "n/a";
                    var monHandle = MonitorHandleFor(bounds);
                    if (monHandle != IntPtr.Zero && TryGetDpiForMonitor(monHandle, out var dpiX))
                    {
                        dpiForMon = dpiX.ToString(CultureInfo.InvariantCulture);
                    }

                    var physText = "n/a";
                    var trueScale = "n/a";
                    if (TryGetPhysicalResolution(deviceName, out var physW, out var physH))
                    {
                        physText = $"{physW}x{physH}";
                        if (bounds.Width > 0)
                        {
                            trueScale = ((double)physW / bounds.Width).ToString("0.00", CultureInfo.InvariantCulture);
                        }
                    }

                    sb.Append($"; mon#{index} {deviceName}{(screen.Primary ? " primary" : string.Empty)} virt={bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height} work={screen.WorkingArea.Width}x{screen.WorkingArea.Height} phys={physText} dpiForMon={dpiForMon} trueScale~{trueScale}");
                    index++;
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "Toast env: diag-failed: " + ex.Message;
            }
        }

        /// <summary>
        /// One line per placement pass: where the toast was moved to and the coordinate spaces that
        /// drove it. The key cross-check the log enables is whether the toast's own HWND pixel width
        /// equals round(ActualWidth * M11); a mismatch is the SizeToContent + DPI&gt;100% clip bug.
        /// </summary>
        internal static string DescribePlacement(
            string stage,
            Window toast,
            IntPtr gameHwnd,
            Rectangle? gamePxRect,
            Rect area,
            string areaSource,
            string transformSource)
        {
            try
            {
                if (toast == null)
                {
                    return $"Toast place[{stage}]: diag-failed: null window";
                }

                var m11 = ResolveTransformM11(toast, out var resolvedSource);
                var effectiveTransformSource = string.IsNullOrEmpty(transformSource) ? resolvedSource : transformSource;

                var content = toast.Content as FrameworkElement;
                var desired = content?.DesiredSize ?? new System.Windows.Size(double.NaN, double.NaN);

                var toastHwnd = HandleFor(toast);
                var hwndText = "no-hwnd";
                var winMon = "?";
                if (toastHwnd != IntPtr.Zero && GetWindowRect(toastHwnd, out var wr))
                {
                    hwndText = $"({wr.Left},{wr.Top} {wr.Right - wr.Left}x{wr.Bottom - wr.Top})";
                    winMon = MonitorNameFor(toastHwnd);
                }

                var gamePxText = gamePxRect.HasValue
                    ? $"({gamePxRect.Value.Left},{gamePxRect.Value.Top} {gamePxRect.Value.Width}x{gamePxRect.Value.Height})"
                    : "none";
                var gameMon = gameHwnd != IntPtr.Zero ? MonitorNameFor(gameHwnd) : "none";

                return string.Format(CultureInfo.InvariantCulture,
                    "Toast place[{0}]: area={1} gamePx={2} dip=({3:0.0},{4:0.0} {5:0.0}x{6:0.0}) M11={7:0.0000} src={8} -> L={9:0.0} T={10:0.0} actual={11:0.0}x{12:0.0} desired={13:0.0}x{14:0.0} sizeToContent={15} hwndPx={16} winMon={17} gameMon={18}",
                    stage,
                    areaSource ?? "?",
                    gamePxText,
                    area.Left, area.Top, area.Width, area.Height,
                    m11,
                    effectiveTransformSource,
                    toast.Left, toast.Top,
                    toast.ActualWidth, toast.ActualHeight,
                    desired.Width, desired.Height,
                    toast.SizeToContent,
                    hwndText, winMon, gameMon);
            }
            catch (Exception ex)
            {
                return $"Toast place[{stage}]: diag-failed: " + ex.Message;
            }
        }

        private static string DescribeAwareness()
        {
            try
            {
                if (GetProcessDpiAwareness(IntPtr.Zero, out var awareness) == 0)
                {
                    switch (awareness)
                    {
                        case 0: return "Unaware";
                        case 1: return "SystemAware";
                        case 2: return "PerMonitorAware";
                        default: return "value" + awareness.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            catch
            {
                // shcore missing (pre-Win8.1) or call failed — fall through.
            }

            return "unknown";
        }

        private static double ResolveSystemScale()
        {
            try
            {
                var main = System.Windows.Application.Current?.MainWindow;
                var source = main != null ? PresentationSource.FromVisual(main) : null;
                if (source?.CompositionTarget != null)
                {
                    return source.CompositionTarget.TransformToDevice.M11;
                }
            }
            catch
            {
                // Fall through to unity.
            }

            return 1.0;
        }

        private static double ResolveTransformM11(Window toast, out string source)
        {
            try
            {
                var target = PresentationSource.FromVisual(toast)?.CompositionTarget;
                if (target != null)
                {
                    source = "window";
                    return target.TransformToDevice.M11;
                }

                var main = System.Windows.Application.Current?.MainWindow;
                var mainTarget = main != null ? PresentationSource.FromVisual(main)?.CompositionTarget : null;
                if (mainTarget != null)
                {
                    source = "mainwindow";
                    return mainTarget.TransformToDevice.M11;
                }
            }
            catch
            {
                // Fall through to identity.
            }

            source = "identity";
            return 1.0;
        }

        private static IntPtr HandleFor(Window window)
        {
            try
            {
                return new WindowInteropHelper(window).Handle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr MonitorHandleFor(Rectangle bounds)
        {
            try
            {
                // No window handle for a Screen; resolve the HMONITOR from a point at its center.
                var center = new POINT(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                return MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static string MonitorNameFor(IntPtr hwnd)
        {
            try
            {
                return Screen.FromHandle(hwnd)?.DeviceName ?? "?";
            }
            catch
            {
                return "?";
            }
        }

        private static bool TryGetDpiForMonitor(IntPtr monitor, out uint dpiX)
        {
            dpiX = 0;
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var x, out _) == 0)
                {
                    dpiX = x;
                    return true;
                }
            }
            catch
            {
                // shcore missing or call failed.
            }

            return false;
        }

        private static bool TryGetPhysicalResolution(string deviceName, out uint width, out uint height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(deviceName))
            {
                return false;
            }

            try
            {
                var dm = new DEVMODE
                {
                    dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE))
                };
                if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0)
                {
                    width = dm.dmPelsWidth;
                    height = dm.dmPelsHeight;
                    return true;
                }
            }
            catch
            {
                // EnumDisplaySettings unavailable — physical resolution stays n/a.
            }

            return false;
        }
    }
}
