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
        /// Arrows around the loop. Width is derived from the spacing, so raising this thins the arrows
        /// to match rather than crowding them: a 64 px icon's hull runs 200-256 DIP, which at this count
        /// leaves a base around 4 DIP and a clear gap either side.
        /// </summary>
        public const int DefaultArrowCount = 32;

        // Lobe counts of the wave. All coprime with the arrow count on purpose: a count that divided it
        // would have every arrow sampling the same few heights forever, and the burst would read as a
        // rigid scallop instead of a wave.
        //
        // Three of them, with no dominant term, because two were not enough to look irregular: when one
        // carried most of the amplitude the envelope came out very nearly mirrored, and since the loop
        // starts at a fixed place on the artwork that mirror line landed on the same part of every icon.
        //
        // The phases are chosen so no such mirror line exists. Subtracting the wave from its own
        // reflection gives -SUM(amplitude * sin(2*pi*lobes*centre + phase) * sin(2*pi*lobes*offset)), so
        // the envelope mirrors about a point exactly when every one of those sines vanishes there at
        // once. Turning the upper two harmonics into sines is what keeps them from ever doing so
        // together: it triples the worst-case gap compared with rounder-looking offsets.
        private const int PrimaryLobes = 3;
        private const int SecondaryLobes = 5;
        private const int TertiaryLobes = 7;
        private const double PrimaryAmplitude = 0.42;
        private const double SecondaryAmplitude = 0.33;
        private const double TertiaryAmplitude = 0.25;
        private const double PrimaryPhase = 0.0;
        private const double SecondaryPhase = Math.PI / 2.0;
        private const double TertiaryPhase = Math.PI / 2.0;

        /// <summary>
        /// How fast the wave itself travels around the loop, relative to the arrows.
        ///
        /// Zero would hold it still against the artwork, which is what made the shape of the burst a
        /// fixed property of each icon rather than something happening to it. Matching the arrows would
        /// carry it along with them and no arrow would ever change size. Between the two, arrows drift
        /// through the crests and swell and shrink as they go, and nothing lines up with the icon
        /// underneath for long enough to look deliberate.
        /// </summary>
        private const double EnvelopeDriftRatio = 0.38;

        /// <summary>
        /// Height of the shortest arrow as a fraction of the tallest. Keeps the crests and troughs
        /// close enough together that the ring reads as one band with a wave running through it, rather
        /// than as tall spikes with gaps between them.
        /// </summary>
        private const double MinHeightFraction = 0.45;

        private const double MinWidthScale = 0.55;

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
        /// Crest height at position u around the wave, in 0..1. Every harmonic has an integer frequency
        /// so the wave closes on itself, and the amplitudes sum to one so the result stays in range
        /// without being clamped.
        /// </summary>
        public static double WaveHeight01(double u)
        {
            var wave = (PrimaryAmplitude * Math.Cos((TwoPi * PrimaryLobes * u) + PrimaryPhase))
                     + (SecondaryAmplitude * Math.Cos((TwoPi * SecondaryLobes * u) + SecondaryPhase))
                     + (TertiaryAmplitude * Math.Cos((TwoPi * TertiaryLobes * u) + TertiaryPhase));
            return 0.5 + (0.5 * wave);
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> with the arrow spines and returns how many were written.
        /// Every dimension scales off the subject's short side, so one set of constants serves a 48 px
        /// compact icon and a full-size cover alike.
        /// </summary>
        /// <param name="laps">
        /// Laps completed, not a wrapped fraction: the wave travels at its own rate, so it needs to know
        /// how far round the arrows have actually been rather than where in the current lap they are.
        /// </param>
        public static int BuildSpines(
            MappedTrack track, double laps, double burstScale, int arrowCount, RayArrowSpine[] buffer)
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
            var travelled = Sane(laps, 0.0, -1e9, 1e9);
            var phase = Frac(travelled);

            // The wave runs at its own rate, so an arrow's height depends on where it sits relative to
            // the wave rather than on where it sits on the artwork.
            var envelope = Frac(travelled * EnvelopeDriftRatio);

            for (var i = 0; i < count; i++)
            {
                var u = Frac((i / (double)count) + phase);
                var wave = WaveHeight01(u - envelope);
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
