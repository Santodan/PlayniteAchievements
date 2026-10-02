using System;
using System.Collections.Generic;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>Bar width of an unlocks-over-time chart. Quarter and Year are chosen automatically only.</summary>
    public enum TimelineBucketUnit
    {
        Day = 0,
        Week = 1,
        Month = 2,
        Quarter = 3,
        Year = 4
    }

    /// <summary>One bar: a calendar period clipped to the window, with its unlock count.</summary>
    public sealed class TimelineBucket
    {
        public TimelineBucket(DateTime periodStart, DateTime nextPeriodStart, DateTime start, DateTime end, int count)
        {
            PeriodStart = periodStart;
            NextPeriodStart = nextPeriodStart;
            Start = start;
            End = end;
            Count = count;
        }

        /// <summary>Nominal boundary of the period (Monday, the 1st, Jan 1); used for labels and ticks.</summary>
        public DateTime PeriodStart { get; }

        /// <summary>Nominal start of the following period (exclusive end of this one).</summary>
        public DateTime NextPeriodStart { get; }

        /// <summary>First day covered, clipped to the window.</summary>
        public DateTime Start { get; }

        /// <summary>Last day covered, inclusive, clipped to the window.</summary>
        public DateTime End { get; }

        public int Count { get; }

        /// <summary>True when the window clips this period on either side.</summary>
        public bool IsPartial => Start != PeriodStart || End != NextPeriodStart.AddDays(-1);

        /// <summary>True when the nominal period contains <paramref name="day"/>.</summary>
        public bool PeriodContains(DateTime day)
        {
            var date = day.Date;
            return date >= PeriodStart && date < NextPeriodStart;
        }
    }

    public sealed class TimelineBucketPlan
    {
        public TimelineBucketPlan(TimelineBucketUnit unit, IReadOnlyList<TimelineBucket> buckets, int total, int max)
        {
            Unit = unit;
            Buckets = buckets;
            Total = total;
            Max = max;
        }

        /// <summary>Effective unit after automatic selection or override escalation.</summary>
        public TimelineBucketUnit Unit { get; }

        /// <summary>Never empty; a one-day window yields one bucket.</summary>
        public IReadOnlyList<TimelineBucket> Buckets { get; }

        /// <summary>Sum of counts inside the window.</summary>
        public int Total { get; }

        /// <summary>Largest bucket count.</summary>
        public int Max { get; }
    }

    /// <summary>
    /// Groups per-day unlock counts into calendar buckets over an inclusive day window. Pure and
    /// culture-independent: weeks start on Monday, and every date is treated as a calendar day.
    /// </summary>
    public static class TimelineBucketing
    {
        /// <summary>Automatic selection aims for this many bars (nearest on a log scale).</summary>
        public const int TargetBarCount = 24;

        /// <summary>
        /// Absolute bar cap; hosts pass a smaller <c>maxBars</c> from their width, because a
        /// LiveCharts column narrower than its padding renders nothing at all.
        /// </summary>
        public const int MaxOverrideBarCount = 400;

        public const DayOfWeek WeekStart = DayOfWeek.Monday;

        private static readonly TimelineBucketUnit[] AllUnits =
        {
            TimelineBucketUnit.Day,
            TimelineBucketUnit.Week,
            TimelineBucketUnit.Month,
            TimelineBucketUnit.Quarter,
            TimelineBucketUnit.Year
        };

        public static TimelineBucketPlan Build(
            DateTime startLocalDate,
            DateTime endLocalDate,
            IReadOnlyDictionary<DateTime, int> countsByLocalDate,
            TimelineGranularity granularity,
            int maxBars = MaxOverrideBarCount)
        {
            var start = startLocalDate.Date;
            var end = endLocalDate.Date;
            if (end < start)
            {
                end = start;
            }

            var unit = SelectUnit(start, end, granularity, maxBars);

            var periods = new List<DateTime>();
            var indexByPeriod = new Dictionary<DateTime, int>();
            for (var period = PeriodStart(unit, start); period <= end; period = NextPeriodStart(unit, period))
            {
                indexByPeriod[period] = periods.Count;
                periods.Add(period);
            }

            var sums = new int[periods.Count];
            var total = 0;
            if (countsByLocalDate != null)
            {
                foreach (var pair in countsByLocalDate)
                {
                    if (pair.Value <= 0)
                    {
                        continue;
                    }

                    var day = pair.Key.Date;
                    if (day < start || day > end)
                    {
                        continue;
                    }

                    if (indexByPeriod.TryGetValue(PeriodStart(unit, day), out var index))
                    {
                        sums[index] += pair.Value;
                        total += pair.Value;
                    }
                }
            }

            var buckets = new List<TimelineBucket>(periods.Count);
            var max = 0;
            for (var i = 0; i < periods.Count; i++)
            {
                var period = periods[i];
                var next = NextPeriodStart(unit, period);
                var bucketStart = period < start ? start : period;
                var lastDay = next.AddDays(-1);
                var bucketEnd = lastDay > end ? end : lastDay;
                buckets.Add(new TimelineBucket(period, next, bucketStart, bucketEnd, sums[i]));
                if (sums[i] > max)
                {
                    max = sums[i];
                }
            }

            return new TimelineBucketPlan(unit, buckets, total, max);
        }

        /// <summary>
        /// Chooses the unit for a window. <see cref="TimelineGranularity.Auto"/> takes the unit whose
        /// bucket count is nearest <see cref="TargetBarCount"/> on a log scale, ties to the finer unit.
        /// Either way the unit escalates while its count exceeds <paramref name="maxBars"/> (capped
        /// at <see cref="MaxOverrideBarCount"/>), so a chart never asks for more columns than fit.
        /// </summary>
        public static TimelineBucketUnit SelectUnit(
            DateTime startLocalDate,
            DateTime endLocalDate,
            TimelineGranularity granularity,
            int maxBars = MaxOverrideBarCount)
        {
            var start = startLocalDate.Date;
            var end = endLocalDate.Date < start ? start : endLocalDate.Date;
            var cap = Math.Max(1, Math.Min(maxBars, MaxOverrideBarCount));

            TimelineBucketUnit chosen;
            if (granularity != TimelineGranularity.Auto)
            {
                chosen = ToUnit(granularity);
            }
            else
            {
                chosen = TimelineBucketUnit.Day;
                var bestScore = double.MaxValue;
                foreach (var unit in AllUnits)
                {
                    var count = CountPeriods(unit, start, end);
                    var score = Math.Abs(Math.Log((double)count / TargetBarCount));
                    if (score < bestScore)
                    {
                        bestScore = score;
                        chosen = unit;
                    }
                }
            }

            while (chosen < TimelineBucketUnit.Year && CountPeriods(chosen, start, end) > cap)
            {
                chosen++;
            }

            return chosen;
        }

        /// <summary>
        /// Whether a forced granularity can be honored for the window without exceeding
        /// <paramref name="maxBars"/>; Auto always fits because it escalates on its own.
        /// </summary>
        public static bool Fits(TimelineGranularity granularity, DateTime startLocalDate, DateTime endLocalDate, int maxBars)
        {
            if (granularity == TimelineGranularity.Auto)
            {
                return true;
            }

            var cap = Math.Max(1, Math.Min(maxBars, MaxOverrideBarCount));
            return CountPeriods(ToUnit(granularity), startLocalDate, endLocalDate) <= cap;
        }

        /// <summary>Number of periods of <paramref name="unit"/> intersecting the inclusive window.</summary>
        public static int CountPeriods(TimelineBucketUnit unit, DateTime startLocalDate, DateTime endLocalDate)
        {
            var start = startLocalDate.Date;
            var end = endLocalDate.Date < start ? start : endLocalDate.Date;
            var first = PeriodStart(unit, start);
            var last = PeriodStart(unit, end);
            switch (unit)
            {
                case TimelineBucketUnit.Day:
                    return (last - first).Days + 1;
                case TimelineBucketUnit.Week:
                    return ((last - first).Days / 7) + 1;
                case TimelineBucketUnit.Month:
                    return MonthsBetween(first, last) + 1;
                case TimelineBucketUnit.Quarter:
                    return (MonthsBetween(first, last) / 3) + 1;
                default:
                    return last.Year - first.Year + 1;
            }
        }

        public static DateTime PeriodStart(TimelineBucketUnit unit, DateTime date)
        {
            var day = date.Date;
            switch (unit)
            {
                case TimelineBucketUnit.Day:
                    return day;
                case TimelineBucketUnit.Week:
                    var offset = ((int)day.DayOfWeek - (int)WeekStart + 7) % 7;
                    return day.AddDays(-offset);
                case TimelineBucketUnit.Month:
                    return new DateTime(day.Year, day.Month, 1);
                case TimelineBucketUnit.Quarter:
                    return new DateTime(day.Year, (((day.Month - 1) / 3) * 3) + 1, 1);
                default:
                    return new DateTime(day.Year, 1, 1);
            }
        }

        public static DateTime NextPeriodStart(TimelineBucketUnit unit, DateTime periodStart)
        {
            switch (unit)
            {
                case TimelineBucketUnit.Day:
                    return periodStart.AddDays(1);
                case TimelineBucketUnit.Week:
                    return periodStart.AddDays(7);
                case TimelineBucketUnit.Month:
                    return periodStart.AddMonths(1);
                case TimelineBucketUnit.Quarter:
                    return periodStart.AddMonths(3);
                default:
                    return periodStart.AddYears(1);
            }
        }

        private static TimelineBucketUnit ToUnit(TimelineGranularity granularity)
        {
            switch (granularity)
            {
                case TimelineGranularity.Day:
                    return TimelineBucketUnit.Day;
                case TimelineGranularity.Week:
                    return TimelineBucketUnit.Week;
                default:
                    return TimelineBucketUnit.Month;
            }
        }

        private static int MonthsBetween(DateTime first, DateTime last)
        {
            return ((last.Year - first.Year) * 12) + last.Month - first.Month;
        }
    }
}
