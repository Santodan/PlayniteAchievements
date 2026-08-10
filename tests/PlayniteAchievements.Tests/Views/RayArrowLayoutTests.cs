using System;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
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
        public void BuildSpines_AtLapsPlusOneOverN_MovesEachBaseOntoItsNeighbour()
        {
            // Positions are pure conveyor, so a step of one arrow-spacing still lands each base exactly
            // where its neighbour was. Heights deliberately do not follow — see the drift test below.
            var mapped = Square();

            foreach (var laps in new[] { 0.0, 0.017, 0.33, 0.71, 0.999, 4.25 })
            {
                var before = new RayArrowLayout.RayArrowSpine[Count];
                var after = new RayArrowLayout.RayArrowSpine[Count];
                RayArrowLayout.BuildSpines(mapped, laps, 1.9, Count, before);
                RayArrowLayout.BuildSpines(mapped, laps + (1.0 / Count), 1.9, Count, after);

                for (var i = 0; i < Count; i++)
                {
                    var neighbour = before[(i + 1) % Count];
                    Assert.AreEqual(neighbour.Base.X, after[i].Base.X, 1e-9);
                    Assert.AreEqual(neighbour.Base.Y, after[i].Base.Y, 1e-9);
                }
            }
        }

        [TestMethod]
        public void BuildSpines_WaveIsNotAnchoredToTheArtwork()
        {
            // Held still against the artwork, the burst's outline became a fixed property of each icon
            // and the same part of every icon always carried the tall arrows. The wave travels at its
            // own rate instead, so a base returning to the same place finds a different height.
            var mapped = Square();
            var first = new RayArrowLayout.RayArrowSpine[Count];
            var later = new RayArrowLayout.RayArrowSpine[Count];

            RayArrowLayout.BuildSpines(mapped, 0.0, 1.9, Count, first);
            RayArrowLayout.BuildSpines(mapped, 1.0, 1.9, Count, later);

            var moved = 0;
            for (var i = 0; i < Count; i++)
            {
                // A whole lap: every base is back where it started.
                Assert.AreEqual(first[i].Base.X, later[i].Base.X, 1e-9);
                Assert.AreEqual(first[i].Base.Y, later[i].Base.Y, 1e-9);

                if (Math.Abs(first[i].Height - later[i].Height) > 1e-6)
                {
                    moved++;
                }
            }

            Assert.IsTrue(moved > Count / 2, $"only {moved} of {Count} arrows changed height over a lap");
        }

        [TestMethod]
        public void ArrowHeight01_StaysInRangeAndClosesOnTheLoop()
        {
            // The alternating component is weighted against the rest rather than added on top, so the
            // total still needs no clamping. Its lobe count is integral so the loop has no seam, which
            // has to hold for an odd arrow count too, where the alternation cannot be exact.
            foreach (var count in new[] { 3, 8, 21, 22, 32 })
            {
                for (var i = 0; i <= 4000; i++)
                {
                    var u = -2.0 + (i * 4.0 / 4000);
                    var value = RayArrowLayout.ArrowHeight01(u, count);
                    Assert.IsTrue(value >= -1e-12 && value <= 1.0 + 1e-12, $"h({u}, {count}) = {value}");
                }

                for (var i = 0; i < 200; i++)
                {
                    var u = i / 200.0;
                    Assert.AreEqual(
                        RayArrowLayout.ArrowHeight01(u, count),
                        RayArrowLayout.ArrowHeight01(u + 1.0, count),
                        1e-9,
                        $"the wave does not meet itself at the seam for {count} arrows");
                }
            }
        }

        [TestMethod]
        public void AlternationLobes_StayBelowTheSamplingLimitAndCoprime()
        {
            // Exactly half the arrow count puts neighbours perfectly out of phase but collapses every
            // arrow onto the same two phases, so the whole ring flattens and inverts together instead of
            // the zigzag travelling round it. The frequency has to stay under that limit and share no
            // factor with the arrow count, so each arrow gets a phase of its own.
            foreach (var count in new[] { 4, 8, 12, 16, 20, 21, 22, 24, 26, 32, 36 })
            {
                var lobes = RayArrowLayout.AlternationLobes(count);

                Assert.IsTrue(lobes >= 1, $"{count} arrows produced {lobes} lobes");
                Assert.IsTrue(
                    lobes * 2 < count || count < 4,
                    $"{count} arrows alternate at {lobes} lobes, at or past the sampling limit");
                Assert.AreEqual(
                    1,
                    Gcd(lobes, count),
                    $"{lobes} lobes shares a factor with {count} arrows, so arrows repeat phases");
            }

            // The chosen count should be near the limit; a low frequency would not alternate at all.
            var chosen = RayArrowLayout.AlternationLobes(Count);
            Assert.IsTrue(
                chosen * 2 > Count - 6,
                $"{chosen} lobes is too slow to make neighbours differ across {Count} arrows");
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                var remainder = a % b;
                a = b;
                b = remainder;
            }

            return a;
        }

        [TestMethod]
        public void BuildSpines_NeighbouringArrowsDifferSharply()
        {
            // The slow harmonics alone only separate arrows that are far apart on the loop. A component
            // at half the arrow count is what makes each arrow differ from the ones either side of it.
            var mapped = Square();
            var spines = new RayArrowLayout.RayArrowSpine[Count];
            var written = RayArrowLayout.BuildSpines(mapped, 0.0, 1.55, Count, spines);

            double shortest = double.MaxValue, tallest = 0.0, stepTotal = 0.0;
            var reversals = 0;
            var previousSign = 0;

            for (var i = 0; i < written; i++)
            {
                shortest = Math.Min(shortest, spines[i].Height);
                tallest = Math.Max(tallest, spines[i].Height);

                var step = spines[(i + 1) % written].Height - spines[i].Height;
                stepTotal += Math.Abs(step);

                var sign = Math.Sign(step);
                if (previousSign != 0 && sign != 0 && sign != previousSign)
                {
                    reversals++;
                }

                if (sign != 0)
                {
                    previousSign = sign;
                }
            }

            var meanStep = stepTotal / written;
            Assert.IsTrue(
                meanStep > 0.25 * (tallest - shortest),
                $"neighbours differ by only {meanStep:N2} against a {tallest - shortest:N2} range");
            Assert.IsTrue(
                reversals > written / 2,
                $"the ring only changes direction {reversals} times over {written} arrows");
        }

        [TestMethod]
        public void BuildSpines_SizePatternRunsAgainstTheArrows()
        {
            // The wave travels backwards relative to the conveyor, so arrows meet the crests head-on.
            var mapped = Square();
            var spines = new RayArrowLayout.RayArrowSpine[Count];

            double crestTravel = 0.0, arrowTravel = 0.0;
            double previousCrest = 0.0, previousArrow = 0.0;

            for (var step = 0; step <= 400; step++)
            {
                RayArrowLayout.BuildSpines(mapped, step * 0.0025, 1.55, Count, spines);

                var tallest = 0;
                for (var i = 1; i < Count; i++)
                {
                    if (spines[i].Height > spines[tallest].Height)
                    {
                        tallest = i;
                    }
                }

                var crest = AngleAbout(mapped, spines[tallest].Base);
                var arrow = AngleAbout(mapped, spines[0].Base);

                if (step > 0)
                {
                    crest = Unwrap(crest, previousCrest);
                    arrow = Unwrap(arrow, previousArrow);
                    crestTravel += crest - previousCrest;
                    arrowTravel += arrow - previousArrow;
                }

                previousCrest = crest;
                previousArrow = arrow;
            }

            Assert.IsTrue(Math.Abs(arrowTravel) > 1.0, "the arrows barely moved, so the test proves nothing");
            Assert.IsTrue(Math.Abs(crestTravel) > 0.5, "the size pattern barely moved");
            Assert.AreNotEqual(
                Math.Sign(arrowTravel),
                Math.Sign(crestTravel),
                $"arrows went {arrowTravel:N2} rad and the size pattern went {crestTravel:N2} rad");
        }

        private static double AngleAbout(RayArrowLayout.MappedTrack track, Point point)
        {
            return Math.Atan2(point.Y - track.Centroid.Y, point.X - track.Centroid.X);
        }

        private static double Unwrap(double angle, double previous)
        {
            while (angle - previous > Math.PI)
            {
                angle -= 2.0 * Math.PI;
            }

            while (angle - previous < -Math.PI)
            {
                angle += 2.0 * Math.PI;
            }

            return angle;
        }

        [TestMethod]
        public void WaveHeight01_IsNotMirrorSymmetric()
        {
            // A cosine with no phase offset is symmetric about the start of the loop, and the loop starts
            // at a fixed place on the artwork — so a near-symmetric envelope put a mirror line through
            // the same part of every icon, which read as deliberate and wrong.
            var worstSymmetry = double.MaxValue;

            for (var c = 0; c < 360; c++)
            {
                var center = c / 360.0;
                var largestDifference = 0.0;

                for (var d = 1; d <= 60; d++)
                {
                    var offset = d / 240.0;
                    var difference = Math.Abs(
                        RayArrowLayout.WaveHeight01(center + offset)
                        - RayArrowLayout.WaveHeight01(center - offset));
                    largestDifference = Math.Max(largestDifference, difference);
                }

                worstSymmetry = Math.Min(worstSymmetry, largestDifference);
            }

            Assert.IsTrue(
                worstSymmetry > 0.18,
                $"the envelope mirrors about some point to within {worstSymmetry:N3}");
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
        public void BuildSpines_KeepsAdjacentArrowsApartEvenAtTheWidestLayer()
        {
            // Rays are softened by stacking progressively wider translucent copies, so it is the widest
            // of those, not the arrow's own width, that decides whether neighbours merge into a collar.
            var mapped = Square();
            var spines = new RayArrowLayout.RayArrowSpine[Count];
            var written = RayArrowLayout.BuildSpines(mapped, 0.0, 1.9, Count, spines);

            var widestLayer = 0.0;
            foreach (var layer in RarityAppearanceHelper.GetRayGlowPalette(RarityTier.Rare).Layers)
            {
                widestLayer = Math.Max(widestLayer, layer.WidthMultiplier);
            }

            var widest = 0.0;
            for (var i = 0; i < written; i++)
            {
                widest = Math.Max(widest, spines[i].HalfWidth);
            }

            Assert.IsTrue(
                2.0 * widest * widestLayer < mapped.Perimeter / Count,
                "the widest copy of an arrow fills the gap to its neighbour");
        }

        [TestMethod]
        public void RayGlowPalette_StacksIntoASoftEdge()
        {
            // A blur is out of reach here: it is a bitmap effect, and this layer moves every frame, so
            // WPF would re-render it to an intermediate surface per row per frame. The softness has to
            // come from the copies instead, which means each one must be narrower, shorter and stronger
            // than the one before it.
            var layers = RarityAppearanceHelper.GetRayGlowPalette(RarityTier.Rare).Layers;

            Assert.IsTrue(layers.Count >= 3, "too few copies to read as a falloff rather than a step");

            for (var i = 1; i < layers.Count; i++)
            {
                Assert.IsTrue(
                    layers[i].WidthMultiplier < layers[i - 1].WidthMultiplier,
                    $"copy {i} is not narrower than the one outside it");
                Assert.IsTrue(
                    layers[i].HeightFraction < layers[i - 1].HeightFraction,
                    $"copy {i} is not shorter than the one outside it");
                Assert.IsTrue(
                    layers[i].Brush.Color.A > layers[i - 1].Brush.Color.A,
                    $"copy {i} is not stronger than the one outside it");
            }

            Assert.IsTrue(layers[0].Brush.Color.A < 0x40, "the outermost copy should be a haze, not an outline");
            foreach (var layer in layers)
            {
                Assert.IsTrue(layer.Brush.IsFrozen, "layer brushes are shared across rows and must be frozen");
            }
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
