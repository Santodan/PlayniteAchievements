using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Metadata.TrueGaming;
using PlayniteAchievements.Services.Metadata;
using PlayniteAchievements.Tests.Services;
using System;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    [TestCategory("Providers")]
    public class TrueGamingPageStoreTests
    {
        private static TrueGamingPageStore Store(FakeMetadataStateDatabase database) =>
            new TrueGamingPageStore(new MetadataSourcePageStore("TrueGaming", () => database, null));

        [TestMethod]
        public void Pages_AreKeptPerSite_InTheDatabase()
        {
            var database = new FakeMetadataStateDatabase();
            var gameId = Guid.NewGuid();
            var store = Store(database);
            store.Set(gameId, "Elden-Ring", TrueGamingSite.TrueAchievements, "Elden-Ring");
            store.Set(gameId, "Elden-Ring", TrueGamingSite.TrueTrophies, "Elden-Ring-PS5");
            store.Set(gameId, "Elden-Ring", TrueGamingSite.TrueSteamAchievements, null);
            store.Set(gameId, "Elden-Ring", TrueGamingSite.TrueSteamAchievements, null);
            Assert.AreEqual(3, database.PageWrites, "an unchanged page writes nothing");

            var reread = Store(database);
            Assert.IsTrue(reread.TryGet(gameId, "elden-ring", TrueGamingSite.TrueTrophies, out var trophies));
            Assert.AreEqual("Elden-Ring-PS5", trophies);
            Assert.IsTrue(reread.TryGet(gameId, "Elden-Ring", TrueGamingSite.TrueSteamAchievements, out var steam), "a site without the game is known");
            Assert.IsNull(steam);
            Assert.IsFalse(reread.TryGet(Guid.NewGuid(), "Elden-Ring", TrueGamingSite.TrueAchievements, out _));
            CollectionAssert.AreEqual(
                new[] { "TrueAchievements=Elden-Ring", "TrueTrophies=Elden-Ring-PS5", "TrueSteamAchievements=" },
                reread.GetPages(gameId, "Elden-Ring").Select(page => page.Site.Title + "=" + page.PageSlug).ToList(),
                "each site looked at, in lookup order");
        }

        [TestMethod]
        public void ANewName_StartsTheGameAgain()
        {
            var database = new FakeMetadataStateDatabase();
            var gameId = Guid.NewGuid();
            var store = Store(database);
            store.Set(gameId, "Peak", TrueGamingSite.TrueTrophies, "Peak");
            store.Set(gameId, "PEAK-2025", TrueGamingSite.TrueAchievements, null);

            Assert.IsFalse(store.TryGet(gameId, "Peak", TrueGamingSite.TrueTrophies, out _), "the old name's pages are gone");
            Assert.IsFalse(store.TryGet(gameId, "PEAK-2025", TrueGamingSite.TrueTrophies, out _), "not looked at under the new name");
            Assert.IsTrue(store.TryGet(gameId, "PEAK-2025", TrueGamingSite.TrueAchievements, out _));

            store.Clear(gameId);
            Assert.IsFalse(store.TryGet(gameId, "PEAK-2025", TrueGamingSite.TrueAchievements, out _), "a cleared game is looked at again");
            Assert.IsFalse(Store(database).TryGet(gameId, "PEAK-2025", TrueGamingSite.TrueAchievements, out _), "and its rows are gone");
        }

        [TestMethod]
        public void Keep_ForgetsTheOtherSites_InTheDatabaseToo()
        {
            var database = new FakeMetadataStateDatabase();
            var gameId = Guid.NewGuid();
            var store = Store(database);
            store.Set(gameId, "Hades", TrueGamingSite.TrueAchievements, "Hades");
            store.Set(gameId, "Hades", TrueGamingSite.TrueTrophies, "Hades-PS5");
            store.Set(gameId, "Hades", TrueGamingSite.TrueSteamAchievements, "Hades");

            store.Keep(gameId, "Hades", new[] { TrueGamingSite.TrueAchievements });
            var writes = database.PageWrites;
            store.Keep(gameId, "Hades", new[] { TrueGamingSite.TrueAchievements });
            store.Keep(gameId, "Other", Array.Empty<TrueGamingSite>());

            Assert.AreEqual(writes, database.PageWrites, "nothing left to forget writes nothing");
            CollectionAssert.AreEqual(
                new[] { "TrueAchievements=Hades" },
                Store(database).GetPages(gameId, "Hades").Select(page => page.Site.Title + "=" + page.PageSlug).ToList());
        }

        [TestMethod]
        public void ClearingTheCache_DropsThePagesKeptInMemory()
        {
            var database = new FakeMetadataStateDatabase();
            var gameId = Guid.NewGuid();
            var state = new MetadataStateStore(null);
            state.AttachDatabase(database);
            var store = new TrueGamingPageStore(state.Pages("TrueGaming"));
            store.Set(gameId, "Hades", TrueGamingSite.TrueAchievements, "Hades");

            database.Clear();

            Assert.IsFalse(store.TryGet(gameId, "Hades", TrueGamingSite.TrueAchievements, out _));
        }
    }
}
