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
        public void Stamp_PlacesASelfRowAsTheFirstChildOfItsCategory()
        {
            var rows = Rows("Story", "Story!self", "Story::Act 1", "Story::Act 2");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            var story = rows[0].TreeShape;
            var self = rows[1].TreeShape;
            var act1 = rows[2].TreeShape;
            var act2 = rows[3].TreeShape;

            Assert.AreEqual(1, story.Depth);
            Assert.IsTrue(story.HasChildren, "the junction opens for the self row");
            Assert.IsFalse(story.IsSelfRow);
            Assert.IsTrue(story.HasSelfRowBelow, "the name cell drops the connector toward the self row");

            Assert.AreEqual(2, self.Depth, "the self row indents one level under its category");
            Assert.IsFalse(self.IsLastSibling, "the child categories follow as its siblings");
            Assert.IsFalse(self.HasChildren);
            Assert.IsTrue(self.IsSelfRow, "the guide draws it as a pass-through, not a node");
            Assert.IsFalse(self.HasSelfRowBelow);

            Assert.AreEqual(2, act1.Depth);
            Assert.IsFalse(act1.IsLastSibling);
            Assert.IsFalse(act1.IsSelfRow);
            Assert.IsFalse(act1.HasSelfRowBelow);
            Assert.IsTrue(act2.IsLastSibling, "the last child category still closes the run");
        }

        [TestMethod]
        public void Stamp_SelfRowOfANestedCategoryKeepsAncestorLanesOpen()
        {
            // Act 1 has a later sibling (Act 2), so every row inside Act 1's subtree must carry
            // Act 1's lane through itself - the self row included.
            var rows = Rows(
                "Story",
                "Story::Act 1",
                "Story::Act 1!self",
                "Story::Act 1::Finale",
                "Story::Act 2",
                "Extras");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            var self = rows[2].TreeShape;
            Assert.AreEqual(3, self.Depth);
            Assert.IsFalse(self.IsLastSibling, "Finale follows as its sibling");
            Assert.AreEqual(1, self.AncestorContinues.Count);
            Assert.IsTrue(self.AncestorContinues[0], "Act 1's lane continues toward Act 2 through the self row");
        }

        [TestMethod]
        public void Stamp_SelfRowAloneStillCountsAsNesting()
        {
            // One mixed root: the only depth comes from the self row itself, and the guide must
            // still draw - without it the row would read as a stray duplicate of its category.
            var rows = Rows("Story", "Story!self", "Story::Act 1");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            Assert.IsNotNull(rows[0].TreeShape);
            Assert.AreEqual(2, rows[1].TreeShape.Depth);
        }

        [TestMethod]
        public void Stamp_ParentKeepsNoDropFlagWhenAFilterHidesItsSelfRow()
        {
            // The flag is resolved against the emitted rows: with the self row filtered out of the
            // list, nothing may dangle toward a row that is not there.
            var rows = Rows("Story", "Story::Act 1");

            CategoryTreeShapeBuilder.Stamp(rows, enabled: true);

            Assert.IsFalse(rows[0].TreeShape.HasSelfRowBelow);
        }

        [TestMethod]
        public void Stamp_DisabledClearsSelfRowShapesToo()
        {
            var rows = Rows("Story", "Story!self", "Story::Act 1");
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
