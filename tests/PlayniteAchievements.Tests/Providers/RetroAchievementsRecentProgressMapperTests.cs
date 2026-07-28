using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class RetroAchievementsRecentProgressMapperTests
    {
        [TestMethod]
        public void Map_SharesFeedAcrossGames_MapsSubsetIds_AndFiltersSessionAndDuplicates()
        {
            var sessionStart = new DateTime(
                2026,
                7,
                28,
                12,
                0,
                0,
                DateTimeKind.Utc);
            var baseGameId = Guid.NewGuid();
            var subsetGameId = Guid.NewGuid();
            var contexts = new[]
            {
                Context(baseGameId, sessionStart, "100"),
                Context(subsetGameId, sessionStart, "200")
            };
            var recent = new[]
            {
                Recent(100, "2026-07-28 12:00:05", hardcore: 1, gameId: 999),
                Recent(100, "2026-07-28 12:00:05", hardcore: 1, gameId: 999),
                Recent(200, "2026-07-28 11:59:59", hardcore: 0, gameId: 123),
                Recent(200, "2026-07-28 12:00:06", hardcore: 0, gameId: 123),
                Recent(300, "not-a-date", hardcore: 1, gameId: 123)
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var mapped = RetroAchievementsRecentProgressMapper.Map(
                recent,
                contexts,
                (key, _) => seen.Add(key));

            Assert.AreEqual(2, mapped.Count);
            var baseResult = mapped.Single(result => result.GameId == baseGameId);
            var subsetResult = mapped.Single(result => result.GameId == subsetGameId);
            Assert.IsTrue(baseResult.IsDelta);
            Assert.AreEqual(1, baseResult.Achievements.Count);
            Assert.AreEqual("100", baseResult.Achievements[0].ApiName);
            Assert.AreEqual("Hardcore", baseResult.Achievements[0].UnlockMode);
            Assert.AreEqual(1, subsetResult.Achievements.Count);
            Assert.AreEqual("200", subsetResult.Achievements[0].ApiName);
            Assert.AreEqual("Softcore", subsetResult.Achievements[0].UnlockMode);
            Assert.AreEqual(2, seen.Count);

            var overlap = RetroAchievementsRecentProgressMapper.Map(
                recent,
                contexts,
                (key, _) => seen.Add(key));
            Assert.IsTrue(overlap.All(result => result.Achievements.Count == 0));
        }

        [TestMethod]
        public void TryParseDate_TreatsEndpointTimestampAsUtc()
        {
            Assert.IsTrue(
                RetroAchievementsRecentProgressMapper.TryParseDate(
                    "2026-07-28 12:34:56",
                    out var parsed));
            Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
            Assert.AreEqual(
                new DateTime(2026, 7, 28, 12, 34, 56, DateTimeKind.Utc),
                parsed);
            Assert.IsFalse(
                RetroAchievementsRecentProgressMapper.TryParseDate(
                    "invalid",
                    out _));
        }

        private static InGameTrackingContext Context(
            Guid gameId,
            DateTime sessionStart,
            string apiName)
        {
            return new InGameTrackingContext
            {
                Game = new Game { Id = gameId, Name = gameId.ToString() },
                SessionStartUtc = sessionStart,
                CachedSchema = new GameAchievementData
                {
                    ProviderKey = "RetroAchievements",
                    Achievements = new List<AchievementDetail>
                    {
                        new AchievementDetail { ApiName = apiName }
                    }
                }
            };
        }

        private static RaRecentAchievement Recent(
            int achievementId,
            string date,
            int hardcore,
            int gameId)
        {
            return new RaRecentAchievement
            {
                AchievementId = achievementId,
                Date = date,
                HardcoreMode = hardcore,
                GameId = gameId
            };
        }
    }
}
