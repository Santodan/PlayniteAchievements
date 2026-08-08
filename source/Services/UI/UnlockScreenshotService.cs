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
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.Images;

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

        /// <summary>
        /// Resolves the window's capture rectangle (physical pixels) via
        /// <see cref="WindowRectangles"/> — the shared measurement, so this path and the recorder's
        /// cannot drift apart on DPI handling again. Prefers the client area so window chrome is
        /// excluded for non-fullscreen windows; a borderless/fullscreen game's client area is the
        /// whole window. Returns false if nothing yields a positive rect.
        /// </summary>
        private static bool TryGetWindowRectangle(IntPtr hwnd, out Rectangle rectangle)
        {
            rectangle = WindowRectangles.Measure(hwnd).PreferredCaptureArea;
            return !rectangle.IsEmpty;
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
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            // Physical pixels on both sides of the intersection below: the measurement is
            // per-monitor-aware by construction and ResolveMonitorBounds returns the same space.
            if (!TryGetWindowRectangle(hwnd, out var window))
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
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            // Both sides of the intersection below are physical (device) pixels: the measurement is
            // per-monitor-aware by construction and ResolveMonitorBounds returns the same space.
            if (!TryGetWindowRectangle(hwnd, out var window))
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
