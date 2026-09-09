using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Builds a category label that does not collide with any existing one, for gestures that
    /// create a node without asking for a name first (add, duplicate).
    /// </summary>
    internal static class CategoryNameGenerator
    {
        /// <summary>
        /// The first of "leaf", "leaf (2)", "leaf (3)", ... under <paramref name="parentPath"/>
        /// (null = top level) whose full path collides with nothing in
        /// <paramref name="existingLabels"/> (case-insensitive over normalized paths). Null when
        /// the base leaf sanitizes away to nothing.
        /// </summary>
        public static string GenerateUniqueLabel(
            IEnumerable<string> existingLabels,
            string parentPath,
            string baseLeafName)
        {
            var leaf = CategoryPathHelper.SanitizeSegment(baseLeafName);
            if (leaf == null)
            {
                return null;
            }

            var existing = new HashSet<string>(
                (existingLabels ?? Enumerable.Empty<string>())
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(CategoryPathHelper.NormalizePath),
                StringComparer.OrdinalIgnoreCase);

            var candidate = CategoryPathHelper.Join(parentPath, leaf);
            for (var suffix = 2; existing.Contains(candidate); suffix++)
            {
                candidate = CategoryPathHelper.Join(parentPath, $"{leaf} ({suffix})");
            }

            return candidate;
        }
    }
}
