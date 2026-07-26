using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services.Friends;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamExophaseFriendOwnershipMapperTests
    {
        [TestMethod]
        public void MapToSteamOwnership_TranslatesMappedExophaseRowsToSteamRows()
        {
            var playniteGameId = Guid.NewGuid();
            var lastPlayed = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

            var result = SteamExophaseFriendOwnershipMapper.MapToSteamOwnership(
                "76561198000000001",
                new[]
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-user",
                        AppId = 480,
                        ProviderGameKey = "steam|game-slug",
                        ProviderPlatformKey = "Steam",
                        GameName = "Shared Game",
                        IconUrl = "https://example/icon.jpg",
                        CoverUrl = "https://example/cover.jpg",
                        PlaytimeForeverMinutes = 123,
                        Playtime2WeeksMinutes = 4,
                        LastPlayedUtc = lastPlayed,
                        AchievementUnlocksHint = 5,
                        AchievementTotalHint = 9
                    }
                },
                new[]
                {
                    new FriendGameMapping
                    {
                        AppId = 480,
                        PlayniteGameId = playniteGameId
                    }
                });

            var row = result.Ownership.Single();
            Assert.AreEqual(1, result.IncomingCount);
            Assert.AreEqual(0, result.SkippedCount);
            Assert.AreEqual("Steam", row.ProviderKey);
            Assert.AreEqual("76561198000000001", row.ExternalUserId);
            Assert.AreEqual(480, row.AppId);
            Assert.AreEqual("Steam", row.ProviderPlatformKey);
            Assert.AreEqual(playniteGameId, row.PlayniteGameId);
            Assert.AreEqual("Shared Game", row.GameName);
            Assert.AreEqual(123, row.PlaytimeForeverMinutes);
            Assert.AreEqual(4, row.Playtime2WeeksMinutes);
            Assert.AreEqual(lastPlayed, row.LastPlayedUtc);
            Assert.AreEqual(5, row.AchievementUnlocksHint);
            Assert.AreEqual(9, row.AchievementTotalHint);
            StringAssert.Contains(row.IconUrl, "/480/");
            StringAssert.Contains(row.CoverUrl, "/480/");
        }

        [TestMethod]
        public void MapToSteamOwnership_SkipsRowsWithoutSteamAppMapping()
        {
            var result = SteamExophaseFriendOwnershipMapper.MapToSteamOwnership(
                "76561198000000001",
                new[]
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        PlayniteGameId = Guid.NewGuid(),
                        GameName = "Provider Only"
                    }
                },
                new List<FriendGameMapping>());

            Assert.AreEqual(1, result.IncomingCount);
            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(0, result.Ownership.Count);
        }

        [TestMethod]
        public void MapToSteamOwnership_UsesIncomingSteamAppIdForProviderOnlyRows()
        {
            var result = SteamExophaseFriendOwnershipMapper.MapToSteamOwnership(
                "76561198000000001",
                new[]
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        AppId = 3768760,
                        ProviderGameKey = "steam|provider-only-game",
                        ProviderPlatformKey = "Steam",
                        GameName = "Provider Only Game",
                        AchievementUnlocksHint = 1,
                        AchievementTotalHint = 10
                    }
                },
                new List<FriendGameMapping>());

            var row = result.Ownership.Single();
            Assert.AreEqual(1, result.IncomingCount);
            Assert.AreEqual(0, result.SkippedCount);
            Assert.AreEqual("Steam", row.ProviderKey);
            Assert.AreEqual(3768760, row.AppId);
            Assert.IsNull(row.PlayniteGameId);
            Assert.IsTrue(string.IsNullOrWhiteSpace(row.ProviderGameKey));
            Assert.AreEqual("Provider Only Game", row.GameName);
            Assert.AreEqual(1, row.AchievementUnlocksHint);
            Assert.AreEqual(10, row.AchievementTotalHint);
        }

        [TestMethod]
        public void MapToSteamOwnership_UsesCurrentUserSteamLabelAppIdWhenCacheMappingMissing()
        {
            var playniteGameId = Guid.NewGuid();

            var result = SteamExophaseFriendOwnershipMapper.MapToSteamOwnership(
                "76561198000000001",
                new[]
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        AppId = 12345,
                        GameName = "Family Shared Game"
                    }
                },
                new List<FriendGameMapping>(),
                new[]
                {
                    new CurrentUserGameLabel
                    {
                        PlayniteGameId = playniteGameId,
                        GameName = "Family Shared Game",
                        ProviderKey = "Steam",
                        ProviderPlatformKey = "Steam",
                        AppId = 12345
                    }
                });

            var row = result.Ownership.Single();
            Assert.AreEqual(1, result.IncomingCount);
            Assert.AreEqual(0, result.SkippedCount);
            Assert.AreEqual("Steam", row.ProviderKey);
            Assert.AreEqual(12345, row.AppId);
            Assert.AreEqual(playniteGameId, row.PlayniteGameId);
        }

        [TestMethod]
        public void MapToSteamOwnership_DeduplicatesBySteamAppId()
        {
            var playniteGameId = Guid.NewGuid();

            var result = SteamExophaseFriendOwnershipMapper.MapToSteamOwnership(
                "steam-id",
                new[]
                {
                    new FriendGameOwnership
                    {
                        PlayniteGameId = playniteGameId,
                        GameName = "First"
                    },
                    new FriendGameOwnership
                    {
                        PlayniteGameId = playniteGameId,
                        GameName = "Duplicate"
                    }
                },
                new[]
                {
                    new FriendGameMapping
                    {
                        AppId = 10,
                        PlayniteGameId = playniteGameId
                    }
                });

            Assert.AreEqual(2, result.IncomingCount);
            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(1, result.Ownership.Count);
            Assert.AreEqual("First", result.Ownership[0].GameName);
        }
    }
}
