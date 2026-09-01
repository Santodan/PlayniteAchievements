using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// A tiled noise brush that hides 8-bit gradient banding in the notification border glow.
    /// The glow effect's falloff is smooth, but composition quantizes it to 8-bit levels, and
    /// over a blur that wide each level occupies a 2-3 px flat ring that reads as an edge on
    /// dark backgrounds (RenderingBias does not change this; Performance and Quality render
    /// identically). Overlaying grain whose amplitude straddles the quantization step drowns
    /// those ring edges: each pixel adds zero, one, or two levels with triangular weighting
    /// (25/50/25), so adjacent rings' brightness distributions overlap and no coherent edge
    /// survives — one-level speckle alone halves the edge but leaves the staircase visible.
    /// Two levels is still only a 0.8% brightness change, invisible at normal exposure. The
    /// tile is deterministic and never animates, so the capture pipeline's frame dedup and
    /// shadow difference layer are unaffected.
    /// </summary>
    public static class DitherNoise
    {
        private const int TileSize = 64;

        /// <summary>Frozen tiled brush, sized so one tile pixel is one DIP.</summary>
        public static ImageBrush Brush { get; } = CreateBrush();

        private static ImageBrush CreateBrush()
        {
            var stride = TileSize * 4;
            var pixels = new byte[stride * TileSize];
            for (var y = 0; y < TileSize; y++)
            {
                for (var x = 0; x < TileSize; x++)
                {
                    // Two interleaved-gradient-noise samples summed: triangular distribution,
                    // decorrelated between neighbors with no visible pattern, and deterministic
                    // so every tile and session renders the same.
                    var noise = Fraction(52.9829189 * Fraction((0.06711056 * x) + (0.00583715 * y)))
                        + Fraction(52.9829189 * Fraction((0.06711056 * (x + 71.3)) + (0.00583715 * (y + 113.7))));
                    var level = (byte)Math.Floor(noise + 0.5);
                    if (level == 0)
                    {
                        continue;
                    }

                    // Premultiplied white at zero, one, or two quantization levels.
                    var i = (y * stride) + (x * 4);
                    pixels[i + 0] = level;
                    pixels[i + 1] = level;
                    pixels[i + 2] = level;
                    pixels[i + 3] = level;
                }
            }

            var bitmap = BitmapSource.Create(
                TileSize, TileSize, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, TileSize, TileSize),
                Stretch = Stretch.None
            };
            brush.Freeze();
            return brush;
        }

        private static double Fraction(double value)
        {
            return value - Math.Floor(value);
        }
    }
}
