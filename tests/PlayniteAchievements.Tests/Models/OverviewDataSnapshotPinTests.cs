using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class OverviewDataSnapshotPinTests
    {
        [TestMethod]
        public void HasSeenAchievementPins_TrueWhenEveryPinWasConsideredAtBuild()
        {
            var gameId = Guid.NewGuid();
            var showcase = CreateShowcase(gameId, "ach_one");
            var snapshot = new OverviewDataSnapshot
            {
                AchievementPinKeysAtBuild = new HashSet<string>
                {
                    OverviewDataSnapshot.AchievementPinKey(gameId, "ACH_ONE")
                }
            };

            Assert.IsTrue(snapshot.HasSeenAchievementPins(showcase));
        }

        [TestMethod]
        public void HasSeenAchievementPins_FalseForAPinAddedAfterTheBuild()
        {
            var gameId = Guid.NewGuid();
            var showcase = CreateShowcase(gameId, "ach_one");
            var snapshot = new OverviewDataSnapshot();

            Assert.IsFalse(snapshot.HasSeenAchievementPins(showcase));
        }

        [TestMethod]
        public void HasSeenAchievementPins_IgnoresMalformedPinsAndMissingCollections()
        {
            var snapshot = new OverviewDataSnapshot();
            var showcase = new ShowcaseSettings
            {
                AchievementPinCollections = new List<PinnedAchievementCollection>
                {
                    null,
                    new PinnedAchievementCollection
                    {
                        Pins = new List<PinnedAchievementReference>
                        {
                            null,
                            new PinnedAchievementReference { GameId = Guid.Empty, ApiName = "x" },
                            new PinnedAchievementReference { GameId = Guid.NewGuid(), ApiName = " " }
                        }
                    }
                }
            };

            Assert.IsTrue(snapshot.HasSeenAchievementPins(showcase));
            Assert.IsTrue(snapshot.HasSeenAchievementPins(null));
        }

        private static ShowcaseSettings CreateShowcase(Guid gameId, string apiName)
        {
            return new ShowcaseSettings
            {
                AchievementPinCollections = new List<PinnedAchievementCollection>
                {
                    new PinnedAchievementCollection
                    {
                        Pins = new List<PinnedAchievementReference>
                        {
                            new PinnedAchievementReference { GameId = gameId, ApiName = apiName }
                        }
                    }
                }
            };
        }
    }
}
