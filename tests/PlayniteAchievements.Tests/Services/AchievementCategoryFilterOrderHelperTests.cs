using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementCategoryFilterOrderHelperTests
    {
        [TestMethod]
        public void BuildOrderedCategoryLabels_PreservesFirstSeenOrder()
        {
            var items = new[]
            {
                new AchievementDisplayItem { CategoryLabel = "Original Game" },
                new AchievementDisplayItem { CategoryLabel = "DLC 1: Revenge" },
                new AchievementDisplayItem { CategoryLabel = "Original Game" },
                new AchievementDisplayItem { CategoryLabel = "DLC 2: Redemption" }
            };

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                items,
                item => item?.CategoryLabel);

            CollectionAssert.AreEqual(
                new[] { "Original Game", "DLC 1: Revenge", "DLC 2: Redemption" },
                ordered);
        }

        [TestMethod]
        public void BuildOrderedCategoryLabels_NormalizesBlankValuesToDefault_AndDedupesCaseInsensitively()
        {
            var items = new[]
            {
                new AchievementDisplayItem { CategoryLabel = null },
                new AchievementDisplayItem { CategoryLabel = " default " },
                new AchievementDisplayItem { CategoryLabel = "DLC 1" },
                new AchievementDisplayItem { CategoryLabel = "dlc 1" },
                new AchievementDisplayItem { CategoryLabel = "" }
            };

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                items,
                item => item?.CategoryLabel);

            CollectionAssert.AreEqual(
                new[] { "Default", "DLC 1" },
                ordered);
        }

        [TestMethod]
        public void BuildOrderedCategoryLabels_AppliesPreferredOrderBeforeFirstSeenRemainder()
        {
            var items = new[]
            {
                new AchievementDisplayItem { CategoryLabel = "Base Game" },
                new AchievementDisplayItem { CategoryLabel = "DLC 2" },
                new AchievementDisplayItem { CategoryLabel = "DLC 1" },
                new AchievementDisplayItem { CategoryLabel = "Event" }
            };

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                items,
                item => item?.CategoryLabel,
                new[] { "dlc 1", "Missing", "base game", "DLC 1" });

            CollectionAssert.AreEqual(
                new[] { "DLC 1", "Base Game", "DLC 2", "Event" },
                ordered);
        }

        [TestMethod]
        public void BuildSelectedGameFilterOptions_UsesCanonicalCategoryOrder_AndPrunesInvalidSelections()
        {
            var selectedCategoryFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DLC 1: Revenge",
                "Missing"
            };

            var options = OverviewAchievementFilters.BuildSelectedGameFilterOptions(
                new[]
                {
                    new AchievementDisplayItem { CategoryType = "Default", CategoryLabel = "Original Game" },
                    new AchievementDisplayItem { CategoryType = "DLC", CategoryLabel = "DLC 1: Revenge" },
                    new AchievementDisplayItem { CategoryType = "DLC", CategoryLabel = "DLC 2: Redemption" },
                    new AchievementDisplayItem { CategoryType = "DLC", CategoryLabel = "Original Game" }
                },
                selectedTypeFilters: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                selectedCategoryFilters: selectedCategoryFilters);

            CollectionAssert.AreEqual(
                new[] { "Original Game", "DLC 1: Revenge", "DLC 2: Redemption" },
                options.CategoryOptions);
            Assert.IsTrue(options.CategorySelectionPruned);
            CollectionAssert.AreEquivalent(
                new[] { "DLC 1: Revenge" },
                selectedCategoryFilters.ToList());
        }
    }
}
