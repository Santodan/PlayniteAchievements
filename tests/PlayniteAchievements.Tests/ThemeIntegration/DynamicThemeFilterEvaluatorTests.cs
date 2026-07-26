using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.ThemeIntegration;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    public class DynamicThemeFilterEvaluatorTests
    {
        [TestMethod]
        public void ApplyAchievementFilters_CategoryTypeKey_KeepsOnlyMatchingType()
        {
            var items = new List<AchievementDetail>
            {
                Achievement("DLC Item", categoryType: "DLC"),
                Achievement("Base Item", categoryType: "Base"),
                Achievement("Untyped Item", categoryType: null)
            };

            var result = DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "DLC");

            AssertNames(result, "DLC Item");
        }

        [TestMethod]
        public void ApplyAchievementFilters_MultiValuedCategoryType_MatchesEachTypeKey()
        {
            var items = new List<AchievementDetail>
            {
                Achievement("Both Types", categoryType: "Base|DLC"),
                Achievement("Multiplayer Only", categoryType: "Multiplayer")
            };

            AssertNames(
                DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "Base"),
                "Both Types");
            AssertNames(
                DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "DLC"),
                "Both Types");
        }

        [TestMethod]
        public void ApplyAchievementFilters_CategoryTypeAlias_CanonicalizesBeforeMatching()
        {
            var items = new List<AchievementDetail>
            {
                Achievement("Alias Item", categoryType: "collectible"),
                Achievement("Base Item", categoryType: "Base")
            };

            var result = DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "Collectable");

            AssertNames(result, "Alias Item");
        }

        [TestMethod]
        public void ApplyAchievementFilters_SameGroupCategoryTypes_MatchEitherType()
        {
            var items = new List<AchievementDetail>
            {
                Achievement("Base Item", categoryType: "Base"),
                Achievement("DLC Item", categoryType: "DLC"),
                Achievement("Multiplayer Item", categoryType: "Multiplayer")
            };

            var result = DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "Base+DLC");

            AssertNames(result, "Base Item", "DLC Item");
        }

        [TestMethod]
        public void ApplyAchievementFilters_CrossGroupKeys_RequireAllGroups()
        {
            var items = new List<AchievementDetail>
            {
                Achievement("Locked DLC", categoryType: "DLC", unlocked: false),
                Achievement("Unlocked DLC", categoryType: "DLC", unlocked: true),
                Achievement("Locked Base", categoryType: "Base", unlocked: false)
            };

            var result = DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "Locked+DLC");

            AssertNames(result, "Locked DLC");
        }

        [TestMethod]
        public void ApplyAchievementFilters_MissingCategoryType_DoesNotMatchTypeKeys()
        {
            var items = new List<AchievementDetail>
            {
                Achievement("Null Type", categoryType: null),
                Achievement("Empty Type", categoryType: string.Empty)
            };

            var result = DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "DLC");

            Assert.AreEqual(0, result.Count());
        }

        [TestMethod]
        public void ApplyAchievementFilters_UnknownFilterKey_ReturnsAllItems()
        {
            var items = new List<AchievementDetail>
            {
                Achievement("DLC Item", categoryType: "DLC"),
                Achievement("Untyped Item", categoryType: null)
            };

            var result = DynamicThemeFilterEvaluator.ApplyAchievementFilters(items, "NotARealFilterKey");

            AssertNames(result, "DLC Item", "Untyped Item");
        }

        private static AchievementDetail Achievement(
            string name,
            string categoryType = null,
            bool unlocked = true)
        {
            return new AchievementDetail
            {
                ApiName = name,
                DisplayName = name,
                CategoryType = categoryType,
                Unlocked = unlocked
            };
        }

        private static void AssertNames(IEnumerable<AchievementDetail> items, params string[] expectedDisplayNames)
        {
            CollectionAssert.AreEqual(
                expectedDisplayNames,
                items.Select(item => item.DisplayName).ToArray());
        }
    }
}
