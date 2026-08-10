using System;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Views.Controls.RayGlow;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// Locks the arrow placement behind the rays glow. The headline invariant is the periodicity one:
    /// because arrow height is keyed to position on the loop rather than to which arrow it is,
    /// advancing the phase by 1/N has to land each arrow on its neighbour's place and height.
    /// </summary>
    [TestClass]
    public class RayArrowLayoutTests
    {
        private const int Count = RayArrowLayout.DefaultArrowCount;

        private static RayArrowLayout.MappedTrack Square()
        {
            return RayArrowLayout.Map(RayTrack.RoundedRect(1.0, 0.12), new Size(72, 88), 2.0);
        }

        [TestMethod]
        public void Map_PlacesTheLoopOnTheDrawnArtwork()
        {
            var mapped = Square();

            Assert.IsNotNull(mapped);
            Assert.IsTrue(mapped.Perimeter > 0);

            // The subject is drawn Stretch="Uniform" inside a 72x88 slot inset by 2, so a square one
            // settles at 68x68 and that short side is what every arrow dimension scales by.
            Assert.AreEqual(68.0, mapped.SubjectMin, 1e-9);
        }

        [TestMethod]
        public void BuildSpines_AtPhasePlusOneOverN_IsThePreviousFrameRotatedByOneArrow()
        {
            var mapped = Square();

            foreach (var phase in new[] { 0.0, 0.017, 0.33, 0.71, 0.999 })
            {
                var before = new RayArrowLayout.RayArrowSpine[Count];
                var after = new RayArrowLayout.RayArrowSpine[Count];
                RayArrowLayout.BuildSpines(mapped, phase, 1.9, Count, before);
                RayArrowLayout.BuildSpines(mapped, phase + (1.0 / Count), 1.9, Count, after);

                for (var i = 0; i < Count; i++)
                {
                    var neighbour = before[(i + 1) % Count];
                    Assert.AreEqual(neighbour.Base.X, after[i].Base.X, 1e-9);
                    Assert.AreEqual(neighbour.Base.Y, after[i].Base.Y, 1e-9);
                    Assert.AreEqual(neighbour.Height, after[i].Height, 1e-9);
                    Assert.AreEqual(neighbour.HalfWidth, after[i].HalfWidth, 1e-9);
                }
            }
        }

        [TestMethod]
        public void WaveHeight01_StaysInRangeWithoutClamping()
        {
            // The coefficients sum to one by construction, so nothing has to clamp the result.
            for (var i = 0; i <= 20000; i++)
            {
                var u = -3.0 + (i * 6.0 / 20000);
                var value = RayArrowLayout.WaveHeight01(u);
                Assert.IsTrue(value >= -1e-12 && value <= 1.0 + 1e-12, $"w({u}) = {value}");
            }
        }

        [TestMethod]
        public void WaveHeight01_ClosesOnTheLoop()
        {
            // Both harmonics have an integer frequency, so the wave meets itself after one lap and an
            // arrow crossing the seam does not jump.
            for (var i = 0; i < 500; i++)
            {
                var u = i / 500.0;
                Assert.AreEqual(RayArrowLayout.WaveHeight01(u), RayArrowLayout.WaveHeight01(u + 1.0), 1e-9);
            }
        }

        [TestMethod]
        public void SampleAt_WrapsAroundTheLoop()
        {
            var mapped = Square();

            RayArrowLayout.SampleAt(mapped, 0.0, out var atZero, out _);
            RayArrowLayout.SampleAt(mapped, 1.0, out var atOne, out _);
            Assert.AreEqual(atZero.X, atOne.X, 1e-9);
            Assert.AreEqual(atZero.Y, atOne.Y, 1e-9);

            RayArrowLayout.SampleAt(mapped, 0.25, out var quarter, out _);
            RayArrowLayout.SampleAt(mapped, 1.25, out var overOne, out _);
            Assert.AreEqual(quarter.X, overOne.X, 1e-9);
            Assert.AreEqual(quarter.Y, overOne.Y, 1e-9);
        }

        [TestMethod]
        public void BuildSpines_AdvancesAlongTheLoopOrder()
        {
            // The loop is stored counterclockwise as it appears on screen, so a rising phase has to
            // carry the bases that way too.
            var mapped = Square();
            var before = new RayArrowLayout.RayArrowSpine[Count];
            var after = new RayArrowLayout.RayArrowSpine[Count];
            RayArrowLayout.BuildSpines(mapped, 0.20, 1.9, Count, before);
            RayArrowLayout.BuildSpines(mapped, 0.201, 1.9, Count, after);

            var loopSign = 0;
            for (var i = 0; i < mapped.Points.Length; i++)
            {
                var from = mapped.Points[i];
                var step = mapped.Points[(i + 1) % mapped.Points.Length] - from;
                var radial = from - mapped.Centroid;
                loopSign += Math.Sign((radial.X * step.Y) - (radial.Y * step.X));
            }

            loopSign = Math.Sign(loopSign);
            Assert.IsTrue(loopSign < 0, "loop should be counterclockwise on screen");

            for (var i = 0; i < Count; i++)
            {
                var radial = before[i].Base - mapped.Centroid;
                var step = after[i].Base - before[i].Base;
                Assert.AreEqual(
                    loopSign,
                    Math.Sign((radial.X * step.Y) - (radial.Y * step.X)),
                    $"arrow {i} moved against the loop");
            }
        }

        [TestMethod]
        public void Emit_HidesBasesInsideTheLoopAndPushesTipsOutside()
        {
            var mapped = Square();
            var spines = new RayArrowLayout.RayArrowSpine[Count];
            var quads = new RayArrowLayout.RayArrowQuad[Count];
            var written = RayArrowLayout.BuildSpines(mapped, 0.31, 1.9, Count, spines);
            RayArrowLayout.Emit(spines, written, 1.35, 1.0, quads);

            for (var i = 0; i < written; i++)
            {
                Assert.IsTrue(Inside(quads[i].BaseLeft, mapped), $"arrow {i} left base escaped the subject");
                Assert.IsTrue(Inside(quads[i].BaseRight, mapped), $"arrow {i} right base escaped the subject");
                Assert.IsFalse(Inside(quads[i].TipLeft, mapped), $"arrow {i} left tip stayed buried");
                Assert.IsFalse(Inside(quads[i].TipRight, mapped), $"arrow {i} right tip stayed buried");
            }
        }

        [TestMethod]
        public void BuildSpines_KeepsAdjacentArrowsApart()
        {
            // Arrows that touch read as a collar rather than as rays.
            var mapped = Square();
            var spines = new RayArrowLayout.RayArrowSpine[Count];
            var written = RayArrowLayout.BuildSpines(mapped, 0.0, 1.9, Count, spines);

            var widest = 0.0;
            for (var i = 0; i < written; i++)
            {
                widest = Math.Max(widest, spines[i].HalfWidth);
            }

            Assert.IsTrue(2.0 * widest * 1.35 < mapped.Perimeter / Count, "the widest halo arrow fills its gap");
        }

        [TestMethod]
        public void BuildSpines_ReachScalesLinearlyWithBurstScale()
        {
            Assert.AreEqual(0.0, MaxHeight(1.0), 1e-12, "no reach means no arrows past the edge");

            var atOneAndAHalf = MaxHeight(1.5);
            var atTwo = MaxHeight(2.0);
            var atThree = MaxHeight(3.0);
            Assert.AreEqual((atTwo - atOneAndAHalf) * 2.0, atThree - atTwo, 1e-9);
        }

        [TestMethod]
        public void Map_RejectsSlotsItCannotDrawIn()
        {
            var track = RayTrack.RoundedRect(1.0, 0.12);

            Assert.IsNull(RayArrowLayout.Map(track, new Size(0, 0), 0.0));
            Assert.IsNull(RayArrowLayout.Map(track, new Size(0, 40), 0.0));
            Assert.IsNull(RayArrowLayout.Map(track, new Size(40, 0), 0.0));
            Assert.IsNull(RayArrowLayout.Map(track, new Size(10, 10), 8.0), "inset should not invert the slot");
            Assert.IsNull(RayArrowLayout.Map(null, new Size(40, 40), 0.0));
        }

        [TestMethod]
        public void BuildSpines_ToleratesNonsenseInputs()
        {
            var mapped = Square();
            var spines = new RayArrowLayout.RayArrowSpine[Count];

            Assert.AreEqual(0, RayArrowLayout.BuildSpines(null, 0.2, 1.9, Count, spines));
            Assert.AreEqual(0, RayArrowLayout.BuildSpines(mapped, 0.2, 1.9, 0, spines));
            Assert.AreEqual(0, RayArrowLayout.BuildSpines(mapped, 0.2, 1.9, Count, null));
            Assert.AreEqual(Count, RayArrowLayout.BuildSpines(mapped, 0.2, 1.9, 999, spines), "should clamp to the buffer");

            var nonsense = new[]
            {
                double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -5.0
            };

            foreach (var scale in nonsense)
            {
                var written = RayArrowLayout.BuildSpines(mapped, 0.2, scale, Count, spines);
                AssertFinite(spines, written, $"BurstScale {scale}");
            }

            foreach (var phase in new[] { double.NaN, double.PositiveInfinity, -7.25, 12345.5 })
            {
                var written = RayArrowLayout.BuildSpines(mapped, phase, 1.9, Count, spines);
                AssertFinite(spines, written, $"phase {phase}");
            }
        }

        [TestMethod]
        public void Map_KeepsNormalsUnitOnNonSquareCoverArt()
        {
            // A uniform scale is what preserves this. Scaling per axis would tilt every arrow toward
            // the long side of the art.
            var mapped = RayArrowLayout.Map(RayTrack.RoundedRect(2.0 / 3.0, 0.06), new Size(200, 300), 0.0);
            var spines = new RayArrowLayout.RayArrowSpine[Count];
            var written = RayArrowLayout.BuildSpines(mapped, 0.13, 1.7, Count, spines);

            for (var i = 0; i < written; i++)
            {
                Assert.AreEqual(1.0, spines[i].Normal.Length, 1e-9, $"arrow {i} normal is not a unit vector");
                Assert.AreEqual(1.0, spines[i].Tangent.Length, 1e-9, $"arrow {i} tangent is not a unit vector");
            }
        }

        private static double MaxHeight(double burstScale)
        {
            var spines = new RayArrowLayout.RayArrowSpine[Count];
            var written = RayArrowLayout.BuildSpines(Square(), 0.0, burstScale, Count, spines);

            var max = 0.0;
            for (var i = 0; i < written; i++)
            {
                max = Math.Max(max, spines[i].Height);
            }

            return max;
        }

        private static void AssertFinite(RayArrowLayout.RayArrowSpine[] spines, int written, string because)
        {
            for (var i = 0; i < written; i++)
            {
                Assert.IsFalse(double.IsNaN(spines[i].Base.X) || double.IsNaN(spines[i].Base.Y), because);
                Assert.IsFalse(double.IsNaN(spines[i].Height) || double.IsInfinity(spines[i].Height), because);
                Assert.IsFalse(double.IsNaN(spines[i].HalfWidth) || double.IsInfinity(spines[i].HalfWidth), because);
            }
        }

        /// <summary>
        /// Crossing-number test. Distance from the middle is no substitute on anything but a circle,
        /// since a corner of the loop sits further out than the middle of an edge.
        /// </summary>
        private static bool Inside(Point point, RayArrowLayout.MappedTrack track)
        {
            var inside = false;
            var count = track.Points.Length;

            for (var i = 0; i < count; i++)
            {
                var a = track.Points[i];
                var b = track.Points[(i + 1) % count];
                if (((a.Y > point.Y) != (b.Y > point.Y)) &&
                    (point.X < (((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)))
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }
}
