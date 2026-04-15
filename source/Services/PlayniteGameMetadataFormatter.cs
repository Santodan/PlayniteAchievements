using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Services
{
    public static class PlayniteGameMetadataFormatter
    {
        public static string GetPlatformText(Game game)
        {
            return JoinDisplayNames(game?.Platforms?.Select(platform => platform?.Name));
        }

        public static string GetRegionText(Game game)
        {
            return JoinDisplayNames(game?.Regions?.Select(region => region?.Name));
        }

        public static string JoinDisplayNames(IEnumerable<string> names)
        {
            if (names == null)
            {
                return string.Empty;
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

            return values.Count > 0 ? string.Join(", ", values) : string.Empty;
        }

        public static string FormatPlaytime(ulong playtimeSeconds)
        {
            var totalMinutes = playtimeSeconds / 60;
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;

            if (hours > 0)
            {
                return minutes > 0
                    ? $"{hours}h{minutes}m"
                    : $"{hours}h";
            }

            return $"{totalMinutes}m";
        }

        public static string BuildSidebarMetadataText(
            string platformText,
            string playtimeText,
            string regionText)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(platformText))
            {
                parts.Add(platformText.Trim());
            }

            if (!string.IsNullOrWhiteSpace(playtimeText))
            {
                parts.Add(playtimeText.Trim());
            }

            if (!string.IsNullOrWhiteSpace(regionText))
            {
                parts.Add(regionText.Trim());
            }

            return parts.Count > 0 ? string.Join(" • ", parts) : string.Empty;
        }
    }
}
