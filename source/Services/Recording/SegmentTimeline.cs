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

        /// <summary>Windows that collapse below this are skipped by the caller.</summary>
        public const int MinimumWindowSeconds = 3;

        public sealed class SegmentInfo
        {
            public string Path { get; set; }

            public DateTime StartUtc { get; set; }

            public long SizeBytes { get; set; }
        }

        public sealed class ClipPlan
        {
            public IReadOnlyList<SegmentInfo> Segments { get; set; }

            /// <summary>Seek offset (seconds) into the concatenated segments.</summary>
            public double StartOffsetSeconds { get; set; }

            public double DurationSeconds { get; set; }
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
        /// seg_yyyyMMdd-HHmmss.ts; pass the aud_/.wav pair for audio chunks — both are written in
        /// the machine's local time zone, injected for tests) into UTC-stamped infos ordered
        /// oldest-first. Unparseable names (and local times invalidated by DST) are skipped.
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
                if (TryParseSegmentStartUtc(file.Path, localTimeZone, out var startUtc, filePrefix, fileExtension))
                {
                    result.Add(new SegmentInfo
                    {
                        Path = file.Path,
                        StartUtc = startUtc,
                        SizeBytes = file.SizeBytes
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
            startUtc = default;
            var prefix = filePrefix ?? RecordingPaths.SegmentFilePrefix;
            var extension = fileExtension ?? RecordingPaths.SegmentFileExtension;
            var name = Path.GetFileName(path ?? string.Empty);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var stamp = name.Substring(
                prefix.Length,
                name.Length - prefix.Length - extension.Length);
            if (!DateTime.TryParseExact(
                    stamp,
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var local))
            {
                return false;
            }

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
        /// </summary>
        public static ClipPlan PlanClip(
            IReadOnlyList<SegmentInfo> orderedSegments,
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            int segmentSeconds)
        {
            if (orderedSegments == null || orderedSegments.Count == 0 || windowEndUtc <= windowStartUtc)
            {
                return null;
            }

            var k = Math.Max(1, segmentSeconds);
            var selected = new List<SegmentInfo>();
            for (var i = 0; i < orderedSegments.Count; i++)
            {
                var segment = orderedSegments[i];
                var segmentEnd = i + 1 < orderedSegments.Count
                    ? Max(orderedSegments[i + 1].StartUtc, segment.StartUtc)
                    : segment.StartUtc.AddSeconds(k);
                if (segmentEnd > windowStartUtc && segment.StartUtc < windowEndUtc)
                {
                    selected.Add(segment);
                }
            }

            if (selected.Count == 0)
            {
                return null;
            }

            var first = selected[0];
            var effectiveStart = windowStartUtc > first.StartUtc ? windowStartUtc : first.StartUtc;
            return new ClipPlan
            {
                Segments = selected,
                StartOffsetSeconds = (effectiveStart - first.StartUtc).TotalSeconds,
                DurationSeconds = (windowEndUtc - effectiveStart).TotalSeconds
            };
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
