using System;
using System.Collections.Generic;
using System.Windows;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// A closed loop around a subject's silhouette, in normalized coordinates, that ray arrow bases
    /// ride along. Built once per icon by <see cref="RayTrackBuilder"/> and shared by every control
    /// showing that icon, so it is immutable and safe to hand across threads.
    ///
    /// The loop is the smoothed convex hull of the subject's alpha. Convex is deliberate: on a convex
    /// loop every outward normal diverges, so arrows can never cross one another or aim back into the
    /// art. Samples are evenly spaced by arc length, so the sample at normalized position u is simply
    /// index u * <see cref="SampleCount"/> — no arc-length table and no search.
    ///
    /// Coordinates are normalized ISOTROPICALLY: both axes divide by the bitmap's longer side, with the
    /// shorter side centered, so the unit square maps to a square region and the subject keeps its true
    /// proportions. Normalizing per axis would squash the space, and a "unit normal" stored in a squashed
    /// space is not perpendicular to the on-screen curve once a consumer un-squashes it — which would
    /// silently tilt every arrow on non-square cover art.
    /// </summary>
    public sealed class RayTrack
    {
        /// <summary>
        /// Samples around the loop. Chord-vs-arc error at this count is about 0.03% of the radius,
        /// sub-pixel even on a full-size cover, and a power of two lets consumers index directly.
        /// </summary>
        public const int SampleCount = 128;

        private readonly Point[] _points;
        private readonly Vector[] _normals;

        private RayTrack(
            Point[] points,
            Vector[] normals,
            Point centroid,
            double normalizedPerimeter,
            double sourceAspect,
            bool isAnalytic,
            bool isEmpty)
        {
            _points = points;
            _normals = normals;
            Centroid = centroid;
            NormalizedPerimeter = normalizedPerimeter;
            SourceAspect = sourceAspect;
            IsAnalytic = isAnalytic;
            IsEmpty = isEmpty;
        }

        /// <summary>Loop samples, ordered counterclockwise as they appear on screen.</summary>
        public IReadOnlyList<Point> Points => _points;

        /// <summary>Unit outward normal at each sample.</summary>
        public IReadOnlyList<Vector> Normals => _normals;

        public Point Centroid { get; }

        /// <summary>Loop length in normalized units, so a consumer can scale it by its own mapping.</summary>
        public double NormalizedPerimeter { get; }

        /// <summary>Source bitmap width divided by its height, for placing the loop on the drawn artwork.</summary>
        public double SourceAspect { get; }

        /// <summary>True when this is a generated rounded rectangle rather than a traced silhouette.</summary>
        public bool IsAnalytic { get; }

        /// <summary>
        /// True when analysis ran and found nothing above the alpha threshold. Distinct from a track
        /// that has not been built yet: there is no subject here, so consumers draw nothing rather than
        /// ringing an empty cell.
        /// </summary>
        public bool IsEmpty { get; }

        /// <summary>
        /// Direct array access for per-frame consumers, avoiding an interface dispatch per sample.
        /// The arrays never escape this assembly and are never written after construction.
        /// </summary>
        internal Point[] PointsArray => _points;

        internal Vector[] NormalsArray => _normals;

        internal static RayTrack Create(
            Point[] points,
            Vector[] normals,
            Point centroid,
            double sourceAspect,
            bool isAnalytic,
            bool isEmpty)
        {
            var perimeter = 0.0;
            for (var i = 0; i < points.Length; i++)
            {
                perimeter += (points[(i + 1) % points.Length] - points[i]).Length;
            }

            return new RayTrack(points, normals, centroid, perimeter, sourceAspect, isAnalytic, isEmpty);
        }

        // Analytic tracks are the effect's default look, not an error path: they cover every opaque
        // rectangle (most achievement icons, and every cover), animated sources, decode failures and
        // the moment before a real track arrives. Only a handful of (radius, aspect) pairs are ever
        // asked for, so they are built once and shared.
        //
        // Declared ahead of Empty on purpose: static field initializers run in declaration order, and
        // Empty is built through RoundedRect, which locks and reads these.
        private static readonly object AnalyticLock = new object();
        private static readonly Dictionary<long, RayTrack> AnalyticCache = new Dictionary<long, RayTrack>();

        /// <summary>An analysis result carrying no subject. Shares one instance; consumers draw nothing.</summary>
        public static readonly RayTrack Empty = CreateEmpty();

        private static RayTrack CreateEmpty()
        {
            var track = RoundedRect(1.0, 0.0);
            return new RayTrack(
                track.PointsArray,
                track.NormalsArray,
                track.Centroid,
                track.NormalizedPerimeter,
                1.0,
                isAnalytic: true,
                isEmpty: true);
        }

        /// <summary>
        /// A rounded rectangle covering the subject's own rect within the unit square, for subjects with
        /// no traceable silhouette.
        /// </summary>
        public static RayTrack RoundedRect(double sourceAspect, double cornerRadiusRatio)
        {
            var aspect = Sane(sourceAspect, 1.0, 0.01, 100.0);
            var radius = Sane(cornerRadiusRatio, 0.0, 0.0, 0.5);

            // Quantize the key so binding churn on a double-valued property cannot grow the cache.
            var key = ((long)Math.Round(aspect * 1000.0) << 20) | (long)Math.Round(radius * 1000.0);
            lock (AnalyticLock)
            {
                if (AnalyticCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var built = BuildRoundedRect(aspect, radius);
                AnalyticCache[key] = built;
                return built;
            }
        }

        private static RayTrack BuildRoundedRect(double aspect, double radiusRatio)
        {
            // Isotropic normalization: the bitmap's longer side spans the unit square and the shorter
            // side is centered within it, matching what RayTrackBuilder produces for a real silhouette.
            var width = aspect >= 1.0 ? 1.0 : aspect;
            var height = aspect >= 1.0 ? 1.0 / aspect : 1.0;
            var left = (1.0 - width) * 0.5;
            var top = (1.0 - height) * 0.5;
            var radius = Math.Min(radiusRatio * Math.Min(width, height), Math.Min(width, height) * 0.5);

            // Walk the outline counterclockwise ON SCREEN, which in these y-down coordinates means
            // top-left, down the left edge, along the bottom, up the right, back across the top.
            var straight = 2.0 * ((width - (2.0 * radius)) + (height - (2.0 * radius)));
            var arcs = 2.0 * Math.PI * radius;
            var perimeter = straight + arcs;
            if (!(perimeter > 0))
            {
                perimeter = 1.0;
            }

            var points = new Point[SampleCount];
            var normals = new Vector[SampleCount];
            var step = perimeter / SampleCount;

            for (var i = 0; i < SampleCount; i++)
            {
                WalkRoundedRect(
                    i * step, left, top, width, height, radius,
                    out points[i], out normals[i]);
            }

            var centroid = new Point(left + (width * 0.5), top + (height * 0.5));
            return Create(points, normals, centroid, aspect, isAnalytic: true, isEmpty: false);
        }

        private static void WalkRoundedRect(
            double distance,
            double left,
            double top,
            double width,
            double height,
            double radius,
            out Point point,
            out Vector normal)
        {
            var right = left + width;
            var bottom = top + height;
            var innerW = Math.Max(0.0, width - (2.0 * radius));
            var innerH = Math.Max(0.0, height - (2.0 * radius));
            var quarter = Math.PI * 0.5 * radius;

            // Segments in traversal order, each paired with the corner arc that follows it.
            // Left edge downward, bottom-left arc, bottom edge rightward, bottom-right arc,
            // right edge upward, top-right arc, top edge leftward, top-left arc.
            var d = distance;

            if (d < innerH)
            {
                point = new Point(left, top + radius + d);
                normal = new Vector(-1, 0);
                return;
            }

            d -= innerH;
            if (d < quarter)
            {
                EmitArc(new Point(left + radius, bottom - radius), radius, Math.PI, d / radius, out point, out normal);
                return;
            }

            d -= quarter;
            if (d < innerW)
            {
                point = new Point(left + radius + d, bottom);
                normal = new Vector(0, 1);
                return;
            }

            d -= innerW;
            if (d < quarter)
            {
                EmitArc(new Point(right - radius, bottom - radius), radius, Math.PI * 0.5, d / radius, out point, out normal);
                return;
            }

            d -= quarter;
            if (d < innerH)
            {
                point = new Point(right, bottom - radius - d);
                normal = new Vector(1, 0);
                return;
            }

            d -= innerH;
            if (d < quarter)
            {
                EmitArc(new Point(right - radius, top + radius), radius, 0.0, d / radius, out point, out normal);
                return;
            }

            d -= quarter;
            if (d < innerW)
            {
                point = new Point(right - radius - d, top);
                normal = new Vector(0, -1);
                return;
            }

            d -= innerW;
            EmitArc(new Point(left + radius, top + radius), radius, -Math.PI * 0.5, d / radius, out point, out normal);
        }

        private static void EmitArc(
            Point center, double radius, double startAngle, double sweep, out Point point, out Vector normal)
        {
            // A square track has no arcs to walk, and the final segment falls through to here with a
            // zero radius: sit on the corner facing the direction the edge before it faced.
            if (!(radius > 0) || double.IsNaN(sweep) || double.IsInfinity(sweep))
            {
                sweep = 0.0;
            }

            // Angles are measured from the +x axis and DECREASE along the traversal, because a
            // counterclockwise-on-screen walk runs clockwise in the usual y-up sense.
            var angle = startAngle - sweep;
            normal = new Vector(Math.Cos(angle), Math.Sin(angle));
            point = new Point(center.X + (normal.X * radius), center.Y + (normal.Y * radius));
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
