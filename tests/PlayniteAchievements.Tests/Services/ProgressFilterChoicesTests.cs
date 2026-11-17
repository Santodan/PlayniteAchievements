using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The Progress filter's choices: the finishes the Achievement styles allow, in their order,
    /// then In Progress and No Progress, each game under the badge its rows show.
    /// </summary>
    [TestClass]
    public class ProgressFilterChoicesTests
    {
        private static readonly ProgressDetailStyle[] PointsFirst =
            { ProgressDetailStyle.Rarity, ProgressDetailStyle.Points, ProgressDetailStyle.Trophy };

        private static GameSummaryItem PlatinumAndMythic() => new GameSummaryItem
        {
            GameName = "Both",
            PlayniteGameId = Guid.NewGuid(),
            TotalAchievements = 10,
            UnlockedAchievements = 10,
            IsCompleted = true,
            CapstoneTotal = 1,
            CapstoneUnlocked = 1,
            CapstonesMatchPlatinums = true,
            TrophyPlatinumTotal = 1,
            TrophyGoldTotal = 3,
            PointsFamily = "PlayStation",
            PlatformScorePointsTotal = 300,
            PlatformScorePoints = 300
        };

        private static GameSummaryItem Plain(string name, int unlocked, bool completed) => new GameSummaryItem
        {
            GameName = name,
            PlayniteGameId = Guid.NewGuid(),
            TotalAchievements = 10,
            UnlockedAchievements = unlocked,
            IsCompleted = completed
        };

        [TestMethod]
        public void Keys_FollowTheAchievementStyles()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    ProgressFilterChoices.Mythic,
                    ProgressFilterChoices.Platinum,
                    ProgressFilterChoices.Completed,
                    ProgressFilterChoices.InProgress,
                    ProgressFilterChoices.NoProgress
                },
                ProgressFilterChoices.Keys(PointsFirst, includeNoProgress: true).ToList());

            // Rarity alone offers no platinum or mythic, and a chart bar can leave No Progress out.
            CollectionAssert.AreEqual(
                new[] { ProgressFilterChoices.Completed, ProgressFilterChoices.InProgress },
                ProgressFilterChoices.Keys(new[] { ProgressDetailStyle.Rarity }, includeNoProgress: false).ToList());
        }

        [TestMethod]
        public void Keys_GivenGames_OfferOnlyTheFinishesTheyShow()
        {
            // With Rarity ahead of Points a game with rarity data shows the completed badge, so
            // Mythic is not offered even though Points is on.
            var rarityFirst = new[] { ProgressDetailStyle.Rarity, ProgressDetailStyle.Points };
            var game = PlatinumAndMythic();
            game.TotalCommonPossible = 10;
            game.CommonCount = 10;
            Assert.AreEqual(ProgressFilterChoices.Completed, ProgressFilterChoices.KeyFor(game, rarityFirst));

            CollectionAssert.AreEqual(
                new[] { ProgressFilterChoices.Completed, ProgressFilterChoices.InProgress, ProgressFilterChoices.NoProgress },
                ProgressFilterChoices.Keys(rarityFirst, includeNoProgress: true, new[] { game, Plain("Started", 3, false) }).ToList());
        }

        [TestMethod]
        public void Matcher_KeepsGamesUnderTheChosenBadge()
        {
            var games = new[] { PlatinumAndMythic(), Plain("Done", 10, true), Plain("Started", 3, false), Plain("Untouched", 0, false) };
            var offered = ProgressFilterChoices.Keys(PointsFirst, includeNoProgress: true);

            var mythic = ProgressFilterChoices.Matcher(new[] { ProgressFilterChoices.Mythic }, offered, PointsFirst);
            CollectionAssert.AreEqual(new[] { "Both" }, games.Where(mythic).Select(game => game.GameName).ToList());

            var open = ProgressFilterChoices.Matcher(
                new[] { ProgressFilterChoices.InProgress, ProgressFilterChoices.NoProgress },
                offered,
                PointsFirst);
            CollectionAssert.AreEqual(new[] { "Started", "Untouched" }, games.Where(open).Select(game => game.GameName).ToList());
        }

        [TestMethod]
        public void Matcher_IgnoresChoicesNotOffered()
        {
            var offered = ProgressFilterChoices.Keys(new[] { ProgressDetailStyle.Rarity }, includeNoProgress: true);

            Assert.IsNull(ProgressFilterChoices.Matcher(new[] { ProgressFilterChoices.Mythic }, offered, new[] { ProgressDetailStyle.Rarity }));
            Assert.IsNull(ProgressFilterChoices.Matcher(null, offered, null));
        }

        [TestMethod]
        public void LegacyPositions_CompleteCoversEveryFinish()
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    ProgressFilterChoices.Mythic,
                    ProgressFilterChoices.Platinum,
                    ProgressFilterChoices.Completed,
                    ProgressFilterChoices.NoProgress
                },
                ProgressFilterChoices.FromLegacyPositions(new[] { 0, 2 }).ToList());
        }

        [TestMethod]
        public void ControlBar_RestoresLegacyPositionsAndSavesKeys()
        {
            var bar = new GameSummaryGridControlBarAdapter();
            bar.RestoreState(new ControlBarFilterState { Progress = new System.Collections.Generic.List<int> { 1 } });

            Assert.IsTrue(bar.IsProgressFilterSelected(ProgressFilterChoices.InProgress));
            Assert.IsFalse(bar.IsProgressFilterSelected(ProgressFilterChoices.Completed));

            var saved = bar.CaptureState();
            Assert.IsNull(saved.Progress);
            CollectionAssert.AreEqual(new[] { ProgressFilterChoices.InProgress }, saved.ProgressKeys);
        }
    }
}
