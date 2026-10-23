using System.Collections.Generic;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    [TestCategory("Views")]
    public class TagChipLayoutTests
    {
        // "+N" as the panel draws it: about a digit and a plus wide, one text line tall.
        private static Size Overflow(int hidden) => new Size(hidden < 10 ? 20 : 28, 19);

        [TestMethod]
        public void ChipsThatFit_AllShow()
        {
            var count = TagChipLayout.VisibleCount(Icons(2, 40), Overflow, 100, 40);

            Assert.AreEqual(2, count);
        }

        [TestMethod]
        public void ChipsPastOneLine_GiveWayToPlusN()
        {
            // Two icons and "+2" need 40 + 4 + 40 + 4 + 20 = 108, so one icon and "+3" show.
            var count = TagChipLayout.VisibleCount(Icons(4, 40), Overflow, 100, 40);

            Assert.AreEqual(1, count);
        }

        [TestMethod]
        public void ChipsWrap_WhenTheRowHasRoomForAnotherLine()
        {
            var count = TagChipLayout.VisibleCount(Icons(4, 40), Overflow, 100, 82);

            Assert.AreEqual(4, count);
        }

        [TestMethod]
        public void SecondLine_TakesPlusN_WhenItCannotTakeEveryChip()
        {
            // Two lines of two hold four slots; with five chips the fourth slot takes "+2".
            var count = TagChipLayout.VisibleCount(Icons(5, 40), Overflow, 100, 82);

            Assert.AreEqual(3, count);
        }

        [TestMethod]
        public void CellTooShortForAnyChip_ShowsOnlyPlusN()
        {
            var count = TagChipLayout.VisibleCount(Icons(3, 40), Overflow, 100, 20);

            Assert.AreEqual(0, count);
        }

        [TestMethod]
        public void Extent_TrimsWideTextToTheCellWidth()
        {
            var extent = TagChipLayout.Extent(new[] { new Size(300, 19), new Size(40, 40) }, 100);

            Assert.AreEqual(new Size(100, 19 + 2 + 40), extent);
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

        private static IReadOnlyList<Size> Icons(int count, double size)
        {
            var chips = new List<Size>();
            for (var i = 0; i < count; i++)
            {
                chips.Add(new Size(size, size));
            }

            return chips;
        }
    }
}
