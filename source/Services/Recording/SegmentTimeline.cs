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
        /// <summary>Tolerance (seconds past observation) a selected anchor timestamp may carry.</summary>
        public const int PreciseLeadSeconds = 5;

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
        /// source-selected video anchor when available, else local observation. The toast is composited at export, so the
        /// anchor is a choice, not an observation — it mimics a zero-latency notification.
        /// </summary>
        public sealed class ClipWindow
        {
            public DateTime StartUtc { get; set; }

            public DateTime EndUtc { get; set; }

            public DateTime ToastAnchorUtc { get; set; }
        }

        /// <summary>
        /// Parses buffer files from their timestamped filenames (current files carry an explicit
        /// UTC Z; legacy files contain local wall time and use the injected zone) into UTC-stamped
        /// infos ordered oldest-first, carrying encoded dimensions where the name declares them.
        /// Unparseable names and legacy local times invalidated by DST are skipped.
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
        /// encoded dimensions: prefix + yyyyMMdd-HHmmssfff + optional _WxH + optional -N (the
        /// writer's same-instant uniquifier) + extension. The stamp is read at its fixed width so
        /// neither trailing token defeats it, falling back to the second-resolution stamp buffers
        /// written before milliseconds were included carry.
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
            if (!TryParseStamp(body, out var stamp, out var isUtc, out var suffix))
            {
                return false;
            }

            TryParseDimensions(suffix, out width, out height);

            try
            {
                startUtc = isUtc
                    ? DateTime.SpecifyKind(stamp, DateTimeKind.Utc)
                    : TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(stamp, DateTimeKind.Unspecified),
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
        /// Reads the leading wall-clock stamp from a file name's body, yielding the local time and
        /// whatever follows it. Tries the millisecond stamp first, then the second-resolution one
        /// buffers written before milliseconds were included carry. Only the writers' own suffixes
        /// may follow — anything else is a foreign file that happens to share the prefix.
        /// </summary>
        private static bool TryParseStamp(string body, out DateTime stamp, out bool isUtc, out string suffix)
        {
            stamp = default;
            isUtc = false;
            suffix = string.Empty;

            if (body.Length >= RecordingPaths.UtcStampLength)
            {
                var candidate = body.Substring(0, RecordingPaths.UtcStampLength);
                var rest = body.Substring(RecordingPaths.UtcStampLength);
                if ((rest.Length == 0 || rest[0] == RecordingPaths.DimensionSeparator || rest[0] == '-') &&
                    DateTime.TryParseExact(
                        candidate,
                        RecordingPaths.UtcStampFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out stamp))
                {
                    isUtc = true;
                    suffix = rest;
                    return true;
                }
            }

            foreach (var length in new[] { RecordingPaths.StampLength, RecordingPaths.LegacyStampLength })
            {
                if (body.Length < length)
                {
                    continue;
                }

                var candidate = body.Substring(0, length);
                var rest = body.Substring(length);
                if (rest.Length > 0 && rest[0] != RecordingPaths.DimensionSeparator && rest[0] != '-')
                {
                    continue;
                }

                var format = length == RecordingPaths.StampLength
                    ? RecordingPaths.StampFormat
                    : "yyyyMMdd-HHmmss";
                if (DateTime.TryParseExact(
                        candidate, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
                {
                    suffix = rest;
                    return true;
                }
            }

            return false;
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
        /// True when the source-selected video anchor can anchor the clip start directly:
        /// non-null, carries a real time-of-day (midnight means a date-only timestamp), and falls
        /// inside the capture window [captureStartUtc, observedUtc + lead].
        /// </summary>
        public static bool IsPreciseUnlockTime(DateTime? anchorUtc, DateTime captureStartUtc, DateTime observedUtc)
        {
            if (!anchorUtc.HasValue || anchorUtc.Value.TimeOfDay == TimeSpan.Zero)
            {
                return false;
            }

            var value = anchorUtc.Value;
            return value >= captureStartUtc && value <= observedUtc.AddSeconds(PreciseLeadSeconds);
        }

        /// <summary>
        /// Computes the clip window in UTC. The clip is built around the moment the achievement was
        /// earned — the real on-screen notification never moves the window, because the card is
        /// composited into the clip at export, on the anchor.
        ///
        /// The anchor is the source-selected timestamp when it is reachable, else observation. Two
        /// floors raise the start: it may not open earlier than one poll interval + pre-roll before
        /// observation, nor earlier than recorded data. When a floor raises the start past the
        /// timestamp itself, that timestamp is discarded and the window is recomputed around
        /// observation.
        ///
        /// That last rule prevents a stale or unreachable source timestamp from collapsing onto the
        /// oldest buffered frame and showing unrelated footage. Local observation is the only
        /// reachable fallback and still yields the full configured pre-roll.
        ///
        /// End: the later of (a) the toast anchor plus the toast slot and tail and (b) the local
        /// observation plus the tail. The second bound guarantees the event that caused detection
        /// remains in the footage even when the two timestamps do not share a clock domain.
        /// </summary>
        public static ClipWindow ComputeClipWindow(
            DateTime? preferredAnchorUtc,
            DateTime observedUtc,
            DateTime captureStartUtc,
            DateTime? oldestSegmentStartUtc,
            int pollIntervalSeconds,
            int preRollSeconds,
            double toastSlotSeconds,
            double tailSeconds)
        {
            var preRoll = Math.Max(0, preRollSeconds);

            // Clamp to data that actually exists.
            var floor = captureStartUtc;
            if (oldestSegmentStartUtc.HasValue && oldestSegmentStartUtc.Value > floor)
            {
                floor = oldestSegmentStartUtc.Value;
            }

            var anchor = IsPreciseUnlockTime(preferredAnchorUtc, captureStartUtc, observedUtc)
                ? preferredAnchorUtc.Value
                : observedUtc;
            var start = ClampWindowStart(
                anchor.AddSeconds(-preRoll), observedUtc, pollIntervalSeconds, preRoll, floor);

            if (start > anchor)
            {
                // The timestamp is unreachable — older than the buffer, or than a promptly-observed
                // unlock could be. Re-anchor on the local source observation.
                anchor = observedUtc;
                start = ClampWindowStart(
                    anchor.AddSeconds(-preRoll), observedUtc, pollIntervalSeconds, preRoll, floor);
            }

            // The toast begins at the clip start when the pre-roll got clamped away entirely (a
            // capture that only just started).
            if (anchor < start)
            {
                anchor = start;
            }

            var tail = Math.Max(0, tailSeconds);
            var end = anchor.AddSeconds(Math.Max(0, toastSlotSeconds) + tail);

            // A provider/source anchor and the local observation can be in different clock domains,
            // or a provider can persist before its state becomes observable. Never let that make the
            // clip end before the source event that caused us to produce it. This is a guardrail even
            // for providers that normally supply an authoritative historical anchor.
            var observedEnd = observedUtc.AddSeconds(tail);
            if (observedEnd > end)
            {
                end = observedEnd;
            }

            return new ClipWindow { StartUtc = start, EndUtc = end, ToastAnchorUtc = anchor };
        }

        /// <summary>
        /// Raises a window start to the earliest moment it may open: no earlier than a promptly
        /// observed unlock could have occurred (one poll interval plus the pre-roll before
        /// observation), and no earlier than recorded data.
        /// </summary>
        private static DateTime ClampWindowStart(
            DateTime start, DateTime observedUtc, int pollIntervalSeconds, int preRoll, DateTime floor)
        {
            var earliest = observedUtc.AddSeconds(-(Math.Max(0, pollIntervalSeconds) + preRoll));
            if (start < earliest)
            {
                start = earliest;
            }

            return start < floor ? floor : start;
        }

        /// <summary>
        /// The oldest moment the rolling buffer keeps, given a storage budget: everything starting
        /// before the returned time is prunable.
        ///
        /// The buffer's size is the budget, not a duration — how far back it reaches is whatever
        /// the budget buys at the user's capture settings, which is why one number works across
        /// resolutions. <paramref name="allBufferFiles"/> must be every file the budget covers
        /// (video segments and both audio streams), so the cutoff is one span all of them share:
        /// a clip needs picture and sound over the same window.
        ///
        /// <paramref name="minimumKeepFromUtc"/> is a floor, not a target: a budget too small to
        /// hold one clip window overruns it rather than leaving clips that cannot be built.
        /// Returns <see cref="DateTime.MinValue"/> (keep everything) for a non-positive budget.
        /// </summary>
        public static DateTime ResolveBudgetCutoffUtc(
            IReadOnlyList<SegmentInfo> allBufferFiles,
            long budgetBytes,
            DateTime minimumKeepFromUtc)
        {
            if (budgetBytes <= 0 || allBufferFiles == null || allBufferFiles.Count == 0)
            {
                return DateTime.MinValue;
            }

            var newestFirst = allBufferFiles
                .Where(file => file != null)
                .OrderByDescending(file => file.StartUtc)
                .ToList();
            if (newestFirst.Count == 0)
            {
                return DateTime.MinValue;
            }

            long total = 0;
            var cutoff = DateTime.MinValue;
            foreach (var file in newestFirst)
            {
                total += Math.Max(0, file.SizeBytes);
                if (total > budgetBytes)
                {
                    // This file does not fit: keep everything from the previous one onward.
                    break;
                }

                cutoff = file.StartUtc;
            }

            // Nothing fit at all — keep the newest file regardless, so the buffer is never empty.
            if (cutoff == DateTime.MinValue)
            {
                cutoff = newestFirst[0].StartUtc;
            }

            return cutoff > minimumKeepFromUtc ? minimumKeepFromUtc : cutoff;
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
        /// Files (oldest-first) the pruner should delete: everything starting before
        /// <paramref name="cutoffUtc"/>, as resolved by <see cref="ResolveBudgetCutoffUtc"/>.
        /// </summary>
        public static List<SegmentInfo> SelectPrunable(
            IReadOnlyList<SegmentInfo> orderedSegments,
            DateTime cutoffUtc)
        {
            var result = new List<SegmentInfo>();
            if (orderedSegments == null)
            {
                return result;
            }

            foreach (var segment in orderedSegments)
            {
                if (segment != null && segment.StartUtc < cutoffUtc)
                {
                    result.Add(segment);
                }
            }

            return result;
        }

        private static DateTime Max(DateTime a, DateTime b)
        {
            return a > b ? a : b;
        }
    }
}
