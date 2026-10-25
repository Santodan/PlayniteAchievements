using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The one blur every blurred-toast output uses: the live card background, the with-notification
    /// screenshot and the unlock clip. Repeated running-sum box passes over interleaved byte pixels,
    /// which approach a gaussian in a few passes at a cost independent of the radius.
    /// </summary>
    internal static class BoxBlur
    {
        /// <summary>Box passes per blur; three are close to a gaussian.</summary>
        public const int Passes = 3;

        /// <summary>
        /// The per-pass box radius that spreads a blur about <paramref name="reachPx"/> pixels,
        /// at least 1.
        /// </summary>
        public static int RadiusFor(double reachPx)
        {
            return Math.Max(1, (int)Math.Round(reachPx / Passes));
        }

        /// <summary>
        /// Blurs a plane in place: <paramref name="width"/> x <paramref name="height"/> pixels of
        /// <paramref name="channels"/> interleaved bytes each, rows <paramref name="stride"/> bytes
        /// apart starting at <paramref name="offset"/>. Edges are clamped. <paramref name="line"/> is
        /// scratch, grown as needed and returned for reuse.
        /// </summary>
        public static byte[] Apply(
            byte[] plane, int offset, int width, int height, int stride, int channels, int radius, byte[] line = null)
        {
            if (plane == null || width <= 0 || height <= 0 || channels <= 0 || radius <= 0)
            {
                return line;
            }

            var needed = Math.Max(width * channels, height);
            if (line == null || line.Length < needed)
            {
                line = new byte[needed];
            }

            for (var pass = 0; pass < Passes; pass++)
            {
                Horizontal(plane, offset, width, height, stride, channels, radius, line);
                Vertical(plane, offset, width, height, stride, channels, radius, line);
            }

            return line;
        }

        private static void Horizontal(
            byte[] plane, int offset, int width, int height, int stride, int channels, int radius, byte[] line)
        {
            var span = radius * 2 + 1;
            var rowBytes = width * channels;
            for (var row = 0; row < height; row++)
            {
                var start = offset + row * stride;
                Buffer.BlockCopy(plane, start, line, 0, rowBytes);
                for (var c = 0; c < channels; c++)
                {
                    var sum = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        sum += line[Clamp(k, width) * channels + c];
                    }

                    for (var x = 0; x < width; x++)
                    {
                        plane[start + x * channels + c] = (byte)(sum / span);
                        sum += line[Clamp(x + radius + 1, width) * channels + c] -
                               line[Clamp(x - radius, width) * channels + c];
                    }
                }
            }
        }

        private static void Vertical(
            byte[] plane, int offset, int width, int height, int stride, int channels, int radius, byte[] line)
        {
            var span = radius * 2 + 1;
            for (var col = 0; col < width; col++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var column = offset + col * channels + c;
                    for (var row = 0; row < height; row++)
                    {
                        line[row] = plane[column + row * stride];
                    }

                    var sum = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        sum += line[Clamp(k, height)];
                    }

                    for (var row = 0; row < height; row++)
                    {
                        plane[column + row * stride] = (byte)(sum / span);
                        sum += line[Clamp(row + radius + 1, height)] - line[Clamp(row - radius, height)];
                    }
                }
            }
        }

        private static int Clamp(int index, int length)
        {
            return index < 0 ? 0 : index >= length ? length - 1 : index;
        }
    }
}
