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
        /// <summary>
        /// Arrow count for the square subject the cases below use. Derived rather than fixed, because
        /// the effect is specified by the gap between arrows and the count follows from the track.
        /// </summary>
        private static readonly int Count = RayArrowLayout.ArrowCountFor(Square());

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
            // total still needs no clamping, and only the travelling part depends on position, so the
            // loop has no seam.
            foreach (var count in new[] { 3, 8, 21, 22, 32 })
            {
                for (var index = 0; index < count; index++)
                {
                    for (var i = 0; i <= 400; i++)
                    {
                        var u = -2.0 + (i * 4.0 / 400);
                        var value = RayArrowLayout.ArrowHeight01(u, index, count);
                        Assert.IsTrue(
                            value >= -1e-12 && value <= 1.0 + 1e-12, $"h({u}, {index}, {count}) = {value}");
                    }

                    for (var i = 0; i < 50; i++)
                    {
                        var u = i / 50.0;
                        Assert.AreEqual(
                            RayArrowLayout.ArrowHeight01(u, index, count),
                            RayArrowLayout.ArrowHeight01(u + 1.0, index, count),
                            1e-9,
                            $"the wave does not meet itself at the seam for {count} arrows");
                    }
                }
            }
        }

        [TestMethod]
        public void ArrowHeight01_AlternatesByArrowRatherThanByPosition()
        {
            // Keyed to a position, the tall and short arrows traded places every time the travelling
            // wave swept past. Keyed to the arrow, an arrow's share of the zigzag is the same wherever
            // the wave happens to be, so a tall one stays tall as it travels.
            foreach (var count in new[] { 8, 22, 32 })
            {
                for (var index = 0; index < count; index++)
                {
                    var reference = RayArrowLayout.ArrowHeight01(0.0, index, count)
                                    - RayArrowLayout.ArrowHeight01(0.0, (index + 1) % count, count);

                    foreach (var u in new[] { 0.13, 0.37, 0.62, 0.88 })
                    {
                        var gap = RayArrowLayout.ArrowHeight01(u, index, count)
                                  - RayArrowLayout.ArrowHeight01(u, (index + 1) % count, count);
                        Assert.AreEqual(
                            reference, gap, 1e-9,
                            $"arrow {index} of {count} changes its share of the zigzag with the wave");
                    }
                }
            }
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
            // Not a strict up-down-up: the travelling harmonic is the larger of the two at the tuned
            // values, so it carries runs of arrows the same way before the zigzag flips them back.
            Assert.IsTrue(
                reversals >= written / 3,
                $"the ring only changes direction {reversals} times over {written} arrows");
        }

        // Arrows no longer hold their rank against their neighbours for the life of the burst, and there
        // is deliberately no test that they do. That held while the fixed zigzag outweighed the
        // travelling wave; at the tuned values the wave is the larger of the two, so it reorders
        // neighbours as it passes. What survives is that an arrow's own share of the zigzag never
        // changes, which ArrowHeight01_AlternatesByArrowRatherThanByPosition covers.

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
        public void Wave_NeverSitsStillAndMirroredOnTheArtwork()
        {
            // The thing that looked wrong was a mirror line pinned to the same part of every icon. Two
            // separate properties can prevent it: an envelope that is not symmetric, or one that does
            // not sit still. The tuned harmonics are symmetric — one of the three is switched off, and
            // the remaining pair vanishes together at the start of the loop — so it is the drift that
            // carries this now, and the drift is what has to be defended.
            var symmetric = WorstMirrorGap() <= 0.05;
            if (!symmetric)
            {
                return;
            }

            Assert.AreNotEqual(
                0.0,
                RayArrowLayout.EnvelopeDriftRatio,
                "the envelope is mirror-symmetric AND stationary, which pins a mirror line to the artwork");
        }

        private static double WorstMirrorGap()
        {
            var worst = double.MaxValue;

            for (var c = 0; c < 360; c++)
            {
                var center = c / 360.0;
                var largest = 0.0;

                for (var d = 1; d <= 60; d++)
                {
                    var offset = d / 240.0;
                    largest = Math.Max(
                        largest,
                        Math.Abs(RayArrowLayout.WaveHeight01(center + offset)
                                 - RayArrowLayout.WaveHeight01(center - offset)));
                }

                worst = Math.Min(worst, largest);
            }

            return worst;
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
        public void BuildSpines_KeepsTheReadablePartOfEachRayWithinItsGap()
        {
            // Rays are softened by stacking progressively wider translucent copies. The faint outer ones
            // are meant to spill into their neighbours — that is what blurring anything does — but every
            // copy strong enough to read as part of a particular ray has to stay inside the gap, or the
            // rays stop being separate rays.
            var mapped = Square();
            var spines = new RayArrowLayout.RayArrowSpine[Count];
            var written = RayArrowLayout.BuildSpines(mapped, 0.0, 1.9, Count, spines);

            var widest = 0.0;
            for (var i = 0; i < written; i++)
            {
                widest = Math.Max(widest, spines[i].HalfWidth);
            }

            var gap = mapped.Perimeter / Count;
            var spilled = false;
            var brightest = 0.0;
            byte brightestAlpha = 0;

            foreach (var layer in RarityAppearanceHelper.GetRayGlowPalette(RarityTier.Rare).Layers)
            {
                var width = 2.0 * widest * layer.WidthMultiplier;
                if (layer.Brush.Color.A >= brightestAlpha)
                {
                    brightestAlpha = layer.Brush.Color.A;
                    brightest = width;
                }

                if (width > gap)
                {
                    spilled = true;
                }
            }

            // Only the brightest copy has to stay inside the gap now. The ones between it and the
            // faintest are allowed across: at the tuned density the rays are meant to blend into one
            // band with bright spines through it, rather than stand apart as separate rays.
            Assert.IsTrue(
                brightest < gap,
                $"the brightest copy spans {brightest:N1} of a {gap:N1} gap, so no ray has a distinct core");
            Assert.IsTrue(spilled, "no copy reaches past the gap, so the rays have no soft tails at all");
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
        public void ArrowCountFor_HoldsSpacingConstantAcrossSubjectSizes()
        {
            // The effect is specified by the gap between arrows, not by how many there are. A fixed
            // count spread around a longer outline gives fewer, fatter arrows further apart — a
            // notification card's outline is about five times an icon's, and the same count read as
            // sparse studs there while looking right on the icon.
            var subjects = new[]
            {
                new { Name = "compact icon", Aspect = 1.0, Slot = new Size(48, 48) },
                new { Name = "grid icon", Aspect = 1.0, Slot = new Size(72, 88) },
                new { Name = "2:3 cover", Aspect = 2.0 / 3.0, Slot = new Size(80, 120) },
                new { Name = "wide banner", Aspect = 16.0 / 9.0, Slot = new Size(160, 90) },
                new { Name = "toast card", Aspect = 410.0 / 96.0, Slot = new Size(410, 96) }
            };

            foreach (var subject in subjects)
            {
                var mapped = RayArrowLayout.Map(RayTrack.RoundedRect(subject.Aspect, 0.12), subject.Slot, 0.0);
                var count = RayArrowLayout.ArrowCountFor(mapped);

                Assert.AreEqual(0, count % 2, $"{subject.Name} got an odd count, so the zigzag cannot close");

                var gap = mapped.Perimeter / count;
                Assert.AreEqual(
                    RayArrowLayout.ArrowSpacing, gap, RayArrowLayout.ArrowSpacing * 0.35,
                    $"{subject.Name} spaces its arrows {gap:N1} apart, not about {RayArrowLayout.ArrowSpacing:N1}");
            }
        }

        [TestMethod]
        public void ScaleLapsToTrack_MovesArrowsAtOneSpeedWhateverTheyRing()
        {
            // Timed in laps, a notification card turns as often as an icon does — and its outline is
            // several times longer, so its arrows crossed the screen several times faster. Scaling the
            // lap rate by the outline's length turns the setting into a speed.
            var subjects = new[]
            {
                new { Name = "grid icon", Aspect = 1.0, Slot = new Size(72, 88) },
                new { Name = "2:3 cover", Aspect = 2.0 / 3.0, Slot = new Size(80, 120) },
                new { Name = "toast card", Aspect = 410.0 / 96.0, Slot = new Size(410, 96) }
            };

            double? firstSpeed = null;

            foreach (var subject in subjects)
            {
                var mapped = RayArrowLayout.Map(RayTrack.RoundedRect(subject.Aspect, 0.12), subject.Slot, 0.0);

                // Distance one arrow covers along the track for the same elapsed time.
                var laps = RayArrowLayout.ScaleLapsToTrack(0.01, mapped);
                var travelled = laps * mapped.Perimeter;

                if (firstSpeed == null)
                {
                    firstSpeed = travelled;
                    continue;
                }

                Assert.AreEqual(
                    firstSpeed.Value, travelled, 1e-9,
                    $"{subject.Name} arrows travel a different distance per tick");
            }
        }

        [TestMethod]
        public void ArrowCountFor_ToleratesDegenerateTracks()
        {
            var count = RayArrowLayout.ArrowCountFor(null);
            Assert.IsTrue(count > 0 && count % 2 == 0, $"null track produced {count}");
        }

        [TestMethod]
        public void BuildSpines_KeepsRayProportionsAcrossSubjectShapes()
        {
            // A ray should look like the same ray on any subject. Width used to come from the gap
            // between arrows, which grows with the perimeter while reach grows with the artwork, so a
            // cover — whose outline is half again as long as a square icon's of the same width — got
            // arrows no longer but much wider, reading as bumps around the edge instead of rays.
            var shapes = new[]
            {
                new { Name = "square icon", Aspect = 1.0, Slot = new Size(72, 88) },
                new { Name = "2:3 cover", Aspect = 2.0 / 3.0, Slot = new Size(80, 120) },
                new { Name = "16:9 banner", Aspect = 16.0 / 9.0, Slot = new Size(160, 90) },
                new { Name = "small icon", Aspect = 1.0, Slot = new Size(48, 48) }
            };

            double? firstSlenderness = null;
            double? firstReachRatio = null;

            foreach (var shape in shapes)
            {
                var mapped = RayArrowLayout.Map(RayTrack.RoundedRect(shape.Aspect, 0.18), shape.Slot, 0.0);
                var spines = new RayArrowLayout.RayArrowSpine[Count];
                var written = RayArrowLayout.BuildSpines(mapped, 0.0, 1.55, Count, spines);

                double tallest = 0.0, widest = 0.0;
                for (var i = 0; i < written; i++)
                {
                    tallest = Math.Max(tallest, spines[i].Height);
                    widest = Math.Max(widest, spines[i].HalfWidth);
                }

                var slenderness = tallest / (2.0 * widest);
                var reachRatio = tallest / mapped.SubjectScale;

                if (firstSlenderness == null)
                {
                    firstSlenderness = slenderness;
                    firstReachRatio = reachRatio;
                    continue;
                }

                Assert.AreEqual(
                    firstSlenderness.Value, slenderness, 0.25,
                    $"{shape.Name} rays are a different shape from the first subject's");
                Assert.AreEqual(
                    firstReachRatio.Value, reachRatio, 0.02,
                    $"{shape.Name} rays reach a different fraction of their artwork");
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
