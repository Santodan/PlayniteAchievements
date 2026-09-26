using System;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// The groups of stat chips under the Manage Achievements sidebar nav. Values are persisted
    /// as numbers, so each keeps its bit.
    /// </summary>
    [Flags]
    public enum ManageSidebarStatGroups
    {
        None = 0,
        Capstones = 1 << 0,
        Rarity = 1 << 1,
        Trophies = 1 << 2,
        Points = 1 << 3,
        Goals = 1 << 4,
        Categorized = 1 << 5
    }
}
