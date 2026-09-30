using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Reads an image file's pixel size without decoding it, and remembers aspect ratios by path
    /// for the session.
    /// </summary>
    /// <remarks>
    /// PNG and JPEG are read straight from their headers (<see cref="ImageHeaderDimensions"/>);
    /// anything else goes through a delayed-creation <c>BitmapFrame</c>, which also reads only the
    /// header. Animated GIFs report their first frame's (logical canvas) size.
    /// </remarks>
    internal static class ImagePixelSize
    {
        private static readonly ConcurrentDictionary<string, double> AspectRatios =
            new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        internal static bool TryRead(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }

                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (ImageHeaderDimensions.TryRead(stream, out width, out height))
                    {
                        return true;
                    }
                }

                var frame = BitmapFrame.Create(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                return width > 0 && height > 0;
            }
            catch
            {
                width = 0;
                height = 0;
                return false;
            }
        }

        /// <summary>The cached width-to-height ratio of <paramref name="path"/>, if it was probed.</summary>
        internal static bool TryGetCachedAspectRatio(string path, out double aspectRatio)
        {
            aspectRatio = 0;
            return !string.IsNullOrWhiteSpace(path) && AspectRatios.TryGetValue(path, out aspectRatio);
        }

        /// <summary>
        /// The width-to-height ratio of <paramref name="path"/>, read once and cached. An unreadable
        /// file caches as 0, so it is not probed again.
        /// </summary>
        internal static double GetAspectRatio(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            return AspectRatios.GetOrAdd(
                path,
                key => TryRead(key, out var width, out var height) ? width / (double)height : 0);
        }
    }
}
