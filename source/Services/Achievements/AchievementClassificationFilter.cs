using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The type filters shared by the grid filter bar, the Overview filters and theme dynamic lists.
    /// One key vocabulary covers both kinds (Base, DLC, ...) and tags, so a filter key matches an
    /// achievement whose category has that kind or that carries that tag.
    /// </summary>
    internal static class AchievementClassificationFilter
    {
        /// <summary>
        /// The filter keys offered for <paramref name="gameId"/> (all games when null): every kind,
        /// then every tag shown there, in list order.
        /// </summary>
        public static IReadOnlyList<string> GetFilterKeys(Guid? gameId = null) =>
            CategoryKindHelper.AssignableKinds
                .Concat(AchievementTagRegistry.GetAvailable(gameId).Where(definition => !definition.Hidden).Select(definition => definition.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// The key in its current spelling: a kind or built-in tag alias (a theme still naming
        /// SideProgression filters by SideQuest), or the key unchanged.
        /// </summary>
        public static string Canonicalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (CategoryKindHelper.IsKnownKind(key))
            {
                return CategoryKindHelper.Normalize(key);
            }

            return AchievementTagHelper.Normalize(key) ?? key.Trim();
        }

        /// <summary>
        /// True when <paramref name="key"/> names a kind or a tag. Keys of other filter groups
        /// (Unlocked, Gold, ...) are neither.
        /// </summary>
        public static bool IsFilterKey(string key)
        {
            var canonical = Canonicalize(key);
            if (canonical == null)
            {
                return false;
            }

            return CategoryKindHelper.IsKnownKind(canonical) ||
                   AchievementTagHelper.IsBuiltInTag(canonical) ||
                   AchievementTagHelper.IsDerivedTag(canonical) ||
                   canonical.StartsWith(AchievementTagHelper.UserKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                   AchievementTagRegistry.IsDefined(canonical);
        }

        /// <summary>
        /// The tag keys that occur on <paramref name="items"/>, in list order, then the derived unlock
        /// modes: the options of the in-app Tags filters. Kinds are filtered by category instead, and
        /// deleted and hidden tags are left out; a source's key no list defines is offered as the game's own.
        /// </summary>
        public static List<string> GetPresentKeys<T>(
            IEnumerable<T> items,
            Func<T, string> tagsSelector,
            Guid? gameId = null)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items ?? Enumerable.Empty<T>())
            {
                if (item == null)
                {
                    continue;
                }

                present.UnionWith(AchievementTagHelper.GetComponents(tagsSelector(item)));
            }

            return AchievementTagRegistry.GetShownKeys(gameId, present)
                .Concat(new[] { AchievementTagHelper.Softcore, AchievementTagHelper.Hardcore })
                .Where(present.Contains)
                .ToList();
        }

        public static bool Matches(string categoryKind, string tags, string key)
        {
            var canonical = Canonicalize(key);
            if (canonical == null)
            {
                return false;
            }

            return CategoryKindHelper.IsKnownKind(canonical)
                ? CategoryKindHelper.Contains(categoryKind, canonical)
                : AchievementTagHelper.Contains(tags, canonical);
        }

        /// <summary>The display name of a filter key: a kind's or a tag's name.</summary>
        public static string GetDisplayName(string key, Guid? gameId = null)
        {
            var canonical = Canonicalize(key);
            if (canonical == null)
            {
                return string.Empty;
            }

            return CategoryKindHelper.IsKnownKind(canonical)
                ? CategoryKindHelper.ToKindDisplayText(canonical)
                : AchievementTagRegistry.GetName(canonical, gameId);
        }
    }
}
