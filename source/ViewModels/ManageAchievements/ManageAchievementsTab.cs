using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public enum ManageAchievementsTab
    {
        Overview,
        ManualTracking,
        Custom,
        // The merged editor: every achievement in one list, with the facets that the Category,
        // Filters, Capstones, Goals, Notes, Order and Icons tabs each own a slice of. Those tabs
        // are still present while it is verified against them.
        Editor,
        Category,
        Filters,
        AchievementOrder,
        Capstones,
        Goals,
        Notes,
        CustomIcons,
        Notifications,
        Overrides
    }

    internal static class ManageAchievementsTabs
    {
        /// <summary>
        /// Tabs that only apply when the game has cached achievement data. Their nav buttons bind
        /// visibility to <c>HasAchievementData</c>, and selection guards use this set so the rail
        /// and the guards cannot drift apart.
        /// </summary>
        public static readonly HashSet<ManageAchievementsTab> RequireAchievementData =
            new HashSet<ManageAchievementsTab>
            {
                // Editor is deliberately absent: like the Custom tab it folds in, it must stay
                // reachable for a game with no cached achievements so custom ones can be authored.
                ManageAchievementsTab.Category,
                ManageAchievementsTab.Filters,
                ManageAchievementsTab.AchievementOrder,
                ManageAchievementsTab.Capstones,
                ManageAchievementsTab.Goals,
                ManageAchievementsTab.Notes,
                ManageAchievementsTab.CustomIcons
            };
    }
}
