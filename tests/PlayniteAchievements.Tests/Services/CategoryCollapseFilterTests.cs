using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CategoryCollapseFilterTests
    {
        [TestMethod]
        public void Apply_CollapsedParentKeepsItselfAndDropsItsSubtreeAndSelfRow()
        {
            var rows = Rows("Story", "Story!self", "Story::Act 1", "Story::Act 1::Finale", "Extras");

            var visible = CategoryCollapseFilter.Apply(rows, Collapsed("Story"), out var removedAny);

            Assert.IsTrue(removedAny);
            CollectionAssert.AreEqual(new[] { "Story", "Extras" }, Paths(visible));
            Assert.IsFalse(visible.Cast<CategorySummaryItem>().Any(c => c.IsSelfRow),
                "the self row shares its category's path and collapses with it");
        }

        [TestMethod]
        public void Apply_MidTreeCollapseKeepsFollowingSiblingsAndTheirSubtrees()
        {
            var rows = Rows("Story", "Story::Act 1", "Story::Act 1::Finale", "Story::Act 2", "Story::Act 2::Coda");

            var visible = CategoryCollapseFilter.Apply(rows, Collapsed("Story::Act 1"), out var removedAny);

            Assert.IsTrue(removedAny);
            CollectionAssert.AreEqual(
                new[] { "Story", "Story::Act 1", "Story::Act 2", "Story::Act 2::Coda" },
                Paths(visible));
        }

        [TestMethod]
        public void Apply_CollapsedNodeInsideAHiddenSubtreeStaysHidden()
        {
            var rows = Rows("Story", "Story::Act 1", "Story::Act 1::Finale");

            var visible = CategoryCollapseFilter.Apply(
                rows, Collapsed("Story", "Story::Act 1"), out _);

            CollectionAssert.AreEqual(new[] { "Story" }, Paths(visible),
                "a nested collapsed key is inert while an ancestor hides it");
        }

        [TestMethod]
        public void Apply_StaleKeysAreInert()
        {
            var rows = Rows("Story", "Story::Act 1");

            var visible = CategoryCollapseFilter.Apply(rows, Collapsed("Removed::Path"), out var removedAny);

            Assert.IsFalse(removedAny);
            CollectionAssert.AreEqual(new[] { "Story", "Story::Act 1" }, Paths(visible));
        }

        [TestMethod]
        public void Apply_MatchingIsCaseInsensitive()
        {
            var rows = Rows("Story", "Story::Act 1");

            var visible = CategoryCollapseFilter.Apply(rows, Collapsed("sTORY"), out var removedAny);

            Assert.IsTrue(removedAny);
            CollectionAssert.AreEqual(new[] { "Story" }, Paths(visible));
        }

        [TestMethod]
        public void Apply_PrefixSiblingIsNotMistakenForADescendant()
        {
            // Descendant tests compare on the separator boundary: "Story Time" must survive a
            // collapsed "Story".
            var rows = Rows("Story", "Story::Act 1", "Story Time");

            var visible = CategoryCollapseFilter.Apply(rows, Collapsed("Story"), out _);

            CollectionAssert.AreEqual(new[] { "Story", "Story Time" }, Paths(visible));
        }

        [TestMethod]
        public void Apply_NothingCollapsedReportsNoRemovals()
        {
            var rows = Rows("Story", "Story::Act 1");

            var visible = CategoryCollapseFilter.Apply(rows, Collapsed(), out var removedAny);

            Assert.IsFalse(removedAny);
            Assert.AreEqual(rows.Count, visible.Count);
        }

        private static ISet<string> Collapsed(params string[] paths)
        {
            return new HashSet<string>(paths, System.StringComparer.OrdinalIgnoreCase);
        }

        private static string[] Paths(IEnumerable<GameSummaryItem> rows)
        {
            return rows.Cast<CategorySummaryItem>().Select(c => c.CategoryPath).ToArray();
        }

        /// <summary>
        /// One row per spec: a plain category path, or "path!self" for that category's self row.
        /// </summary>
        private static List<GameSummaryItem> Rows(params string[] specs)
        {
            var rows = new List<GameSummaryItem>();
            foreach (var spec in specs)
            {
                var text = spec;
                var marker = text.IndexOf("!self", System.StringComparison.Ordinal);
                var isSelf = marker >= 0;
                if (isSelf)
                {
                    text = text.Substring(0, marker);
                }

                rows.Add(new CategorySummaryItem
                {
                    CategoryPath = text,
                    CategoryLabel = text,
                    IsSelfRow = isSelf
                });
            }

            return rows;
        }
    }
}
