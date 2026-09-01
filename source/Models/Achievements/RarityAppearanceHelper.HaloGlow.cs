using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Bitmap for the notification card's border halo.
    ///
    /// The halo used to be a DropShadowEffect, and at the card's size that banded: the effect's
    /// falloff is smooth, but 8-bit composition quantizes it, and over a blur this wide each
    /// brightness level occupies a 2-3 px flat ring that reads as a visible edge on dark
    /// backgrounds (measured on the BlurRadius-36 render; RenderingBias made no difference —
    /// Performance and Quality renders were byte-identical). An effect also cannot dither, so
    /// the halo is generated here instead: the same falloff computed in float from the card's
    /// rounded-rectangle distance field, quantized with a +-1-level triangular-noise dither
    /// that breaks the rings into grain the eye cannot pick out at normal exposure.
    /// </summary>
    public static partial class RarityAppearanceHelper
    {
        // Alpha falloff sampled from a BlurRadius-36 DropShadowEffect render at 1.5x scale,
        // one sample per rendered pixel moving out from the card edge (index 0 = the edge,
        // where a blurred step function crosses half). Interpolated and rescaled by the glow
        // radius, so the generated halo keeps the effect's exact profile at any radius.
        private static readonly byte[] HaloFalloff =
        {
            128, 122, 117, 111, 105, 100, 95, 89, 84, 79, 74, 69, 65, 60, 56, 52, 48, 44, 40,
            37, 34, 31, 28, 25, 23, 20, 18, 16, 15, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 3, 2, 2,
            1, 1, 1, 0
        };

        // The table above was sampled at 1.5 px/DIP for a radius of 36, so one blur radius
        // spans 36 / (1/1.5) = 54 samples.
        private const double HaloFalloffSamplesPerRadius = 54.0;

        /// <summary>
        /// Pixels per DIP the halo renders at. Above 1 so fractional layout scaling (fit scale,
        /// DPI compensation) downsamples the dither instead of stretching its grain.
        /// </summary>
        public const double HaloRenderScale = 2.0;

        // Resolved from OnRender, so UI thread only and no lock is needed. Keyed by everything
        // that shapes the pixels; cleared on recolor with the ray palettes, and capped because
        // a full-size entry is card-plus-margin at HaloRenderScale.
        private static readonly Dictionary<string, BitmapSource> HaloCache =
            new Dictionary<string, BitmapSource>();

        private const int HaloCacheCap = 12;

        internal static void ClearBorderHaloCache()
        {
            HaloCache.Clear();
        }

        /// <summary>
        /// The dithered halo for a card of the given DIP size, as a frozen bitmap sized to the
        /// card plus <paramref name="margin"/> on every side. Premultiplied, transparent where
        /// the falloff ends; the region under the card is filled at the falloff's interior
        /// value, matching how an effect's shadow sits behind translucent card art.
        /// </summary>
        public static BitmapSource GetBorderHaloBitmap(
            Color color, double cardWidth, double cardHeight,
            double cornerRadius, double glowRadius, double margin)
        {
            if (cardWidth <= 0 || cardHeight <= 0 || glowRadius <= 0 || margin < 0)
            {
                return null;
            }

            var key = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:X8}|{1:0.##}|{2:0.##}|{3:0.##}|{4:0.##}|{5:0.##}",
                (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B,
                cardWidth, cardHeight, cornerRadius, glowRadius, margin);
            if (HaloCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var bitmap = RenderBorderHalo(color, cardWidth, cardHeight, cornerRadius, glowRadius, margin);
            if (HaloCache.Count >= HaloCacheCap)
            {
                HaloCache.Clear();
            }

            HaloCache[key] = bitmap;
            return bitmap;
        }

        private static BitmapSource RenderBorderHalo(
            Color color, double cardWidth, double cardHeight,
            double cornerRadius, double glowRadius, double margin)
        {
            var scale = HaloRenderScale;
            var pixelW = Math.Max(1, (int)Math.Ceiling((cardWidth + (2 * margin)) * scale));
            var pixelH = Math.Max(1, (int)Math.Ceiling((cardHeight + (2 * margin)) * scale));
            var stride = pixelW * 4;
            var pixels = new byte[stride * pixelH];

            var centerX = pixelW / 2.0;
            var centerY = pixelH / 2.0;
            var halfW = cardWidth * scale / 2.0;
            var halfH = cardHeight * scale / 2.0;
            var radius = Math.Min(cornerRadius * scale, Math.Min(halfW, halfH));
            var samplesPerPixel = HaloFalloffSamplesPerRadius / (glowRadius * scale);

            for (var y = 0; y < pixelH; y++)
            {
                var row = y * stride;
                for (var x = 0; x < pixelW; x++)
                {
                    var d = RoundedRectDistance(x + 0.5, y + 0.5, centerX, centerY, halfW, halfH, radius);
                    var alpha = d >= 0
                        ? SampleHaloFalloff(d * samplesPerPixel)
                        : 1.0 - SampleHaloFalloff(-d * samplesPerPixel);
                    if (alpha <= 0)
                    {
                        continue;
                    }

                    // Triangular-PDF dither at +-1 quantization level, applied to the alpha and
                    // carried into the premultiplied channels through the quantized alpha, so the
                    // grain never shifts the hue.
                    var noise = GradientNoise(x, y) + GradientNoise(x + 71.3, y + 113.7) - 1.0;
                    var alphaByte = ClampToByte((alpha * 255.0) + noise);
                    if (alphaByte == 0)
                    {
                        continue;
                    }

                    var i = row + (x * 4);
                    pixels[i + 0] = (byte)Math.Round(color.B * alphaByte / 255.0);
                    pixels[i + 1] = (byte)Math.Round(color.G * alphaByte / 255.0);
                    pixels[i + 2] = (byte)Math.Round(color.R * alphaByte / 255.0);
                    pixels[i + 3] = alphaByte;
                }
            }

            var bitmap = BitmapSource.Create(
                pixelW, pixelH, 96.0 * scale, 96.0 * scale,
                PixelFormats.Pbgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static double SampleHaloFalloff(double index)
        {
            if (index <= 0)
            {
                return HaloFalloff[0] / 255.0;
            }

            if (index >= HaloFalloff.Length - 1)
            {
                return 0;
            }

            var i = (int)index;
            var t = index - i;
            return ((HaloFalloff[i] * (1.0 - t)) + (HaloFalloff[i + 1] * t)) / 255.0;
        }

        /// <summary>Signed distance to the card's rounded rectangle, negative inside.</summary>
        private static double RoundedRectDistance(
            double px, double py, double centerX, double centerY,
            double halfW, double halfH, double radius)
        {
            var qx = Math.Abs(px - centerX) - (halfW - radius);
            var qy = Math.Abs(py - centerY) - (halfH - radius);
            var ox = Math.Max(qx, 0);
            var oy = Math.Max(qy, 0);
            return Math.Sqrt((ox * ox) + (oy * oy)) + Math.Min(Math.Max(qx, qy), 0) - radius;
        }

        // Interleaved gradient noise, uniform in [0, 1) and decorrelated between neighboring
        // pixels, so the dither carries no visible pattern of its own.
        private static double GradientNoise(double x, double y)
        {
            var v = 52.9829189 * Fraction((0.06711056 * x) + (0.00583715 * y));
            return Fraction(v);
        }

        private static double Fraction(double value)
        {
            return value - Math.Floor(value);
        }

        private static byte ClampToByte(double value)
        {
            var rounded = (int)Math.Round(value);
            return (byte)Math.Max(0, Math.Min(255, rounded));
        }
    }
}
