using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Pure clip-window and buffer math over the rolling segment recording, all in UTC. The
    /// invariant every window upholds: a clip contains the unlock moment (with its pre-roll)
    /// plus a toast-duration slot after it — the toast itself is composited into the clip at
    /// export, so the window never depends on when the toast actually displayed on screen.
    /// Clamped only to recorded data. No filesystem access — fully unit-testable.
    /// </summary>
    internal static class SegmentTimeline
    {
        /// <summary>Tolerance (seconds past detection) a trusted unlock timestamp may carry.</summary>
        public const int PreciseLeadSeconds = 5;

        /// <summary>Width of the yyyyMMdd-HHmmss stamp every buffer file name starts with.</summary>
        private const int StampLength = 15;

        /// <summary>Windows that collapse below this are skipped by the caller.</summary>
        public const int MinimumWindowSeconds = 3;

        public sealed class SegmentInfo
        {
            public string Path { get; set; }

            public DateTime StartUtc { get; set; }

            public long SizeBytes { get; set; }

            /// <summary>
            /// Encoded dimensions parsed from the file name, 0 when the name carries none (audio
            /// chunks, and any segment written before the name included them). Segments that
            /// report the same pair can be concatenated; a change means the capture was rebuilt at
            /// a new size mid-session.
            /// </summary>
            public int Width { get; set; }

            public int Height { get; set; }
        }

        public sealed class ClipPlan
        {
            public IReadOnlyList<SegmentInfo> Segments { get; set; }

            /// <summary>Seek offset (seconds) into the concatenated segments.</summary>
            public double StartOffsetSeconds { get; set; }

            public double DurationSeconds { get; set; }

            /// <summary>
            /// The absolute moment the planned clip ends. Equal to the requested window end unless
            /// a dimension change cut the plan short, so callers planning a second track over the
            /// same window (the audio chunks) can clamp to the video they will actually get.
            /// </summary>
            public DateTime EndUtc { get; set; }

            /// <summary>
            /// True when segments overlapping the window were dropped because their dimensions
            /// differed from the kept run.
            /// </summary>
            public bool TruncatedByResize { get; set; }
        }

        /// <summary>
        /// A computed clip window plus the moment the toast should appear inside it: the trusted
        /// unlock time when available, else detection. The toast is composited at export, so the
        /// anchor is a choice, not an observation — it mimics a zero-latency notification.
        /// </summary>
        public sealed class ClipWindow
        {
            public DateTime StartUtc { get; set; }

            public DateTime EndUtc { get; set; }

            public DateTime ToastAnchorUtc { get; set; }
        }

        /// <summary>
        /// Parses buffer files from their wall-clock filenames (default: the video segments,
        /// seg_yyyyMMdd-HHmmss_WxH.mp4; pass the aud_/.wav pair for audio chunks — both are
        /// written in the machine's local time zone, injected for tests) into UTC-stamped infos
        /// ordered oldest-first, carrying the encoded dimensions where the name declares them.
        /// Unparseable names (and local times invalidated by DST) are skipped.
        /// </summary>
        public static List<SegmentInfo> ParseSegments(
            IEnumerable<(string Path, long SizeBytes)> files,
            TimeZoneInfo localTimeZone,
            string filePrefix = null,
            string fileExtension = null)
        {
            var result = new List<SegmentInfo>();
            foreach (var file in files ?? Enumerable.Empty<(string, long)>())
            {
                if (TryParseSegment(file.Path, localTimeZone, out var startUtc, out var width, out var height, filePrefix, fileExtension))
                {
                    result.Add(new SegmentInfo
                    {
                        Path = file.Path,
                        StartUtc = startUtc,
                        SizeBytes = file.SizeBytes,
                        Width = width,
                        Height = height
                    });
                }
            }

            result.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            return result;
        }

        public static bool TryParseSegmentStartUtc(
            string path,
            TimeZoneInfo localTimeZone,
            out DateTime startUtc,
            string filePrefix = null,
            string fileExtension = null)
        {
            return TryParseSegment(path, localTimeZone, out startUtc, out _, out _, filePrefix, fileExtension);
        }

        /// <summary>
        /// Splits a buffer file name into its wall-clock stamp and, for video segments, the
        /// encoded dimensions: prefix + yyyyMMdd-HHmmss + optional _WxH + optional -N (the
        /// writer's same-second uniquifier) + extension. The stamp is read at its fixed width so
        /// neither trailing token defeats it.
        /// </summary>
        private static bool TryParseSegment(
            string path,
            TimeZoneInfo localTimeZone,
            out DateTime startUtc,
            out int width,
            out int height,
            string filePrefix,
            string fileExtension)
        {
            startUtc = default;
            width = 0;
            height = 0;
            var prefix = filePrefix ?? RecordingPaths.SegmentFilePrefix;
            var extension = fileExtension ?? RecordingPaths.SegmentFileExtension;
            var name = Path.GetFileName(path ?? string.Empty);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var body = name.Substring(
                prefix.Length,
                name.Length - prefix.Length - extension.Length);
            if (body.Length < StampLength)
            {
                return false;
            }

            var stamp = body.Substring(0, StampLength);
            var suffix = body.Substring(StampLength);
            // Only the writer's own suffixes may follow the stamp; anything else is a foreign file.
            if (suffix.Length > 0 && suffix[0] != RecordingPaths.DimensionSeparator && suffix[0] != '-')
            {
                return false;
            }

            if (!DateTime.TryParseExact(
                    stamp,
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var local))
            {
                return false;
            }

            TryParseDimensions(suffix, out width, out height);

            try
            {
                startUtc = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
                    localTimeZone ?? TimeZoneInfo.Local);
                return true;
            }
            catch
            {
                // Skipped or ambiguous local time (DST transition) — drop the segment.
                return false;
            }
        }

        /// <summary>
        /// Reads the _WxH token from a segment name's suffix, ignoring any trailing -N uniquifier.
        /// Leaves both at 0 when the suffix carries no dimensions.
        /// </summary>
        private static void TryParseDimensions(string suffix, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(suffix) || suffix[0] != RecordingPaths.DimensionSeparator)
            {
                return;
            }

            var token = suffix.Substring(1);
            var uniquifier = token.IndexOf('-');
            if (uniquifier >= 0)
            {
                token = token.Substring(0, uniquifier);
            }

            var separator = token.IndexOf('x');
            if (separator <= 0 || separator == token.Length - 1)
            {
                return;
            }

            if (int.TryParse(token.Substring(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var w) &&
                int.TryParse(token.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h) &&
                w > 0 && h > 0)
            {
                width = w;
                height = h;
            }
        }

        /// <summary>
        /// True when the provider-reported unlock time can anchor the clip start directly:
        /// non-null, carries a real time-of-day (midnight means a date-only timestamp), and falls
        /// inside the capture window [captureStartUtc, detectionUtc + lead].
        /// </summary>
        public static bool IsPreciseUnlockTime(DateTime? unlockTimeUtc, DateTime captureStartUtc, DateTime detectionUtc)
        {
            if (!unlockTimeUtc.HasValue || unlockTimeUtc.Value.TimeOfDay == TimeSpan.Zero)
            {
                return false;
            }

            var value = unlockTimeUtc.Value;
            return value >= captureStartUtc && value <= detectionUtc.AddSeconds(PreciseLeadSeconds);
        }

        /// <summary>
        /// Computes the clip window in UTC, anchored purely on the unlock — the on-screen toast
        /// never moves the window because the toast is composited into the clip at export.
        /// Start: preRollSeconds (the user's setting) before the unlock moment — the precise
        /// timestamp when trusted, else pre-roll before detection. A floor keeps the start no
        /// earlier than one poll interval + pre-roll before detection, so a far-back but
        /// in-session precise timestamp can't open a runaway clip; the start is also clamped to
        /// recorded data. End: the toast anchor (raised to the clip start when the pre-roll got
        /// clamped away) plus the toast slot and tail.
        /// </summary>
        public static ClipWindow ComputeClipWindow(
            DateTime? unlockTimeUtc,
            DateTime detectionUtc,
            DateTime captureStartUtc,
            DateTime? oldestSegmentStartUtc,
            int pollIntervalSeconds,
            int preRollSeconds,
            double toastSlotSeconds,
            double tailSeconds)
        {
            var anchor = IsPreciseUnlockTime(unlockTimeUtc, captureStartUtc, detectionUtc)
                ? unlockTimeUtc.Value
                : detectionUtc;

            var preRoll = Math.Max(0, preRollSeconds);
            var start = anchor.AddSeconds(-preRoll);

            // Start floor: never open earlier than the oldest moment a promptly-detected unlock
            // could have occurred (one poll interval back) plus the pre-roll.
            var earliest = detectionUtc.AddSeconds(-(Math.Max(0, pollIntervalSeconds) + preRoll));
            if (start < earliest)
            {
                start = earliest;
            }

            // Clamp to data that actually exists.
            var floor = captureStartUtc;
            if (oldestSegmentStartUtc.HasValue && oldestSegmentStartUtc.Value > floor)
            {
                floor = oldestSegmentStartUtc.Value;
            }

            if (start < floor)
            {
                start = floor;
            }

            // The toast begins at the clip start when the pre-roll got clamped away entirely.
            if (anchor < start)
            {
                anchor = start;
            }

            var end = anchor.AddSeconds(Math.Max(0, toastSlotSeconds) + Math.Max(0, tailSeconds));
            return new ClipWindow { StartUtc = start, EndUtc = end, ToastAnchorUtc = anchor };
        }

        /// <summary>
        /// Rolling buffer depth in seconds: covers the worst-case clip span (pre-roll + poll
        /// interval + toast display) plus refresh-tick and toast-queue latency margin.
        /// </summary>
        public static int BufferDepthSeconds(int pollIntervalSeconds, int preRollSeconds)
        {
            var interval = Math.Max(0, pollIntervalSeconds);
            return Math.Max(3 * interval, interval + Math.Max(0, preRollSeconds) + 30);
        }

        /// <summary>Segments kept by the pruner for the given depth: ceil(depth/K) + 2.</summary>
        public static int RetainedSegmentCount(int bufferDepthSeconds, int segmentSeconds)
        {
            var k = Math.Max(1, segmentSeconds);
            return (Math.Max(0, bufferDepthSeconds) + k - 1) / k + 2;
        }

        /// <summary>
        /// Maps a clip window onto the ordered segment list: the overlapping segments plus the
        /// seek offset into the first one and the total duration. Returns null when no recorded
        /// segment overlaps the window. Each segment nominally covers K seconds; interior
        /// segments are bounded by their successor's start so drifting timestamps don't create
        /// coverage gaps.
        ///
        /// The result never mixes dimensions. A clip is stream-copied against a single declared
        /// media type, so a window spanning a mid-session capture rebuild (the game window
        /// resized) is narrowed to one contiguous same-size run: the one holding
        /// <paramref name="anchorUtc"/> — the unlock the clip exists to show — else the run
        /// covering the most time. The clip gets shorter rather than corrupt.
        /// </summary>
        public static ClipPlan PlanClip(
            IReadOnlyList<SegmentInfo> orderedSegments,
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            int segmentSeconds,
            DateTime? anchorUtc = null)
        {
            if (orderedSegments == null || orderedSegments.Count == 0 || windowEndUtc <= windowStartUtc)
            {
                return null;
            }

            var k = Math.Max(1, segmentSeconds);
            var selected = new List<SegmentInfo>();
            var selectedEnds = new List<DateTime>();
            for (var i = 0; i < orderedSegments.Count; i++)
            {
                var segment = orderedSegments[i];
                var segmentEnd = i + 1 < orderedSegments.Count
                    ? Max(orderedSegments[i + 1].StartUtc, segment.StartUtc)
                    : segment.StartUtc.AddSeconds(k);
                if (segmentEnd > windowStartUtc && segment.StartUtc < windowEndUtc)
                {
                    selected.Add(segment);
                    selectedEnds.Add(segmentEnd);
                }
            }

            if (selected.Count == 0)
            {
                return null;
            }

            SelectSameSizeRun(selected, selectedEnds, anchorUtc, out var runStart, out var runCount);
            var truncated = runCount < selected.Count;
            var run = truncated ? selected.GetRange(runStart, runCount) : selected;
            var runEnd = selectedEnds[runStart + runCount - 1];

            var first = run[0];
            var effectiveStart = windowStartUtc > first.StartUtc ? windowStartUtc : first.StartUtc;
            // Only a run cut short by a later dimension change ends before the window does; an
            // untruncated plan keeps running to the requested end, as it always has.
            var effectiveEnd = truncated && runEnd < windowEndUtc ? runEnd : windowEndUtc;
            if (effectiveEnd <= effectiveStart)
            {
                return null;
            }

            return new ClipPlan
            {
                Segments = run,
                StartOffsetSeconds = (effectiveStart - first.StartUtc).TotalSeconds,
                DurationSeconds = (effectiveEnd - effectiveStart).TotalSeconds,
                EndUtc = effectiveEnd,
                TruncatedByResize = truncated
            };
        }

        /// <summary>
        /// Locates the contiguous run of equally-sized segments the clip should be built from,
        /// as an index and length into <paramref name="selected"/>. Segments carrying no
        /// dimensions (audio chunks) all compare equal, so they always yield a single run.
        /// </summary>
        private static void SelectSameSizeRun(
            IReadOnlyList<SegmentInfo> selected,
            IReadOnlyList<DateTime> ends,
            DateTime? anchorUtc,
            out int runStart,
            out int runCount)
        {
            runStart = 0;
            runCount = selected.Count;

            var start = 0;
            var bestStart = -1;
            var bestCount = 0;
            var bestSpan = TimeSpan.MinValue;
            var anchored = false;
            for (var i = 1; i <= selected.Count; i++)
            {
                if (i < selected.Count &&
                    selected[i].Width == selected[start].Width &&
                    selected[i].Height == selected[start].Height)
                {
                    continue;
                }

                var count = i - start;
                var runEnd = ends[i - 1];
                var holdsAnchor = anchorUtc.HasValue &&
                    anchorUtc.Value >= selected[start].StartUtc && anchorUtc.Value < runEnd;
                var span = runEnd - selected[start].StartUtc;

                // A run holding the unlock wins outright; otherwise the longest-covering run does,
                // and a later run breaks a tie so the freshest footage is kept.
                if ((holdsAnchor && !anchored) || (holdsAnchor == anchored && span >= bestSpan))
                {
                    bestStart = start;
                    bestCount = count;
                    bestSpan = span;
                    anchored |= holdsAnchor;
                }

                start = i;
            }

            if (bestStart >= 0)
            {
                runStart = bestStart;
                runCount = bestCount;
            }
        }

        /// <summary>
        /// Segments (oldest-first) the pruner should delete: everything older than the retained
        /// buffer depth, plus — regardless of age — the oldest segments once the newest-first
        /// cumulative size exceeds the byte cap.
        /// </summary>
        public static List<SegmentInfo> SelectPrunable(
            IReadOnlyList<SegmentInfo> orderedSegments,
            int pollIntervalSeconds,
            int targetClipSeconds,
            int segmentSeconds,
            long maxTotalBytes)
        {
            var result = new List<SegmentInfo>();
            if (orderedSegments == null || orderedSegments.Count == 0)
            {
                return result;
            }

            var retain = RetainedSegmentCount(
                BufferDepthSeconds(pollIntervalSeconds, targetClipSeconds),
                segmentSeconds);

            // Newest-first byte walk: find how many newest segments fit under the cap.
            var byteBudgetCount = orderedSegments.Count;
            if (maxTotalBytes > 0)
            {
                long total = 0;
                byteBudgetCount = 0;
                for (var i = orderedSegments.Count - 1; i >= 0; i--)
                {
                    total += Math.Max(0, orderedSegments[i].SizeBytes);
                    if (total > maxTotalBytes && byteBudgetCount > 0)
                    {
                        break;
                    }

                    byteBudgetCount++;
                }
            }

            var keep = Math.Min(retain, byteBudgetCount);
            for (var i = 0; i < orderedSegments.Count - keep; i++)
            {
                result.Add(orderedSegments[i]);
            }

            return result;
        }

        private static DateTime Max(DateTime a, DateTime b)
        {
            return a > b ? a : b;
        }
    }
}
