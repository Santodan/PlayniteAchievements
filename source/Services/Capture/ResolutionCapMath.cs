using System;
using System.Drawing;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure output sizing for captured surfaces: the height cap a user picked, applied to a
    /// captured size. Shared by the clip encoder and the screenshot pipeline so both read the same
    /// resolution options the same way.
    /// </summary>
    internal static class ResolutionCapMath
    {
        /// <summary>The height cap a recording resolution asks for; 0 when it asks for none.</summary>
        public static int CapHeightFor(RecordingResolution resolution)
        {
            switch (resolution)
            {
                case RecordingResolution.P1080: return 1080;
                case RecordingResolution.P720: return 720;
                default: return 0;
            }
        }

        /// <summary>The height cap a screenshot resolution asks for; 0 when it asks for none.</summary>
        public static int CapHeightFor(ScreenshotResolution resolution)
        {
            switch (resolution)
            {
                case ScreenshotResolution.P1080: return 1080;
                case ScreenshotResolution.P720: return 720;
                default: return 0;
            }
        }

        /// <summary>
        /// The captured size after the cap: caps the height to <paramref name="capHeight"/>
        /// (0 for none), preserving the aspect ratio and never upscaling.
        /// </summary>
        /// <param name="evenDimensions">
        /// Round the result down to even width and height, as an H.264 encode requires and a still
        /// image does not. Also raises the floor from 1 pixel to 2.
        /// </param>
        public static Size Apply(int width, int height, int capHeight, bool evenDimensions)
        {
            var minimum = evenDimensions ? 2 : 1;
            if (capHeight <= 0 || height <= capHeight)
            {
                return new Size(
                    Math.Max(minimum, Round(width, evenDimensions)),
                    Math.Max(minimum, Round(height, evenDimensions)));
            }

            var scaledWidth = (int)Math.Round(width * (capHeight / (double)height));
            return new Size(
                Math.Max(minimum, Round(scaledWidth, evenDimensions)),
                Math.Max(minimum, Round(capHeight, evenDimensions)));
        }

        private static int Round(int value, bool evenDimensions)
        {
            return evenDimensions ? value & ~1 : value;
        }
    }
}
