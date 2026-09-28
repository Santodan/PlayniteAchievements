using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>Axis and tooltip text for one bucket plan.</summary>
    public sealed class TimelineAxisLabels
    {
        public TimelineAxisLabels(IReadOnlyList<string> axisLabels, IReadOnlyList<string> tooltipLabels, int tickCount)
        {
            AxisLabels = axisLabels;
            TooltipLabels = tooltipLabels;
            TickCount = tickCount;
        }

        /// <summary>One entry per bucket; empty for buckets without a tick.</summary>
        public IReadOnlyList<string> AxisLabels { get; }

        /// <summary>One entry per bucket; always non-empty and describes the covered range.</summary>
        public IReadOnlyList<string> TooltipLabels { get; }

        public int TickCount { get; }
    }

    /// <summary>
    /// Places axis ticks on calendar boundaries (Mondays, month starts, quarter starts, Jan 1) and
    /// thins them to a readable count instead of labeling every Nth bar.
    /// </summary>
    public static class TimelineAxisTicks
    {
        public const int DefaultMaxTicks = 8;

        private const string EnDash = "–";

        private enum LabelKind
        {
            Day,
            MonthYear,
            MonthOnly,
            QuarterYear,
            Year
        }

        private sealed class TickRule
        {
            public TickRule(Func<DateTime, bool> isBoundary, LabelKind kind)
            {
                IsBoundary = isBoundary;
                Kind = kind;
            }

            /// <summary>Whether a calendar day is a boundary for this rule.</summary>
            public Func<DateTime, bool> IsBoundary { get; }

            public LabelKind Kind { get; }
        }

        private static readonly TickRule[] DayLadder =
        {
            new TickRule(_ => true, LabelKind.Day),
            new TickRule(d => d.Day % 2 == 1, LabelKind.Day),
            new TickRule(d => d.DayOfWeek == TimelineBucketing.WeekStart, LabelKind.Day),
            new TickRule(d => d.Day == 1 || d.Day == 15, LabelKind.Day),
            new TickRule(IsMonthStart, LabelKind.MonthYear),
            new TickRule(IsQuarterStart, LabelKind.MonthYear),
            new TickRule(IsYearStart, LabelKind.Year)
        };

        private static readonly TickRule[] WeekLadder =
        {
            new TickRule(d => d.DayOfWeek == TimelineBucketing.WeekStart, LabelKind.Day),
            new TickRule(IsMonthStart, LabelKind.MonthOnly),
            new TickRule(IsQuarterStart, LabelKind.MonthYear),
            new TickRule(IsYearStart, LabelKind.Year)
        };

        private static readonly TickRule[] MonthLadder =
        {
            new TickRule(IsMonthStart, LabelKind.MonthYear),
            new TickRule(IsQuarterStart, LabelKind.MonthYear),
            new TickRule(IsYearStart, LabelKind.Year),
            new TickRule(d => IsYearStart(d) && d.Year % 2 == 0, LabelKind.Year),
            new TickRule(d => IsYearStart(d) && d.Year % 5 == 0, LabelKind.Year)
        };

        private static readonly TickRule[] QuarterLadder =
        {
            new TickRule(IsQuarterStart, LabelKind.QuarterYear),
            new TickRule(IsYearStart, LabelKind.Year),
            new TickRule(d => IsYearStart(d) && d.Year % 2 == 0, LabelKind.Year),
            new TickRule(d => IsYearStart(d) && d.Year % 5 == 0, LabelKind.Year)
        };

        private static readonly TickRule[] YearLadder =
        {
            new TickRule(IsYearStart, LabelKind.Year),
            new TickRule(d => IsYearStart(d) && d.Year % 2 == 0, LabelKind.Year),
            new TickRule(d => IsYearStart(d) && d.Year % 5 == 0, LabelKind.Year),
            new TickRule(d => IsYearStart(d) && d.Year % 10 == 0, LabelKind.Year)
        };

        public static TimelineAxisLabels Plan(
            IReadOnlyList<TimelineBucket> buckets,
            TimelineBucketUnit unit,
            int maxTicks,
            CultureInfo culture)
        {
            culture = culture ?? CultureInfo.CurrentCulture;
            var count = buckets?.Count ?? 0;
            if (count == 0)
            {
                return new TimelineAxisLabels(new string[0], new string[0], 0);
            }

            maxTicks = Math.Max(1, maxTicks);
            var ladder = LadderFor(unit);
            var tickDays = new DateTime?[count];
            TickRule chosen = null;
            var chosenTicks = 0;

            foreach (var rule in ladder)
            {
                var ticks = 0;
                var candidate = new DateTime?[count];
                for (var i = 0; i < count; i++)
                {
                    var day = FirstBoundaryIn(buckets[i], rule.IsBoundary);
                    candidate[i] = day;
                    if (day.HasValue)
                    {
                        ticks++;
                    }
                }

                // Keep the finest rule that fits; if none fits, the coarsest rule is used.
                chosen = rule;
                chosenTicks = ticks;
                tickDays = candidate;
                if (ticks <= maxTicks)
                {
                    break;
                }
            }

            if (chosenTicks == 0)
            {
                // No boundary fell inside any bucket (a window shorter than the rule's spacing):
                // label the first bucket with the unit's finest label.
                chosen = ladder[0];
                tickDays[0] = buckets[0].Start;
                chosenTicks = 1;
            }

            var axis = new string[count];
            var tooltips = new string[count];
            for (var i = 0; i < count; i++)
            {
                axis[i] = tickDays[i].HasValue ? FormatLabel(tickDays[i].Value, chosen.Kind, culture) : string.Empty;
                tooltips[i] = FormatTooltip(buckets[i], unit, culture);
            }

            return new TimelineAxisLabels(axis, tooltips, chosenTicks);
        }

        /// <summary>
        /// The culture's short date pattern with the year removed, e.g. "M/d" for en-US, "dd.MM" for
        /// de-DE, "MM-dd" for sv-SE.
        /// </summary>
        public static string ShortMonthDayPattern(CultureInfo culture)
        {
            culture = culture ?? CultureInfo.CurrentCulture;
            var pattern = culture.DateTimeFormat.ShortDatePattern;
            var year = Regex.Match(pattern, "y+");
            if (!year.Success)
            {
                return pattern;
            }

            string result;
            if (year.Index == 0)
            {
                result = Regex.Replace(pattern, @"^y+[^dM]*", string.Empty);
            }
            else
            {
                result = Regex.Replace(pattern, @"[^dM]*y+[^dM]*$", string.Empty);
                if (result.Length == pattern.Length)
                {
                    result = Regex.Replace(pattern, @"[^dM]*y+", string.Empty);
                }
            }

            result = result.Trim();
            return result.Length == 0 ? "M/d" : result;
        }

        private static TickRule[] LadderFor(TimelineBucketUnit unit)
        {
            switch (unit)
            {
                case TimelineBucketUnit.Day:
                    return DayLadder;
                case TimelineBucketUnit.Week:
                    return WeekLadder;
                case TimelineBucketUnit.Month:
                    return MonthLadder;
                case TimelineBucketUnit.Quarter:
                    return QuarterLadder;
                default:
                    return YearLadder;
            }
        }

        /// <summary>
        /// First boundary day inside the bucket's clipped range, so a partial bucket at either end of
        /// the window never carries a label for a boundary the window does not include.
        /// </summary>
        private static DateTime? FirstBoundaryIn(TimelineBucket bucket, Func<DateTime, bool> isBoundary)
        {
            for (var day = bucket.Start; day <= bucket.End; day = day.AddDays(1))
            {
                if (isBoundary(day))
                {
                    return day;
                }
            }

            return null;
        }

        private static string FormatLabel(DateTime day, LabelKind kind, CultureInfo culture)
        {
            switch (kind)
            {
                case LabelKind.Day:
                    return day.ToString(ShortMonthDayPattern(culture), culture);
                case LabelKind.MonthOnly:
                    return IsYearStart(day) ? day.ToString("MMM yy", culture) : day.ToString("MMM", culture);
                case LabelKind.MonthYear:
                    return day.ToString("MMM yy", culture);
                case LabelKind.QuarterYear:
                    return "Q" + Quarter(day).ToString(culture) + " " + day.ToString("yy", culture);
                default:
                    return day.ToString("yyyy", culture);
            }
        }

        private static string FormatTooltip(TimelineBucket bucket, TimelineBucketUnit unit, CultureInfo culture)
        {
            if (unit == TimelineBucketUnit.Day)
            {
                return bucket.Start.ToString("d", culture);
            }

            if (unit == TimelineBucketUnit.Week || bucket.IsPartial)
            {
                return bucket.Start.ToString("d", culture) + " " + EnDash + " " + bucket.End.ToString("d", culture);
            }

            switch (unit)
            {
                case TimelineBucketUnit.Month:
                    return bucket.PeriodStart.ToString(culture.DateTimeFormat.YearMonthPattern, culture);
                case TimelineBucketUnit.Quarter:
                    return "Q" + Quarter(bucket.PeriodStart).ToString(culture) + " " + bucket.PeriodStart.ToString("yyyy", culture);
                default:
                    return bucket.PeriodStart.ToString("yyyy", culture);
            }
        }

        private static bool IsMonthStart(DateTime day) => day.Day == 1;

        private static bool IsQuarterStart(DateTime day) => day.Day == 1 && (day.Month - 1) % 3 == 0;

        private static bool IsYearStart(DateTime day) => day.Day == 1 && day.Month == 1;

        private static int Quarter(DateTime day) => ((day.Month - 1) / 3) + 1;
    }
}
