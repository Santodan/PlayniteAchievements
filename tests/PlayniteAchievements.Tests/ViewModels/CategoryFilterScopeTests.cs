using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.ManageAchievements;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The Categories tab pushes one filter scope onto a category and everything under it. The
    /// scale itself is stored as two flags on the achievements, so these pin the three things a
    /// push depends on: what a scope means, what a group of achievements adds up to, and that a
    /// push over part of a game leaves the rest of the game's filters alone.
    /// </summary>
    [TestClass]
    public class CategoryFilterScopeTests
    {
        [TestMethod]
        public void ToFlags_NeverSetsBothFlags()
        {
            foreach (var scope in new[]
            {
                AchievementFilterScope.None,
                AchievementFilterScope.Summary,
                AchievementFilterScope.All
            })
            {
                AchievementFilterScopes.ToFlags(scope, out var isFiltered, out var isSummaryFiltered);
                Assert.IsFalse(
                    isFiltered && isSummaryFiltered,
                    $"{scope} stored both flags; filtering out entirely already covers summaries.");
            }
        }

        [TestMethod]
        public void ToFlags_MatchesTheScaleTheEditorStores()
        {
            AchievementFilterScopes.ToFlags(AchievementFilterScope.None, out var noneFiltered, out var noneSummary);
            Assert.IsFalse(noneFiltered);
            Assert.IsFalse(noneSummary);

            AchievementFilterScopes.ToFlags(AchievementFilterScope.Summary, out var summaryFiltered, out var summarySummary);
            Assert.IsFalse(summaryFiltered);
            Assert.IsTrue(summarySummary);

            AchievementFilterScopes.ToFlags(AchievementFilterScope.All, out var allFiltered, out var allSummary);
            Assert.IsTrue(allFiltered);
            Assert.IsFalse(allSummary);
        }

        [TestMethod]
        public void FromFlags_RoundTripsEveryRealScope()
        {
            foreach (var scope in new[]
            {
                AchievementFilterScope.None,
                AchievementFilterScope.Summary,
                AchievementFilterScope.All
            })
            {
                AchievementFilterScopes.ToFlags(scope, out var isFiltered, out var isSummaryFiltered);
                Assert.AreEqual(scope, AchievementFilterScopes.FromFlags(isFiltered, isSummaryFiltered));
            }
        }

        [TestMethod]
        public void FromMemberCounts_ReportsTheScopeAnAgreeingSubtreeHolds()
        {
            Assert.AreEqual(
                AchievementFilterScope.All,
                AchievementFilterScopes.FromMemberCounts(total: 4, filteredCount: 4, summaryEffectiveCount: 4));

            Assert.AreEqual(
                AchievementFilterScope.None,
                AchievementFilterScopes.FromMemberCounts(total: 4, filteredCount: 0, summaryEffectiveCount: 0));

            Assert.AreEqual(
                AchievementFilterScope.Summary,
                AchievementFilterScopes.FromMemberCounts(total: 4, filteredCount: 0, summaryEffectiveCount: 4));
        }

        [TestMethod]
        public void FromMemberCounts_ReportsMixedWhenTheSubtreeDisagrees()
        {
            Assert.AreEqual(
                AchievementFilterScope.Mixed,
                AchievementFilterScopes.FromMemberCounts(total: 4, filteredCount: 1, summaryEffectiveCount: 1));

            // Every member is out of summaries but only some are out of the views as well, so the
            // subtree agrees on nothing a single choice could express.
            Assert.AreEqual(
                AchievementFilterScope.Mixed,
                AchievementFilterScopes.FromMemberCounts(total: 4, filteredCount: 2, summaryEffectiveCount: 4));
        }

        [TestMethod]
        public void FromMemberCounts_TreatsAnEmptySubtreeAsUnfiltered()
        {
            Assert.AreEqual(
                AchievementFilterScope.None,
                AchievementFilterScopes.FromMemberCounts(total: 0, filteredCount: 0, summaryEffectiveCount: 0));
        }

        [TestMethod]
        public void Apply_LeavesAchievementsOutsideTheGroupUntouched()
        {
            // The writer replaces both stored lists wholesale, so a push that only mutated its own
            // achievements would drop every filter set elsewhere in the game the moment it wrote.
            var filtered = NewSet("outside-filtered");
            var summaryFiltered = NewSet("outside-summary");

            AchievementFilterScopes.Apply(
                filtered,
                summaryFiltered,
                new[] { "inside-a", "inside-b" },
                AchievementFilterScope.All);

            CollectionAssert.AreEquivalent(
                new[] { "outside-filtered", "inside-a", "inside-b" },
                filtered.ToList());
            CollectionAssert.AreEquivalent(new[] { "outside-summary" }, summaryFiltered.ToList());
        }

        [TestMethod]
        public void Apply_MovingToSummariesClearsTheStrongerFlag()
        {
            var filtered = NewSet("a");
            var summaryFiltered = NewSet();

            AchievementFilterScopes.Apply(filtered, summaryFiltered, new[] { "a" }, AchievementFilterScope.Summary);

            Assert.IsFalse(filtered.Contains("a"), "Summaries must not leave the achievement filtered out entirely.");
            Assert.IsTrue(summaryFiltered.Contains("a"));
        }

        [TestMethod]
        public void Apply_NoneClearsBothFlags()
        {
            var filtered = NewSet("a");
            var summaryFiltered = NewSet("a");

            AchievementFilterScopes.Apply(filtered, summaryFiltered, new[] { "a" }, AchievementFilterScope.None);

            Assert.IsFalse(filtered.Contains("a"));
            Assert.IsFalse(summaryFiltered.Contains("a"));
        }

        [TestMethod]
        public void SubtreeIndex_GivesAParentItsSubcategoriesAchievements()
        {
            var hacksA = Path("Romhacks", "Hacks A");
            var index = CategorySubtreeIndex.Build(
                new[]
                {
                    new Member(hacksA, "a1"),
                    new Member(Path("Romhacks", "Hacks B"), "b1"),
                    new Member("Main Set", "m1")
                },
                member => member.Category,
                member => member.ApiName);

            CollectionAssert.AreEquivalent(new[] { "a1", "b1" }, index["Romhacks"]);
            CollectionAssert.AreEquivalent(new[] { "a1" }, index[hacksA]);
            CollectionAssert.AreEquivalent(new[] { "m1" }, index["Main Set"]);
            Assert.IsFalse(
                index["Romhacks"].Contains("m1"),
                "A push on one category must not reach a sibling category.");
        }

        [TestMethod]
        public void SubtreeIndex_SkipsAchievementsWithoutAnApiName()
        {
            var index = CategorySubtreeIndex.Build(
                new[] { new Member("Romhacks", " "), new Member("Romhacks", "a1") },
                member => member.Category,
                member => member.ApiName);

            CollectionAssert.AreEquivalent(new[] { "a1" }, index["Romhacks"]);
        }

        /// <summary>
        /// Builds a nested label with the helper's own separator, which is internal to the path
        /// format and never typed by a user, so a literal here would pin the wrong contract.
        /// </summary>
        private static string Path(string parent, string child)
        {
            return string.Concat(parent, CategoryPathHelper.Separator, child);
        }

        private static HashSet<string> NewSet(params string[] values)
        {
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }

        private sealed class Member
        {
            public Member(string category, string apiName)
            {
                Category = category;
                ApiName = apiName;
            }

            public string Category { get; }

            public string ApiName { get; }
        }
    }
}
