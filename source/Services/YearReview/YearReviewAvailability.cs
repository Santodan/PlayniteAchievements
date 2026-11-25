using System;

namespace PlayniteAchievements.Services.YearReview
{
    /// <summary>
    /// When the Year in Review can be opened: from <see cref="FirstDay"/> on, from the main menu
    /// in any month and from the Overview around the turn of the year.
    /// </summary>
    internal static class YearReviewAvailability
    {
        /// <summary>The first day the Year in Review is offered at all.</summary>
        public static readonly DateTime FirstDay = new DateTime(2026, 12, 1);

        /// <summary>True when the window may be opened on <paramref name="today"/>.</summary>
        public static bool IsReleased(DateTime today) => today.Date >= FirstDay;

        /// <summary>True when the Overview offers the window on <paramref name="today"/>: December and January.</summary>
        public static bool IsSeason(DateTime today) =>
            IsReleased(today) && (today.Month == 12 || today.Month == 1);
    }
}
