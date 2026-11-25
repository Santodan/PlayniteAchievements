using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>A run of consecutive local days that each have at least one unlock.</summary>
    public readonly struct UnlockStreak
    {
        public UnlockStreak(DateTime start, DateTime end)
        {
            Start = start.Date;
            End = end.Date;
        }

        public DateTime Start { get; }

        public DateTime End { get; }

        /// <summary>The number of days in the run; 0 for the empty streak.</summary>
        public int Days => End < Start ? 0 : (int)(End - Start).TotalDays + 1;

        public static UnlockStreak None => new UnlockStreak(DateTime.MinValue.AddDays(1), DateTime.MinValue);
    }

    /// <summary>Streaks over a set of local unlock days.</summary>
    public static class UnlockStreaks
    {
        /// <summary>The longest run in <paramref name="days"/>; the earliest one when several tie.</summary>
        public static UnlockStreak Longest(IEnumerable<DateTime> days)
        {
            var best = UnlockStreak.None;
            DateTime? start = null;
            DateTime? previous = null;
            foreach (var day in (days ?? Enumerable.Empty<DateTime>()).Select(day => day.Date).Distinct().OrderBy(day => day))
            {
                if (!previous.HasValue || day != previous.Value.AddDays(1))
                {
                    start = day;
                }

                var run = new UnlockStreak(start.Value, day);
                if (run.Days > best.Days)
                {
                    best = run;
                }

                previous = day;
            }

            return best;
        }

        /// <summary>
        /// The run ending today, or ending yesterday when today has no unlock yet, so a streak
        /// is still current until the day it would break.
        /// </summary>
        public static int Current(IEnumerable<DateTime> days, DateTime today)
        {
            var set = new HashSet<DateTime>((days ?? Enumerable.Empty<DateTime>()).Select(day => day.Date));
            var current = 0;
            var cursor = set.Contains(today.Date) ? today.Date : today.Date.AddDays(-1);
            while (set.Contains(cursor))
            {
                current++;
                cursor = cursor.AddDays(-1);
            }

            return current;
        }

        /// <summary>The current and longest streak over the days of <paramref name="counts"/> with a positive count.</summary>
        public static void Calculate(IDictionary<DateTime, int> counts, DateTime today, out int current, out int longest)
        {
            var days = (counts ?? new Dictionary<DateTime, int>())
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key.Date)
                .ToList();
            longest = Longest(days).Days;
            current = Current(days, today);
        }
    }
}
