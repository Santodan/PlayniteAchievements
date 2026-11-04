using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.Services.ThemeIntegration;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    [DoNotParallelize]
    public class PlatformScoreTotalsTests
    {
        [TestMethod]
        public void Add_SumsByPointsFamily()
        {
            var totals = new PlatformScoreTotals();
            totals.Add("Xbox", 1000, 1500);
            totals.Add("xenia", 250, 250);
            totals.Add("Epic", 1250, 2000);
            totals.Add("RetroAchievements", 400, 800);
            totals.Add("PSN", 90, 300);
            totals.Add("RPCS3", 30, 30);
            totals.Add("ShadPS4", 15, 15);
            totals.Add("Steam", 70, 100);
            totals.Add(null, 9999, 9999);
            totals.Add("Custom", 9999, 9999);
            totals.Add("Xbox", -5, -5);

            Assert.AreEqual(1250, totals.Gamerscore);
            Assert.AreEqual(1250, totals.EpicXp);
            Assert.AreEqual(400, totals.RetroPoints);
            Assert.AreEqual(135, totals.Get(PointsFamilies.PlayStation));
            Assert.AreEqual(70, totals.Get("Steam"));
            Assert.AreEqual(1250, totals.Get(ScoreCardType.Gamerscore));
            Assert.AreEqual(135, totals.Get(ScoreCardType.Points("RPCS3")));
            Assert.AreEqual(0, totals.Get(ScoreCardType.Collection));
            Assert.AreEqual(1750, totals.GamerscoreTotal);
            Assert.AreEqual(2000, totals.EpicXpTotal);
            Assert.AreEqual(800, totals.RetroPointsTotal);
            Assert.AreEqual(345, totals.GetPossible(PointsFamilies.PlayStation));
            Assert.AreEqual(345, totals.GetPossible(ScoreCardType.Points("ShadPS4")));
            Assert.AreEqual(0, totals.GetPossible(ScoreCardType.Prestige));
        }

        [TestMethod]
        public void Add_CustomProvidersAndOtherPlatformsGetTheirOwnFamily()
        {
            var totals = new PlatformScoreTotals();
            totals.Add("Custom:abc", 40, 40);
            totals.Add("Switch", 20, 60);

            Assert.AreEqual(40, totals.Get("Custom:abc"));
            Assert.AreEqual(20, totals.Get("Switch"));
            Assert.AreEqual(60, totals.GetPossible("Switch"));
            Assert.AreEqual(0, totals.Gamerscore);
            Assert.AreEqual(0, totals.Get(PointsFamilies.PlayStation));
        }

        [TestMethod]
        public void Families_ListsFamiliesWithPointsToEarnMostEarnedFirst()
        {
            var totals = new PlatformScoreTotals();
            totals.Add("Steam", 10, 100);
            totals.Add("Xenia", 50, 50);
            totals.Add("PSN", 30, 90);
            // Nothing earned yet, but points to earn: still a family.
            totals.Add("GOG", 0, 400);
            // No points at all: not a family.
            totals.Add("Ubisoft", 0, 0);

            CollectionAssert.AreEqual(
                new[] { "Xbox", "PSN", "Steam", "GOG" },
                new List<string>(totals.Families));
        }

        [TestMethod]
        public void Accumulator_LeavesSoftcoreUnlocksOutOfEarnedButNotPossiblePoints()
        {
            var stats = AchievementStatsAccumulator.FromAchievements(new[]
            {
                Achievement(10, unlocked: true, categoryType: "Base|Hardcore"),
                Achievement(25, unlocked: true, categoryType: "Base|Softcore"),
                Achievement(5, unlocked: true, categoryType: null),
                Achievement(50, unlocked: false, categoryType: null)
            });

            Assert.AreEqual(40, stats.Points);
            Assert.AreEqual(15, stats.PlatformScorePoints);
            // Softcore marks how an achievement was unlocked, so it still counts as possible.
            Assert.AreEqual(90, stats.PlatformScorePointsTotal);
        }

        [TestMethod]
        public void LibraryRuntimeState_SumsPlatformScoresAcrossProviders()
        {
            var allData = new List<GameAchievementData>
            {
                Game("Xbox", null, Achievement(100, true, null), Achievement(50, false, null)),
                // Exophase-sourced Xbox 360 game: counts toward Gamerscore through its platform key.
                Game("Exophase", "Xbox", Achievement(30, true, null)),
                Game("Xenia", null, Achievement(20, true, null)),
                Game("Epic", null, Achievement(45, true, null)),
                Game("RetroAchievements", null,
                    Achievement(10, true, "Base|Hardcore"),
                    Achievement(25, true, "Base|Softcore")),
                // Exophase-sourced RetroAchievements game: no unlock mode, so every unlock counts.
                Game("Exophase", "RetroAchievements", Achievement(5, true, null)),
                Game("Steam", null, Achievement(1000, true, null))
            };

            var state = LibraryRuntimeStateBuilder.Build(
                allData,
                api: null,
                token: default,
                includeHeavyAchievementLists: false);

            Assert.AreEqual(150, state.GamerscoreScore);
            Assert.AreEqual(45, state.EpicXpScore);
            Assert.AreEqual(15, state.RetroPointsScore);
            Assert.AreEqual(200, state.GamerscoreTotal);
            Assert.AreEqual(45, state.EpicXpTotal);
            Assert.AreEqual(40, state.RetroPointsTotal);
            Assert.AreEqual("Steam", state.PointsScores[0].FamilyKey);
            Assert.AreEqual(1000, state.PointsScores[0].Score);
            Assert.AreEqual(1000, state.PointsScores[0].Total);
            Assert.AreEqual("Xbox", state.PointsScores[1].FamilyKey);
            Assert.AreEqual(150, state.PointsScores[1].Score);
            Assert.AreEqual(200, state.PointsScores[1].Total);
            Assert.AreEqual(4, state.PointsScores.Count);
        }

        [TestMethod]
        public void OverviewSnapshot_DeltaTotalsSumPlatformScores()
        {
            var games = new List<GameSummaryItem>
            {
                new GameSummaryItem { ProviderKey = "Xbox", PointsFamily = "Xbox", PlatformScorePoints = 6000, PlatformScorePointsTotal = 9000, Points = 6000 },
                new GameSummaryItem { ProviderKey = "Xenia", PointsFamily = "Xbox", PlatformScorePoints = 1500, PlatformScorePointsTotal = 1500, Points = 1500 },
                new GameSummaryItem { ProviderKey = "RetroAchievements", PointsFamily = "RetroAchievements", PlatformScorePoints = 400, PlatformScorePointsTotal = 1200, Points = 900 },
                new GameSummaryItem { ProviderKey = "Steam", PointsFamily = null, PlatformScorePoints = 70, PlatformScorePointsTotal = 70, Points = 70 }
            };

            var snapshot = new OverviewDataSnapshot();
            snapshot.ApplyGameSummaryTotals(games, (a, b) => a + b);

            Assert.AreEqual(7500, snapshot.GamerscoreScore);
            Assert.AreEqual(10500, snapshot.GetPossibleScore(ScoreCardType.Gamerscore));
            Assert.AreEqual(400, snapshot.RetroPointsScore);
            Assert.AreEqual(1200, snapshot.GetPossibleScore(ScoreCardType.RetroPoints));
            Assert.AreEqual(0, snapshot.PointsTotals.Get("Steam"));
            Assert.AreEqual(0, snapshot.EpicXpScore);
            Assert.AreEqual(7500, snapshot.GetScore(ScoreCardType.Gamerscore));
        }

        private static GameAchievementData Game(
            string providerKey,
            string providerPlatformKey,
            params AchievementDetail[] achievements)
        {
            var gameId = Guid.NewGuid();
            return new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = providerKey + " game" },
                ProviderKey = providerKey,
                ProviderPlatformKey = providerPlatformKey,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>(achievements)
            };
        }

        private static AchievementDetail Achievement(int points, bool unlocked, string categoryType)
        {
            return new AchievementDetail
            {
                ApiName = Guid.NewGuid().ToString("N"),
                DisplayName = "Achievement",
                Points = points,
                Unlocked = unlocked,
                UnlockTimeUtc = unlocked ? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc) : (DateTime?)null,
                CategoryKind = PlayniteAchievements.Services.Achievements.LegacyCategoryTypes.SplitKind(categoryType),
                Tags = PlayniteAchievements.Services.Achievements.AchievementTagHelper.Normalize(categoryType)
            };
        }
    }
}
