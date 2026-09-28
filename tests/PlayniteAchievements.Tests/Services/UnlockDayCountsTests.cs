using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class UnlockDayCountsTests
    {
        private static readonly Guid Game = Guid.NewGuid();
        private static readonly DateTime Day = new DateTime(2026, 5, 5);

        [TestMethod]
        public void AddDay_CreatesPerGameMapAndIncrementsBoth()
        {
            var global = new Dictionary<DateTime, int>();
            var byGame = new Dictionary<Guid, Dictionary<DateTime, int>>();

            UnlockDayCounts.AddDay(global, byGame, Game, Day);
            UnlockDayCounts.AddDay(global, byGame, Game, Day.AddHours(5), 2);

            Assert.AreEqual(3, global[Day]);
            Assert.AreEqual(3, byGame[Game][Day]);
            Assert.AreEqual(1, byGame.Count);
        }

        [TestMethod]
        public void AddDay_WithoutGame_TouchesOnlyGlobal()
        {
            var global = new Dictionary<DateTime, int>();
            var byGame = new Dictionary<Guid, Dictionary<DateTime, int>>();

            UnlockDayCounts.AddDay(global, byGame, null, Day);
            UnlockDayCounts.AddDay(global, byGame, Guid.Empty, Day);

            Assert.AreEqual(2, global[Day]);
            Assert.AreEqual(0, byGame.Count);
        }

        [TestMethod]
        public void RemoveDay_DeletesKeysThatReachZero()
        {
            var global = new Dictionary<DateTime, int>();
            var byGame = new Dictionary<Guid, Dictionary<DateTime, int>>();
            UnlockDayCounts.AddDay(global, byGame, Game, Day, 2);

            Assert.IsTrue(UnlockDayCounts.RemoveDay(global, byGame, Game, Day));
            Assert.AreEqual(1, global[Day]);
            Assert.AreEqual(1, byGame[Game][Day]);

            Assert.IsTrue(UnlockDayCounts.RemoveDay(global, byGame, Game, Day));
            Assert.IsFalse(global.ContainsKey(Day));
            Assert.IsFalse(byGame.ContainsKey(Game));
        }

        [TestMethod]
        public void RemoveDay_MissingKey_ReturnsFalse()
        {
            var global = new Dictionary<DateTime, int>();

            Assert.IsFalse(UnlockDayCounts.RemoveDay(global, null, Game, Day));
        }

        [TestMethod]
        public void Earliest_IgnoresNonPositiveCounts()
        {
            var counts = new Dictionary<DateTime, int>
            {
                { new DateTime(2020, 1, 1), 0 },
                { new DateTime(2021, 6, 6, 14, 0, 0), 3 },
                { new DateTime(2019, 1, 1), -1 },
                { new DateTime(2022, 1, 1), 1 }
            };

            Assert.AreEqual(new DateTime(2021, 6, 6), UnlockDayCounts.Earliest(counts));
            Assert.IsNull(UnlockDayCounts.Earliest(new Dictionary<DateTime, int>()));
            Assert.IsNull(UnlockDayCounts.Earliest(null));
        }
    }
}
