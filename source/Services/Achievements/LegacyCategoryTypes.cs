using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Reads the single "category type" override written before kinds and tags split.
    /// </summary>
    internal static class LegacyCategoryTypes
    {
        /// <summary>
        /// Moves a legacy type override's tags onto <paramref name="entry"/>. A legacy override
        /// replaced every provider type, so one without tags says "no tags". Its kinds live on
        /// categories now (see <see cref="PromoteKinds"/>). Derived unlock modes stay the provider's
        /// and are dropped.
        /// Tags the entry already sets are kept.
        /// </summary>
        public static void ApplyTo(AchievementOverride entry, string legacyValue)
        {
            if (entry == null || string.IsNullOrWhiteSpace(legacyValue))
            {
                return;
            }

            // An override of nothing but unlock modes overrode nothing: those were always the
            // provider's. "Default" alone still counts, as the user clearing every type.
            var meaningful = legacyValue
                .Split(new[] { '|', ',', ';', '/' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => token.Length > 0)
                .Any(token => !AchievementTagHelper.IsDerivedTag(AchievementTagHelper.Normalize(token) ?? token));
            if (!meaningful)
            {
                return;
            }

            if (entry.Tags == null && !entry.ClearTags)
            {
                var tags = SplitTags(legacyValue);
                entry.Tags = tags;
                entry.ClearTags = tags == null;
            }
        }

        /// <summary>
        /// <paramref name="categoryKinds"/> plus the kinds legacy type overrides gave achievements,
        /// moved onto their categories. A category takes a kind only when every overridden
        /// achievement in it that names one names the same; a category whose achievements disagree,
        /// or that <paramref name="categoryKinds"/> already sets, keeps what it has. Each pair is an
        /// achievement's curated category and its legacy type value.
        /// </summary>
        public static Dictionary<string, string> PromoteKinds(
            Dictionary<string, string> categoryKinds,
            IEnumerable<KeyValuePair<string, string>> legacyTypesByCategory)
        {
            var agreed = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            var conflicted = new HashSet<string>(CategoryPathHelper.LabelComparer);
            foreach (var pair in legacyTypesByCategory ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                var category = AchievementCategoryHelper.NormalizeCategory(pair.Key);
                var kind = CategoryKindHelper.NormalizeStored(SplitKind(pair.Value));
                if (category == null || kind == null)
                {
                    continue;
                }

                category = CategoryPathHelper.NormalizePath(category);
                if (conflicted.Contains(category))
                {
                    continue;
                }

                if (agreed.TryGetValue(category, out var seen))
                {
                    if (!string.Equals(seen, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        agreed.Remove(category);
                        conflicted.Add(category);
                    }

                    continue;
                }

                agreed[category] = kind;
            }

            if (agreed.Count == 0)
            {
                return categoryKinds;
            }

            var result = categoryKinds != null
                ? new Dictionary<string, string>(categoryKinds, CategoryPathHelper.LabelComparer)
                : new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            foreach (var pair in agreed)
            {
                if (!result.ContainsKey(pair.Key))
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        /// <summary>The kinds of a legacy type value; null when it has none.</summary>
        public static string SplitKind(string legacyValue) =>
            CategoryKindHelper.Combine(CategoryKindHelper.Parse(legacyValue).FindAll(CategoryKindHelper.IsKnownKind));

        /// <summary>The non-derived tags of a legacy type value; null when it has none.</summary>
        public static string SplitTags(string legacyValue) =>
            AchievementTagHelper.StripDerived(legacyValue);
    }
}
