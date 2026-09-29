using System.Collections.Generic;

namespace PlayniteAchievements.Services.ThemeMigration
{
    /// <summary>
    /// Static mappings for converting Legacy theme elements to Modern theme elements.
    /// Used during Full migration mode.
    /// </summary>
    public static class ControlMappings
    {
        /// <summary>
        /// Maps Legacy control names to Modern control names.
        /// Keys are the legacy names (without underscore prefix), values are the modern names.
        /// </summary>
        public static readonly Dictionary<string, string> LegacyToModernControlNames = new Dictionary<string, string>
        {
            { "PluginButton", "AchievementButton" },
            { "PluginChart", "AchievementBarChart" },
            { "PluginCompactList", "AchievementCompactList" },
            { "PluginCompactLocked", "AchievementCompactLockedList" },
            { "PluginCompactUnlocked", "AchievementCompactUnlockedList" },
            { "PluginList", "AchievementDataGrid" },
            { "PluginProgressBar", "AchievementProgressBar" },
            { "PluginUserStats", "AchievementStats" },
            { "PluginViewItem", "AchievementViewItem" }
        };

        /// <summary>
        /// Maps LegacyData binding paths to Theme binding paths.
        /// Keys are the legacy binding path suffixes, values are the modern binding path suffixes.
        /// Full bindings are in format: {Binding LegacyData.XXX} -> {Binding Theme.YYY}
        /// </summary>
        public static readonly Dictionary<string, string> LegacyToModernBindingPaths = new Dictionary<string, string>
        {
            { "HasData", "HasAchievements" },
            { "Total", "AchievementCount" },
            { "Unlocked", "UnlockedCount" },
            { "Percent", "ProgressPercentage" },
            { "Is100Percent", "IsCompleted" },
            { "Locked", "LockedCount" },
            { "ListAchievements", "AllAchievements" },
            { "ListAchUnlockDateAsc", "AchievementsOldestFirst" },
            { "ListAchUnlockDateDesc", "AchievementsNewestFirst" }
        };
    }
}

