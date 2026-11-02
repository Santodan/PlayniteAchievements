using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class MetadataLinkStoreTests
    {
        private string _directory;
        private FakeMetadataStateDatabase _database;

        [TestInitialize]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pa-metadata-links-" + Guid.NewGuid().ToString("N"));
            _database = new FakeMetadataStateDatabase();
        }

        private MetadataLinkStore Store() => MetadataTestStores.Links(_directory, _database);

        [TestCleanup]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [TestMethod]
        public void AutoLink_RoundTripsThroughDisk()
        {
            var game = Guid.NewGuid();
            Store().SetAuto(game, "PlayStation", "NPWR22392_00", "God of War Ragnarök");

            Assert.IsTrue(Store().TryGet(game, "playstation", out var record));
            Assert.AreEqual("NPWR22392_00", record.Id);
            Assert.AreEqual("God of War Ragnarök", record.Label);
            Assert.AreEqual(MetadataLinkOrigin.Auto, record.Origin);
        }

        [TestMethod]
        public void AutoLink_DoesNotReplaceUserLink()
        {
            var store = Store();
            var game = Guid.NewGuid();
            store.SetUser(game, "Steam", "1593500");

            Assert.IsFalse(store.SetAuto(game, "Steam", "999"));
            store.TryGet(game, "Steam", out var record);
            Assert.AreEqual("1593500", record.Id);
            Assert.AreEqual(MetadataLinkOrigin.User, record.Origin);
        }

        [TestMethod]
        public void BlankUserLink_IsAnExplicitNoMatch_AndClearRemovesIt()
        {
            var store = Store();
            var game = Guid.NewGuid();
            store.SetUser(game, "Steam", "  ");

            Assert.IsTrue(store.TryGet(game, "Steam", out var record));
            Assert.IsTrue(record.IsNoMatch);
            Assert.IsFalse(store.SetAuto(game, "Steam", "1"));

            Assert.IsTrue(store.Clear(game, "Steam"));
            Assert.IsFalse(store.TryGet(game, "Steam", out _));
            Assert.IsTrue(store.SetAuto(game, "Steam", "1"));
        }

        [TestMethod]
        public void Links_TravelWithTheGamesPortableData()
        {
            var game = Guid.NewGuid();
            var store = Store();
            store.SetAuto(game, "PlayStation", "NPWR24695_00", "Cyberpunk 2077");
            store.SetUser(game, "SteamHunters", "1091500");

            var stored = new GameCustomDataStore(_directory).LoadOrDefault(game);
            var portable = GameCustomDataNormalizer.NormalizePortable(stored.ToPortable(), game);
            var imported = GameCustomDataNormalizer.NormalizeInternal(GameCustomDataFile.FromPortable(portable, game, null, null), game);

            Assert.AreEqual("NPWR24695_00", imported.MetadataLinks["PlayStation"].Id);
            Assert.IsFalse(imported.MetadataLinks["PlayStation"].IsUserSet);
            Assert.IsTrue(imported.MetadataLinks["SteamHunters"].IsUserSet);
        }

        [TestMethod]
        public void OnlyUserLinks_CountAsCustomization()
        {
            var auto = new GameCustomDataFile
            {
                MetadataLinks = new Dictionary<string, MetadataLinkData> { ["PlayStation"] = new MetadataLinkData { Id = "NPWR1" } }
            };
            Assert.IsTrue(GameCustomDataNormalizer.HasInternalData(auto));
            Assert.IsFalse(GameCustomDataNormalizer.HasVisibleCustomization(auto));

            auto.MetadataLinks["PlayStation"].IsUserSet = true;
            Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(auto));
        }

        [TestMethod]
        public void CapstoneArtRecord_RoundTripsThroughDisk()
        {
            var game = Guid.NewGuid();
            Store().SetCapstoneArtRecord(game, "custom:auto-capstone", new CapstoneArtRecord { Source = "https://psn/plat.png", Length = 42 });

            var record = Store().GetCapstoneArtRecord(game, "custom:auto-capstone");
            Assert.AreEqual("https://psn/plat.png", record.Source);
            Assert.AreEqual(42, record.Length);
        }

        [TestMethod]
        public void CollectionPlatinums_ComeBackFromTheDatabase()
        {
            var game = Guid.NewGuid();
            var capstones = new[]
            {
                new MetadataCategoryCapstone { Category = "Mass Effect 1", ArtUrl = "https://psn/me1.png", Title = "N7 Elite", MatchApiName = "ME1_ALL" },
                new MetadataCategoryCapstone { Category = "Mass Effect 2", ArtUrl = "https://psn/me2.png", Title = "N7 Elite" }
            };
            Store().RecordApplied(game, null, null, null, null, null, null, null, capstones);

            var reread = Store().GetCategoryCapstones(game);
            CollectionAssert.AreEqual(new[] { "Mass Effect 1", "Mass Effect 2" }, reread.Select(capstone => capstone.Category).ToList());
            Assert.AreEqual("ME1_ALL", reread[0].MatchApiName);
            Assert.AreEqual("https://psn/me2.png", reread[1].ArtUrl);
        }

        [TestMethod]
        public void AppliedState_ComesBackFromTheDatabase_UntilTheCacheIsCleared()
        {
            var game = Guid.NewGuid();
            var coverage = new Dictionary<string, MetadataCoverage>
            {
                ["TrueGaming"] = new MetadataCoverage { Matched = 46, Total = 600 }
            };
            Store().RecordApplied(game, "PlayStation", "https://psn/plat.png", coverage, new[] { "a1" }, "Platinum", "All trophies", "all-achievements");

            var reread = Store();
            Assert.AreEqual(46, reread.GetCoverage(game, "truegaming").Matched);
            Assert.AreEqual("PlayStation", reread.GetBorrowedIconSource(game, "A1"));
            Assert.AreEqual("https://psn/plat.png", reread.GetCapstoneArtUrl(game));
            Assert.AreEqual("Platinum", reread.GetCapstoneText(game).Title);
            Assert.AreEqual("all-achievements", reread.GetCapstoneMatch(game));

            reread.SetCapstoneArtRecord(game, "custom:auto-capstone", new CapstoneArtRecord { Source = "https://psn/plat.png" });
            _database.Clear();
            Assert.IsNull(reread.GetCoverage(game, "TrueGaming"), "the applied state went with the cache");
            Assert.IsNotNull(Store().GetCapstoneArtRecord(game, "custom:auto-capstone"), "the capstone art record outlasts it");
        }

        [TestMethod]
        public void OldMetadataFolder_CarriesItsCapstoneRecordsOver_AndIsRemoved()
        {
            var game = Guid.NewGuid();
            var folder = Path.Combine(_directory, MetadataFolderRetirement.FolderName);
            Directory.CreateDirectory(Path.Combine(folder, "sources"));
            File.WriteAllText(Path.Combine(folder, "sources", game + ".json"), "{}");
            File.WriteAllText(Path.Combine(folder, "icon-match-test.csv"), "Game");
            File.WriteAllText(Path.Combine(folder, "links.json"),
                "{\"version\":2,\"applied\":{},\"capstoneArt\":{\"" + game + "\":{\"custom:auto-capstone\":" +
                "{\"title\":\"Platinum\",\"source\":\"https://psn/plat.png\",\"path\":\"C:\\\\art.png\",\"length\":42,\"writeUtc\":\"2026-10-20T08:00:00Z\"}}}}");

            MetadataFolderRetirement.Run(_directory, new GameCustomDataStore(_directory), null);

            Assert.IsFalse(Directory.Exists(folder), "the whole folder goes");
            var record = Store().GetCapstoneArtRecord(game, "custom:auto-capstone");
            Assert.AreEqual("https://psn/plat.png", record.Source);
            Assert.AreEqual("Platinum", record.Title);
            Assert.AreEqual(42, record.Length);
            Assert.AreEqual(new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), record.WriteTimeUtc.ToUniversalTime());
        }

        [TestMethod]
        public void UnreadableLinksFile_IsMovedAside_AndTheFolderStillGoes()
        {
            var folder = Path.Combine(_directory, MetadataFolderRetirement.FolderName);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "links.json"), "{ not json");

            MetadataFolderRetirement.Run(_directory, new GameCustomDataStore(_directory), null);

            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(1, Directory.GetFiles(_directory, "metadata-links.unreadable-*.json").Length);
        }

        [TestMethod]
        public void UnchangedWrite_RaisesNoEvent()
        {
            var store = Store();
            var game = Guid.NewGuid();
            var raised = 0;
            store.LinkChanged += _ => raised++;

            store.SetAuto(game, "Steam", "1");
            store.SetAuto(game, "Steam", "1");

            Assert.AreEqual(1, raised);
        }
    }
}
