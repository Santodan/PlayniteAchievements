using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Playnite.SDK;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Local;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Captures a screenshot of the running game's window (monitor capture for the out-of-game
    /// test fire) and saves it under a user-chosen base directory as
    /// &lt;base&gt;\Game\NNN_AchievementName_&lt;variant&gt;.png. Used by the unlock-toast
    /// pipeline to record images per own-unlock wave. All failures are swallowed (logged at
    /// debug) so screenshotting never disrupts toasts.
    /// </summary>
    internal sealed class UnlockScreenshotService
    {
        /// <summary>
        /// Subfolder under the configured screenshot/recording root that receives captures from the
        /// manual test-notification fire, keeping them apart from genuine per-game unlock captures.
        /// Shared by the screenshot planner and the clip output-path builder.
        /// </summary>
        public const string TestFolderName = "Test";

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
                var resolved = TryResolveGameWindowBounds(knownHwnd, startedProcessId, out var rect, out var hwnd);

                // WGC per-window capture: HDR-correct (tone-maps an HDR desktop to SDR) and captures
                // the game window even when it is unfocused or occluded. Falls back to the GDI region
                // copy below when WGC can't deliver (minimized, no window, pre-1903 Windows, or a
                // transient device failure) — that path is SDR-only and blows out on HDR.
                if (resolved && hwnd != IntPtr.Zero && WgcWindowCapture.IsSupported)
                {
                    using (var wgc = new WgcWindowCapture())
                    {
                        var captured = wgc.CaptureWindow(hwnd);
                        if (captured?.Bitmap != null)
                        {
                            return captured.Bitmap;
                        }
                    }
                }

                var bounds = resolved ? rect : ResolveMonitorBounds(hwnd);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                // Rgb (not Argb): CopyFromScreen writes RGB only and never sets alpha, so an Argb
                // buffer would carry alpha=0 and save transparent PNGs. On an Rgb bitmap the alpha
                // is treated as opaque, so every saved variant is fully opaque.
                var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    CopyScreenPhysical(graphics, bounds);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Unlock screenshot capture failed.");
                return null;
            }
        }

        public Bitmap CaptureUsingMode(
            LocalUnlockScreenshotCaptureMode mode,
            IntPtr knownHwnd,
            int? startedProcessId)
        {
            try
            {
                Rectangle bounds;
                if (mode == LocalUnlockScreenshotCaptureMode.FullDesktop)
                {
                    bounds = SystemInformation.VirtualScreen;
                }
                else
                {
                    var foreground = GetForegroundWindow();
                    if (!TryGetWindowRectangle(foreground, out bounds))
                    {
                        return CaptureGameWindow(knownHwnd, startedProcessId);
                    }
                }

                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                // CopyFromScreen writes RGB but does not populate alpha. Argb would save a
                // transparent PNG even though the captured color pixels are valid.
                var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Custom unlock screenshot capture failed.");
                return null;
            }
        }

        public void SaveUsingCustomSettings(
            Bitmap bitmap,
            LocalSettings settings,
            Game game,
            string providerKey,
            string achievementName,
            int number,
            int total)
        {
            if (bitmap == null || settings == null)
            {
                return;
            }

            try
            {
                var timestamp = DateTime.Now;
                var directory = LocalAchievementScreenshotService.ReplaceTokens(
                    settings.EffectiveScreenshotSaveFolder,
                    game,
                    achievementName,
                    timestamp,
                    Math.Max(1, number),
                    Math.Max(1, total),
                    sanitizeValues: true,
                    providerName: providerKey);
                var filename = LocalAchievementScreenshotService.ReplaceTokens(
                    settings.ScreenshotFilenameTemplate,
                    game,
                    achievementName,
                    timestamp,
                    Math.Max(1, number),
                    Math.Max(1, total),
                    sanitizeValues: false,
                    providerName: providerKey);
                filename = LocalAchievementScreenshotService.SanitizeFileName(filename);
                Directory.CreateDirectory(directory);
                var extension = settings.ScreenshotImageFormat == LocalUnlockScreenshotImageFormat.Jpeg
                    ? ".jpg"
                    : ".png";
                var path = LocalAchievementScreenshotService.BuildUniquePath(directory, filename, extension);
                bitmap.Save(
                    path,
                    settings.ScreenshotImageFormat == LocalUnlockScreenshotImageFormat.Jpeg
                        ? ImageFormat.Jpeg
                        : ImageFormat.Png);
                _logger?.Info($"[Screenshot] Saved custom unlock screenshot: {path}");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Custom unlock screenshot save failed.");
            }
        }

        /// <summary>
        /// Captures the entire monitor that <paramref name="windowOnMonitor"/> sits on (the
        /// Playnite window when no game is running), so a notification fired out of game captures
        /// the whole screen where it appears rather than just the Playnite window. Falls back to the
        /// primary monitor when the handle is zero. Returns null on failure.
        /// </summary>
        public Bitmap CaptureMonitor(IntPtr windowOnMonitor)
        {
            try
            {
                // WGC monitor capture (HDR-correct) for the out-of-game test fire, where there is
                // no game window to capture — taken once at wave start, before the toast shows;
                // the card is composited on per item like the in-game path. GDI fallback below is
                // SDR-only.
                if (windowOnMonitor != IntPtr.Zero && WgcWindowCapture.IsSupported)
                {
                    using (var wgc = new WgcWindowCapture())
                    {
                        var captured = wgc.CaptureMonitorForWindow(windowOnMonitor);
                        if (captured?.Bitmap != null)
                        {
                            return captured.Bitmap;
                        }
                    }
                }

                var bounds = ResolveMonitorBounds(windowOnMonitor);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    CopyScreenPhysical(graphics, bounds);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Unlock monitor capture failed.");
                return null;
            }
        }

        /// <summary>
        /// GDI screen copy for the SDR fallback paths, taken in a Per-Monitor-V2 thread scope so the
        /// screen DC is addressed in the same physical pixels the bounds are measured in. Without the
        /// scope a system-aware process addresses the screen in virtualized coordinates, so on a
        /// monitor scaled above 100% the copy would read the wrong region at the wrong size.
        /// </summary>
        private static void CopyScreenPhysical(Graphics graphics, Rectangle bounds)
        {
            using (Common.DpiAwarenessScope.PerMonitorV2())
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
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
            var game = SanitizeCaptureGameName(gameName);
            var name = AchievementIconCachePathBuilder.SanitizeSegment(achievementName);

            var width = Math.Max(3, Math.Max(1, total).ToString(CultureInfo.InvariantCulture).Length);
            var prefix = Math.Max(0, number).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

            // Suffix may be user-configured; sanitize it like the other segments. The
            // whitespace check must come first because SanitizeSegment maps empty input to a
            // fallback stem, while a blank suffix means "no suffix".
            var suffix = string.IsNullOrWhiteSpace(variantSuffix)
                ? string.Empty
                : $"_{AchievementIconCachePathBuilder.SanitizeSegment(variantSuffix)}";
            return (game, $"{prefix}_{name}{suffix}{extension}");
        }

        internal static string SanitizeCaptureGameName(string gameName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(gameName?.Length ?? 0);

            foreach (var c in gameName ?? string.Empty)
            {
                if (char.IsControl(c) || Array.IndexOf(invalidChars, c) >= 0)
                {
                    continue;
                }

                builder.Append(c);
            }

            return AchievementIconCachePathBuilder.SanitizeSegment(builder.ToString());
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
        /// foreground), in physical pixels. Used by capture services that need the game monitor.
        /// Returns null when no window or monitor can be resolved.
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
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            // Physical pixels on both sides of the intersection below (the scope is re-entrant, so
            // callers that already established one are unaffected).
            Rectangle window;
            using (Common.DpiAwarenessScope.PerMonitorV2())
            {
                if (!TryGetWindowRectangle(hwnd, out window))
                {
                    return false;
                }
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
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            // Read the window rect in the same physical (device) pixels ResolveMonitorBounds returns,
            // so the intersection below stays in one coordinate space. Capture works in physical
            // pixels throughout; without this scope a system-aware read would be virtualized and the
            // captured region would be wrong on a monitor scaled above 100%.
            Rectangle window;
            using (Common.DpiAwarenessScope.PerMonitorV2())
            {
                if (!TryGetWindowRectangle(hwnd, out window))
                {
                    return false;
                }
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

        /// <summary>
        /// The monitor rect a window rect is clamped against, in the same physical (device) pixels the
        /// window rect is read in. <c>Screen.Bounds</c> cannot be used for that: this process is
        /// system-DPI-aware, so WinForms reports virtualized (and process-cached) bounds, and
        /// intersecting those with a Per-Monitor-V2 window rect mixes two coordinate spaces — at 200%
        /// scaling that halves or empties the result. Screen.Bounds remains the last-resort fallback
        /// for when the Win32 query fails.
        /// </summary>
        private static Rectangle ResolveMonitorBounds(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero &&
                    ToastWindowPlacer.TryGetMonitorBoundsPhysical(hwnd, out var physical))
                {
                    return physical;
                }

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
