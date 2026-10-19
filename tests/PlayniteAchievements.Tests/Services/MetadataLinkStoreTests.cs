using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class MetadataLinkStoreTests
    {
        private string _directory;

        [TestInitialize]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pa-metadata-links-" + Guid.NewGuid().ToString("N"));
        }

        private MetadataLinkStore Store()
        {
            return new MetadataLinkStore(_directory, new GameCustomDataStore(_directory), null);
        }

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
