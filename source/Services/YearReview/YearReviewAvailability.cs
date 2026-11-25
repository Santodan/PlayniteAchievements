using System;
using System.IO;

namespace PlayniteAchievements.Services.YearReview
{
    /// <summary>
    /// When the Year in Review can be opened: from <see cref="FirstDay"/> on, from the main menu
    /// in any month and from the Overview around the turn of the year. Dropping
    /// <see cref="OptInFileName"/> into the plugin's user data folder opens it before then, on
    /// the next Playnite start.
    /// </summary>
    internal static class YearReviewAvailability
    {
        /// <summary>The first day the Year in Review is offered at all.</summary>
        public static readonly DateTime FirstDay = new DateTime(2026, 12, 1);

        internal const string OptInFileName = "yearreview.enabled";

        /// <summary>True when the opt-in file was present at startup.</summary>
        internal static bool OptedIn { get; private set; }

        /// <summary>Reads the opt-in file once at startup. A probe failure leaves the date gate in place.</summary>
        public static void Configure(string pluginUserDataPath)
        {
            if (OptedIn || string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                return;
            }

            try
            {
                OptedIn = File.Exists(Path.Combine(pluginUserDataPath, OptInFileName));
            }
            catch
            {
            }
        }

        /// <summary>True when the window may be opened on <paramref name="today"/>.</summary>
        public static bool IsReleased(DateTime today) => OptedIn || today.Date >= FirstDay;

        /// <summary>True when the Overview offers the window on <paramref name="today"/>: December and January, or any day when opted in.</summary>
        public static bool IsSeason(DateTime today) =>
            OptedIn || (IsReleased(today) && (today.Month == 12 || today.Month == 1));
    }
}
