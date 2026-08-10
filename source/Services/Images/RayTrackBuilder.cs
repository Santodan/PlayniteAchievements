using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Turns a subject's alpha channel into a <see cref="RayTrack"/>: read the silhouette, take its
    /// convex hull, round the corners off, and resample the result evenly by arc length.
    ///
    /// Pure geometry with no cache and no thread affinity of its own — it reads a frozen
    /// <see cref="BitmapSource"/> and returns an immutable value, so <see cref="RayTrackService"/> can
    /// call it from the thread pool.
    ///
    /// An analytic result (<see cref="RayTrack.IsAnalytic"/>) carries no corner rounding: the radius is
    /// a per-surface choice, so callers re-round through <see cref="RayTrack.RoundedRect"/> with their
    /// own ratio. That keeps one cached silhouette serving surfaces that clip their art differently.
    /// </summary>
    public static class RayTrackBuilder
    {
        /// <summary>
        /// Alpha at or above which a pixel counts as part of the subject, out of 255. Low on purpose:
        /// for a hull we want any meaningfully visible pixel, and including the antialiased fringe puts
        /// the loop where the eye already sees the edge. Encoders write a hard zero in fully transparent
        /// regions, so this separates "nothing here" from "edge ramp" without catching the former.
        /// </summary>
        internal const byte AlphaThreshold = 16;

        /// <summary>Corner-cutting passes. Two takes the visible angle off a hull corner; a third moves
        /// the boundary by well under a percent and is thrown away by the resample.</summary>
        private const int ChaikinIterations = 2;

        // Analysis runs at a small decode, so anything this large means an unexpected native-resolution
        // decode. Scanning it would cost more than the effect is worth.
        private const int MaxAnalysisPixels = 4_000_000;

        public static RayTrack Build(BitmapSource source)
        {
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return RayTrack.RoundedRect(1.0, 0.0);
            }

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var aspect = width / (double)height;

            if ((long)width * height > MaxAnalysisPixels)
            {
                return RayTrack.RoundedRect(aspect, 0.0);
            }

            byte[] pixels;
            try
            {
                pixels = ReadBgra32(source, width, height);
            }
            catch
            {
                return RayTrack.RoundedRect(aspect, 0.0);
            }

            if (pixels == null)
            {
                return RayTrack.RoundedRect(aspect, 0.0);
            }

            var stride = width * 4;

            // Four reads settle the common case. If all four corners are opaque then the convex hull of
            // the subject IS the full rect: the hull contains the four corners, and a convex set that
            // contains the corners of a rectangle contains the whole rectangle. Most achievement icons
            // are opaque squares and every JPEG cover is opaque, so the scan below rarely runs.
            if (CornersOpaque(pixels, width, height, stride))
            {
                return RayTrack.RoundedRect(aspect, 0.0);
            }

            var hull = BuildHull(pixels, width, height, stride);
            if (hull == null)
            {
                return RayTrack.Empty;
            }

            var smoothed = hull;
            for (var i = 0; i < ChaikinIterations; i++)
            {
                smoothed = Chaikin(smoothed);
            }

            var resampled = ResampleByArcLength(smoothed, RayTrack.SampleCount);
            if (resampled == null)
            {
                return RayTrack.RoundedRect(aspect, 0.0);
            }

            // Isotropic: both axes divide by the longer side, shorter side centered. See RayTrack's
            // class comment for why per-axis normalization would break the stored normals.
            var longest = Math.Max(width, height);
            var offsetX = (longest - width) * 0.5;
            var offsetY = (longest - height) * 0.5;
            for (var i = 0; i < resampled.Length; i++)
            {
                resampled[i] = new Point(
                    (resampled[i].X + offsetX) / longest,
                    (resampled[i].Y + offsetY) / longest);
            }

            var normals = BuildNormals(resampled, out var centroid);
            return RayTrack.Create(resampled, normals, centroid, aspect, isAnalytic: false, isEmpty: false);
        }

        private static byte[] ReadBgra32(BitmapSource source, int width, int height)
        {
            var bgra = source;
            if (bgra.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bgra;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                converted.Freeze();
                bgra = converted;
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        private static bool CornersOpaque(byte[] pixels, int width, int height, int stride)
        {
            return pixels[3] >= AlphaThreshold
                && pixels[((width - 1) * 4) + 3] >= AlphaThreshold
                && pixels[((height - 1) * stride) + 3] >= AlphaThreshold
                && pixels[((height - 1) * stride) + ((width - 1) * 4) + 3] >= AlphaThreshold;
        }

        private struct IntPt
        {
            public int X;
            public int Y;

            public IntPt(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        /// <summary>
        /// Convex hull of the above-threshold pixels, counterclockwise as it appears on screen, or null
        /// when nothing is above threshold.
        /// </summary>
        private static Point[] BuildHull(byte[] pixels, int width, int height, int stride)
        {
            // Only each row's leftmost and rightmost lit pixels are fed to the hull. Every other lit
            // pixel in that row lies on the segment between them, and a point on a segment between two
            // members of a set is inside that set's hull — so the result is identical to hulling every
            // pixel, at O(height) points instead of O(width * height).
            //
            // The pixel at index (x, y) covers the AREA [x, x+1) x [y, y+1), so both of its corners on
            // each axis are emitted. Hulling the bare indices would undercut the silhouette by half a
            // pixel on every side.
            var points = new IntPt[height * 4];
            var count = 0;

            for (var y = 0; y < height; y++)
            {
                var rowStart = y * stride;
                var min = -1;
                var max = -1;

                for (var x = 0; x < width; x++)
                {
                    if (pixels[rowStart + (x * 4) + 3] < AlphaThreshold)
                    {
                        continue;
                    }

                    if (min < 0)
                    {
                        min = x;
                    }

                    max = x;
                }

                if (min < 0)
                {
                    continue;
                }

                points[count++] = new IntPt(min, y);
                points[count++] = new IntPt(max + 1, y);
                points[count++] = new IntPt(min, y + 1);
                points[count++] = new IntPt(max + 1, y + 1);
            }

            if (count == 0)
            {
                return null;
            }

            var hull = MonotoneChain(points, count);

            // A silhouette that is a single row, a single column, or a perfect diagonal has no area for
            // arrows to stand on. Fall back to its bounding box, never thinner than two pixels.
            if (hull == null || hull.Length < 3 || Math.Abs(SignedArea(hull)) < 1.0)
            {
                hull = BoundingBox(points, count);
            }

            // Counterclockwise on screen. These coordinates run y-down, which flips the usual sense, so
            // the shoelace area of a counterclockwise-looking loop is NEGATIVE. Forcing it once here
            // lets everything downstream — normals above all — assume the sign.
            if (SignedArea(hull) > 0)
            {
                Array.Reverse(hull);
            }

            return hull;
        }

        /// <summary>Andrew's monotone chain. Integer input keeps every cross product exact, so the
        /// hull is bit-for-bit reproducible for identical pixels.</summary>
        private static Point[] MonotoneChain(IntPt[] points, int count)
        {
            Array.Sort(points, 0, count, IntPtComparer.Instance);

            var hull = new IntPt[count * 2];
            var k = 0;

            for (var i = 0; i < count; i++)
            {
                // Collinear points are popped along with reflex ones, so straight runs cost no vertices.
                while (k >= 2 && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0)
                {
                    k--;
                }

                hull[k++] = points[i];
            }

            var lower = k + 1;
            for (var i = count - 2; i >= 0; i--)
            {
                while (k >= lower && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0)
                {
                    k--;
                }

                hull[k++] = points[i];
            }

            // The last entry repeats the first.
            var length = Math.Max(0, k - 1);
            if (length < 3)
            {
                return null;
            }

            var result = new Point[length];
            for (var i = 0; i < length; i++)
            {
                result[i] = new Point(hull[i].X, hull[i].Y);
            }

            return result;
        }

        private sealed class IntPtComparer : System.Collections.Generic.IComparer<IntPt>
        {
            public static readonly IntPtComparer Instance = new IntPtComparer();

            public int Compare(IntPt a, IntPt b)
            {
                if (a.X != b.X)
                {
                    return a.X < b.X ? -1 : 1;
                }

                if (a.Y != b.Y)
                {
                    return a.Y < b.Y ? -1 : 1;
                }

                return 0;
            }
        }

        private static long Cross(IntPt o, IntPt a, IntPt b)
        {
            return ((long)(a.X - o.X) * (b.Y - o.Y)) - ((long)(a.Y - o.Y) * (b.X - o.X));
        }

        private static Point[] BoundingBox(IntPt[] points, int count)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (var i = 0; i < count; i++)
            {
                if (points[i].X < minX) minX = points[i].X;
                if (points[i].X > maxX) maxX = points[i].X;
                if (points[i].Y < minY) minY = points[i].Y;
                if (points[i].Y > maxY) maxY = points[i].Y;
            }

            if (maxX - minX < 2)
            {
                maxX = minX + 2;
            }

            if (maxY - minY < 2)
            {
                maxY = minY + 2;
            }

            return new[]
            {
                new Point(minX, minY),
                new Point(maxX, minY),
                new Point(maxX, maxY),
                new Point(minX, maxY)
            };
        }

        private static double SignedArea(Point[] polygon)
        {
            var sum = 0.0;
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                sum += (a.X * b.Y) - (b.X * a.Y);
            }

            return sum * 0.5;
        }

        /// <summary>
        /// Chaikin corner cutting on a closed loop. Each new point is a convex combination of two
        /// adjacent vertices, so the result stays inside the old polygon and stays convex — which is
        /// the whole reason an interpolating spline is not used here. Catmull-Rom would keep the hull
        /// corners and overshoot outside them, reintroducing exactly the local concavity that taking a
        /// convex hull was meant to rule out.
        /// </summary>
        private static Point[] Chaikin(Point[] polygon)
        {
            var n = polygon.Length;
            if (n < 3)
            {
                return polygon;
            }

            var result = new Point[n * 2];
            for (var i = 0; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                result[i * 2] = new Point((0.75 * a.X) + (0.25 * b.X), (0.75 * a.Y) + (0.25 * b.Y));
                result[(i * 2) + 1] = new Point((0.25 * a.X) + (0.75 * b.X), (0.25 * a.Y) + (0.75 * b.Y));
            }

            return result;
        }

        /// <summary>
        /// Walks the loop emitting a point every perimeter/count, so consumers can find the sample at
        /// normalized position u by plain indexing instead of searching an arc-length table.
        /// </summary>
        private static Point[] ResampleByArcLength(Point[] polygon, int count)
        {
            var n = polygon.Length;
            if (n < 3 || count < 3)
            {
                return null;
            }

            var segment = new double[n];
            var perimeter = 0.0;
            for (var i = 0; i < n; i++)
            {
                segment[i] = (polygon[(i + 1) % n] - polygon[i]).Length;
                perimeter += segment[i];
            }

            if (!(perimeter > 0))
            {
                return null;
            }

            var step = perimeter / count;
            var result = new Point[count];
            var index = 0;
            var walked = 0.0;

            for (var i = 0; i < count; i++)
            {
                var target = i * step;

                while (index < n - 1 && walked + segment[index] <= target)
                {
                    walked += segment[index];
                    index++;
                }

                var length = segment[index];
                var t = length > 0 ? (target - walked) / length : 0.0;
                if (t < 0) t = 0;
                if (t > 1) t = 1;

                var a = polygon[index];
                var b = polygon[(index + 1) % n];
                result[i] = new Point(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
            }

            return result;
        }

        /// <summary>
        /// Unit outward normal at each sample, by central difference so the normal is centered on its
        /// sample rather than lagging half a segment. Rotating the tangent by (-y, x) is outward for a
        /// counterclockwise-on-screen loop in y-down coordinates — a left edge runs downward, tangent
        /// (0, 1), giving (-1, 0), which points left, away from the shape. Since the loop is convex and
        /// its winding was forced during the hull, this needs no per-point correction.
        /// </summary>
        private static Vector[] BuildNormals(Point[] points, out Point centroid)
        {
            var n = points.Length;
            var sumX = 0.0;
            var sumY = 0.0;
            for (var i = 0; i < n; i++)
            {
                sumX += points[i].X;
                sumY += points[i].Y;
            }

            centroid = new Point(sumX / n, sumY / n);

            var normals = new Vector[n];
            for (var i = 0; i < n; i++)
            {
                var tangent = points[(i + 1) % n] - points[((i - 1) + n) % n];
                var length = tangent.Length;
                if (length > 1e-12)
                {
                    normals[i] = new Vector(-tangent.Y / length, tangent.X / length);
                    continue;
                }

                // Coincident neighbours leave no tangent to rotate; on a convex loop, straight out from
                // the middle is the right answer anyway.
                var radial = points[i] - centroid;
                var radialLength = radial.Length;
                normals[i] = radialLength > 1e-12
                    ? new Vector(radial.X / radialLength, radial.Y / radialLength)
                    : new Vector(0, -1);
            }

            return normals;
        }
    }
}
