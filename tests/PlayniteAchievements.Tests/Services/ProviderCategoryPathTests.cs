using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Covers the path composition providers use to emit nested categories, and the repoint that
    /// carries a game's label-keyed metadata across the labels that changed shape.
    /// </summary>
    [TestClass]
    public class ProviderCategoryPathTests
    {
        [TestMethod]
        public void SanitizeSegment_CollapsesSeparatorSoAnUpstreamNameCannotInventALevel()
        {
            Assert.AreEqual("Chapter : One", CategoryPathHelper.SanitizeSegment("Chapter :: One"));
            Assert.AreEqual("A:B", CategoryPathHelper.SanitizeSegment("A::B"));

            // Non-overlapping Replace leaves "::" behind on the first pass; the loop finishes it.
            Assert.AreEqual("A:B", CategoryPathHelper.SanitizeSegment("A:::B"));
            Assert.AreEqual("A:B", CategoryPathHelper.SanitizeSegment("A::::B"));
        }

        [TestMethod]
        public void SanitizeSegment_TrimsAndTreatsBlankAsAbsent()
        {
            Assert.AreEqual("A", CategoryPathHelper.SanitizeSegment("  A  "));
            Assert.IsNull(CategoryPathHelper.SanitizeSegment(null));
            Assert.IsNull(CategoryPathHelper.SanitizeSegment("   "));
        }

        [TestMethod]
        public void JoinRaw_ComposesAPathAndKeepsAnUpstreamSeparatorInsideOneSegment()
        {
            Assert.AreEqual("A::B", CategoryPathHelper.JoinRaw("A", "B"));
            Assert.AreEqual("A:B::C", CategoryPathHelper.JoinRaw("A::B", "C"));
            Assert.AreEqual(2, CategoryPathHelper.Split(CategoryPathHelper.JoinRaw("A::B", "C")).Count);
        }

        [TestMethod]
        public void JoinRaw_DegeneratesToTheParentWhenTheChildIsMissing()
        {
            Assert.AreEqual("A", CategoryPathHelper.JoinRaw("A", null));
            Assert.AreEqual("A", CategoryPathHelper.JoinRaw("A", "   "));
            Assert.AreEqual("B", CategoryPathHelper.JoinRaw(null, "B"));
        }

        [TestMethod]
        public void JoinRaw_ReturnsNullWhenNothingSurvivesSoTheHydratorAppliesTheDefault()
        {
            Assert.IsNull(CategoryPathHelper.JoinRaw(null, null));
            Assert.IsNull(CategoryPathHelper.JoinRaw("  ", null));
            Assert.IsNull(CategoryPathHelper.JoinRaw((string[])null));
        }

        [TestMethod]
        public void JoinRaw_FoldsAChainDeeperThanTheDepthCap()
        {
            var deep = Enumerable.Range(1, CategoryPathHelper.MaxDepth + 3)
                .Select(i => "L" + i)
                .ToArray();

            var path = CategoryPathHelper.JoinRaw(deep);

            Assert.AreEqual(CategoryPathHelper.MaxDepth, CategoryPathHelper.Split(path).Count);
        }

        [TestMethod]
        public void Migration_RepointsOrderAndArtFromTheOldFlatLabel()
        {
            var images = new Dictionary<string, CategoryImageOverrideData>
            {
                ["Game A - DLC Pack"] = new CategoryImageOverrideData { Art = "art.png" }
            };

            var plan = ProviderCategoryPathMigration.Plan(
                new[] { "Game A::DLC Pack" },
                new List<string> { "Game A - DLC Pack" },
                images,
                currentSummaryCategory: null);

            Assert.IsNotNull(plan);
            CollectionAssert.Contains(plan.Order, "Game A::DLC Pack");
            Assert.IsTrue(plan.Images.ContainsKey("Game A::DLC Pack"));
            Assert.AreEqual("art.png", plan.Images["Game A::DLC Pack"].Art);
        }

        [TestMethod]
        public void Migration_RebuildsTheOldLabelFromSegmentsSoATitleContainingADashSurvives()
        {
            // The old code composed "{setTitle} - {groupName}", and the set title itself contains
            // " - ". Reversing the separator textually would have split it in the wrong place.
            var plan = ProviderCategoryPathMigration.Plan(
                new[] { "Ratchet & Clank - Size Matters::Bonus" },
                new List<string> { "Ratchet & Clank - Size Matters - Bonus" },
                currentImages: null,
                currentSummaryCategory: null);

            Assert.IsNotNull(plan);
            CollectionAssert.Contains(plan.Order, "Ratchet & Clank - Size Matters::Bonus");
        }

        [TestMethod]
        public void Migration_LeavesAUserLabelContainingADashAloneWhenNoProviderPathMatches()
        {
            var plan = ProviderCategoryPathMigration.Plan(
                new[] { "Something::Else" },
                new List<string> { "My - Own Label" },
                currentImages: null,
                currentSummaryCategory: null);

            Assert.IsNull(plan);
        }

        [TestMethod]
        public void Migration_IsANoOpOnceTheGameAlreadyHoldsTheNewPath()
        {
            var plan = ProviderCategoryPathMigration.Plan(
                new[] { "Game A::DLC Pack" },
                new List<string> { "Game A - DLC Pack", "Game A::DLC Pack" },
                currentImages: null,
                currentSummaryCategory: null);

            Assert.IsNull(plan);
        }

        [TestMethod]
        public void Migration_IgnoresFlatProviderLabels()
        {
            var plan = ProviderCategoryPathMigration.Plan(
                new[] { "Base Game" },
                new List<string> { "Base Game" },
                currentImages: null,
                currentSummaryCategory: null);

            Assert.IsNull(plan);
        }

        [TestMethod]
        public void Migration_CarriesTheSummarySelectionAcross()
        {
            var summary = new GameSummaryCategoryData { Label = "Game A - DLC Pack" };

            var plan = ProviderCategoryPathMigration.Plan(
                new[] { "Game A::DLC Pack" },
                new List<string> { "Game A - DLC Pack" },
                currentImages: null,
                currentSummaryCategory: summary);

            Assert.IsNotNull(plan);
            Assert.AreEqual("Game A::DLC Pack", plan.SummaryCategory.Label);
        }
    }
}
