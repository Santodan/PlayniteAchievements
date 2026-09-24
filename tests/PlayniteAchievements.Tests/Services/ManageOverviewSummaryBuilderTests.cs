using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.ManageAchievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class ManageOverviewSummaryBuilderTests
    {
        [TestMethod]
        public void BuildBreakdown_Empty_ReturnsZeroes()
        {
            var breakdown = ManageOverviewSummaryBuilder.BuildBreakdown(null);

            Assert.AreEqual(0, breakdown.Stats.TotalAchievements);
            Assert.AreEqual(0, breakdown.TotalPoints);
            Assert.AreEqual(0, breakdown.CategorizedCount);
        }

        [TestMethod]
        public void BuildBreakdown_CountsPointsAndCategorized()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("a", unlocked: true, points: 10, category: "Base"),
                Achievement("b", unlocked: false, points: 20, category: "DLC"),
                Achievement("c", unlocked: true, points: 30, category: "base"),
                Achievement("d", unlocked: false, points: null, category: null),
                Achievement("e", unlocked: false, points: null, category: "Default"),
                null
            };

            var breakdown = ManageOverviewSummaryBuilder.BuildBreakdown(achievements);

            Assert.AreEqual(5, breakdown.Stats.TotalAchievements);
            Assert.AreEqual(60, breakdown.TotalPoints);
            Assert.AreEqual(40, breakdown.UnlockedPoints);
            Assert.AreEqual(3, breakdown.CategorizedCount);
        }

        [TestMethod]
        public void BuildCapstones_PairsNamesWithNonDefaultCategories()
        {
            var game = Achievement("a", unlocked: true, points: null, category: null);
            game.IsCapstone = true;
            game.DisplayName = "All Done";
            var dlc = Achievement("b", unlocked: false, points: null, category: "DLC");
            dlc.IsCapstone = true;
            var notCapstone = Achievement("c", unlocked: false, points: null, category: "DLC");

            var capstones = ManageOverviewSummaryBuilder.BuildCapstones(new[] { game, dlc, notCapstone });

            Assert.AreEqual(2, capstones.Count);
            Assert.IsNull(capstones[0].Item1);
            Assert.AreEqual("All Done", capstones[0].Item2);
            Assert.AreEqual("DLC", capstones[1].Item1);
            Assert.AreEqual("b", capstones[1].Item2);
        }

        [TestMethod]
        public void BuildCustomizationCounts_NullOrEmpty_ReturnsNothing()
        {
            Assert.AreEqual(0, ManageOverviewSummaryBuilder.BuildCustomizationCounts(null).Count);
            Assert.AreEqual(0, ManageOverviewSummaryBuilder.BuildCustomizationCounts(new GameCustomDataFile()).Count);
        }

        [TestMethod]
        public void BuildCustomizationCounts_CountsOverrideFieldsAndSkipsBlanks()
        {
            var data = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["a"] = new AchievementOverride { DisplayName = "New", UnlockedIconPath = "a.png", Note = "n" },
                    ["b"] = new AchievementOverride { DisplayName = "Other", LockedIconPath = "b.png", ClearUnlockTime = true },
                    ["c"] = new AchievementOverride { DisplayName = "   ", UnlockedIconPath = "c.png", Hidden = false },
                }
            };

            var counts = ToMap(ManageOverviewSummaryBuilder.BuildCustomizationCounts(data));

            Assert.AreEqual(2, counts["LOCPlayAch_Column_AchievementName"]);
            Assert.AreEqual(2, counts["LOCPlayAch_ManageAchievements_Custom_UnlockedIcon"]);
            Assert.AreEqual(1, counts["LOCPlayAch_ManageAchievements_Custom_LockedIcon"]);
            Assert.AreEqual(1, counts["LOCPlayAch_ManageAchievements_Notes_Note"]);
            Assert.AreEqual(1, counts["LOCPlayAch_Common_UnlockTime"]);
            Assert.AreEqual(1, counts["LOCPlayAch_ManageAchievements_Custom_Hidden"]);
            Assert.IsFalse(counts.ContainsKey("LOCPlayAch_Column_Description"));
        }

        [TestMethod]
        public void BuildCustomizationCounts_FilterScopeDedupesAcrossBothLists()
        {
            var data = new GameCustomDataFile
            {
                FilteredAchievementApiNames = new List<string> { "a", "b" },
                SummaryFilteredAchievementApiNames = new List<string> { "B", "c", " " },
                GoalAchievementApiNames = new List<string> { "a", "a" }
            };

            var counts = ToMap(ManageOverviewSummaryBuilder.BuildCustomizationCounts(data));

            Assert.AreEqual(3, counts["LOCPlayAch_Menu_Filters"]);
            Assert.AreEqual(1, counts["LOCPlayAch_ManageAchievements_Editor_Goal"]);
        }

        [TestMethod]
        public void BuildCustomizationCounts_MaterializedEmptyCapstones_AreLeftOut()
        {
            var data = new GameCustomDataFile { CapstonesMaterialized = true };

            var counts = ToMap(ManageOverviewSummaryBuilder.BuildCustomizationCounts(data));

            Assert.IsFalse(counts.ContainsKey("LOCPlayAch_Dynamic_Capstone"));
        }

        [TestMethod]
        public void BuildCustomizationCounts_GameLevelSettingsAreFlagsInDisplayOrder()
        {
            var data = new GameCustomDataFile
            {
                CustomAchievements = new List<CustomAchievementDefinition> { new CustomAchievementDefinition() },
                AchievementCategoryOrder = new List<string> { "DLC" },
                ProviderOverride = new ProviderOverrideData { ProviderKey = "Steam" },
                UseSeparateLockedIconsOverride = true
            };

            var counts = ManageOverviewSummaryBuilder.BuildCustomizationCounts(data);

            CollectionAssert.AreEqual(
                new[]
                {
                    ManageOverviewSummaryBuilder.CustomAchievementsLabelKey,
                    ManageOverviewSummaryBuilder.OrderLabelKey,
                    ManageOverviewSummaryBuilder.ProviderOverrideLabelKey,
                    ManageOverviewSummaryBuilder.SeparateLockedIconsLabelKey
                },
                counts.Select(c => c.LabelKey).ToArray());
            Assert.AreEqual(1, counts[0].Count);
            Assert.IsNull(counts[1].Count);
            Assert.IsNull(counts[2].Count);
        }

        private static Dictionary<string, int?> ToMap(IEnumerable<ManageOverviewCustomizationCount> counts)
        {
            return counts.ToDictionary(c => c.LabelKey, c => c.Count);
        }

        private static AchievementDetail Achievement(
            string apiName,
            bool unlocked,
            int? points,
            string category)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                Unlocked = unlocked,
                Points = points,
                Category = category
            };
        }
    }
}
