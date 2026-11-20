using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Providers.Local
{
    internal static class NativeSourceIconService
    {
        internal const string FilePrefix = "NativeSourceFile:";
        internal const string ConsolePrefix = "NativeSourceConsole:";
        private static readonly Dictionary<string, DrawingImage> Images = new Dictionary<string, DrawingImage>();

        internal static string IconKey(NativeSourceOption source, LocalSettings settings)
        {
            settings.NativeSourceIcons.TryGetValue(source.Key, out var icon);
            if (!string.IsNullOrWhiteSpace(icon?.CustomIconPath) && File.Exists(icon.CustomIconPath)) return FilePrefix + icon.CustomIconPath;
            if (!string.IsNullOrWhiteSpace(icon?.BorrowedProviderIconKey) && ProviderRegistry.Instance?.TryGetProviderVisuals(icon.BorrowedProviderIconKey, out var borrowedKey, out _) == true)
                return FileKey(borrowedKey);
            if (!string.IsNullOrWhiteSpace(source.OriginalConsoleIcon)) return ConsolePrefix + source.OriginalConsoleIcon;
            if (ProviderRegistry.Instance?.TryGetProviderVisuals(source.IconProviderKey, out var key, out _) == true && !string.IsNullOrWhiteSpace(key)) return FileKey(key);
            return "ProviderIcon" + source.IconProviderKey;
        }
        private static string FileKey(string key) => !string.IsNullOrWhiteSpace(key) && Path.IsPathRooted(key) && File.Exists(key) ? FilePrefix + key : key;
        internal static string Color(NativeSourceOption source, LocalSettings settings)
        {
            settings.NativeSourceIcons.TryGetValue(source.Key, out var icon);
            if (!string.IsNullOrWhiteSpace(icon?.BorrowedProviderIconKey) && ProviderRegistry.Instance?.TryGetProviderVisuals(icon.BorrowedProviderIconKey, out _, out var borrowedColor) == true && !string.IsNullOrWhiteSpace(borrowedColor)) return borrowedColor;
            if (!string.IsNullOrWhiteSpace(source.OriginalConsoleIcon)) return source.OriginalConsoleIcon == "gamecube" ? "#8E7CC3" : "#E60012";
            if (ProviderRegistry.Instance?.TryGetProviderVisuals(source.IconProviderKey, out _, out var color) == true && !string.IsNullOrWhiteSpace(color)) return color;
            return source.IconColorHex;
        }
        internal static DrawingImage Build(string key, string color)
        {
            try
            {
                var console = key.StartsWith(ConsolePrefix, StringComparison.Ordinal);
                var value = key.Substring(console ? ConsolePrefix.Length : FilePrefix.Length);
                if (console && value != "gameboycolor" && value != "gamecube" && value != "n64" && value != "nes") return null;
                if (!console && (!Path.IsPathRooted(value) || !File.Exists(value))) return null;
                var stamp = console ? color : new FileInfo(value).LastWriteTimeUtc.Ticks.ToString();
                var cacheKey = key + "|" + stamp;
                if (Images.TryGetValue(cacheKey, out var cached)) return cached;
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 64;
                bitmap.UriSource = console ? new Uri("pack://application:,,,/PlayniteAchievements;component/Providers/Local/Santodan/NativeSources/Icons/" + value + ".png") : new Uri(value, UriKind.Absolute);
                bitmap.EndInit(); bitmap.Freeze();
                var rect = new Rect(0, 0, 64, 64);
                Drawing drawing;
                if (console)
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                    var group = new DrawingGroup { OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Fill } };
                    group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(rect))); drawing = group;
                }
                else drawing = new ImageDrawing(bitmap, rect);
                var image = new DrawingImage(drawing); image.Freeze();
                if (Images.Count > 128) Images.Clear();
                Images[cacheKey] = image;
                return image;
            }
            catch { return null; }
        }
        internal static async Task<string> ImportAsync(string sourceKey, string file)
        {
            if (NativeSourceCatalog.Find(sourceKey) == null || !File.Exists(file)) return null;
            var service = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (service == null) return null;
            // Unique images keep the saved icon intact while the settings edit copy is open.
            // Clear removes only the reference; Cancel must never delete a saved image.
            var target = Path.Combine(service.GetCacheDirectoryPath(), "provider_icons", "native", sourceKey + "-" + Guid.NewGuid().ToString("N") + ".png");
            return await service.GetOrCopyLocalIconToPathAsync(file, target, LocalSettings.ProviderIconMaxPixelSize,
                CancellationToken.None, overwriteExistingTarget: false).ConfigureAwait(false);
        }
    }
}
