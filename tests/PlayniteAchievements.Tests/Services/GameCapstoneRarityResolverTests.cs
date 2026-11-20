using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class GameCapstoneRarityResolverTests
    {
        private static readonly Guid Game = Guid.NewGuid();

        [TestMethod]
        public void BaseCapstone_WinsOverRarerDlcCapstone()
        {
            var index = GameCapstoneRarityResolver.Index(new[]
            {
                Item(12.5, capstone: true, categoryType: "Base"),
                Item(1.2, capstone: true, categoryType: "DLC"),
                Item(0.4)
            });

            Assert.AreEqual(12.5, GameCapstoneRarityResolver.Resolve(index[Game], hasCapstones: true));
        }

        [TestMethod]
        public void WithoutBaseCapstone_RarestCapstoneWins()
        {
            var index = GameCapstoneRarityResolver.Index(new[]
            {
                Item(8.0, capstone: true),
                Item(3.0, capstone: true, categoryType: "DLC"),
                Item(0.4)
            });

            Assert.AreEqual(3.0, GameCapstoneRarityResolver.Resolve(index[Game], hasCapstones: true));
        }

        [TestMethod]
        public void GameWithoutCapstones_FallsBackToRarestAchievement()
        {
            var index = GameCapstoneRarityResolver.Index(new[]
            {
                Item(40.0),
                Item(2.5),
                Item(9.0)
            });

            Assert.AreEqual(2.5, GameCapstoneRarityResolver.Resolve(index[Game], hasCapstones: false));
        }

        [TestMethod]
        public void GameWithCapstonesButNoCapstonePercent_ShowsNoBar()
        {
            var index = GameCapstoneRarityResolver.Index(new[]
            {
                Item(null, capstone: true),
                Item(2.5)
            });

            Assert.IsNull(GameCapstoneRarityResolver.Resolve(index[Game], hasCapstones: true));
        }

        [TestMethod]
        public void LockedAndAbsentPercents_AreIgnored()
        {
            var index = GameCapstoneRarityResolver.Index(new[]
            {
                Item(0.1, unlocked: false),
                Item(0),
                Item(null),
                Item(7.0)
            });

            Assert.AreEqual(7.0, GameCapstoneRarityResolver.Resolve(index[Game], hasCapstones: false));
        }

        [TestMethod]
        public void NullCandidates_ResolveToNull()
        {
            Assert.IsNull(GameCapstoneRarityResolver.Resolve(null, hasCapstones: false));
            Assert.AreEqual(0, GameCapstoneRarityResolver.Index(null).Count);
        }

        // One unlock as the summary read groups it: a locked achievement is no unlock, and only a
        // positive percent counts, among the base-game capstones too.
        private static UnlockFact Item(
            double? percent,
            bool capstone = false,
            string categoryType = null,
            bool unlocked = true) =>
            !unlocked ? null : new UnlockFact
            {
                GameId = Game,
                Tally = new AchievementTally(null, RarityTier.Common, null, capstone, 1, 1),
                MinPercent = percent > 0 ? percent : null,
                MinBasePercent = capstone && percent > 0 &&
                    PlayniteAchievements.Services.Achievements.CategoryKindHelper.IsBase(
                        PlayniteAchievements.Services.Achievements.LegacyCategoryTypes.SplitKind(categoryType))
                    ? percent
                    : null
            };
    }
}
