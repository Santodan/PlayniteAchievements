using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Helper class for creating mock achievement data for settings previews.
    /// </summary>
    public static class MockDataHelper
    {
        private const string UnlockedIconPath = "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";
        private const string LockedIconPath = "pack://application:,,,/PlayniteAchievements;component/Resources/HiddenAchIcon.png";

        /// <summary>
        /// Creates a mock AchievementDisplayItem for preview purposes.
        /// </summary>
        /// <param name="unlocked">Whether the achievement is unlocked.</param>
        /// <param name="hidden">Whether the achievement is a hidden achievement.</param>
        /// <param name="globalPercent">Global unlock percentage (null for no rarity data).</param>
        /// <param name="displayName">Display name for the achievement.</param>
        /// <param name="description">Description for the achievement.</param>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showRarityGlow">Whether to show the rarity glow.</param>
        /// <returns>A mock AchievementDisplayItem.</returns>
        public static AchievementDisplayItem CreateMockAchievement(
            bool unlocked = true,
            bool hidden = false,
            double? globalPercent = 45.0,
            string displayName = "Mock Achievement",
            string description = "Mock description for preview",
            bool showRarityBar = true,
            bool showRarityGlow = true)
        {
            var item = new AchievementDisplayItem
            {
                DisplayName = displayName,
                Description = description,
                Unlocked = unlocked,
                Hidden = hidden,
                GlobalPercentUnlocked = globalPercent,
                IconPath = unlocked ? UnlockedIconPath : LockedIconPath,
                ShowHiddenIcon = true,
                ShowHiddenTitle = true,
                ShowHiddenDescription = true,
                ShowLockedIcon = true,
                ShowRarityGlow = showRarityGlow,
                ShowRarityBar = showRarityBar,
                GameName = "Preview Game"
            };

            if (unlocked)
            {
                item.UnlockTimeUtc = DateTime.UtcNow.AddDays(-1);
            }

            return item;
        }

        /// <summary>
        /// Creates a list of mock AchievementDisplayItems for compact list preview.
        /// 2 hidden, 2 locked, 1 unlocked.
        /// </summary>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showRarityGlow">Whether to show the rarity glow.</param>
        /// <returns>List of mock achievement items.</returns>
        public static List<AchievementDisplayItem> CreateMockCompactListItems(bool showRarityBar = true, bool showRarityGlow = true)
        {
            var items = new List<AchievementDisplayItem>();

            // 2 Hidden achievements (locked)
            items.Add(CreateMockAchievement(false, true, 15.0, "Hidden Secret #1", "Discover the hidden mystery", showRarityBar, showRarityGlow));
            items.Add(CreateMockAchievement(false, true, 8.0, "Hidden Secret #2", "Another hidden challenge", showRarityBar, showRarityGlow));

            // 2 Locked achievements (not hidden)
            items.Add(CreateMockAchievement(false, false, 25.0, "Locked Challenge", "Complete this task to unlock", showRarityBar, showRarityGlow));
            items.Add(CreateMockAchievement(false, false, 75.0, "Common Task", "A straightforward objective", showRarityBar, showRarityGlow));

            // 1 Unlocked achievement
            items.Add(CreateMockAchievement(true, false, 2.5, "Ultra Rare Victory", "An incredibly rare feat", showRarityBar, showRarityGlow));

            return items;
        }

        /// <summary>
        /// Creates a list of mock AchievementDisplayItems for datagrid preview.
        /// 2 unlocked (1 ultra rare, 1 gold), 1 locked.
        /// </summary>
        /// <returns>List of mock achievement items for datagrid.</returns>
        public static List<AchievementDisplayItem> CreateMockDataGridItems()
        {
            var items = new List<AchievementDisplayItem>();

            // Unlocked - Ultra Rare
            items.Add(CreateMockAchievement(true, false, 2.5, "Ultra Rare Victory", "Completed an incredibly difficult challenge", true, true));

            // Unlocked - Rare (Gold badge)
            items.Add(CreateMockAchievement(true, false, 8.0, "Gold Medal Run", "Earned a prestigious gold medal", true, true));

            // Locked
            items.Add(CreateMockAchievement(false, false, 45.0, "Locked Achievement", "This achievement is still locked", true, true));

            return items;
        }

        /// <summary>
        /// Updates the ShowRarityBar property on all items in the list.
        /// </summary>
        /// <param name="items">The list of items to update.</param>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        public static void UpdateRarityBarVisibility(IList<AchievementDisplayItem> items, bool showRarityBar)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                item.ShowRarityBar = showRarityBar;
            }
        }

        /// <summary>
        /// Updates the ShowRarityGlow property on all items in the list.
        /// </summary>
        /// <param name="items">The list of items to update.</param>
        /// <param name="showRarityGlow">Whether to show the rarity glow.</param>
        public static void UpdateRarityGlowVisibility(IList<AchievementDisplayItem> items, bool showRarityGlow)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                item.ShowRarityGlow = showRarityGlow;
            }
        }
    }
}