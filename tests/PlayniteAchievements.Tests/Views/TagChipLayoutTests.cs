using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    [TestCategory("Views")]
    public class TagChipLayoutTests
    {
        private const double TextHeight = 19;

        [TestMethod]
        public void OneIcon_FillsTheCellHeight()
        {
            var size = TagChipLayout.LargestIconSize(new[] { TagChipBox.Icon() }, TextHeight, 200, 60, 14);

            Assert.AreEqual(60.0, size);
        }

        [TestMethod]
        public void OneIcon_InANarrowColumn_FillsTheWidth()
        {
            var size = TagChipLayout.LargestIconSize(new[] { TagChipBox.Icon() }, TextHeight, 40, 60, 14);

            Assert.AreEqual(40.0, size);
        }

        [TestMethod]
        public void ManyIcons_WrapOrShrinkToFit()
        {
            var chips = Icons(4);

            var size = TagChipLayout.LargestIconSize(chips, TextHeight, 100, 100, 14).Value;

            // Two lines of two: 2s + 4 <= 100 and 2s + 2 <= 100.
            Assert.AreEqual(48.0, size);
            Assert.IsTrue(TagChipLayout.Fits(chips, size, TextHeight, 100, 100));
            Assert.IsFalse(TagChipLayout.Fits(chips, size + 1, TextHeight, 100, 100));
        }

        [TestMethod]
        public void TextChip_ReservesItsWidth()
        {
            var chips = new[] { TagChipBox.Text(60), TagChipBox.Icon() };

            var size = TagChipLayout.LargestIconSize(chips, TextHeight, 100, 30, 14);

            // On one line the icon gets 100 - 60 - 4 = 36, capped by the 30 height.
            Assert.AreEqual(30.0, size);
            Assert.IsFalse(TagChipLayout.Fits(new[] { TagChipBox.Text(60), TagChipBox.Icon() }, 37, TextHeight, 100, 37));
        }

        [TestMethod]
        public void FewIcons_StopAtTheCeiling()
        {
            var size = TagChipLayout.LargestIconSize(Icons(2), TextHeight, 200, 60, 14, 24);

            Assert.AreEqual(24.0, size);
        }

        [TestMethod]
        public void CrowdedIcons_ShrinkBelowTheCeiling()
        {
            // Eight icons in two lines of four: 4s + 12 <= 100.
            var size = TagChipLayout.LargestIconSize(Icons(8), TextHeight, 100, 60, 14, 24);

            Assert.AreEqual(22.0, size);
        }

        [TestMethod]
        public void TextOnly_SetsNoLimit()
        {
            Assert.IsNull(TagChipLayout.LargestIconSize(new[] { TagChipBox.Text(50) }, TextHeight, 100, 60, 14));
        }

        [TestMethod]
        public void TooManyIcons_StopAtTheFloor()
        {
            var size = TagChipLayout.LargestIconSize(Icons(50), TextHeight, 60, 30, 14);

            Assert.AreEqual(14.0, size);
        }

        [TestMethod]
        public void BreakLines_WrapsAtTheWidth_AndGivesAnOversizedChipItsOwnLine()
        {
            var lines = TagChipLayout.BreakLines(new double[] { 30, 30, 30, 200, 10 }, 70);

            Assert.AreEqual(4, lines.Count);
            Assert.AreEqual(0, lines[0].Start);
            Assert.AreEqual(2, lines[0].Count);
            Assert.AreEqual(64.0, lines[0].Width);
            Assert.AreEqual(1, lines[1].Count);
            Assert.AreEqual(3, lines[2].Start);
            Assert.AreEqual(1, lines[2].Count);
            Assert.AreEqual(4, lines[3].Start);
        }

        private static IReadOnlyList<TagChipBox> Icons(int count)
        {
            var chips = new List<TagChipBox>();
            for (var i = 0; i < count; i++)
            {
                chips.Add(TagChipBox.Icon());
            }

            return chips;
        }
    }
}
