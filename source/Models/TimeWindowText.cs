using System;
using Playnite.SDK;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// The single mapping from a <see cref="TimeWindow"/> to display text: presets use the range
    /// chip labels, custom windows show their dates in the plugin's formatting culture.
    /// </summary>
    public static class TimeWindowText
    {
        private const string EnDash = "–";

        public static string Describe(TimeWindow window, DateTime? earliestData = null)
        {
            window = window ?? TimeWindow.All;
            if (window.Preset.HasValue)
            {
                return TimelineRangeText.Describe(window.Preset.Value);
            }

            var culture = FormattingCulture.Current;
            var start = window.From ?? earliestData;
            var startText = start.HasValue
                ? start.Value.ToString("d", culture)
                : ResourceProvider.GetString("LOCPlayAch_Common_All");
            // A rolling end reads as "Today" (Playnite's own string), which is what it resolves to.
            var endText = window.To.HasValue
                ? window.To.Value.ToString("d", culture)
                : ResourceProvider.GetString("LOCToday");
            return startText + " " + EnDash + " " + endText;
        }
    }
}
