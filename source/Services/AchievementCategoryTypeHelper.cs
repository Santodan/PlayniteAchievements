using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services
{
    internal static class AchievementCategoryTypeHelper
    {
        public const string DefaultCategoryType = "Default";
        public const string DefaultCategoryLabel = "Default";

        private static readonly string[] CanonicalOrder =
        {
            DefaultCategoryType,
            "Base",
            "DLC",
            "Singleplayer",
            "Multiplayer"
        };

        private static readonly Dictionary<string, string> CanonicalByAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = DefaultCategoryType,
                ["base"] = "Base",
                ["dlc"] = "DLC",
                ["singleplayer"] = "Singleplayer",
                ["single player"] = "Singleplayer",
                ["sp"] = "Singleplayer",
                ["multiplayer"] = "Multiplayer",
                ["multi player"] = "Multiplayer",
                ["mp"] = "Multiplayer"
            };

        public static IReadOnlyList<string> AllowedCategoryTypes => CanonicalOrder;

        public static string Normalize(string rawValue)
        {
            var values = ParseValues(rawValue);
            return values.Count == 0 ? null : string.Join("|", values);
        }

        public static string NormalizeOrDefault(string rawValue)
        {
            var normalized = Normalize(rawValue);
            return string.IsNullOrWhiteSpace(normalized) ? DefaultCategoryType : normalized;
        }

        public static string NormalizeCategory(string rawValue)
        {
            var normalized = (rawValue ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public static string NormalizeCategoryOrDefault(string rawValue)
        {
            var normalized = NormalizeCategory(rawValue);
            return string.IsNullOrWhiteSpace(normalized) ? DefaultCategoryLabel : normalized;
        }

        public static string Combine(IEnumerable<string> categoryTypes)
        {
            if (categoryTypes == null)
            {
                return null;
            }

            return Normalize(string.Join("|", categoryTypes));
        }

        public static List<string> ParseValues(string rawValue)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new List<string>();
            }

            var separators = new[] { '|', ',', ';', '/' };
            var split = rawValue
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => (a ?? string.Empty).Trim())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            if (split.Count == 0)
            {
                split.Add(rawValue.Trim());
            }

            for (var i = 0; i < split.Count; i++)
            {
                if (TryCanonicalize(split[i], out var canonical))
                {
                    result.Add(canonical);
                }
            }

            if (result.Count > 1 && result.Contains(DefaultCategoryType))
            {
                result.Remove(DefaultCategoryType);
            }

            return CanonicalOrder
                .Where(result.Contains)
                .ToList();
        }

        public static string ToDisplayText(string rawValue)
        {
            var values = ParseValues(NormalizeOrDefault(rawValue));
            return values.Count == 0 ? DefaultCategoryType : string.Join(", ", values);
        }

        private static bool TryCanonicalize(string rawValue, out string canonical)
        {
            canonical = null;
            var normalized = (rawValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return CanonicalByAlias.TryGetValue(normalized, out canonical);
        }
    }
}
