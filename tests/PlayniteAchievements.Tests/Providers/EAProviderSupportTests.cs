using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.EA;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class EAProviderSupportTests
    {
        [DataTestMethod]
        [DataRow("EA")]
        [DataRow("EA app")]
        [DataRow("Origin")]
        [DataRow("Electronic Arts")]
        public void IsEaSourceName_ReturnsTrue_ForEaSourceNames(string sourceName)
        {
            Assert.IsTrue(EAProviderSupport.IsEaSourceName(sourceName));
        }

        [TestMethod]
        public void IsEaCapable_ReturnsTrue_ForOriginPluginId()
        {
            var pluginId = Guid.NewGuid();
            var game = new Game
            {
                PluginId = pluginId
            };

            Assert.IsTrue(EAProviderSupport.IsEaCapable(game, pluginId));
        }

        [DataTestMethod]
        [DataRow("Origin.OFR.50.0001456", "OFR.50.0001456")]
        [DataRow("EA.OFR.50.0001456", "OFR.50.0001456")]
        [DataRow("OFR.50.0001456", "OFR.50.0001456")]
        public void ExtractOfferIdFromGameId_NormalizesKnownPrefixes(string gameId, string expected)
        {
            Assert.AreEqual(expected, EAProviderSupport.ExtractOfferIdFromGameId(gameId));
        }

        [TestMethod]
        public void BuildOfferIdCandidates_IncludesStrippedAndOriginPrefixedOfferIds()
        {
            var candidates = EAProviderSupport.BuildOfferIdCandidates(
                matchedOfferId: null,
                gameId: "Origin.OFR.50.0001456");

            CollectionAssert.AreEqual(
                new[] { "OFR.50.0001456", "Origin.OFR.50.0001456" },
                new List<string>(candidates));
        }

        [TestMethod]
        public void BuildOfferIdCandidates_AddsOriginPrefixForBareOfferId()
        {
            var candidates = EAProviderSupport.BuildOfferIdCandidates(
                matchedOfferId: null,
                gameId: "OFR.50.0001456");

            CollectionAssert.AreEqual(
                new[] { "OFR.50.0001456", "Origin.OFR.50.0001456" },
                new List<string>(candidates));
        }

        [TestMethod]
        public void MatchGame_MatchesNormalizedOfferIdBeforeFallbacks()
        {
            var ownedGames = new List<EaOwnedGame>
            {
                new EaOwnedGame
                {
                    OriginOfferId = "OFR.50.0001456",
                    GameSlug = "different-slug",
                    ProductName = "Different Name"
                }
            };

            var game = new Game
            {
                Name = "Some Game",
                GameId = "Origin.OFR.50.0001456"
            };

            var matched = EAProviderSupport.MatchGame(ownedGames, game, game.GameId);

            Assert.IsNotNull(matched);
            Assert.AreEqual("OFR.50.0001456", matched.OriginOfferId);
        }

        [TestMethod]
        public void MapToGameData_PreservesUnlockedStateWithoutUnlockDate()
        {
            var gameId = Guid.NewGuid();
            var data = EAProviderSupport.MapToGameData(
                new Game
                {
                    Id = gameId,
                    Name = "Mass Effect"
                },
                new[]
                {
                    new EaAchievementItem
                    {
                        AchievementId = "ach_1",
                        Title = "Unlocked",
                        IsUnlocked = true,
                        UnlockTimeUtc = null
                    }
                });

            Assert.AreEqual("EA", data.ProviderKey);
            Assert.AreEqual(gameId, data.PlayniteGameId);
            Assert.IsTrue(data.HasAchievements);
            Assert.AreEqual(1, data.Achievements.Count);
            Assert.IsTrue(data.Achievements[0].Unlocked);
            Assert.IsNull(data.Achievements[0].UnlockTimeUtc);
        }

        [TestMethod]
        public void IsTransientError_ClassifiesRetryableFailures()
        {
            Assert.IsTrue(EAProviderSupport.IsTransientError(new EaTransientException("retry")));
            Assert.IsTrue(EAProviderSupport.IsTransientError(new EaApiHttpException((HttpStatusCode)429, "rate limited")));
            Assert.IsTrue(EAProviderSupport.IsTransientError(new EaApiHttpException(HttpStatusCode.InternalServerError, "server error")));
            Assert.IsTrue(EAProviderSupport.IsTransientError(new HttpRequestException("network")));

            Assert.IsFalse(EAProviderSupport.IsTransientError(new EaApiHttpException(HttpStatusCode.BadRequest, "bad request")));
            Assert.IsFalse(EAProviderSupport.IsTransientError(new InvalidOperationException("logic error")));
        }
    }
}
