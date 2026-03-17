using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Helper class for creating mock achievement data for settings previews.
    /// </summary>
    public static class MockDataHelper
    {
        // Use the same icon for all achievements - grayscale is applied by AchievementDisplayItem.DisplayIcon
        private const string UnlockedIconPath = "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";

        private static MockThemeData _mockThemeData;

        /// <summary>
        /// Gets standard mock achievements used across all previews.
        /// Contains: 2 unlocked (ultra rare, rare), 1 locked, 2 locked+hidden
        /// </summary>
        public static List<AchievementDisplayItem> GetStandardMockAchievements(
            bool showRarityBar = true,
            bool showRarityGlow = true,
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
                showRarityBar: showRarityBar, showRarityGlow: showRarityGlow,
                showHiddenSuffix: showHiddenSuffix));

            // Unlocked Rare (8.0%)
            items.Add(CreateMockAchievement(
                unlocked: true, hidden: false, globalPercent: 8.0,
                displayName: "Gold Medal Run", description: "Earned a prestigious gold medal",
                showRarityBar: showRarityBar, showRarityGlow: showRarityGlow,
                showHiddenSuffix: showHiddenSuffix));

            // Locked (25.0%)
            items.Add(CreateMockAchievement(
                unlocked: false, hidden: false, globalPercent: 25.0,
                displayName: "Locked Challenge", description: "Complete this task to unlock",
                showRarityBar: showRarityBar, showRarityGlow: showRarityGlow,
                showHiddenSuffix: showHiddenSuffix, showLockedIcon: showLockedIcon));

            // Locked Hidden (15.0%)
            items.Add(CreateMockAchievement(
                unlocked: false, hidden: true, globalPercent: 15.0,
                displayName: "Hidden Secret", description: "Discover the hidden mystery",
                showRarityBar: showRarityBar, showRarityGlow: showRarityGlow,
                showHiddenIcon: showHiddenIcon, showHiddenTitle: showHiddenTitle,
                showHiddenDescription: showHiddenDescription, showHiddenSuffix: showHiddenSuffix,
                showLockedIcon: showLockedIcon));

            // Locked Hidden Common (75.0%)
            items.Add(CreateMockAchievement(
                unlocked: false, hidden: true, globalPercent: 75.0,
                displayName: "Common Secret", description: "A straightforward hidden objective",
                showRarityBar: showRarityBar, showRarityGlow: showRarityGlow,
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
        /// <param name="showRarityGlow">Whether to show the rarity glow.</param>
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
            bool showRarityGlow = true,
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
                IconPath = UnlockedIconPath, // Same icon for all - grayscale applied by DisplayIcon when locked
                ShowHiddenIcon = showHiddenIcon,
                ShowHiddenTitle = showHiddenTitle,
                ShowHiddenDescription = showHiddenDescription,
                ShowHiddenSuffix = showHiddenSuffix,
                ShowLockedIcon = showLockedIcon,
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
        /// Uses the standard mock achievements.
        /// </summary>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showRarityGlow">Whether to show the rarity glow.</param>
        /// <param name="showHiddenIcon">Whether to show hidden icons.</param>
        /// <param name="showHiddenTitle">Whether to show hidden titles.</param>
        /// <param name="showHiddenDescription">Whether to show hidden descriptions.</param>
        /// <param name="showLockedIcon">Whether to show locked icons.</param>
        /// <returns>List of mock achievement items.</returns>
        public static List<AchievementDisplayItem> CreateMockCompactListItems(
            bool showRarityBar = true,
            bool showRarityGlow = true,
            bool showHiddenIcon = true,
            bool showHiddenTitle = true,
            bool showHiddenDescription = true,
            bool showHiddenSuffix = true,
            bool showLockedIcon = true)
        {
            return GetStandardMockAchievements(
                showRarityBar, showRarityGlow,
                showHiddenIcon, showHiddenTitle, showHiddenDescription, showHiddenSuffix, showLockedIcon);
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
                showRarityBar, showRarityGlow,
                showHiddenIcon, showHiddenTitle, showHiddenDescription, showHiddenSuffix, showLockedIcon);
        }

        /// <summary>
        /// Creates a list of mock unlocked AchievementDisplayItems for unlocked list preview.
        /// Filters standard mock items to only include unlocked.
        /// </summary>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showRarityGlow">Whether to show the rarity glow.</param>
        /// <returns>List of mock unlocked achievement items.</returns>
        public static List<AchievementDisplayItem> CreateMockUnlockedListItems(
            bool showRarityBar = true,
            bool showRarityGlow = true,
            bool showLockedIcon = true)
        {
            var all = GetStandardMockAchievements(showRarityBar, showRarityGlow, true, true, true, true, showLockedIcon);
            return all.FindAll(item => item.Unlocked);
        }

        /// <summary>
        /// Creates a list of mock locked AchievementDisplayItems for locked list preview.
        /// Filters standard mock items to only include locked.
        /// </summary>
        /// <param name="showRarityBar">Whether to show the rarity bar.</param>
        /// <param name="showRarityGlow">Whether to show the rarity glow.</param>
        /// <param name="showHiddenIcon">Whether to show hidden icons.</param>
        /// <param name="showHiddenTitle">Whether to show hidden titles.</param>
        /// <param name="showHiddenDescription">Whether to show hidden descriptions.</param>
        /// <param name="showLockedIcon">Whether to show locked icons.</param>
        /// <returns>List of mock locked achievement items.</returns>
        public static List<AchievementDisplayItem> CreateMockLockedListItems(
            bool showRarityBar = true,
            bool showRarityGlow = true,
            bool showHiddenIcon = true,
            bool showHiddenTitle = true,
            bool showHiddenDescription = true,
            bool showHiddenSuffix = true,
            bool showLockedIcon = true)
        {
            var all = GetStandardMockAchievements(
                showRarityBar, showRarityGlow,
                showHiddenIcon, showHiddenTitle, showHiddenDescription, showHiddenSuffix, showLockedIcon);
            return all.FindAll(item => !item.Unlocked);
        }

        /// <summary>
        /// Creates mock theme data for previewing controls that bind to ThemeData.
        /// </summary>
        /// <returns>A MockThemeData object with standard preview values.</returns>
        public static MockThemeData GetMockThemeData()
        {
            if (_mockThemeData == null)
            {
                _mockThemeData = new MockThemeData();
            }
            return _mockThemeData;
        }

        /// <summary>
        /// Gets a ThemeData populated with mock achievements for settings preview.
        /// Used by desktop controls via ThemeDataOverride.
        /// </summary>
        /// <returns>A ThemeData with mock achievement data.</returns>
        public static ThemeData GetPreviewThemeData()
        {
            return CreatePreviewThemeData();
        }

        /// <summary>
        /// Gets a ThemeData with a single unlocked achievement for preview.
        /// </summary>
        public static ThemeData GetUnlockedPreviewThemeData()
        {
            return CreateSingleAchievementThemeData(unlocked: true, hidden: false);
        }

        /// <summary>
        /// Gets a ThemeData with a single locked+hidden achievement for preview.
        /// </summary>
        public static ThemeData GetHiddenPreviewThemeData()
        {
            return CreateSingleAchievementThemeData(unlocked: false, hidden: true);
        }

        /// <summary>
        /// Gets a ThemeData with a single locked (non-hidden) achievement for preview.
        /// </summary>
        public static ThemeData GetLockedPreviewThemeData()
        {
            return CreateSingleAchievementThemeData(unlocked: false, hidden: false);
        }

        private static ThemeData CreateSingleAchievementThemeData(bool unlocked, bool hidden)
        {
            var achievement = new AchievementDetail
            {
                ApiName = unlocked ? "preview_unlocked" : (hidden ? "preview_hidden" : "preview_locked"),
                DisplayName = unlocked ? "Unlocked Achievement" : (hidden ? "Hidden Secret" : "Locked Challenge"),
                Description = unlocked ? "You accomplished this goal" : (hidden ? "Discover the mystery" : "Complete this to unlock"),
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = UnlockedIconPath,
                Unlocked = unlocked,
                Hidden = hidden,
                GlobalPercentUnlocked = unlocked ? 8.0 : (hidden ? 15.0 : 25.0),
                UnlockTimeUtc = unlocked ? DateTime.UtcNow.AddDays(-1) : (DateTime?)null
            };

            var themeData = new ThemeData
            {
                HasAchievements = true,
                IsCompleted = unlocked,
                AchievementCount = 1,
                UnlockedCount = unlocked ? 1 : 0,
                LockedCount = unlocked ? 0 : 1,
                ProgressPercentage = unlocked ? 100.0 : 0.0,
                AllAchievements = new List<AchievementDetail> { achievement }
            };

            return themeData;
        }

        private static ThemeData CreatePreviewThemeData()
        {
            var themeData = new ThemeData
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
            themeData.UltraRare = new AchievementRarityStats { Unlocked = 1, Total = 1 };
            themeData.Rare = new AchievementRarityStats { Unlocked = 1, Total = 1 };
            themeData.Uncommon = new AchievementRarityStats { Unlocked = 0, Total = 1 };
            themeData.Common = new AchievementRarityStats { Unlocked = 0, Total = 2 };

            return themeData;
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
                GlobalPercentUnlocked = 25.0
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
                GlobalPercentUnlocked = 15.0
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
                GlobalPercentUnlocked = 75.0
            });

            return achievements;
        }
    }

    /// <summary>
    /// Mock theme data for settings preview controls.
    /// Mimics the structure of ThemeData for preview purposes.
    /// </summary>
    public class MockThemeData : ObservableObject
    {
        private int _unlockedCount = 2;
        private int _achievementCount = 5;
        private double _progressPercentage = 40.0;
        private bool _isCompleted = false;
        private bool _showRarityBar = true;
        private bool _showRarityGlow = true;

        private RarityStats _ultraRare = new RarityStats { Unlocked = 1, Total = 1 };
        private RarityStats _rare = new RarityStats { Unlocked = 1, Total = 1 };
        private RarityStats _uncommon = new RarityStats { Unlocked = 0, Total = 1 };
        private RarityStats _common = new RarityStats { Unlocked = 0, Total = 2 };

        public int UnlockedCount
        {
            get => _unlockedCount;
            set => SetValue(ref _unlockedCount, value);
        }

        public int AchievementCount
        {
            get => _achievementCount;
            set => SetValue(ref _achievementCount, value);
        }

        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => SetValue(ref _progressPercentage, value);
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set => SetValue(ref _isCompleted, value);
        }

        public bool ShowRarityBar
        {
            get => _showRarityBar;
            set => SetValue(ref _showRarityBar, value);
        }

        public bool ShowRarityGlow
        {
            get => _showRarityGlow;
            set => SetValue(ref _showRarityGlow, value);
        }

        public RarityStats UltraRare
        {
            get => _ultraRare;
            set => SetValue(ref _ultraRare, value);
        }

        public RarityStats Rare
        {
            get => _rare;
            set => SetValue(ref _rare, value);
        }

        public RarityStats Uncommon
        {
            get => _uncommon;
            set => SetValue(ref _uncommon, value);
        }

        public RarityStats Common
        {
            get => _common;
            set => SetValue(ref _common, value);
        }
    }

    /// <summary>
    /// Rarity statistics for mock theme data.
    /// </summary>
    public class RarityStats : ObservableObject
    {
        private int _unlocked;
        private int _total;

        public int Unlocked
        {
            get => _unlocked;
            set => SetValue(ref _unlocked, value);
        }

        public int Total
        {
            get => _total;
            set => SetValue(ref _total, value);
        }
    }
}
