using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public enum ManageAchievementsTab
    {
        Overview,
        // The merged editor: every achievement in one list, authored and provider-supplied alike.
        // It replaced the Custom tab outright and absorbed the Manual Tracking, Filters, Capstones,
        // Goals, Notes, Order and Icons tabs, which each owned a slice of what it now does.
        Editor,
        Category,
        Notifications,
        CustomSchema,
        LocalOverrides
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
                ManageAchievementsTab.Category
            };
    }
}
