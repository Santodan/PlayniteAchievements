using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class TimelineTierSeriesTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        private static UnlockFact Unlock(RarityTier rarity, string trophy = null, int? dayOffset = 0) =>
            new UnlockFact
            {
                GameId = Guid.NewGuid(),
                Day = dayOffset.HasValue ? UnlockDayCounts.DayOf(Noon.AddDays(dayOffset.Value)) : (DateTime?)null,
                Tally = new AchievementTally(null, rarity, AchievementTally.NormalizeTrophy(trophy), false, 1, 1)
            };

        [TestMethod]
        public void ByRarity_CountsUnlocksPerTierRarestFirst()
        {
            var snapshot = new OverviewDataSnapshot
            {
                Unlocks = new[]
                {
                    Unlock(RarityTier.Common),
                    Unlock(RarityTier.Common, dayOffset: 1),
                    Unlock(RarityTier.UltraRare),
                    Unlock(RarityTier.Rare, dayOffset: null)
                }
            };

            var series = TimelineTierSeries.ByRarity(snapshot);

            CollectionAssert.AreEqual(new[] { "UltraRare", "Common" }, series.Select(item => item.Key).ToArray(),
                "tiers with no dated unlock are left out");
            Assert.IsTrue(series[0].Order < series[1].Order);
            Assert.AreEqual(2, series[1].CountsByDate.Values.Sum());
            Assert.AreEqual(2, series[1].CountsByDate.Count);
        }

        [TestMethod]
        public void ByTrophy_StacksUnlocksWithoutATrophyTypeLast()
        {
            var snapshot = new OverviewDataSnapshot
            {
                Unlocks = new[]
                {
                    Unlock(RarityTier.Common, "Gold"),
                    Unlock(RarityTier.Common, "platinum"),
                    Unlock(RarityTier.Common),
                    Unlock(RarityTier.Common, "mystery")
                }
            };

            var series = TimelineTierSeries.ByTrophy(snapshot);

            CollectionAssert.AreEqual(new[] { "platinum", "gold", "Unknown" }, series.Select(item => item.Key).ToArray());
            Assert.AreEqual(2, series[2].CountsByDate.Values.Sum(), "the bars keep their totals");
        }
    }
}