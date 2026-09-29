using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// A time window over local calendar days: either a rolling preset (<see cref="TimelineRange"/>)
    /// or a custom range with optional bounds. A blank <see cref="To"/> means "until today", a blank
    /// <see cref="From"/> means "from the earliest data". Immutable; compare by value.
    /// </summary>
    public sealed class TimeWindow : IEquatable<TimeWindow>
    {
        private const string CustomPrefix = "Custom:";
        private const string RangeSeparator = "..";
        private const string DateFormat = "yyyy-MM-dd";

        /// <summary>The unbounded window.</summary>
        public static readonly TimeWindow All = new TimeWindow(TimelineRange.All, null, null);

        /// <summary>The presets offered as quick choices. Other enum members still parse.</summary>
        public static readonly IReadOnlyList<TimelineRange> Presets = new[]
        {
            TimelineRange.SevenDays,
            TimelineRange.OneMonth,
            TimelineRange.ThreeMonths,
            TimelineRange.SixMonths,
            TimelineRange.OneYear,
            TimelineRange.All
        };

        private TimeWindow(TimelineRange? preset, DateTime? from, DateTime? to)
        {
            Preset = preset;
            From = from;
            To = to;
        }

        /// <summary>The rolling preset, or null for a custom range.</summary>
        public TimelineRange? Preset { get; }

        /// <summary>Custom lower bound as a local calendar date (00:00, Kind Unspecified); null = earliest data.</summary>
        public DateTime? From { get; }

        /// <summary>Custom upper bound as a local calendar date (00:00, Kind Unspecified); null = today.</summary>
        public DateTime? To { get; }

        public bool IsPreset => Preset.HasValue;

        public bool IsCustom => !Preset.HasValue;

        /// <summary>True when the end re-evaluates to today on each resolution.</summary>
        public bool IsRolling => !To.HasValue;

        public bool IsUnbounded => Preset == TimelineRange.All;

        public static TimeWindow FromPreset(TimelineRange preset)
        {
            if (!Enum.IsDefined(typeof(TimelineRange), preset))
            {
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "Undefined timeline range.");
            }

            return preset == TimelineRange.All ? All : new TimeWindow(preset, null, null);
        }

        /// <summary>
        /// Builds a custom window. Times are truncated to the calendar date, a reversed pair is
        /// swapped, and two blank bounds collapse to <see cref="All"/>.
        /// </summary>
        public static TimeWindow Custom(DateTime? from, DateTime? to)
        {
            var start = ToDay(from);
            var end = ToDay(to);
            if (!start.HasValue && !end.HasValue)
            {
                return All;
            }

            if (start.HasValue && end.HasValue && start.Value > end.Value)
            {
                var swap = start;
                start = end;
                end = swap;
            }

            return new TimeWindow(null, start, end);
        }

        /// <summary>
        /// Open-ended bounds for filters: a null side means no bound on that side.
        /// </summary>
        public DayBounds ResolveBounds(DateTime todayLocal)
        {
            var today = todayLocal.Date;
            if (!Preset.HasValue)
            {
                return new DayBounds(From, To ?? today);
            }

            switch (Preset.Value)
            {
                case TimelineRange.SevenDays:
                    return new DayBounds(today.AddDays(-6), today);
                case TimelineRange.FourteenDays:
                    return new DayBounds(today.AddDays(-13), today);
                case TimelineRange.OneMonth:
                    return new DayBounds(today.AddMonths(-1).AddDays(1), today);
                case TimelineRange.ThreeMonths:
                    return new DayBounds(today.AddMonths(-3).AddDays(1), today);
                case TimelineRange.SixMonths:
                    return new DayBounds(today.AddMonths(-6).AddDays(1), today);
                case TimelineRange.OneYear:
                    return new DayBounds(today.AddYears(-1).AddDays(1), today);
                default:
                    return DayBounds.Unbounded;
            }
        }

        /// <summary>
        /// Closed range for enumeration. A missing start falls back to the earliest data date, then
        /// to the end; the start never lies past the end.
        /// </summary>
        public DayRange Resolve(DateTime todayLocal, DateTime? earliestData)
        {
            var bounds = ResolveBounds(todayLocal);
            var end = bounds.End ?? todayLocal.Date;
            var start = bounds.Start ?? earliestData?.Date ?? end;
            if (start > end)
            {
                start = end;
            }

            return new DayRange(start, end);
        }

        /// <summary>Canonical string; also the wire format and the cache key component.</summary>
        public string ToKey()
        {
            if (Preset.HasValue)
            {
                return Preset.Value.ToString();
            }

            return CustomPrefix + FormatDay(From) + RangeSeparator + FormatDay(To);
        }

        public static bool TryParse(string text, out TimeWindow window)
        {
            window = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (trimmed.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var body = trimmed.Substring(CustomPrefix.Length);
                var separator = body.IndexOf(RangeSeparator, StringComparison.Ordinal);
                if (separator < 0)
                {
                    return false;
                }

                if (!TryParseDay(body.Substring(0, separator), out var from) ||
                    !TryParseDay(body.Substring(separator + RangeSeparator.Length), out var to))
                {
                    return false;
                }

                window = Custom(from, to);
                return true;
            }

            if (Enum.TryParse<TimelineRange>(trimmed, true, out var preset) &&
                Enum.IsDefined(typeof(TimelineRange), preset))
            {
                window = FromPreset(preset);
                return true;
            }

            return false;
        }

        public static TimeWindow ParseOrDefault(string text, TimeWindow fallback)
        {
            return TryParse(text, out var window) ? window : fallback;
        }

        public bool Equals(TimeWindow other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Preset == other.Preset && From == other.From && To == other.To;
        }

        public override bool Equals(object obj) => Equals(obj as TimeWindow);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Preset.HasValue ? (int)Preset.Value + 1 : 0;
                hash = (hash * 397) ^ (From?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ (To?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public override string ToString() => ToKey();

        public static bool operator ==(TimeWindow left, TimeWindow right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            return !ReferenceEquals(left, null) && left.Equals(right);
        }

        public static bool operator !=(TimeWindow left, TimeWindow right) => !(left == right);

        private static DateTime? ToDay(DateTime? value)
        {
            return value.HasValue
                ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified)
                : (DateTime?)null;
        }

        private static string FormatDay(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString(DateFormat, CultureInfo.InvariantCulture) : string.Empty;
        }

        private static bool TryParseDay(string text, out DateTime? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (DateTime.TryParseExact(
                    text.Trim(),
                    DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                value = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified);
                return true;
            }

            return false;
        }
    }

    /// <summary>Open-ended day bounds; a null side is unbounded.</summary>
    public readonly struct DayBounds
    {
        public static readonly DayBounds Unbounded = new DayBounds(null, null);

        public DayBounds(DateTime? start, DateTime? end)
        {
            Start = start?.Date;
            End = end?.Date;
        }

        public DateTime? Start { get; }

        public DateTime? End { get; }

        public bool IsUnbounded => !Start.HasValue && !End.HasValue;

        public bool Contains(DateTime localDay)
        {
            var day = localDay.Date;
            return (!Start.HasValue || day >= Start.Value) && (!End.HasValue || day <= End.Value);
        }
    }

    /// <summary>Closed, inclusive day range.</summary>
    public readonly struct DayRange
    {
        public DayRange(DateTime start, DateTime end)
        {
            Start = start.Date;
            End = end.Date < Start ? Start : end.Date;
        }

        public DateTime Start { get; }

        public DateTime End { get; }

        public int DayCount => (End - Start).Days + 1;

        public bool Contains(DateTime localDay)
        {
            var day = localDay.Date;
            return day >= Start && day <= End;
        }
    }
}
