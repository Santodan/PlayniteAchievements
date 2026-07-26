using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Reconstructs the stable achievement api name for scraped community-page rows by matching each
    /// row's icon filename against the schema's icon/icongray hashes. Icon hashes are language
    /// independent, so this resolves api names even when the scraped display text is in a different
    /// language than the schema. Description/display name are used only as a tie breaker when
    /// multiple achievements share an icon.
    /// </summary>
    internal static class SteamAchievementApiNameResolver
    {
        public static IReadOnlyDictionary<ScrapedAchievement, string> Resolve(
            SchemaAndPercentages schema,
            IReadOnlyCollection<ScrapedAchievement> rows)
        {
            var result = new Dictionary<ScrapedAchievement, string>();
            if (rows == null || rows.Count == 0)
            {
                return result;
            }

            var iconFileToAchievements = BuildIconFileToAchievements(schema);
            if (iconFileToAchievements.Count == 0)
            {
                return result;
            }

            foreach (var row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                var apiName = ResolveRowApiName(row, iconFileToAchievements);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    result[row] = apiName;
                }
            }

            return result;
        }

        private static Dictionary<string, List<SchemaAchievement>> BuildIconFileToAchievements(SchemaAndPercentages schema)
        {
            var iconFileToAchievements = new Dictionary<string, List<SchemaAchievement>>(StringComparer.OrdinalIgnoreCase);
            if (schema?.Achievements == null)
            {
                return iconFileToAchievements;
            }

            foreach (var ach in schema.Achievements)
            {
                if (string.IsNullOrWhiteSpace(ach.Name))
                {
                    continue;
                }

                var iconFile = ExtractIconFilename(ach.Icon);
                if (!string.IsNullOrWhiteSpace(iconFile))
                {
                    if (!iconFileToAchievements.ContainsKey(iconFile))
                        iconFileToAchievements[iconFile] = new List<SchemaAchievement>();
                    iconFileToAchievements[iconFile].Add(ach);
                }

                var iconGrayFile = ExtractIconFilename(ach.IconGray);
                if (!string.IsNullOrWhiteSpace(iconGrayFile) &&
                    !string.Equals(iconGrayFile, iconFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (!iconFileToAchievements.ContainsKey(iconGrayFile))
                        iconFileToAchievements[iconGrayFile] = new List<SchemaAchievement>();
                    iconFileToAchievements[iconGrayFile].Add(ach);
                }
            }

            return iconFileToAchievements;
        }

        private static string ResolveRowApiName(
            ScrapedAchievement row,
            Dictionary<string, List<SchemaAchievement>> iconFileToAchievements)
        {
            var iconFile = ExtractIconFilename(row.IconUrl);
            if (string.IsNullOrWhiteSpace(iconFile) ||
                !iconFileToAchievements.TryGetValue(iconFile, out var achievements))
            {
                return null;
            }

            if (achievements.Count == 1)
            {
                // Icon maps to exactly one achievement - use it directly.
                return achievements[0].Name;
            }

            var rowDescription = NormalizeMatchText(row.Description);
            var rowDisplayName = NormalizeMatchText(row.DisplayName);

            // Multiple achievements share this icon - prioritize: Description, then DisplayName.
            var descMatches = achievements.Where(a =>
                string.Equals(
                    NormalizeMatchText(a.Description),
                    rowDescription,
                    StringComparison.OrdinalIgnoreCase)).ToList();

            if (descMatches.Count == 1)
            {
                return descMatches[0].Name;
            }

            // Description matched zero or multiple - fall back to DisplayName.
            return achievements.FirstOrDefault(a =>
                string.Equals(
                    NormalizeMatchText(a.DisplayName),
                    rowDisplayName,
                    StringComparison.OrdinalIgnoreCase))?.Name;
        }

        private static string ExtractIconFilename(string iconUrl)
        {
            if (string.IsNullOrWhiteSpace(iconUrl))
                return null;

            var queryIndex = iconUrl.IndexOf('?');
            if (queryIndex > 0)
                iconUrl = iconUrl.Substring(0, queryIndex);

            var lastSlash = iconUrl.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash >= iconUrl.Length - 1)
                return null;

            return iconUrl.Substring(lastSlash + 1);
        }

        private static string NormalizeMatchText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }
    }
}
