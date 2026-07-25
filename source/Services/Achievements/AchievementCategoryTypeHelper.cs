using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Achievements
{
    internal static class AchievementCategoryTypeHelper
    {
        public const string DefaultCategoryType = "Default";
        public const string DefaultCategoryLabel = "Default";

        public const string SoftcoreCategoryType = "Softcore";
        public const string HardcoreCategoryType = "Hardcore";

        private static readonly string[] CanonicalOrder =
        {
            DefaultCategoryType,
            "Base",
            "DLC",
            "Update",
            "Subset",
            "Singleplayer",
            "Multiplayer",
            "Collectable",
            "Missable",
            "Difficulty",
            "Stackable",
            SoftcoreCategoryType,
            HardcoreCategoryType
        };

        // Category types derived automatically from achievement state (e.g. RetroAchievements
        // unlock mode). Recognized for normalization, display, and filtering, but excluded
        // from the manual "Add Type" assignment menus since users do not set them by hand.
        private static readonly HashSet<string> DerivedCategoryTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                SoftcoreCategoryType,
                HardcoreCategoryType
            };

        // Category types derived from an achievement's provider group/subset membership (which
        // set/DLC/subset it belongs to), as opposed to intrinsic attributes (Missable, Stackable,
        // Difficulty, ...) or state-derived types (Softcore/Hardcore). A category merge replaces
        // these with the target category's group tags while preserving the rest.
        private static readonly HashSet<string> GroupBasedCategoryTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Base",
                "DLC",
                "Update",
                "Subset"
            };

        private const int NormalizeCacheCapacity = 512;

        private static readonly ConcurrentDictionary<string, string> NormalizeOrDefaultCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly char[] ValueSeparators = { '|', ',', ';', '/' };

        private static readonly Dictionary<string, string> CanonicalByAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = DefaultCategoryType,
                ["base"] = "Base",
                ["dlc"] = "DLC",
                ["update"] = "Update",
                ["subset"] = "Subset",
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
                ["stacking"] = "Stackable",
                ["softcore"] = SoftcoreCategoryType,
                ["casual"] = SoftcoreCategoryType,
                ["hardcore"] = HardcoreCategoryType
            };

        public static IReadOnlyList<string> AllowedCategoryTypes => CanonicalOrder;

        /// <summary>
        /// Category types a user can assign manually. Excludes <see cref="DefaultCategoryType"/>
        /// and automatically derived types (see <see cref="DerivedCategoryTypes"/>).
        /// </summary>
        public static IReadOnlyList<string> AssignableCategoryTypes => CanonicalOrder
            .Where(type => !string.Equals(type, DefaultCategoryType, StringComparison.OrdinalIgnoreCase)
                && !DerivedCategoryTypes.Contains(type))
            .ToList();

        public static string Normalize(string rawValue)
        {
            var values = ParseValues(rawValue);
            return values.Count == 0 ? null : string.Join("|", values);
        }

        public static string NormalizeOrDefault(string rawValue)
        {
            // Called once per achievement row when materializing the whole library from the
            // cache database, with raw values drawn from a tiny fixed vocabulary. Memoized to
            // avoid the per-call split/LINQ allocations; results are pure functions of the
            // immutable canonical tables, so entries never need invalidation.
            var key = rawValue ?? string.Empty;
            if (NormalizeOrDefaultCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var normalized = Normalize(rawValue);
            var result = string.IsNullOrWhiteSpace(normalized) ? DefaultCategoryType : normalized;
            if (NormalizeOrDefaultCache.Count < NormalizeCacheCapacity)
            {
                NormalizeOrDefaultCache.TryAdd(key, result);
            }

            return result;
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

        /// <summary>
        /// The group-based components (Base/DLC/Update/Subset) of a category type value, in
        /// canonical order. These describe which provider group/subset an achievement belongs to.
        /// </summary>
        public static IReadOnlyList<string> GetGroupTypeComponents(string rawValue)
        {
            return ParseValues(rawValue)
                .Where(GroupBasedCategoryTypes.Contains)
                .ToList();
        }

        /// <summary>
        /// The non-group components of a category type value (everything except Base/DLC/Update/
        /// Subset), in canonical order. These are preserved when an achievement is merged into
        /// another category.
        /// </summary>
        public static IReadOnlyList<string> GetNonGroupTypeComponents(string rawValue)
        {
            return ParseValues(rawValue)
                .Where(value => !GroupBasedCategoryTypes.Contains(value))
                .ToList();
        }

        /// <summary>
        /// Replaces the group-based components of <paramref name="achievementType"/> with
        /// <paramref name="targetGroupComponents"/>, preserving all non-group components. An empty
        /// or null target drops the group tags entirely. Normalizes to <see cref="DefaultCategoryType"/>
        /// when nothing remains.
        /// </summary>
        public static string ReplaceGroupTypes(string achievementType, IEnumerable<string> targetGroupComponents)
        {
            var preserved = GetNonGroupTypeComponents(achievementType);
            var group = (targetGroupComponents ?? Enumerable.Empty<string>())
                .SelectMany(ParseValues)
                .Where(GroupBasedCategoryTypes.Contains);

            var combined = Combine(group.Concat(preserved));
            return string.IsNullOrWhiteSpace(combined) ? DefaultCategoryType : combined;
        }

        /// <summary>
        /// Returns <paramref name="categoryTypeValue"/> with <paramref name="categoryType"/> added
        /// (<paramref name="include"/> true) or removed (<paramref name="include"/> false), in
        /// canonical order. Normalizes to <see cref="DefaultCategoryType"/> when no components
        /// remain. A null/blank <paramref name="categoryType"/> leaves the value unchanged.
        /// </summary>
        public static string WithCategoryType(string categoryTypeValue, string categoryType, bool include)
        {
            var token = Normalize(categoryType);
            if (string.IsNullOrWhiteSpace(token))
            {
                return NormalizeOrDefault(categoryTypeValue);
            }

            var tokens = ParseValues(categoryTypeValue)
                .Where(value => !string.Equals(value, token, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (include)
            {
                tokens.Add(token);
            }

            return NormalizeOrDefault(Combine(tokens));
        }

        public static List<string> ParseValues(string rawValue)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new List<string>();
            }

            var split = rawValue
                .Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries)
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

        /// <summary>
        /// Joined display text for a category type value. The Default sentinel renders as an
        /// empty string so untyped achievements show blank Type cells; use
        /// <see cref="ToCategoryTypeDisplayText"/> where a single token (including Default)
        /// needs a visible name, e.g. filter and selector option labels.
        /// </summary>
        public static string ToDisplayText(IEnumerable<string> categoryTypes)
        {
            var values = (categoryTypes ?? Enumerable.Empty<string>())
                .SelectMany(value => ParseValues(NormalizeOrDefault(value)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(value => !string.Equals(value, DefaultCategoryType, StringComparison.OrdinalIgnoreCase))
                .ToList();

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

        /// <summary>
        /// Grid-cell variant of <see cref="ToCategoryLabelDisplayText"/>: the Default bucket
        /// renders as an empty string instead of the localized "Default" placeholder. Category
        /// management rows, summaries, and theme options keep the named bucket.
        /// </summary>
        public static string ToCategoryLabelCellText(string rawValue)
        {
            var label = NormalizeCategory(rawValue);
            return label == null || string.Equals(label, DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : label;
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Length > 4 &&
                value.StartsWith("<!", StringComparison.Ordinal) &&
                value.EndsWith("!>", StringComparison.Ordinal)
                ? fallback
                : value;
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
