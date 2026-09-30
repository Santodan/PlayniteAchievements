using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
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
    }
}
