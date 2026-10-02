using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// The CSV shape written into a custom-achievements .pa package. The header uses readable column names;
    /// <see cref="CustomAchievementTextImportService"/> normalizes them back to fields, so a
    /// template and an export share this single header. It carries no unlock state or
    /// progress, which stay with the user who earned them.
    /// </summary>
    public static class CustomAchievementCsvFormat
    {
        public const string Header =
            "ID,Title,Description,Points,Trophy Type,Hidden,Rarity,Global Percent,Progress Total,Unlocked Icon,Locked Icon";

        public static List<string> BuildLines(IEnumerable<CustomAchievementDefinition> definitions)
        {
            var lines = new List<string> { Header };
            lines.AddRange(
                (definitions ?? Enumerable.Empty<CustomAchievementDefinition>())
                    .Where(definition => definition != null)
                    .Select(FormatRow));
            return lines;
        }

        public static string FormatRow(CustomAchievementDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var fields = new[]
            {
                definition.Id,
                definition.DisplayName,
                definition.Description,
                definition.Points?.ToString(CultureInfo.InvariantCulture),
                definition.TrophyType,
                definition.Hidden ? "true" : "false",
                definition.Rarity,
                definition.GlobalPercentUnlocked?.ToString(CultureInfo.InvariantCulture),
                definition.ProgressDenom?.ToString(CultureInfo.InvariantCulture),
                definition.UnlockedIconPath,
                definition.LockedIconPath
            };

            return string.Join(",", fields.Select(Escape));
        }

        public static string Escape(string value)
        {
            var safe = value ?? string.Empty;
            if (safe.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return safe;
            }

            return "\"" + safe.Replace("\"", "\"\"") + "\"";
        }
    }
}
