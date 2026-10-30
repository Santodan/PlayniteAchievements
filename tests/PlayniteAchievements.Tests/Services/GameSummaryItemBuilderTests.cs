using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Summaries;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class GameSummaryItemBuilderTests
    {
        private static GameAchievementData ExcludedGame()
        {
            return new GameAchievementData
            {
                GameName = "Excluded Game",
                ProviderKey = "Steam",
                HasAchievements = true,
                ExcludedFromSummaries = true,
                Achievements = new List<AchievementDetail>
                {
                    new AchievementDetail { ApiName = "a", DisplayName = "a", Unlocked = true },
                    new AchievementDetail { ApiName = "b", DisplayName = "b" }
                }
            };
        }

        [TestMethod]
        public void Build_ExcludedGame_ReturnsNullForLibrarySummaries()
        {
            var builder = new GameSummaryItemBuilder(null, null);

            Assert.IsNull(builder.Build(ExcludedGame(), null));
        }

        [TestMethod]
        public void Build_ExcludedGame_ReturnsRowForSingleGameSurface()
        {
            var builder = new GameSummaryItemBuilder(null, null);

            var item = builder.Build(ExcludedGame(), null, forSingleGame: true);

            Assert.IsNotNull(item);
            Assert.AreEqual("Excluded Game", item.GameName);
        }

        [TestMethod]
        public void Build_PointsProviderGame_CountsTowardItsPointsFamily()
        {
            var builder = new GameSummaryItemBuilder(null, null);
            var game = new GameAchievementData
            {
                GameName = "Xbox Game",
                ProviderKey = "Xbox",
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    new AchievementDetail { ApiName = "a", DisplayName = "a", Unlocked = true, Points = 50 },
                    new AchievementDetail { ApiName = "b", DisplayName = "b", Points = 25 }
                }
            };

            var item = builder.Build(game, null);
            var snapshot = new OverviewDataSnapshot();
            snapshot.ApplyGameSummaryTotals(new[] { item }, (a, b) => a + b);

            Assert.AreEqual(PointsFamilies.Xbox, item.PointsFamily);
            Assert.AreEqual(50, snapshot.GetScore(ScoreCardType.Gamerscore));
            Assert.AreEqual(75, snapshot.GetPossibleScore(ScoreCardType.Gamerscore));
        }
    }
}
