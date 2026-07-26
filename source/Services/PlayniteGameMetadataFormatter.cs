using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services
{
    public static class PlayniteGameMetadataFormatter
    {
        public static string GetPlatformText(Game game)
        {
            return JoinDisplayNames(game?.Platforms?.Select(platform => platform?.Name));
        }

        /// <summary>
        /// Returns the game's platform names, deduplicated and trimmed, preserving order.
        /// </summary>
        public static IReadOnlyList<string> GetPlatformNames(Game game)
        {
            return DistinctDisplayNames(game?.Platforms?.Select(platform => platform?.Name));
        }

        public static string GetRegionText(Game game)
        {
            return JoinDisplayNames(game?.Regions?.Select(region => region?.Name));
        }

        public static string JoinDisplayNames(IEnumerable<string> names)
        {
            var values = DistinctDisplayNames(names);
            return values.Count > 0 ? string.Join(", ", values) : string.Empty;
        }

        private static IReadOnlyList<string> DistinctDisplayNames(IEnumerable<string> names)
        {
            if (names == null)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                var normalized = (name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                values.Add(normalized);
            }

            return values;
        }

        public static string FormatPlaytime(ulong playtimeSeconds)
        {
            return FormatPlaytime(playtimeSeconds, ResolvePlaytimeDisplayMode());
        }

        public static string FormatPlaytime(ulong playtimeSeconds, PlaytimeDisplayMode displayMode)
        {
            if (playtimeSeconds < 60)
            {
                // Zero doubles as the "no playtime data" sentinel (providers such as
                // Exophase report no playtime), and sub-minute residue would render a
                // zero duration ("0m"), so show nothing instead.
                return string.Empty;
            }

            var totalMinutes = playtimeSeconds / 60;
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;

            if (hours == 0)
            {
                return L("LOCPlayAch_Playtime_LessThanOneHour");
            }

            var culture = FormattingCulture.Current;
            var hoursText = hours.ToString("N0", culture);

            if (displayMode == PlaytimeDisplayMode.HoursOnly)
            {
                return string.Format(culture, L("LOCPlayAch_Playtime_Hours"), hoursText);
            }

            return minutes > 0
                ? string.Format(culture, L("LOCPlayAch_Playtime_HoursMinutes"), hoursText, minutes)
                : string.Format(culture, L("LOCPlayAch_Playtime_Hours"), hoursText);
        }

        private static PlaytimeDisplayMode ResolvePlaytimeDisplayMode()
        {
            try
            {
                return PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.PlaytimeDisplayMode ??
                    PlaytimeDisplayMode.HoursAndMinutes;
            }
            catch (Exception)
            {
                return PlaytimeDisplayMode.HoursAndMinutes;
            }
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        public static string BuildOverviewMetadataText(
            string platformText,
            string playtimeText,
            string regionText)
        {
            return BuildOverviewMetadataText(
                platformText,
                playtimeText,
                regionText,
                showPlatform: true,
                showPlaytime: true,
                showRegion: true);
        }

        public static string BuildOverviewMetadataText(
            string platformText,
            string playtimeText,
            string regionText,
            bool showPlatform,
            bool showPlaytime,
            bool showRegion)
        {
            var parts = new List<string>();
            if (showPlatform && !string.IsNullOrWhiteSpace(platformText))
            {
                parts.Add(platformText.Trim());
            }

            if (showPlaytime && !string.IsNullOrWhiteSpace(playtimeText) && !IsZeroPlaytimeText(playtimeText))
            {
                parts.Add(playtimeText.Trim());
            }

            if (showRegion && !string.IsNullOrWhiteSpace(regionText))
            {
                parts.Add(regionText.Trim());
            }

            return parts.Count > 0 ? string.Join(" • ", parts) : string.Empty;
        }

        /// <summary>
        /// True when the playtime text is a zero duration in any unit format
        /// ("0m", "0h", "0h0m", "0 hours", localized digits-and-units variants):
        /// it contains at least one digit and every digit is zero. Zero playtime
        /// means "no playtime data", so the metadata line drops the segment.
        /// </summary>
        public static bool IsZeroPlaytimeText(string playtimeText)
        {
            if (string.IsNullOrWhiteSpace(playtimeText))
            {
                return false;
            }

            var hasDigit = false;
            foreach (var ch in playtimeText)
            {
                if (!char.IsDigit(ch))
                {
                    continue;
                }

                if (ch != '0')
                {
                    return false;
                }

                hasDigit = true;
            }

            return hasDigit;
        }
    }
}
