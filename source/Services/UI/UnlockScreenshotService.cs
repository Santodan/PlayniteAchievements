using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Playnite.SDK;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Captures a screenshot of the monitor the running game is on and saves it under a
    /// user-chosen base directory as &lt;base&gt;\Game\NNN_AchievementName_&lt;variant&gt;.png.
    /// Used by the unlock-toast pipeline to record images per own-unlock wave. All failures are
    /// swallowed (logged at debug) so screenshotting never disrupts toasts.
    /// </summary>
    internal sealed class UnlockScreenshotService
    {
        private readonly ILogger _logger;

        public UnlockScreenshotService(ILogger logger)
        {
            _logger = logger;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        // Excludes the invisible resize border/shadow that GetWindowRect includes, so the capture
        // matches the visible window instead of bleeding a few pixels onto the desktop.
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

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
        }

        /// <summary>
        /// Resolves the window's capture rectangle (physical pixels). Prefers the client area so
        /// window chrome (title bar, borders) is excluded for non-fullscreen windows; a
        /// borderless/fullscreen game's client area is the whole window. Falls back to the DWM
        /// extended frame bounds, then GetWindowRect. Returns false if none yields a positive rect.
        /// </summary>
        private static bool TryGetWindowRectangle(IntPtr hwnd, out Rectangle rectangle)
        {
            rectangle = Rectangle.Empty;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            if (TryGetClientRectangle(hwnd, out var client))
            {
                rectangle = client;
                return true;
            }

            try
            {
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwm, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    var frame = Rectangle.FromLTRB(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);
                    if (frame.Width > 0 && frame.Height > 0)
                    {
                        rectangle = frame;
                        return true;
                    }
                }
            }
            catch
            {
                // DWM unavailable — fall back to GetWindowRect below.
            }

            if (GetWindowRect(hwnd, out var win))
            {
                var rect = Rectangle.FromLTRB(win.Left, win.Top, win.Right, win.Bottom);
                if (rect.Width > 0 && rect.Height > 0)
                {
                    rectangle = rect;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The window's client area (game content, no title bar or borders) in physical screen
        /// pixels, via GetClientRect + ClientToScreen.
        /// </summary>
        private static bool TryGetClientRectangle(IntPtr hwnd, out Rectangle rectangle)
        {
            rectangle = Rectangle.Empty;
            if (!GetClientRect(hwnd, out var client))
            {
                return false;
            }

            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var origin = new POINT { X = client.Left, Y = client.Top };
            if (!ClientToScreen(hwnd, ref origin))
            {
                return false;
            }

            rectangle = new Rectangle(origin.X, origin.Y, width, height);
            return true;
        }

        /// <summary>
        /// Captures the game window (resolved via <see cref="TryResolveGameWindowBounds"/> — the
        /// same resolution toast placement uses), clamped to that window's monitor. Falls back to
        /// the whole monitor if the window rect is unavailable. Returns null on failure.
        /// </summary>
        public Bitmap CaptureGameWindow(int? startedProcessId)
        {
            return CaptureGameWindow(IntPtr.Zero, startedProcessId);
        }

        /// <summary>
        /// Capture overload for callers that already resolved the game window (e.g. via the
        /// foreground tracker): a valid <paramref name="knownHwnd"/> wins, the started-process
        /// resolution is the fallback.
        /// </summary>
        public Bitmap CaptureGameWindow(IntPtr knownHwnd, int? startedProcessId)
        {
            try
            {
                var bounds = TryResolveGameWindowBounds(knownHwnd, startedProcessId, out var rect, out var hwnd)
                    ? rect
                    : ResolveMonitorBounds(hwnd);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Unlock screenshot capture failed.");
                return null;
            }
        }

        /// <summary>
        /// Maps a single screenshot variant to its filename suffix ("clean"/"toast"/"framed").
        /// Returns null for None or combined flags.
        /// </summary>
        internal static string VariantSuffix(ScreenshotVariants variant)
        {
            switch (variant)
            {
                case ScreenshotVariants.Clean: return "clean";
                case ScreenshotVariants.WithToast: return "toast";
                case ScreenshotVariants.Framed: return "framed";
                default: return null;
            }
        }

        /// <summary>
        /// Saves an already-captured bitmap to
        /// &lt;baseDir&gt;\Game\NNN_AchievementName_&lt;variant&gt;.png. Creates directories as
        /// needed and avoids clobbering an existing file by appending " (2)", " (3)"...
        /// </summary>
        public void Save(
            Bitmap bitmap,
            string baseDir,
            string providerKey,
            string gameName,
            string achievementName,
            int number,
            int total,
            string variantSuffix = null)
        {
            if (bitmap == null)
            {
                return;
            }

            SaveCore(
                path => bitmap.Save(path, ImageFormat.Png),
                baseDir, providerKey, gameName, achievementName, number, total, variantSuffix);
        }

        /// <summary>
        /// Saves an already-rendered (frozen) WPF bitmap — the framed composite — via
        /// PngBitmapEncoder using the same naming scheme as the GDI overload.
        /// </summary>
        public void Save(
            System.Windows.Media.Imaging.BitmapSource source,
            string baseDir,
            string providerKey,
            string gameName,
            string achievementName,
            int number,
            int total,
            string variantSuffix = null)
        {
            if (source == null)
            {
                return;
            }

            SaveCore(
                path =>
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        encoder.Save(stream);
                    }
                },
                baseDir, providerKey, gameName, achievementName, number, total, variantSuffix);
        }

        private void SaveCore(
            Action<string> writeToPath,
            string baseDir,
            string providerKey,
            string gameName,
            string achievementName,
            int number,
            int total,
            string variantSuffix)
        {
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return;
            }

            try
            {
                var relative = BuildRelativePath(providerKey, gameName, achievementName, number, total, variantSuffix);
                var folder = Path.Combine(baseDir, relative.Folder);
                Directory.CreateDirectory(folder);
                var path = EnsureUniquePath(Path.Combine(folder, relative.FileName));
                writeToPath(path);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Unlock screenshot save failed.");
            }
        }

        /// <summary>
        /// Pure path builder: folder "Game", file "NNN_AchievementName[_suffix].ext" where NNN
        /// is zero-padded to the width of the game's total achievement count (min 3). Every
        /// segment is sanitized for the filesystem.
        /// </summary>
        public static (string Folder, string FileName) BuildRelativePath(
            string providerKey,
            string gameName,
            string achievementName,
            int number,
            int total,
            string variantSuffix = null,
            string extension = ".png")
        {
            var game = AchievementIconCachePathBuilder.SanitizeSegment(gameName);
            var name = AchievementIconCachePathBuilder.SanitizeSegment(achievementName);

            var width = Math.Max(3, Math.Max(1, total).ToString(CultureInfo.InvariantCulture).Length);
            var prefix = Math.Max(0, number).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

            var suffix = string.IsNullOrWhiteSpace(variantSuffix) ? string.Empty : $"_{variantSuffix}";
            return (game, $"{prefix}_{name}{suffix}{extension}");
        }

        /// <summary>
        /// Game window bounds for clamping toast placement, using the exact same resolution as
        /// capture. Returns null when no game is running (so preview toasts fall back to the work
        /// area) or when no window can be resolved.
        /// </summary>
        public Rectangle? TryGetGameWindowBounds(int? startedProcessId)
        {
            return TryGetGameWindowBounds(IntPtr.Zero, startedProcessId);
        }

        /// <summary>
        /// Bounds overload for callers with a known game window handle; the started-process
        /// resolution is the fallback.
        /// </summary>
        public Rectangle? TryGetGameWindowBounds(IntPtr knownHwnd, int? startedProcessId)
        {
            // No game window and no game running -> caller (toast placement) uses the work area.
            if (knownHwnd == IntPtr.Zero && !startedProcessId.HasValue)
            {
                return null;
            }

            return TryResolveGameWindowBounds(knownHwnd, startedProcessId, out var bounds, out _)
                ? bounds
                : (Rectangle?)null;
        }

        /// <summary>
        /// Bounds of the monitor hosting the game window (started-process main window, else
        /// foreground), in physical pixels. Used by the unlock-recording service to scope the
        /// ffmpeg screen capture: ffmpeg can't follow a moving window, so the whole monitor is
        /// recorded. Returns null when no window or monitor can be resolved.
        /// </summary>
        public Rectangle? TryGetGameMonitorBounds(int? startedProcessId)
        {
            return TryGetGameMonitorBounds(IntPtr.Zero, startedProcessId);
        }

        /// <summary>
        /// Monitor-bounds overload for callers with a known game window handle; the
        /// started-process resolution is the fallback.
        /// </summary>
        public Rectangle? TryGetGameMonitorBounds(IntPtr knownHwnd, int? startedProcessId)
        {
            var hwnd = ResolveWindow(knownHwnd, startedProcessId);
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            var bounds = ResolveMonitorBounds(hwnd);
            return bounds.Width > 0 && bounds.Height > 0 ? bounds : (Rectangle?)null;
        }

        /// <summary>
        /// Resolves the game window handle once (started-process main window, else foreground),
        /// for cheap per-frame toast following via <see cref="TryGetClientBounds"/>. Returns
        /// IntPtr.Zero when no game is running so preview toasts don't follow Playnite's window.
        /// </summary>
        public IntPtr ResolveGameWindowHandle(int? startedProcessId)
        {
            return ResolveGameWindowHandle(IntPtr.Zero, startedProcessId);
        }

        /// <summary>
        /// Handle-resolution overload for callers with a known game window handle; the
        /// started-process resolution is the fallback.
        /// </summary>
        public IntPtr ResolveGameWindowHandle(IntPtr knownHwnd, int? startedProcessId)
        {
            if (knownHwnd != IntPtr.Zero && TryGetWindowRectangle(knownHwnd, out _))
            {
                return knownHwnd;
            }

            return startedProcessId.HasValue ? ResolveWindow(startedProcessId) : IntPtr.Zero;
        }

        /// <summary>
        /// Cheap client-area bounds for a known window handle (client rect clamped to its monitor),
        /// used to reposition the toast every frame while following the game window.
        /// </summary>
        public bool TryGetClientBounds(IntPtr hwnd, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (hwnd == IntPtr.Zero || !TryGetWindowRectangle(hwnd, out var window))
            {
                return false;
            }

            var monitor = ResolveMonitorBounds(hwnd);
            if (!monitor.IsEmpty)
            {
                window.Intersect(monitor);
            }

            if (window.Width <= 0 || window.Height <= 0)
            {
                return false;
            }

            bounds = window;
            return true;
        }

        /// <summary>
        /// Shared window resolver for both capture and toast placement: prefers the started
        /// process's main window (the actual game process for emulators/direct-exe games), falling
        /// back to the foreground window (the game during play for launcher-wrapped titles). Yields
        /// the window's client rect (no chrome) clamped to its monitor.
        /// </summary>
        private static bool TryResolveGameWindowBounds(int? startedProcessId, out Rectangle bounds, out IntPtr hwnd)
        {
            return TryResolveGameWindowBounds(IntPtr.Zero, startedProcessId, out bounds, out hwnd);
        }

        private static bool TryResolveGameWindowBounds(
            IntPtr knownHwnd,
            int? startedProcessId,
            out Rectangle bounds,
            out IntPtr hwnd)
        {
            bounds = Rectangle.Empty;
            hwnd = ResolveWindow(knownHwnd, startedProcessId);
            if (hwnd == IntPtr.Zero || !TryGetWindowRectangle(hwnd, out var window))
            {
                return false;
            }

            var monitor = ResolveMonitorBounds(hwnd);
            if (!monitor.IsEmpty)
            {
                window.Intersect(monitor);
            }

            if (window.Width <= 0 || window.Height <= 0)
            {
                return false;
            }

            bounds = window;
            return true;
        }

        private static IntPtr ResolveWindow(IntPtr knownHwnd, int? startedProcessId)
        {
            // A caller-supplied handle (foreground tracker) beats pid resolution: for
            // launcher-wrapped titles the started process often has no (or the wrong) window.
            if (knownHwnd != IntPtr.Zero && TryGetWindowRectangle(knownHwnd, out _))
            {
                return knownHwnd;
            }

            return ResolveWindow(startedProcessId);
        }

        private static IntPtr ResolveWindow(int? startedProcessId)
        {
            if (startedProcessId.HasValue && startedProcessId.Value > 0)
            {
                try
                {
                    using (var process = Process.GetProcessById(startedProcessId.Value))
                    {
                        var handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            return handle;
                        }
                    }
                }
                catch
                {
                    // Process gone or inaccessible — fall back to the foreground window.
                }
            }

            return GetForegroundWindow();
        }

        private static Rectangle ResolveMonitorBounds(IntPtr hwnd)
        {
            try
            {
                var screen = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.PrimaryScreen;
                return (screen ?? Screen.PrimaryScreen).Bounds;
            }
            catch
            {
                return Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
            }
        }

        internal static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (var i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return path;
        }
    }
}
