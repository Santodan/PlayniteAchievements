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
        /// replaced every provider type, so one without tags says "no tags". Its kinds are dropped:
        /// kinds live on categories. Derived unlock modes stay the provider's and are dropped too.
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

        /// <summary>The kinds of a legacy type value; null when it has none.</summary>
        public static string SplitKind(string legacyValue) =>
            CategoryKindHelper.Combine(CategoryKindHelper.Parse(legacyValue).FindAll(CategoryKindHelper.IsKnownKind));

        /// <summary>The non-derived tags of a legacy type value; null when it has none.</summary>
        public static string SplitTags(string legacyValue) =>
            AchievementTagHelper.StripDerived(legacyValue);
    }
}
