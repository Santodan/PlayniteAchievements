using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.ThemeIntegration;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    public class DynamicThemeOptionFactoryTests
    {
        [TestMethod]
        public void CreateCategoryLabelOptions_PlacesAllFirstWithTotalCount()
        {
            var options = DynamicThemeOptionFactory.CreateCategoryLabelOptions(
                new List<AchievementDetail>
                {
                    Achievement("One", category: "Base Game"),
                    Achievement("Two", category: "Base Game"),
                    Achievement("Three", category: "Frozen Wilds")
                },
                selectedKey: null);

            Assert.AreEqual(DynamicThemeViewKeys.All, options[0].Key);
            Assert.AreEqual("All", options[0].Label);
            Assert.AreEqual(3, options[0].Count);
        }

        [TestMethod]
        public void CreateCategoryLabelOptions_OrdersCustomOrderedLabelsFirstThenFirstSeen()
        {
            var options = DynamicThemeOptionFactory.CreateCategoryLabelOptions(
                new List<AchievementDetail>
                {
                    Achievement("Z1", category: "Zeta", categoryOrderIndex: 1),
                    Achievement("A1", category: "Alpha", categoryOrderIndex: 0),
                    Achievement("M1", category: "Midway")
                },
                selectedKey: null);

            CollectionAssert.AreEqual(
                new[] { DynamicThemeViewKeys.All, "Alpha", "Zeta", "Midway" },
                options.Select(option => option.Key).ToArray());
        }

        [TestMethod]
        public void CreateCategoryLabelOptions_CountsItemsPerLabel()
        {
            var options = DynamicThemeOptionFactory.CreateCategoryLabelOptions(
                new List<AchievementDetail>
                {
                    Achievement("One", category: "Base Game"),
                    Achievement("Two", category: "Base Game"),
                    Achievement("Three", category: "Frozen Wilds")
                },
                selectedKey: null);

            Assert.AreEqual(2, options.Single(option => option.Key == "Base Game").Count);
            Assert.AreEqual(1, options.Single(option => option.Key == "Frozen Wilds").Count);
        }

        [TestMethod]
        public void CreateCategoryLabelOptions_BlankCategoryNormalizesToDefault()
        {
            var options = DynamicThemeOptionFactory.CreateCategoryLabelOptions(
                new List<AchievementDetail>
                {
                    Achievement("One", category: null),
                    Achievement("Two", category: "   ")
                },
                selectedKey: null);

            var defaultOption = options.Single(option => option.Key == "Default");
            Assert.AreEqual("Default", defaultOption.Label);
            Assert.AreEqual(2, defaultOption.Count);
        }

        [TestMethod]
        public void CreateCategoryLabelOptions_SelectedKeyMarksMatchingOption()
        {
            var options = DynamicThemeOptionFactory.CreateCategoryLabelOptions(
                new List<AchievementDetail>
                {
                    Achievement("One", category: "Base Game"),
                    Achievement("Two", category: "Frozen Wilds")
                },
                selectedKey: "Frozen Wilds");

            Assert.IsTrue(options.Single(option => option.Key == "Frozen Wilds").IsSelected);
            Assert.IsFalse(options.Single(option => option.Key == DynamicThemeViewKeys.All).IsSelected);
            Assert.IsFalse(options.Single(option => option.Key == "Base Game").IsSelected);
        }

        private static AchievementDetail Achievement(
            string name,
            string category = null,
            int categoryOrderIndex = int.MaxValue)
        {
            return new AchievementDetail
            {
                ApiName = name,
                DisplayName = name,
                Category = category,
                CategoryOrderIndex = categoryOrderIndex
            };
        }
    }
}
