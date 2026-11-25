using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class AchievementTagStatsBuilderTests
    {
        [TestInitialize]
        public void Initialize()
        {
            AchievementTagRegistry.SetGlobal(new[]
            {
                new AchievementTagDefinition { Key = "Missable", Name = "Can miss", Color = "#FF0000" },
                new AchievementTagDefinition { Key = "Collectable" },
                new AchievementTagDefinition { Key = "Multiplayer", Hidden = true }
            });
        }

        [TestCleanup]
        public void Cleanup()
        {
            AchievementTagRegistry.SetGlobal(AchievementTagHelper.CreateDefaultDefinitions());
        }

        [TestMethod]
        public void Build_CountsEachShownTag_InTagListOrder()
        {
            var stats = AchievementTagStatsBuilder.Build(
                new[]
                {
                    new AchievementTally("Collectable", 1, 2),
                    new AchievementTally("Missable|Collectable", 1, 3),
                    new AchievementTally(string.Empty, 4, 4)
                },
                null);

            CollectionAssert.AreEqual(new[] { "Missable", "Collectable" }, stats.Select(item => item.Key).ToList());
            Assert.AreEqual("Can miss", stats[0].Name);
            Assert.AreEqual("#FF0000", stats[0].Color);
            Assert.IsTrue(stats[0].HasColor);
            Assert.AreEqual(1, stats[0].Unlocked);
            Assert.AreEqual(3, stats[0].Total);
            Assert.AreEqual(2, stats[0].Locked);
            Assert.AreEqual(2, stats[1].Unlocked);
            Assert.AreEqual(5, stats[1].Total);
            Assert.IsFalse(stats[1].HasColor);
        }

        [TestMethod]
        public void Build_LeavesOutHiddenTagsAndTagsWithoutAchievements()
        {
            var stats = AchievementTagStatsBuilder.Build(
                new[] { new AchievementTally("Multiplayer|Collectable", 0, 2) },
                null);

            CollectionAssert.AreEqual(new[] { "Collectable" }, stats.Select(item => item.Key).ToList());
        }

        [TestMethod]
        public void Build_NoTallies_IsEmpty()
        {
            Assert.AreEqual(0, AchievementTagStatsBuilder.Build(null, null).Count);
        }

        [TestMethod]
        public void BuildFromCachedSummary_SumsTagCountsAcrossGames()
        {
            var state = LibraryRuntimeStateBuilder.BuildFromCachedSummary(
                new CachedSummaryData
                {
                    Games = new List<CachedGameSummaryData>
                    {
                        Game(new AchievementTally("Missable", 1, 2)),
                        Game(new AchievementTally("Missable", 0, 3), new AchievementTally("Collectable", 2, 2))
                    }
                },
                rarestRecent: null,
                api: null,
                token: default);

            var missable = state.TotalTagStats.Single(item => item.Key == "Missable");
            Assert.AreEqual(1, missable.Unlocked);
            Assert.AreEqual(5, missable.Total);
            Assert.AreEqual(2, state.TotalTagStats.Single(item => item.Key == "Collectable").Total);
        }

        private static CachedGameSummaryData Game(params AchievementTally[] tallies) =>
            new CachedGameSummaryData
            {
                PlayniteGameId = Guid.NewGuid(),
                ProviderKey = "Steam",
                GameName = "Tagged game",
                HasAchievements = true,
                TotalAchievements = tallies.Sum(tally => tally.Total),
                UnlockedAchievements = tallies.Sum(tally => tally.Unlocked),
                Tallies = tallies
            };
    }
}
