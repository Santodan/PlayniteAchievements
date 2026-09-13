using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public enum ManageAchievementsTab
    {
        Overview,
        ManualTracking,
        // The merged editor: every achievement in one list, authored and provider-supplied alike.
        // It replaced the Custom tab outright — it is that same editor widened to every
        // achievement — and folds in the facets the Category, Filters, Capstones, Goals, Notes,
        // Order and Icons tabs each own a slice of. Those tabs remain while it is verified
        // against them.
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

        /// <summary>
        /// Tabs the merged editor replaced. Kept in the enum, the rail markup and the content host so
        /// nothing is deleted while the editor is being proven against them, but hidden from the
        /// rail and refused by the selection guards.
        /// </summary>
        /// <remarks>
        /// Flip <c>ManageAchievementsViewModel.ShowReplacedTabs</c> to true to bring them back.
        /// </remarks>
        public static readonly HashSet<ManageAchievementsTab> Replaced =
            new HashSet<ManageAchievementsTab>
            {
                // Category is absent on purpose: the editor replaced its Assign half, but nothing
                // replaces Manage Categories, so the tab stays and only that half is collapsed.
                ManageAchievementsTab.Filters,
                ManageAchievementsTab.AchievementOrder,
                ManageAchievementsTab.Capstones,
                ManageAchievementsTab.Goals,
                ManageAchievementsTab.Notes,
                ManageAchievementsTab.CustomIcons
            };
    }
}
