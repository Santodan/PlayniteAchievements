using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System.Linq;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileMatchFinderTests
    {
        private static AchievementDetail[] Achievements(int count)
        {
            return Enumerable.Range(1, count)
                .Select(i => new AchievementDetail { ApiName = "ach_" + i, DisplayName = "Achievement " + i })
                .ToArray();
        }

        [TestMethod]
        public void Best_PrefersAColumnOfApiNames()
        {
            var table = LinkedFileTable.Build(
                new[] { "id", "done" },
                new[] { new[] { "ach_2", "1" }, new[] { "ach_1", "0" } });

            var best = LinkedFileMatchFinder.Best(LinkedFileMatchFinder.Find(table, Achievements(2)));

            Assert.AreEqual("id", best.Column);
            Assert.AreEqual(LinkedFileMatchBy.ApiName, best.By);
        }

        [TestMethod]
        public void RowOrder_FitsOnlyWithOneRowPerAchievement()
        {
            var table = LinkedFileTable.Build(new[] { "done" }, new[] { new[] { "yes" }, new[] { "no" }, new[] { "yes" } });

            var exact = LinkedFileMatchFinder.Best(LinkedFileMatchFinder.Find(table, Achievements(3)));
            Assert.AreEqual(LinkedFileSchema.PositionColumn, exact.Column);
            Assert.AreEqual(LinkedFileMatchBy.PositionFromOne, exact.By);

            var found = LinkedFileMatchFinder.Find(table, Achievements(5));
            Assert.IsNull(LinkedFileMatchFinder.Best(found), "Three rows for five achievements may be any three.");
            Assert.IsTrue(found.Any(candidate => candidate.IsPositionColumn && !candidate.Fits), "Row order is still offered.");
        }

        [TestMethod]
        public void NumberedColumn_ReadsFromZeroWhenItHoldsAZero()
        {
            // Trophy ids of the earned trophies only: 0 and 2 of 0..3.
            var table = LinkedFileTable.Build(new[] { "trophy" }, new[] { new[] { "2" }, new[] { "0" } });

            var best = LinkedFileMatchFinder.Best(LinkedFileMatchFinder.Find(table, Achievements(4)));

            Assert.AreEqual("trophy", best.Column);
            Assert.AreEqual(LinkedFileMatchBy.PositionFromZero, best.By);
            Assert.AreEqual(2, best.Matched);
        }

        [TestMethod]
        public void Names_WinOverPositionsUnlessPositionsPairMoreThanTwiceAsMany()
        {
            var achievements = Achievements(4);
            var oneName = LinkedFileTable.Build(
                new[] { "title" },
                new[] { new[] { "Achievement 1" }, new[] { "x" }, new[] { "y" }, new[] { "z" } });
            Assert.IsTrue(LinkedFileMatchFinder.Best(LinkedFileMatchFinder.Find(oneName, achievements)).IsPositionColumn);

            var twoNames = LinkedFileTable.Build(
                new[] { "title" },
                new[] { new[] { "Achievement 1" }, new[] { "Achievement 2" }, new[] { "y" }, new[] { "z" } });
            var best = LinkedFileMatchFinder.Best(LinkedFileMatchFinder.Find(twoNames, achievements));
            Assert.AreEqual("title", best.Column);
            Assert.AreEqual(LinkedFileMatchBy.Name, best.By);
        }

        [TestMethod]
        public void Pairing_ListsTheWaysThatPairARowWithAnAchievement()
        {
            var achievements = Achievements(3);
            var table = LinkedFileTable.Build(new[] { "n" }, new[] { new[] { "1" }, new[] { "2" } });
            var found = LinkedFileMatchFinder.Find(table, achievements);

            var ways = LinkedFileMatchFinder.Pairing(found, 0, achievements[1]);

            Assert.AreEqual(LinkedFileMatchBy.PositionFromZero, ways.Single().By, "Row 1 holds 1, which is the second achievement counting from 0.");
        }
    }
}
