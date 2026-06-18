using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;

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
            "Multiplayer",
            "Collectable",
            "Missable",
            "Difficulty",
            "Stackable"
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
                ["mp"] = "Multiplayer",
                ["collectable"] = "Collectable",
                ["collectible"] = "Collectable",
                ["missable"] = "Missable",
                ["miss-able"] = "Missable",
                ["difficulty"] = "Difficulty",
                ["diff"] = "Difficulty",
                ["stackable"] = "Stackable",
                ["stack"] = "Stackable",
                ["stacking"] = "Stackable"
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
            return ToDisplayText(values);
        }

        public static string ToDisplayText(IEnumerable<string> categoryTypes)
        {
            var values = (categoryTypes ?? Enumerable.Empty<string>())
                .SelectMany(value => ParseValues(NormalizeOrDefault(value)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count == 0)
            {
                values.Add(DefaultCategoryType);
            }

            return string.Join(
                ", ",
                CanonicalOrder
                    .Where(values.Contains)
                    .Select(ToCategoryTypeDisplayText));
        }

        public static string ToCategoryTypeDisplayText(string categoryType)
        {
            var values = ParseValues(NormalizeOrDefault(categoryType));
            var canonical = values.Count == 0 ? DefaultCategoryType : values[0];
            return L($"LOCPlayAch_ManageAchievements_Category_Type_{canonical}", canonical);
        }

        public static string ToCategoryLabelDisplayText(string rawValue)
        {
            var label = NormalizeCategoryOrDefault(rawValue);
            return string.Equals(label, DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? L("LOCPlayAch_Common_Default", DefaultCategoryLabel)
                : label;
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
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
