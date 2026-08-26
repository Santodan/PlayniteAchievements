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
            int defaultOrderIndex = int.MaxValue)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = apiName,
                Unlocked = true,
                UnlockTimeUtc = unlockTimeUtc,
                DefaultOrderIndex = defaultOrderIndex
            };
        }
    }
}
