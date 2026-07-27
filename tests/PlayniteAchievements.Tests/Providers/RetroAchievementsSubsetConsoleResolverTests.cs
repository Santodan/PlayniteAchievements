using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RetroAchievements.Models;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsSubsetConsoleResolverTests
    {
        [TestMethod]
        public void Resolve_UsesApiConsoleWhenFallbackMissing()
        {
            var result = RetroAchievementsSubsetConsoleResolver.Resolve(
                new RaGameInfoUserProgress { ConsoleId = 7 },
                fallbackConsoleId: null);

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(7, result.Value);
        }

        [TestMethod]
        public void Resolve_UsesApiConsoleWhenFallbackDiffers()
        {
            var result = RetroAchievementsSubsetConsoleResolver.Resolve(
                new RaGameInfoUserProgress { ConsoleId = 7 },
                fallbackConsoleId: 1);

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(7, result.Value);
        }

        [TestMethod]
        public void Resolve_FallsBackWhenApiConsoleMissingOrInvalid()
        {
            var missing = RetroAchievementsSubsetConsoleResolver.Resolve(
                new RaGameInfoUserProgress(),
                fallbackConsoleId: 9);
            var invalid = RetroAchievementsSubsetConsoleResolver.Resolve(
                new RaGameInfoUserProgress { ConsoleId = -1 },
                fallbackConsoleId: 9);

            Assert.IsTrue(missing.HasValue);
            Assert.AreEqual(9, missing.Value);
            Assert.IsTrue(invalid.HasValue);
            Assert.AreEqual(9, invalid.Value);
        }

        [TestMethod]
        public void Resolve_ReturnsNullWhenApiAndFallbackConsoleInvalid()
        {
            var result = RetroAchievementsSubsetConsoleResolver.Resolve(
                new RaGameInfoUserProgress { ConsoleId = -1 },
                fallbackConsoleId: null);

            Assert.IsFalse(result.HasValue);
        }

        [TestMethod]
        public void Deserialize_PopulatesConsoleIdFromApiConsoleID()
        {
            var gameInfo = JsonConvert.DeserializeObject<RaGameInfoUserProgress>(
                "{\"ID\":1,\"Title\":\"Sonic the Hedgehog\",\"ConsoleID\":1}");

            Assert.IsNotNull(gameInfo);
            Assert.AreEqual(1, gameInfo.ConsoleId);
        }

        [TestMethod]
        public void Deserialize_AllowsNullParentGameId()
        {
            var gameInfo = JsonConvert.DeserializeObject<RaGameInfoUserProgress>(
                "{\"ID\":1,\"Title\":\"Sonic the Hedgehog\",\"ConsoleID\":1,\"ParentGameID\":null}");

            Assert.IsNotNull(gameInfo);
            Assert.IsFalse(gameInfo.ParentGameId.HasValue);
        }
    }
}
