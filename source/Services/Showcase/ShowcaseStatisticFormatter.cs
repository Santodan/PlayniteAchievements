using System;
using Playnite.SDK;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// Formats <see cref="ShowcaseStatistic"/> values for display: percents through
    /// <see cref="PercentFormatter"/> (culture-correct percent-sign spacing), playtime as hours,
    /// and streaks/rates with their localized unit strings. Shared by the statistics widget and
    /// the profile widget's streak line.
    /// </summary>
    public static class ShowcaseStatisticFormatter
    {
        public static string Format(ShowcaseStatistic item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            switch (item.Key)
            {
                case "playtime":
                    var hours = Math.Max(0, item.Value) / 3600d;
                    return hours >= 1000
                        ? string.Format(
                            FormattingCulture.Current,
                            Localize("LOCPlayAch_Showcase_ThousandsHours"),
                            hours / 1000d)
                        : string.Format(
                            FormattingCulture.Current,
                            Localize("LOCPlayAch_Showcase_Hours"),
                            hours);
                case "thirtyDayRate":
                    return string.Format(
                        FormattingCulture.Current,
                        Localize("LOCPlayAch_Showcase_PerDay"),
                        item.Value);
                case "completion":
                case "averageGlobalUnlock":
                    return item.HasValue
                        ? PercentFormatter.Format(item.Value, 1)
                        : "—";
                case "activeDayRate":
                    return item.Value.ToString("N1", FormattingCulture.Current);
                case "currentStreak":
                case "longestStreak":
                    var days = (int)Math.Round(item.Value);
                    return string.Format(
                        FormattingCulture.Current,
                        Localize(days == 1
                            ? "LOCPlayAch_Showcase_Day"
                            : "LOCPlayAch_Showcase_Days"),
                        days.ToString("N0", FormattingCulture.Current));
                default:
                    return item.Value.ToString("N0", FormattingCulture.Current);
            }
        }

        private static string Localize(string key) => ResourceProvider.GetString(key);
    }
}
