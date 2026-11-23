using System;
using System.Collections.Generic;
using System.Linq;
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

        [TestMethod]
        public void GroupByKey_GroupsGamesAndPutsUnattributedCountsInTheRemainder()
        {
            var steamA = Guid.NewGuid();
            var steamB = Guid.NewGuid();
            var psn = Guid.NewGuid();
            var unkeyed = Guid.NewGuid();
            var global = new Dictionary<DateTime, int>();
            var byGame = new Dictionary<Guid, Dictionary<DateTime, int>>();
            UnlockDayCounts.AddDay(global, byGame, steamA, Day, 2);
            UnlockDayCounts.AddDay(global, byGame, steamB, Day, 1);
            UnlockDayCounts.AddDay(global, byGame, psn, Day.AddDays(1), 4);
            UnlockDayCounts.AddDay(global, byGame, unkeyed, Day, 1);
            UnlockDayCounts.AddDay(global, byGame, null, Day.AddDays(1), 3);
            var keys = new Dictionary<Guid, string> { { steamA, "Steam" }, { steamB, "steam" }, { psn, "PSN" } };

            var groups = UnlockDayCounts.GroupByKey(
                global,
                byGame,
                id => keys.TryGetValue(id, out var key) ? key : null,
                "Unknown");

            Assert.AreEqual(3, groups.Count);
            Assert.AreEqual(3, groups["Steam"][Day]);
            Assert.AreEqual(4, groups["PSN"][Day.AddDays(1)]);
            Assert.AreEqual(1, groups["Unknown"][Day]);
            Assert.AreEqual(3, groups["Unknown"][Day.AddDays(1)]);
            foreach (var day in global)
            {
                Assert.AreEqual(day.Value, groups.Values.Sum(group => group.TryGetValue(day.Key, out var count) ? count : 0));
            }
        }

        [TestMethod]
        public void GroupByKey_WithoutGlobal_HasNoRemainder()
        {
            var byGame = new Dictionary<Guid, Dictionary<DateTime, int>>
            {
                { Game, new Dictionary<DateTime, int> { { Day, 2 } } }
            };

            var groups = UnlockDayCounts.GroupByKey(null, byGame, _ => "Steam", "Unknown");

            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(2, groups["Steam"][Day]);
        }
    }
}
