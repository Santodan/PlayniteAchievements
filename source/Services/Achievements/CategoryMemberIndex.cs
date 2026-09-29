using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Maps each category label to the achievements carrying exactly that label, for the surfaces
    /// that act on one category's own members.
    /// </summary>
    public static class CategoryMemberIndex
    {
        /// <summary>
        /// Groups achievements under their own normalized category label.
        /// </summary>
        /// <remarks>
        /// A parent holds only what carries its own label, never its subcategories' achievements,
        /// matching how the category rows count their members.
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
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                if (!map.TryGetValue(label, out var bucket))
                {
                    bucket = new List<string>();
                    map[label] = bucket;
                }

                bucket.Add(apiName);
            }

            return map;
        }
    }
}
