using System;
using System.Collections.Generic;
using System.ComponentModel;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Helper class for creating mock achievement data for settings previews.
    /// </summary>
    public static class MockDataHelper
    {
        // Use the same icon for all achievements - grayscale is applied by AchievementDisplayItem.DisplayIcon
        private const string UnlockedIconPath = "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";

        /// <summary>
        /// Gets standard mock achievements used across all previews.
        /// Contains: 2 unlocked (ultra rare, rare), 1 locked, 2 locked+hidden
        /// </summary>
        public static List<AchievementDisplayItem> GetStandardMockAchievements(
            bool showRarityBar = true,
            bool showHiddenIcon = true,
            bool showHiddenTitle = true,
            bool showHiddenDescription = true,
            bool showHiddenSuffix = true,
            bool showLockedIcon = true)
        {
            // Always create fresh items to reflect current settings
            var items = new List<AchievementDisplayItem>();

            // Unlocked Ultra Rare (2.5%)
            items.Add(CreateMockAchievement(
                unlocked: true, hidden: false, globalPercent: 2.5,
                displayName: "Ultra Rare Victory", description: "An incredibly rare feat",
                showRarityBar: showRarityBar,
                showHiddenSuffix: showHiddenSuffix));

            // Unlocked Rare (8.0%)
            items.Add(CreateMockAchievement(
                unlocked: true, hidden: false, globalPercent: 8.0,
                displayName: "Gold Medal Run", description: "Earned a prestigious gold medal",
                showRarityBar: showRarityBar,
                showHiddenSuffix: showHiddenSuffix));

            // Locked (25.0%)
            items.Add(CreateMockAchievement(
                unlocked: false, hidden: false, globalPercent: 25.0,
                displayName: "Locked Challenge", description: "Complete this task to unlock",
                showRarityBar: showRarityBar,
                showHiddenSuffix: showHiddenSuffix, showLockedIcon: showLockedIcon));

            // Locked Hidden (15.0%)
            items.Add(CreateMockAchievement(
                unlocked: false, hidden: true, globalPercent: 15.0,
                displayName: "Hidden Secret", description: "Discover the hidden mystery",
                showRarityBar: showRarityBar,
                showHiddenIcon: showHiddenIcon, showHiddenTitle: showHiddenTitle,
                showHiddenDescription: showHiddenDescription, showHiddenSuffix: showHiddenSuffix,
                showLockedIcon: showLockedIcon));

            // Locked Hidden Common (75.0%)
            items.Add(CreateMockAchievement(
                unlocked: false, hidden: true, globalPercent: 75.0,
                displayName: "Common Secret", description: "A straightforward hidden objective",
                showRarityBar: showRarityBar,
                showHiddenIcon: showHiddenIcon, showHiddenTitle: showHiddenTitle,
                showHiddenDescription: showHiddenDescription, showHiddenSuffix: showHiddenSuffix,
                showLockedIcon: showLockedIcon));

            return items;
        }

        /// <summary>
        /// Creates a mock AchievementDisplayItem for preview purposes.
        /// </summary>
        /// <param name="unlocked">Whether the achievement is unlocked.</param>
        /// <param name="hidden">Whether the achievement is a hidden achievement.</param>
        /// <param name="globalPercent">Global unlock percentage (null for no rarity data).</param>
        /// <param name="displayName">Display name for the achievement.</param>
        /// <param name="description">Description for the achievement.</param>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showHiddenIcon">Whether to show hidden icons.</param>
        /// <param name="showHiddenTitle">Whether to show hidden titles.</param>
        /// <param name="showHiddenDescription">Whether to show hidden descriptions.</param>
        /// <param name="showHiddenSuffix">Whether to show hidden suffix.</param>
        /// <param name="showLockedIcon">Whether to show locked icons.</param>
        /// <returns>A mock AchievementDisplayItem.</returns>
        public static AchievementDisplayItem CreateMockAchievement(
            bool unlocked = true,
            bool hidden = false,
            double? globalPercent = 45.0,
            string displayName = "Mock Achievement",
            string description = "Mock description for preview",
            bool showRarityBar = true,
            bool showHiddenIcon = true,
            bool showHiddenTitle = true,
            bool showHiddenDescription = true,
            bool showHiddenSuffix = true,
            bool showLockedIcon = true)
        {
            var item = new AchievementDisplayItem
            {
                DisplayName = displayName,
                Description = description,
                Unlocked = unlocked,
                Hidden = hidden,
                GlobalPercentUnlocked = globalPercent,
                Rarity = globalPercent.HasValue
                    ? PercentRarityHelper.GetRarityTier(globalPercent.Value)
                    : (hidden ? RarityTier.Rare : RarityTier.Common),
                IconPath = UnlockedIconPath, // Same icon for all - grayscale applied by DisplayIcon when locked
                ShowHiddenIcon = showHiddenIcon,
                ShowHiddenTitle = showHiddenTitle,
                ShowHiddenDescription = showHiddenDescription,
                ShowHiddenSuffix = showHiddenSuffix,
                ShowLockedIcon = showLockedIcon,
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
        /// Uses the standard mock achievements.
        /// </summary>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showHiddenIcon">Whether to show hidden icons.</param>
        /// <param name="showHiddenTitle">Whether to show hidden titles.</param>
        /// <param name="showHiddenDescription">Whether to show hidden descriptions.</param>
        /// <param name="showLockedIcon">Whether to show locked icons.</param>
        /// <returns>List of mock achievement items.</returns>
        public static List<AchievementDisplayItem> CreateMockCompactListItems(
            bool showRarityBar = true,
            bool showHiddenIcon = true,
            bool showHiddenTitle = true,
            bool showHiddenDescription = true,
            bool showHiddenSuffix = true,
            bool showLockedIcon = true)
        {
            return GetStandardMockAchievements(
                showRarityBar,
                showHiddenIcon, showHiddenTitle, showHiddenDescription, showHiddenSuffix, showLockedIcon);
        }

        public static List<AchievementDisplayItem> CreateMockCompactListItems(
            bool showRarityBar,
            bool showRarityGlow,
            bool showHiddenIcon,
            bool showHiddenTitle,
            bool showHiddenDescription,
            bool showHiddenSuffix,
            bool showLockedIcon)
        {
            return CreateMockCompactListItems(
                showRarityBar,
                showHiddenIcon,
                showHiddenTitle,
                showHiddenDescription,
                showHiddenSuffix,
                showLockedIcon);
        }

        /// <summary>
        /// Creates a list of mock AchievementDisplayItems for datagrid preview.
        /// Uses the standard mock achievements.
        /// </summary>
        /// <returns>List of mock achievement items for datagrid.</returns>
        public static List<AchievementDisplayItem> CreateMockDataGridItems(
            bool showRarityBar = true,
            bool showRarityGlow = true,
            bool showHiddenIcon = true,
            bool showHiddenTitle = true,
            bool showHiddenDescription = true,
            bool showHiddenSuffix = true,
            bool showLockedIcon = true)
        {
            return GetStandardMockAchievements(
                showRarityBar,
                showHiddenIcon, showHiddenTitle, showHiddenDescription, showHiddenSuffix, showLockedIcon);
        }

        /// <summary>
        /// Creates a list of mock unlocked AchievementDisplayItems for unlocked list preview.
        /// Filters standard mock items to only include unlocked.
        /// </summary>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <returns>List of mock unlocked achievement items.</returns>
        public static List<AchievementDisplayItem> CreateMockUnlockedListItems(
            bool showRarityBar = true,
            bool showLockedIcon = true)
        {
            var all = GetStandardMockAchievements(showRarityBar, true, true, true, true, showLockedIcon);
            return all.FindAll(item => item.Unlocked);
        }

        public static List<AchievementDisplayItem> CreateMockUnlockedListItems(
            bool showRarityBar,
            bool showRarityGlow,
            bool showLockedIcon)
        {
            return CreateMockUnlockedListItems(showRarityBar, showLockedIcon);
        }

        /// <summary>
        /// Creates a list of mock locked AchievementDisplayItems for locked list preview.
        /// Filters standard mock items to only include locked.
        /// </summary>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showHiddenIcon">Whether to show hidden icons.</param>
        /// <param name="showHiddenTitle">Whether to show hidden titles.</param>
        /// <param name="showHiddenDescription">Whether to show hidden descriptions.</param>
        /// <param name="showLockedIcon">Whether to show locked icons.</param>
        /// <returns>List of mock locked achievement items.</returns>
        public static List<AchievementDisplayItem> CreateMockLockedListItems(
            bool showRarityBar = true,
            bool showHiddenIcon = true,
            bool showHiddenTitle = true,
            bool showHiddenDescription = true,
            bool showHiddenSuffix = true,
            bool showLockedIcon = true)
        {
            var all = GetStandardMockAchievements(
                showRarityBar,
                showHiddenIcon, showHiddenTitle, showHiddenDescription, showHiddenSuffix, showLockedIcon);
            return all.FindAll(item => !item.Unlocked);
        }

        public static List<AchievementDisplayItem> CreateMockLockedListItems(
            bool showRarityBar,
            bool showRarityGlow,
            bool showHiddenIcon,
            bool showHiddenTitle,
            bool showHiddenDescription,
            bool showHiddenSuffix,
            bool showLockedIcon)
        {
            return CreateMockLockedListItems(
                showRarityBar,
                showHiddenIcon,
                showHiddenTitle,
                showHiddenDescription,
                showHiddenSuffix,
                showLockedIcon);
        }

        /// <summary>
        /// Gets modern theme bindings populated with mock achievements for settings preview.
        /// Used by modern controls via ThemeDataOverride.
        /// </summary>
        /// <returns>Modern theme bindings with mock achievement data.</returns>
        public static ModernThemeBindings GetPreviewThemeData()
        {
            return CreatePreviewThemeData();
        }

        public static ModernThemeBindings GetUnlockedPreviewThemeData() => GetPreviewThemeData();

        public static ModernThemeBindings GetHiddenPreviewThemeData() => GetAchievementVisibilityPreviewThemeData();

        public static ModernThemeBindings GetLockedPreviewThemeData() => GetAchievementVisibilityPreviewThemeData();

        /// <summary>
        /// Gets modern theme bindings for the achievement visibility preview.
        /// </summary>
        public static ModernThemeBindings GetAchievementVisibilityPreviewThemeData()
        {
            return CreateAchievementVisibilityThemeData();
        }

        private static ModernThemeBindings CreateAchievementVisibilityThemeData()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail
                {
                    ApiName = "preview_locked",
                    DisplayName = "Locked Challenge",
                    Description = "Complete this to unlock",
                    UnlockedIconPath = UnlockedIconPath,
                    LockedIconPath = UnlockedIconPath,
                    Unlocked = false,
                    Hidden = false,
                    GlobalPercentUnlocked = 25.0,
                    Rarity = RarityTier.Uncommon
                },
                new AchievementDetail
                {
                    ApiName = "preview_hidden",
                    DisplayName = "Hidden Secret",
                    Description = "Discover the mystery",
                    UnlockedIconPath = UnlockedIconPath,
                    LockedIconPath = UnlockedIconPath,
                    Unlocked = false,
                    Hidden = true,
                    GlobalPercentUnlocked = 15.0,
                    Rarity = RarityTier.Rare
                }
            };

            var themeData = new ModernThemeBindings
            {
                HasAchievements = true,
                IsCompleted = false,
                AchievementCount = achievements.Count,
                UnlockedCount = 0,
                LockedCount = achievements.Count,
                ProgressPercentage = 0.0,
                AllAchievements = achievements
            };

            themeData.Rare = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.Uncommon = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.RareAndUltraRare = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.TotalUncommon = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.TotalRare = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.TotalRareAndUltraRare = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.TotalOverall = new AchievementRarityStats { Unlocked = 0, Locked = achievements.Count, Total = achievements.Count };
            PopulateOrderedAchievementLists(themeData);

            return themeData;
        }

        private static ModernThemeBindings CreatePreviewThemeData()
        {
            var themeData = new ModernThemeBindings
            {
                HasAchievements = true,
                IsCompleted = false,
                AchievementCount = 5,
                UnlockedCount = 2,
                LockedCount = 3,
                ProgressPercentage = 40.0,
                AllAchievements = CreateMockAchievementDetails()
            };

            // Set rarity stats
            themeData.UltraRare = new AchievementRarityStats { Unlocked = 1, Locked = 0, Total = 1 };
            themeData.Rare = new AchievementRarityStats { Unlocked = 1, Locked = 0, Total = 1 };
            themeData.Uncommon = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.Common = new AchievementRarityStats { Unlocked = 0, Locked = 2, Total = 2 };
            themeData.RareAndUltraRare = new AchievementRarityStats { Unlocked = 2, Locked = 0, Total = 2 };
            themeData.TotalCommon = new AchievementRarityStats { Unlocked = 0, Locked = 2, Total = 2 };
            themeData.TotalUncommon = new AchievementRarityStats { Unlocked = 0, Locked = 1, Total = 1 };
            themeData.TotalRare = new AchievementRarityStats { Unlocked = 1, Locked = 0, Total = 1 };
            themeData.TotalUltraRare = new AchievementRarityStats { Unlocked = 1, Locked = 0, Total = 1 };
            themeData.TotalRareAndUltraRare = new AchievementRarityStats { Unlocked = 2, Locked = 0, Total = 2 };
            themeData.TotalOverall = new AchievementRarityStats { Unlocked = 2, Locked = 3, Total = 5 };
            themeData.SelectedGameSummary = CreatePreviewGameSummary(themeData.AllAchievements);
            PopulateOrderedAchievementLists(themeData);

            return themeData;
        }

        /// <summary>
        /// Creates the mock game summary row shown above the preview data grid.
        /// </summary>
        private static GameSummaryItem CreatePreviewGameSummary(List<AchievementDetail> achievements)
        {
            var item = new GameSummaryItem
            {
                GameName = "Preview Game",
                SortingName = "Preview Game",
                PlatformText = "PC (Windows)",
                PlaytimeSeconds = 12UL * 3600,
                LastPlayed = DateTime.Now.AddDays(-1),
                IsCompleted = false
            };

            AchievementStatsAccumulator
                .FromAchievements(achievements)
                .ApplyTo(item);

            return item;
        }

        /// <summary>
        /// Populates ordered achievement lists used by modern compact list controls in preview mode.
        /// </summary>
        private static void PopulateOrderedAchievementLists(ModernThemeBindings themeData)
        {
            var all = themeData?.AllAchievements ?? new List<AchievementDetail>();

            themeData.AchievementDefaultOrder = new List<AchievementDetail>(all);

            // Keep the preview deterministic: source order is newest-first by default.
            themeData.AchievementsNewestFirst = new List<AchievementDetail>(all);

            themeData.AchievementsOldestFirst = AchievementSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.UnlockTime),
                ListSortDirection.Ascending);

            themeData.AchievementsRarityAsc = AchievementSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Ascending);
            themeData.AchievementsRarityDesc = AchievementSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Descending);
            themeData.AllAchievementsRarityAsc = new List<AchievementDetail>(themeData.AchievementsRarityAsc);
            themeData.AllAchievementsRarityDesc = new List<AchievementDetail>(themeData.AchievementsRarityDesc);
        }

        /// <summary>
        /// Creates standard mock AchievementDetail objects for preview.
        /// Contains: 2 unlocked (ultra rare, rare), 1 locked, 2 locked+hidden
        /// </summary>
        private static List<AchievementDetail> CreateMockAchievementDetails()
        {
            var achievements = new List<AchievementDetail>();

            // Unlocked Ultra Rare (2.5%)
            achievements.Add(new AchievementDetail
            {
                ApiName = "mock_ultra_rare",
                DisplayName = "Ultra Rare Victory",
                Description = "An incredibly rare feat",
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = UnlockedIconPath,
                Unlocked = true,
                Hidden = false,
                GlobalPercentUnlocked = 2.5,
                Rarity = RarityTier.UltraRare,
                UnlockTimeUtc = DateTime.UtcNow.AddDays(-1)
            });

            // Unlocked Rare (8.0%)
            achievements.Add(new AchievementDetail
            {
                ApiName = "mock_rare",
                DisplayName = "Gold Medal Run",
                Description = "Earned a prestigious gold medal",
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = UnlockedIconPath,
                Unlocked = true,
                Hidden = false,
                GlobalPercentUnlocked = 8.0,
                Rarity = RarityTier.Rare,
                UnlockTimeUtc = DateTime.UtcNow.AddDays(-2)
            });

            // Locked (25.0%)
            achievements.Add(new AchievementDetail
            {
                ApiName = "mock_locked",
                DisplayName = "Locked Challenge",
                Description = "Complete this task to unlock",
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = UnlockedIconPath,
                Unlocked = false,
                Hidden = false,
                GlobalPercentUnlocked = 25.0,
                Rarity = RarityTier.Uncommon
            });

            // Locked Hidden (15.0%)
            achievements.Add(new AchievementDetail
            {
                ApiName = "mock_hidden_rare",
                DisplayName = "Hidden Secret",
                Description = "Discover the hidden mystery",
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = UnlockedIconPath,
                Unlocked = false,
                Hidden = true,
                GlobalPercentUnlocked = 15.0,
                Rarity = RarityTier.Rare
            });

            // Locked Hidden Common (75.0%)
            achievements.Add(new AchievementDetail
            {
                ApiName = "mock_hidden_common",
                DisplayName = "Common Secret",
                Description = "A straightforward hidden objective",
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = UnlockedIconPath,
                Unlocked = false,
                Hidden = true,
                GlobalPercentUnlocked = 75.0,
                Rarity = RarityTier.Common
            });

            return achievements;
        }
    }

}

