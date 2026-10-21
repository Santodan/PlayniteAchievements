using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Metadata.TrueGaming;
using System;
using System.IO;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    [TestCategory("Providers")]
    public class TrueGamingPageStoreTests
    {
        private string _directory;

        [TestInitialize]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pa-truegaming-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [TestMethod]
        public void Pages_AreKeptPerSite_AndSurviveASave()
        {
            var path = Path.Combine(_directory, "truegaming-pages.json");
            var gameId = Guid.NewGuid();
            var store = new TrueGamingPageStore(path, null);
            store.Set(gameId, "Elden-Ring", TrueGamingSite.TrueAchievements, "Elden-Ring");
            store.Set(gameId, "Elden-Ring", TrueGamingSite.TrueTrophies, "Elden-Ring-PS5");
            store.Set(gameId, "Elden-Ring", TrueGamingSite.TrueSteamAchievements, null);
            store.Save();

            var reread = new TrueGamingPageStore(path, null);
            Assert.IsTrue(reread.TryGet(gameId, "elden-ring", TrueGamingSite.TrueTrophies, out var trophies));
            Assert.AreEqual("Elden-Ring-PS5", trophies);
            Assert.IsTrue(reread.TryGet(gameId, "Elden-Ring", TrueGamingSite.TrueSteamAchievements, out var steam), "a site without the game is known");
            Assert.IsNull(steam);
            Assert.IsFalse(reread.TryGet(Guid.NewGuid(), "Elden-Ring", TrueGamingSite.TrueAchievements, out _));
        }

        [TestMethod]
        public void ANewName_StartsTheGameAgain()
        {
            var gameId = Guid.NewGuid();
            var store = new TrueGamingPageStore(Path.Combine(_directory, "pages.json"), null);
            store.Set(gameId, "Peak", TrueGamingSite.TrueTrophies, "Peak");
            store.Set(gameId, "PEAK-2025", TrueGamingSite.TrueAchievements, null);

            Assert.IsFalse(store.TryGet(gameId, "Peak", TrueGamingSite.TrueTrophies, out _), "the old name's pages are gone");
            Assert.IsFalse(store.TryGet(gameId, "PEAK-2025", TrueGamingSite.TrueTrophies, out _), "not looked at under the new name");
            Assert.IsTrue(store.TryGet(gameId, "PEAK-2025", TrueGamingSite.TrueAchievements, out _));
        }
    }
}