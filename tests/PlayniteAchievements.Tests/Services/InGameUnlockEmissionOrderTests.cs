using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.InGameMonitoring;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class InGameUnlockEmissionOrderTests
    {
        private static readonly DateTime Tied =
            new DateTime(2026, 8, 11, 18, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void SameTimestamp_OrdersByDefaultOrderAscending()
        {
            var sorted = InGameUnlockEmissionOrder.Sort(new List<AchievementDetail>
            {
                Detail("second", Tied, defaultOrderIndex: 1),
                Detail("first", Tied, defaultOrderIndex: 0)
            });

            CollectionAssert.AreEqual(
                new[] { "first", "second" },
                sorted.Select(a => a.ApiName).ToArray());
        }

        [TestMethod]
        public void DistinctTimestamps_TimeAscendingDominatesDefaultOrder()
        {
            var sorted = InGameUnlockEmissionOrder.Sort(new List<AchievementDetail>
            {
                Detail("later-time-earlier-order", Tied.AddMinutes(5), defaultOrderIndex: 0),
                Detail("earlier-time-later-order", Tied, defaultOrderIndex: 1)
            });

            CollectionAssert.AreEqual(
                new[] { "earlier-time-later-order", "later-time-earlier-order" },
                sorted.Select(a => a.ApiName).ToArray());
        }

        [TestMethod]
        public void MissingTimestamps_SortLast()
        {
            var sorted = InGameUnlockEmissionOrder.Sort(new List<AchievementDetail>
            {
                Detail("no-time", null, defaultOrderIndex: 0),
                Detail("min-value", DateTime.MinValue, defaultOrderIndex: 1),
                Detail("dated", Tied, defaultOrderIndex: 2)
            });

            CollectionAssert.AreEqual(
                new[] { "dated", "no-time", "min-value" },
                sorted.Select(a => a.ApiName).ToArray());
        }

        [TestMethod]
        public void SameTimestamp_CapstoneEmitsAfterRegularUnlocks()
        {
            // A platinum-style capstone is often listed first in game order but pops with the
            // final trophy; its wave must follow the unlocks that earned it.
            var sorted = InGameUnlockEmissionOrder.Sort(new List<AchievementDetail>
            {
                Detail("capstone", Tied, defaultOrderIndex: 0, isCapstone: true),
                Detail("regular", Tied, defaultOrderIndex: 1)
            });

            CollectionAssert.AreEqual(
                new[] { "regular", "capstone" },
                sorted.Select(a => a.ApiName).ToArray());
        }

        [TestMethod]
        public void DistinctTimestamps_ChronologyDominatesCapstonePlacement()
        {
            // A capstone unlocked earlier (e.g. a mid-game win condition caught in a backlog
            // batch) keeps its chronological slot rather than being forced last.
            var sorted = InGameUnlockEmissionOrder.Sort(new List<AchievementDetail>
            {
                Detail("later-regular", Tied.AddMinutes(5), defaultOrderIndex: 1),
                Detail("earlier-capstone", Tied, defaultOrderIndex: 0, isCapstone: true)
            });

            CollectionAssert.AreEqual(
                new[] { "earlier-capstone", "later-regular" },
                sorted.Select(a => a.ApiName).ToArray());
        }

        [TestMethod]
        public void UnstampedBatch_KeepsInputOrder()
        {
            // A batch whose hydration failed sits entirely at int.MaxValue; the stable sort keeps
            // the incoming provider enumeration order.
            var sorted = InGameUnlockEmissionOrder.Sort(new List<AchievementDetail>
            {
                Detail("zeta", Tied),
                Detail("alpha", Tied)
            });

            CollectionAssert.AreEqual(
                new[] { "zeta", "alpha" },
                sorted.Select(a => a.ApiName).ToArray());
        }

        private static AchievementDetail Detail(
            string apiName,
            DateTime? unlockTimeUtc,
            int defaultOrderIndex = int.MaxValue,
            bool isCapstone = false)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = apiName,
                Unlocked = true,
                UnlockTimeUtc = unlockTimeUtc,
                DefaultOrderIndex = defaultOrderIndex,
                IsCapstone = isCapstone
            };
        }
    }
}
