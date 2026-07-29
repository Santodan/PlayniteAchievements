using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Services.Local
{
    internal sealed class LocalAchievementScreenshotService
    {
        private const string ProviderName = "Local";
        private static readonly Regex TokenPattern = new Regex("<([a-zA-Z0-9]+)>", RegexOptions.Compiled);

        private readonly ILogger _logger;

        public LocalAchievementScreenshotService(ILogger logger)
        {
            _logger = logger;
        }

        public async Task TryCaptureUnlockScreenshotsAsync(
            Game game,
            IReadOnlyList<string> unlockedAchievements,
            CancellationToken cancellationToken,
            string providerName = ProviderName)
        {
            if (game == null || unlockedAchievements == null || unlockedAchievements.Count == 0)
            {
                return;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.EnableActiveGameMonitoring != true ||
                settings.EnableUnlockScreenshots != true)
            {
                return;
            }

            try
            {
                var delay = settings.ScreenshotDelayMilliseconds;
                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                var timestamp = DateTime.Now;
                var unlockCount = unlockedAchievements.Count;
                var achievementName = ResolveBatchAchievementName(unlockedAchievements);
                var targetDirectory = ResolveTargetDirectory(settings, game, achievementName, timestamp, 1, unlockCount, providerName);
                if (string.IsNullOrWhiteSpace(targetDirectory))
                {
                    return;
                }

                Directory.CreateDirectory(targetDirectory);

                using (var bitmap = CaptureBitmap(settings))
                {
                    if (bitmap == null)
                    {
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var baseFileName = BuildFileName(settings, game, achievementName, timestamp, 1, unlockCount, providerName);
                    var extension = GetFileExtension(settings);
                    var outputPath = BuildUniquePath(targetDirectory, baseFileName, extension);
                    SaveBitmap(bitmap, outputPath, settings);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed capturing unlock screenshot for '{game.Name}'.");
            }
        }

        private static string ResolveBatchAchievementName(IReadOnlyList<string> unlockedAchievements)
        {
            var names = unlockedAchievements?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToList() ?? new List<string>();

            if (names.Count <= 0)
            {
                return "Achievement";
            }

            if (names.Count == 1)
            {
                return names[0];
            }

            return string.Format(CultureInfo.InvariantCulture, "{0} (+{1} more)", names[0], names.Count - 1);
        }

        private static string ResolveTargetDirectory(LocalSettings settings, Game game, string achievementName, DateTime timestamp, int unlockIndex, int unlockCount, string providerName)
        {
            var configuredPath = settings?.EffectiveScreenshotSaveFolder?.Trim();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            try
            {
                var expandedPath = ReplaceTokens(configuredPath, game, achievementName, timestamp, unlockIndex, unlockCount, sanitizeValues: true, providerName: providerName);
                return Path.GetFullPath(expandedPath);
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap CaptureBitmap(LocalSettings settings)
        {
            switch (settings?.ScreenshotCaptureMode ?? LocalUnlockScreenshotCaptureMode.FullDesktop)
            {
                case LocalUnlockScreenshotCaptureMode.ActiveWindow:
                    return CaptureForegroundWindow();

                case LocalUnlockScreenshotCaptureMode.FullDesktop:
                default:
                    return CaptureVirtualScreen();
            }
        }

        private static Bitmap CaptureVirtualScreen()
        {
            var left = (int)Math.Floor(System.Windows.SystemParameters.VirtualScreenLeft);
            var top = (int)Math.Floor(System.Windows.SystemParameters.VirtualScreenTop);
            var width = (int)Math.Ceiling(System.Windows.SystemParameters.VirtualScreenWidth);
            var height = (int)Math.Ceiling(System.Windows.SystemParameters.VirtualScreenHeight);

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            return bitmap;
        }

        private static Bitmap CaptureForegroundWindow()
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
            {
                return null;
            }

            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            return bitmap;
        }

        private static string BuildFileName(LocalSettings settings, Game game, string achievementName, DateTime timestamp, int unlockIndex, int unlockCount, string providerName)
        {
            var template = string.IsNullOrWhiteSpace(settings?.ScreenshotFilenameTemplate)
                ? LocalSettings.DefaultScreenshotFilenameTemplate
                : settings.ScreenshotFilenameTemplate;

            var replaced = ReplaceTokens(template, game, achievementName, timestamp, unlockIndex, unlockCount, sanitizeValues: false, providerName: providerName);

            return SanitizeFileName(string.IsNullOrWhiteSpace(replaced)
                ? LocalSettings.DefaultScreenshotFilenameTemplate
                : replaced);
        }

        internal static string ReplaceTokens(
            string template,
            Game game,
            string achievementName,
            DateTime timestamp,
            int unlockIndex,
            int unlockCount,
            bool sanitizeValues,
            string providerName = ProviderName)
        {
            var sourceName = game?.Source?.Name ?? string.Empty;
            return TokenPattern.Replace(template ?? string.Empty, match =>
            {
                string value;
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "gamename":
                        value = game?.Name ?? string.Empty;
                        break;
                    case "achievementname":
                        value = achievementName ?? string.Empty;
                        break;
                    case "date":
                        value = timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        break;
                    case "time":
                        value = timestamp.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
                        break;
                    case "datetime":
                        value = timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                        break;
                    case "source":
                        value = sourceName;
                        break;
                    case "provider":
                        value = providerName ?? ProviderName;
                        break;
                    case "gameid":
                        value = game?.Id.ToString() ?? string.Empty;
                        break;
                    case "unlockindex":
                        value = unlockIndex.ToString(CultureInfo.InvariantCulture);
                        break;
                    case "unlockcount":
                        value = unlockCount.ToString(CultureInfo.InvariantCulture);
                        break;
                    default:
                        return match.Value;
                }

                return sanitizeValues ? SanitizePathComponent(value) : value;
            });
        }

        private static string SanitizePathComponent(string value)
        {
            var sanitized = SanitizeFileName(value);
            return string.Equals(sanitized, "unlock_screenshot", StringComparison.Ordinal) ? "_" : sanitized;
        }

        internal static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value?.Length ?? 0);

            foreach (var character in value ?? string.Empty)
            {
                builder.Append(invalidChars.Contains(character) ? '_' : character);
            }

            var sanitized = builder
                .ToString()
                .Replace("/", "_")
                .Replace("\\", "_")
                .Trim();

            while (sanitized.Contains("__"))
            {
                sanitized = sanitized.Replace("__", "_");
            }

            sanitized = sanitized.Trim(' ', '.','_');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return "unlock_screenshot";
            }

            return sanitized.Length <= 120 ? sanitized : sanitized.Substring(0, 120).Trim(' ', '.', '_');
        }

        internal static string BuildUniquePath(string directory, string baseFileName, string extension)
        {
            var candidate = Path.Combine(directory, baseFileName + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            for (var suffix = 1; suffix < 10000; suffix++)
            {
                candidate = Path.Combine(directory, $"{baseFileName}_{suffix}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{baseFileName}_{Guid.NewGuid():N}{extension}");
        }

        private static string GetFileExtension(LocalSettings settings)
        {
            return (settings?.ScreenshotImageFormat ?? LocalUnlockScreenshotImageFormat.Png) == LocalUnlockScreenshotImageFormat.Jpeg
                ? ".jpg"
                : ".png";
        }

        private static void SaveBitmap(Bitmap bitmap, string outputPath, LocalSettings settings)
        {
            if (bitmap == null || string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            if ((settings?.ScreenshotImageFormat ?? LocalUnlockScreenshotImageFormat.Png) == LocalUnlockScreenshotImageFormat.Jpeg)
            {
                bitmap.Save(outputPath, ImageFormat.Jpeg);
                return;
            }

            bitmap.Save(outputPath, ImageFormat.Png);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
