using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public enum ManageAchievementsTab
    {
        Overview,
        CustomSchema,
        ManualTracking,
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
