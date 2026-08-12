using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Tests.Images
{
    /// <summary>
    /// Locks the silhouette-to-track contract the rays glow rides on: the loop is convex, wound
    /// counterclockwise as it appears on screen, carries unit outward normals, and normalizes against
    /// the bitmap's bounds isotropically so a non-square subject keeps its true proportions.
    /// </summary>
    [TestClass]
    public class RayTrackBuilderTests
    {
        [TestMethod]
        public void OpaqueSquare_TakesTheAnalyticFastPath()
        {
            // Four opaque corners mean the hull is the whole rect, so the scan never has to run. This
            // is the common case: most achievement icons are opaque squares.
            var track = RayTrackBuilder.Build(Solid(32, 32, 255));

            Assert.IsTrue(track.IsAnalytic);
            Assert.IsFalse(track.IsEmpty);
            Assert.AreEqual(RayTrack.SampleCount, track.Points.Count);
        }

        [TestMethod]
        public void Circle_ProducesAConvexCounterclockwiseLoop()
        {
            var track = RayTrackBuilder.Build(Circle(64, 64, 28));

            Assert.IsFalse(track.IsAnalytic, "a cut-out silhouette should be traced, not assumed");
            Assert.AreEqual(RayTrack.SampleCount, track.Points.Count);
            AssertConvex(track);

            // These coordinates run y-down, which flips the usual sense, so a loop that looks
            // counterclockwise on screen has a negative shoelace area.
            Assert.IsTrue(SignedArea(track) < 0, "loop should be counterclockwise on screen");
        }

        [TestMethod]
        public void Circle_NormalsAreUnitOutwardAndPerpendicular()
        {
            var track = RayTrackBuilder.Build(Circle(64, 64, 28));
            var count = track.Points.Count;

            for (var i = 0; i < count; i++)
            {
                var normal = track.Normals[i];
                Assert.AreEqual(1.0, normal.Length, 1e-9, $"normal {i} is not a unit vector");

                var outward = ((track.Points[i] - track.Centroid).X * normal.X)
                            + ((track.Points[i] - track.Centroid).Y * normal.Y);
                Assert.IsTrue(outward > 0, $"normal {i} points inward");

                // Built from the central difference, so it is perpendicular to the chord through the
                // neighbours rather than to either edge on its own.
                var chord = track.Points[(i + 1) % count] - track.Points[((i - 1) + count) % count];
                chord.Normalize();
                Assert.AreEqual(
                    0.0,
                    (chord.X * normal.X) + (chord.Y * normal.Y),
                    1e-9,
                    $"normal {i} is not perpendicular to its chord");
            }
        }

        [TestMethod]
        public void TransparentPaddedIcon_TracksTheArtworkNotTheCanvas()
        {
            // The whole point of reading the alpha: a logo floating in a transparent canvas gets a loop
            // around the logo, not around the cell it happens to be drawn in.
            var track = RayTrackBuilder.Build(Rectangle(64, 64, 22, 22, 42, 42));

            Bounds(track, out var minX, out var maxX, out var minY, out var maxY);
            Assert.IsTrue(minX > 0.28 && maxX < 0.72, $"x span [{minX}, {maxX}] should hug the artwork");
            Assert.IsTrue(minY > 0.28 && maxY < 0.72, $"y span [{minY}, {maxY}] should hug the artwork");
        }

        [TestMethod]
        public void NonSquareSubject_NormalizesIsotropically()
        {
            // A round blob on a 1:2 canvas has to stay round in track space. Under per-axis
            // normalization it would come out squashed, and the stored normals would then no longer be
            // perpendicular to the on-screen curve once a consumer mapped them onto the artwork.
            var track = RayTrackBuilder.Build(Circle(32, 64, 12));

            Bounds(track, out var minX, out var maxX, out var minY, out var maxY);
            Assert.AreEqual(maxX - minX, maxY - minY, 0.02, "the blob should not be squashed");
            Assert.AreEqual(0.5, track.SourceAspect, 1e-9);
        }

        [TestMethod]
        public void Crescent_HullSpansTheNotch()
        {
            // Convex is the deliberate choice: it fills a concavity rather than following it, which is
            // what keeps every outward normal diverging so arrows can never cross or aim back inward.
            var track = RayTrackBuilder.Build(Crescent(64, 64));

            Assert.IsFalse(track.IsAnalytic);
            AssertConvex(track);
            Assert.IsTrue(SignedArea(track) < 0);
        }

        [TestMethod]
        public void FullyTransparent_ReportsEmptyRatherThanRingingNothing()
        {
            var track = RayTrackBuilder.Build(Solid(32, 32, 0));

            Assert.IsTrue(track.IsEmpty);
        }

        [TestMethod]
        public void AlphaBelowThreshold_IsNotPartOfTheSubject()
        {
            // A faint full-canvas wash must not drag the loop out to the edges; only the solid block
            // counts. This is what pins the threshold.
            var bitmap = Rectangle(32, 32, 12, 12, 20, 20, backgroundAlpha: 8);
            var track = RayTrackBuilder.Build(bitmap);

            Bounds(track, out var minX, out var maxX, out _, out _);
            Assert.IsTrue(minX > 0.30 && maxX < 0.70, $"x span [{minX}, {maxX}] should ignore the wash");
        }

        [TestMethod]
        public void DegenerateSubjects_StayFinite()
        {
            var cases = new[]
            {
                Rectangle(16, 8, 0, 4, 16, 5),   // a single row
                Rectangle(8, 16, 4, 0, 5, 16),   // a single column
                Solid(1, 1, 255),
                Solid(1, 1, 0)
            };

            foreach (var bitmap in cases)
            {
                var track = RayTrackBuilder.Build(bitmap);
                Assert.IsNotNull(track);

                for (var i = 0; i < track.Points.Count; i++)
                {
                    Assert.IsFalse(double.IsNaN(track.Points[i].X) || double.IsNaN(track.Points[i].Y));
                    Assert.IsFalse(double.IsInfinity(track.Points[i].X) || double.IsInfinity(track.Points[i].Y));
                    Assert.AreEqual(1.0, track.Normals[i].Length, 1e-6);
                }
            }
        }

        [TestMethod]
        public void NullSource_FallsBackInsteadOfThrowing()
        {
            var track = RayTrackBuilder.Build(null);

            Assert.IsNotNull(track);
            Assert.IsTrue(track.IsAnalytic);
        }

        [TestMethod]
        public void Build_IsBitIdenticalForIdenticalPixels()
        {
            // The hull runs on integer coordinates so every cross product is exact, and everything after
            // it is a fixed sequence of doubles. Equality here is exact on purpose.
            var first = RayTrackBuilder.Build(Circle(48, 48, 20));
            var second = RayTrackBuilder.Build(Circle(48, 48, 20));

            for (var i = 0; i < first.Points.Count; i++)
            {
                Assert.AreEqual(first.Points[i].X, second.Points[i].X);
                Assert.AreEqual(first.Points[i].Y, second.Points[i].Y);
                Assert.AreEqual(first.Normals[i].X, second.Normals[i].X);
                Assert.AreEqual(first.Normals[i].Y, second.Normals[i].Y);
            }
        }

        [TestMethod]
        public void RoundedRect_IsConvexCounterclockwiseAndOutward()
        {
            var track = RayTrack.RoundedRect(1.0, 0.12);

            Assert.AreEqual(RayTrack.SampleCount, track.Points.Count);
            Assert.IsTrue(SignedArea(track) < 0);

            for (var i = 0; i < track.Points.Count; i++)
            {
                Assert.AreEqual(1.0, track.Normals[i].Length, 1e-9);
                var radial = track.Points[i] - track.Centroid;
                Assert.IsTrue((radial.X * track.Normals[i].X) + (radial.Y * track.Normals[i].Y) > 0);
            }
        }

        [TestMethod]
        public void RoundedRect_WithoutCornersStaysFinite()
        {
            // A square track has no arcs to walk, so the corner segments collapse to zero length.
            var track = RayTrack.RoundedRect(1.0, 0.0);

            for (var i = 0; i < track.Points.Count; i++)
            {
                Assert.IsFalse(double.IsNaN(track.Points[i].X) || double.IsNaN(track.Points[i].Y));
                Assert.AreEqual(1.0, track.Normals[i].Length, 1e-9);
            }
        }

        [TestMethod]
        public void RoundedRect_KeepsTheSubjectsProportions()
        {
            var track = RayTrack.RoundedRect(0.5, 0.12);

            Bounds(track, out var minX, out var maxX, out var minY, out var maxY);
            Assert.AreEqual(0.5, (maxX - minX) / (maxY - minY), 0.02);
            Assert.AreEqual(1.0, maxY - minY, 1e-9, "the longer side should span the unit square");
        }

        private static void AssertConvex(RayTrack track)
        {
            var count = track.Points.Count;
            var sign = 0;

            for (var i = 0; i < count; i++)
            {
                var o = track.Points[i];
                var a = track.Points[(i + 1) % count];
                var b = track.Points[(i + 2) % count];
                var cross = ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
                if (Math.Abs(cross) < 1e-12)
                {
                    continue;
                }

                var current = Math.Sign(cross);
                if (sign == 0)
                {
                    sign = current;
                }
                else
                {
                    Assert.AreEqual(sign, current, $"loop turns both ways at sample {i}");
                }
            }
        }

        private static double SignedArea(RayTrack track)
        {
            var sum = 0.0;
            var count = track.Points.Count;
            for (var i = 0; i < count; i++)
            {
                var a = track.Points[i];
                var b = track.Points[(i + 1) % count];
                sum += (a.X * b.Y) - (b.X * a.Y);
            }

            return sum * 0.5;
        }

        private static void Bounds(RayTrack track, out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = double.MaxValue;
            maxX = double.MinValue;
            minY = double.MaxValue;
            maxY = double.MinValue;

            foreach (var point in track.Points)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        private static BitmapSource Solid(int width, int height, byte alpha)
        {
            return Create(width, height, (x, y) => alpha);
        }

        private static BitmapSource Circle(int width, int height, double radius)
        {
            var cx = (width - 1) * 0.5;
            var cy = (height - 1) * 0.5;
            return Create(width, height, (x, y) =>
            {
                var dx = x - cx;
                var dy = y - cy;
                return Math.Sqrt((dx * dx) + (dy * dy)) <= radius ? (byte)255 : (byte)0;
            });
        }

        private static BitmapSource Rectangle(
            int width, int height, int left, int top, int right, int bottom, byte backgroundAlpha = 0)
        {
            return Create(width, height, (x, y) =>
                x >= left && x < right && y >= top && y < bottom ? (byte)255 : backgroundAlpha);
        }

        private static BitmapSource Crescent(int width, int height)
        {
            var cx = (width - 1) * 0.5;
            var cy = (height - 1) * 0.5;
            return Create(width, height, (x, y) =>
            {
                var outer = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                var bite = Math.Sqrt(((x - (cx + 12.5)) * (x - (cx + 12.5))) + ((y - cy) * (y - cy)));
                return outer <= 28 && bite > 22 ? (byte)255 : (byte)0;
            });
        }

        private static BitmapSource Create(int width, int height, Func<int, int, byte> alphaAt)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = (y * stride) + (x * 4);
                    pixels[index + 0] = 255;
                    pixels[index + 1] = 255;
                    pixels[index + 2] = 255;
                    pixels[index + 3] = alphaAt(x, y);
                }
            }

            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
