using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.PSN
{
    // Classifies a PSN trophy by its trophy group: the canonical category type (Base/DLC) and the
    // free-text category label. Kept separate from PsnScanner so the pure group-to-category logic is
    // unit-testable without the scanner's HTTP/session dependencies.
    internal static class PsnTrophyCategoryHelper
    {
        // Maps a trophy's group id to the canonical category type. The base/default group (empty,
        // "default", "base", or "000") is "Base"; every other group is "DLC".
        internal static string MapTrophyGroupToCategoryType(string trophyGroupId)
        {
            var normalized = (trophyGroupId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "000", StringComparison.OrdinalIgnoreCase))
            {
                return "Base";
            }

            return "DLC";
        }

        // Resolves the free-text category label for a trophy from its group title. Every group
        // (including the base/default group, whose title is typically the game name) takes its own
        // label; a group with no resolvable title returns null, which the hydrator renders as the
        // localized "Default" label. The base group stays typed Base via MapTrophyGroupToCategoryType,
        // independent of its label.
        internal static string ResolveCategory(
            string trophyGroupId,
            IReadOnlyDictionary<string, string> groupNameById)
        {
            var key = PsnTrophyMatchHelper.NormalizeGroupId(trophyGroupId);
            if (groupNameById != null &&
                groupNameById.TryGetValue(key, out var name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            return null;
        }
    }
}
