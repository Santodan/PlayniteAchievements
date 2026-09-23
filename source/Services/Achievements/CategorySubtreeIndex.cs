using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Maps each category label to everything beneath it, for the surfaces that act on a category
    /// and its subcategories at once rather than on one label.
    /// </summary>
    public static class CategorySubtreeIndex
    {
        /// <summary>
        /// Groups achievements under their own category and under every ancestor of it.
        /// </summary>
        /// <remarks>
        /// Built by walking each achievement up its own ancestry, so the work is one pass over the
        /// achievements times the depth of a category path. Asking instead whether each achievement
        /// sits under each category would cost a descendant test per achievement per category, which
        /// is the same answer for a great deal more work once a game has many categories.
        ///
        /// A parent therefore appears here holding its children's achievements even when nothing
        /// carries the parent's own label. That is deliberate and is not what the category rows
        /// count: those count the exact label, because a parent and its children reporting the same
        /// achievements would double them up in a progress total.
        /// </remarks>
        public static Dictionary<string, List<string>> Build<T>(
            IEnumerable<T> achievements,
            Func<T, string> categoryLabelSelector,
            Func<T, string> apiNameSelector)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || categoryLabelSelector == null || apiNameSelector == null)
            {
                return map;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var apiName = (apiNameSelector(achievement) ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                    categoryLabelSelector(achievement));
                while (!string.IsNullOrWhiteSpace(label))
                {
                    if (!map.TryGetValue(label, out var bucket))
                    {
                        bucket = new List<string>();
                        map[label] = bucket;
                    }

                    bucket.Add(apiName);
                    label = CategoryPathHelper.GetParentPath(label);
                }
            }

            return map;
        }
    }
}
