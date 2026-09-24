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
        public void BuildBreakdown_Empty_ReturnsZeroesAndNoCategories()
        {
            var breakdown = ManageOverviewSummaryBuilder.BuildBreakdown(null);

            Assert.AreEqual(0, breakdown.Stats.TotalAchievements);
            Assert.AreEqual(0, breakdown.TotalPoints);
            Assert.AreEqual(0, breakdown.HiddenCount);
            Assert.IsNull(breakdown.LastUnlockUtc);
            Assert.AreEqual(0, breakdown.CategorizedCount);
            Assert.IsFalse(breakdown.HasPoints);
            Assert.IsFalse(breakdown.HasTrophies);
        }

        [TestMethod]
        public void BuildBreakdown_CountsPointsHiddenLastUnlockAndCategorized()
        {
            var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var late = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            var achievements = new List<AchievementDetail>
            {
                Achievement("a", unlocked: true, points: 10, category: "Base", unlockTimeUtc: early),
                Achievement("b", unlocked: false, points: 20, category: "DLC", hidden: true),
                Achievement("c", unlocked: true, points: 30, category: "base", unlockTimeUtc: late),
                Achievement("d", unlocked: false, points: null, category: null),
                Achievement("e", unlocked: false, points: null, category: "Default"),
                null
            };

            var breakdown = ManageOverviewSummaryBuilder.BuildBreakdown(achievements);

            Assert.AreEqual(5, breakdown.Stats.TotalAchievements);
            Assert.AreEqual(60, breakdown.TotalPoints);
            Assert.AreEqual(40, breakdown.UnlockedPoints);
            Assert.AreEqual(1, breakdown.HiddenCount);
            Assert.AreEqual(late, breakdown.LastUnlockUtc);
            Assert.AreEqual(3, breakdown.CategorizedCount);
        }

        [TestMethod]
        public void BuildBreakdown_TrophyTypes_SetHasTrophies()
        {
            var breakdown = ManageOverviewSummaryBuilder.BuildBreakdown(new[]
            {
                Achievement("a", unlocked: true, points: null, category: null, trophy: "gold")
            });

            Assert.IsTrue(breakdown.HasTrophies);
            Assert.AreEqual(1, breakdown.Stats.TrophyGoldTotal);
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
            string category,
            bool hidden = false,
            DateTime? unlockTimeUtc = null,
            string trophy = null)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                Unlocked = unlocked,
                Points = points,
                Category = category,
                Hidden = hidden,
                UnlockTimeUtc = unlockTimeUtc,
                TrophyType = trophy
            };
        }
    }
}
