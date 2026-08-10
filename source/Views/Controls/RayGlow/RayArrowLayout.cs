using System;
using System.Windows;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Views.Controls.RayGlow
{
    /// <summary>
    /// Places the arrows of the rays glow on a subject's <see cref="RayTrack"/>.
    ///
    /// The bases ride the loop like a conveyor belt: one shared phase advances them all at a constant
    /// arc-length speed, so they stay evenly spaced whatever shape they are going around. Each arrow
    /// spans inward from the loop as well as outward, so the opaque subject covers its base and the
    /// subject's own pixels decide where the arrow appears to begin — nothing draws a hard start edge.
    ///
    /// Height and width come from a standing wave keyed to POSITION on the loop rather than to which
    /// arrow it is, so arrows swell and shrink as they pass through fixed regions of the outline. That
    /// makes the whole picture repeat every 1/N of a lap: advancing the phase by 1/N moves each arrow
    /// onto its neighbour's place and its neighbour's height, which is a rotation of the same frame.
    ///
    /// Pure math, no WPF control types, so it can be exercised directly by tests.
    /// </summary>
    public static class RayArrowLayout
    {
        private const double TwoPi = Math.PI * 2.0;

        /// <summary>
        /// Arrows around the loop. A 64 px icon's hull runs 200-256 DIP, so this spacing gives a base
        /// around 7-9 DIP: heavy enough to read on the 48 px compact list, fine enough not to merge into
        /// a collar on the widest grid icon.
        /// </summary>
        public const int DefaultArrowCount = 16;

        // Lobe counts of the standing wave. Both are coprime with the arrow count on purpose: a count
        // that divided it would have every arrow sampling the same few heights forever, and the burst
        // would read as a rigid scallop instead of a wave.
        private const int PrimaryLobes = 3;
        private const int SecondaryLobes = 5;
        private const double SecondaryPhase = 1.1;

        private const double MinHeightFraction = 0.35;
        private const double MinWidthScale = 0.60;

        /// <summary>How far inside the loop a base sits, as a fraction of the subject's short side.</summary>
        private const double InwardFraction = 0.22;

        private const double MinInwardDip = 5.0;

        /// <summary>Base width as a fraction of the gap between arrows, leaving them clearly separate.</summary>
        private const double WidthFraction = 0.55;

        /// <summary>Tips stay blunt: a needle antialiases into a thorn, a sliver into a ray.</summary>
        private const double TipWidthFraction = 0.14;

        /// <summary>A <see cref="RayTrack"/> placed on a specific layout slot, in device units.</summary>
        public sealed class MappedTrack
        {
            public Point[] Points;
            public Vector[] Normals;
            public Point Centroid;
            public double Perimeter;

            /// <summary>Short side of the drawn artwork, the yardstick every arrow dimension scales by.</summary>
            public double SubjectMin;
        }

        public struct RayArrowSpine
        {
            public Point Base;
            public Vector Normal;
            public Vector Tangent;
            public double Height;
            public double HalfWidth;
            public double Inward;
        }

        public struct RayArrowQuad
        {
            public Point BaseLeft;
            public Point TipLeft;
            public Point TipRight;
            public Point BaseRight;
        }

        /// <summary>
        /// Places a track on an arranged slot. The subject is drawn Stretch="Uniform", so it occupies a
        /// centered rectangle of its own aspect rather than the whole slot, and the loop has to land on
        /// that rectangle — the bases are only hidden if they coincide with the real artwork.
        ///
        /// Because the track normalizes isotropically, the unit square corresponds to a SQUARE of the
        /// artwork's longer side, so this is a single uniform scale. Normals therefore carry over
        /// untouched and arc length scales by the same factor, which is what lets the conveyor run at a
        /// constant speed on non-square art.
        /// </summary>
        public static MappedTrack Map(RayTrack track, Size slot, double inset)
        {
            if (track == null || track.PointsArray == null || track.PointsArray.Length < 3)
            {
                return null;
            }

            var slotWidth = slot.Width - (2.0 * Sane(inset, 0.0, 0.0, 64.0));
            var slotHeight = slot.Height - (2.0 * Sane(inset, 0.0, 0.0, 64.0));
            if (!IsUsable(slotWidth) || !IsUsable(slotHeight))
            {
                return null;
            }

            var aspect = Sane(track.SourceAspect, 1.0, 0.01, 100.0);
            var artHeight = Math.Min(slotHeight, slotWidth / aspect);
            var artWidth = artHeight * aspect;
            if (!IsUsable(artWidth) || !IsUsable(artHeight))
            {
                return null;
            }

            var side = Math.Max(artWidth, artHeight);
            var centerX = slot.Width * 0.5;
            var centerY = slot.Height * 0.5;

            var source = track.PointsArray;
            var sourceNormals = track.NormalsArray;
            var count = source.Length;
            var points = new Point[count];
            var normals = new Vector[count];

            for (var i = 0; i < count; i++)
            {
                points[i] = new Point(
                    centerX + ((source[i].X - 0.5) * side),
                    centerY + ((source[i].Y - 0.5) * side));
                normals[i] = sourceNormals[i];
            }

            return new MappedTrack
            {
                Points = points,
                Normals = normals,
                Centroid = new Point(
                    centerX + ((track.Centroid.X - 0.5) * side),
                    centerY + ((track.Centroid.Y - 0.5) * side)),
                Perimeter = track.NormalizedPerimeter * side,
                SubjectMin = Math.Min(artWidth, artHeight)
            };
        }

        /// <summary>
        /// Point and outward normal at normalized position u around the loop. Samples are evenly spaced
        /// by arc length, so this indexes straight in rather than searching an arc-length table.
        /// </summary>
        public static void SampleAt(MappedTrack track, double u, out Point point, out Vector normal)
        {
            var count = track.Points.Length;
            var scaled = Frac(u) * count;
            var index = (int)scaled;
            if (index >= count)
            {
                index = count - 1;
            }

            var t = scaled - index;
            var next = (index + 1) % count;

            var a = track.Points[index];
            var b = track.Points[next];
            point = new Point(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

            var na = track.Normals[index];
            var nb = track.Normals[next];
            var lerped = new Vector(na.X + ((nb.X - na.X) * t), na.Y + ((nb.Y - na.Y) * t));
            var length = lerped.Length;
            if (length > 1e-12)
            {
                normal = new Vector(lerped.X / length, lerped.Y / length);
                return;
            }

            // Adjacent normals never oppose on a convex loop, so this only fires on a degenerate track.
            var radial = point - track.Centroid;
            var radialLength = radial.Length;
            normal = radialLength > 1e-12
                ? new Vector(radial.X / radialLength, radial.Y / radialLength)
                : new Vector(0, -1);
        }

        /// <summary>
        /// Crest height at position u around the loop, in 0..1. Both harmonics have an integer frequency
        /// so the wave closes on itself around the loop, and the coefficients sum to one so the result
        /// stays in range without being clamped.
        /// </summary>
        public static double WaveHeight01(double u)
        {
            var wave = (0.62 * Math.Cos(TwoPi * PrimaryLobes * u))
                     + (0.38 * Math.Cos((TwoPi * SecondaryLobes * u) + SecondaryPhase));
            return 0.5 + (0.5 * wave);
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> with the arrow spines for a phase and returns how many were
        /// written. Every dimension scales off the subject's short side, so one set of constants serves
        /// a 48 px compact icon and a full-size cover alike.
        /// </summary>
        public static int BuildSpines(
            MappedTrack track, double phase01, double burstScale, int arrowCount, RayArrowSpine[] buffer)
        {
            if (track == null || buffer == null)
            {
                return 0;
            }

            var count = Math.Min(arrowCount, buffer.Length);
            if (count <= 0 || !IsUsable(track.Perimeter) || !IsUsable(track.SubjectMin))
            {
                return 0;
            }

            var scale = Sane(burstScale, 1.0, 1.0, 8.0);
            var reach = (scale - 1.0) * 0.5 * track.SubjectMin;
            var inward = Math.Max(MinInwardDip, InwardFraction * track.SubjectMin);
            var spacing = track.Perimeter / count;
            var phase = Frac(Sane(phase01, 0.0, -1e9, 1e9));

            for (var i = 0; i < count; i++)
            {
                var u = Frac((i / (double)count) + phase);
                var wave = WaveHeight01(u);
                SampleAt(track, u, out var basePoint, out var normal);

                buffer[i].Base = basePoint;
                buffer[i].Normal = normal;

                // Counterclockwise tangent, so an arrow's left and right flanks stay on consistent sides
                // all the way around the loop.
                buffer[i].Tangent = new Vector(-normal.Y, normal.X);
                buffer[i].Height = reach * (MinHeightFraction + ((1.0 - MinHeightFraction) * wave));
                buffer[i].HalfWidth = 0.5 * WidthFraction * spacing
                                          * (MinWidthScale + ((1.0 - MinWidthScale) * wave));
                buffer[i].Inward = inward;
            }

            return count;
        }

        /// <summary>
        /// Turns spines into quads at a given width and length multiple. Splitting this from
        /// <see cref="BuildSpines"/> lets several fill passes share one interpolation pass over the loop.
        /// </summary>
        public static void Emit(
            RayArrowSpine[] spines,
            int count,
            double widthMultiplier,
            double heightFraction,
            RayArrowQuad[] quads)
        {
            if (spines == null || quads == null)
            {
                return;
            }

            var limit = Math.Min(count, Math.Min(spines.Length, quads.Length));
            for (var i = 0; i < limit; i++)
            {
                var spine = spines[i];
                var halfWidth = spine.HalfWidth * widthMultiplier;
                var height = spine.Height * heightFraction;

                var inner = spine.Base - (spine.Normal * spine.Inward);
                var outer = spine.Base + (spine.Normal * height);
                var across = spine.Tangent * halfWidth;
                var tip = spine.Tangent * (halfWidth * TipWidthFraction);

                quads[i].BaseLeft = inner - across;
                quads[i].TipLeft = outer - tip;
                quads[i].TipRight = outer + tip;
                quads[i].BaseRight = inner + across;
            }
        }

        private static double Frac(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }

            var fraction = value - Math.Floor(value);
            return fraction < 0 ? 0.0 : (fraction >= 1.0 ? 0.0 : fraction);
        }

        private static bool IsUsable(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Sane(double value, double fallback, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return fallback;
            }

            return Math.Max(min, Math.Min(max, value));
        }
    }
}
