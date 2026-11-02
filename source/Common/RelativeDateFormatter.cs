using System;
using System.Globalization;
using Playnite.SDK;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Unit of elapsed time a relative date is expressed in, from most to least recent.
    /// </summary>
    public enum RelativeDateUnit
    {
        Today,
        Yesterday,
        Days,
        Weeks,
        Months,
        Years
    }

    /// <summary>
    /// Maps a local date to an elapsed unit and count ("3 weeks ago") and its localized label.
    /// Unit computation is pure (no localization or clock dependency) so it can be unit tested;
    /// label resolution is kept separate.
    /// </summary>
    public static class RelativeDateFormatter
    {
        /// <summary>
        /// Determines the elapsed unit and count for <paramref name="localValue"/> against <paramref name="localNow"/>.
        /// Both arguments are expected in local time and compared by calendar date.
        /// Today: same day or future. Yesterday: 1 day. Days: 2 to 6. Weeks: 7 to 27 days (1 to 3).
        /// Months: 28 days up to 11 whole months. Years: 12 months and older.
        /// </summary>
        public static (RelativeDateUnit Unit, int Count) GetRelative(DateTime localValue, DateTime localNow)
        {
            var valueDate = localValue.Date;
            var today = localNow.Date;
            var days = (today - valueDate).Days;

            if (days <= 0)
            {
                return (RelativeDateUnit.Today, 0);
            }

            if (days == 1)
            {
                return (RelativeDateUnit.Yesterday, 1);
            }

            if (days < 7)
            {
                return (RelativeDateUnit.Days, days);
            }

            if (days < 28)
            {
                return (RelativeDateUnit.Weeks, days / 7);
            }

            // Whole calendar months; at least 1 so 28-30 day gaps short of a full month read as 1 month.
            var months = (today.Year * 12 + today.Month) - (valueDate.Year * 12 + valueDate.Month);
            if (today.Day < valueDate.Day)
            {
                months--;
            }

            months = Math.Max(1, months);
            return months < 12
                ? (RelativeDateUnit.Months, months)
                : (RelativeDateUnit.Years, months / 12);
        }

        /// <summary>
        /// Returns the localized label for <paramref name="localValue"/> relative to <paramref name="localNow"/>.
        /// </summary>
        public static string ToRelativeLabel(DateTime localValue, DateTime localNow)
        {
            var (unit, count) = GetRelative(localValue, localNow);
            switch (unit)
            {
                case RelativeDateUnit.Today:
                    return ResourceProvider.GetString("LOCPlayAch_Common_Today");
                case RelativeDateUnit.Yesterday:
                    return ResourceProvider.GetString("LOCPlayAch_Common_Yesterday");
                case RelativeDateUnit.Days:
                    return Format("LOCPlayAch_Common_Date_DaysAgo", count);
                case RelativeDateUnit.Weeks:
                    return count == 1
                        ? ResourceProvider.GetString("LOCPlayAch_Common_Date_OneWeekAgo")
                        : Format("LOCPlayAch_Common_Date_WeeksAgo", count);
                case RelativeDateUnit.Months:
                    return count == 1
                        ? ResourceProvider.GetString("LOCPlayAch_Common_Date_OneMonthAgo")
                        : Format("LOCPlayAch_Common_Date_MonthsAgo", count);
                default:
                    return count == 1
                        ? ResourceProvider.GetString("LOCPlayAch_Common_Date_OneYearAgo")
                        : Format("LOCPlayAch_Common_Date_YearsAgo", count);
            }
        }

        private static string Format(string key, int count)
        {
            return string.Format(CultureInfo.CurrentCulture, ResourceProvider.GetString(key), count);
        }
    }
}
