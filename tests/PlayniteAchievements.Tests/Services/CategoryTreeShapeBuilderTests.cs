using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CategoryTreeShapeBuilderTests
    {
        [TestMethod]
        public void Stamp_PlacesChildrenUnderTheirCategory()
        {
            var rows = Rows("Story", "Story::Act 1", "Story::Act 2");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            var story = rows[0].TreeShape;
            var act1 = rows[1].TreeShape;
            var act2 = rows[2].TreeShape;

            Assert.AreEqual(1, story.Depth);
            Assert.IsTrue(story.HasChildren, "the junction opens for the children");

            Assert.AreEqual(2, act1.Depth);
            Assert.IsFalse(act1.IsLastSibling);
            Assert.IsTrue(act2.IsLastSibling, "the last child category closes the run");
        }

        [TestMethod]
        public void Stamp_NestedRowsKeepAncestorLanesOpen()
        {
            // Act 1 has a later sibling (Act 2), so every row inside Act 1's subtree must carry
            // Act 1's lane through itself.
            var rows = Rows(
                "Story",
                "Story::Act 1",
                "Story::Act 1::Finale",
                "Story::Act 2",
                "Extras");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            var finale = rows[2].TreeShape;
            Assert.AreEqual(3, finale.Depth);
            Assert.IsTrue(finale.IsLastSibling);
            Assert.AreEqual(1, finale.AncestorContinues.Count);
            Assert.IsTrue(finale.AncestorContinues[0], "Act 1's lane continues toward Act 2 through the row");
        }

        [TestMethod]
        public void Stamp_DisabledClearsShapes()
        {
            var rows = Rows("Story", "Story::Act 1");
            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            CategoryTreeShapeBuilder.Stamp(rows, enabled: false);

            Assert.IsTrue(rows.All(r => r.TreeShape == null));
        }

        [TestMethod]
        public void Stamp_FlatListStillClearsShapesByDefault()
        {
            var rows = Rows("Story", "Extras");
            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            Assert.IsTrue(rows.All(r => r.TreeShape == null), "a flat game pays nothing for the guide");
        }

        [TestMethod]
        public void Stamp_AssumeNestingKeepsShapesOnAFlatList()
        {
            // Collapse-all can leave only depth-1 rows visible; their shapes must survive or the
            // "+" toggles that re-expand them vanish with the guide.
            var rows = Rows("Story", "Extras");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true, assumeNesting: true);

            Assert.IsTrue(rows.All(r => r.TreeShape != null));
            Assert.IsTrue(rows.All(r => !r.TreeShape.HasChildren),
                "with the children filtered out, nothing in the run opens a subtree");
        }

        [TestMethod]
        public void Stamp_CollapsedParentReadsAsChildlessAgainstTheVisibleRows()
        {
            // The IsCollapsed flag on the row, not the shape, carries the parenthood cue in this
            // state - the shape only ever describes the rows actually present.
            var rows = Rows("Story", "Extras", "Extras::Bonus");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true, assumeNesting: true);
            var collapsed = Rows("Story", "Extras");
            CategoryTreeShapeBuilder.Stamp(collapsed, enabled: true, assumeNesting: true);

            Assert.IsTrue(rows[1].TreeShape.HasChildren);
            Assert.IsFalse(collapsed[1].TreeShape.HasChildren);
        }

        /// <summary>One row per category path.</summary>
        private static List<GameSummaryItem> Rows(params string[] paths)
        {
            var rows = new List<GameSummaryItem>();
            foreach (var path in paths)
            {
                rows.Add(new CategorySummaryItem
                {
                    CategoryPath = path,
                    CategoryLabel = path
                });
            }

            return rows;
        }
    }
}
