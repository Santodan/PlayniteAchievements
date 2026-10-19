using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Metadata;
using System;
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
            new MetadataLinkStore(_directory, null).SetAuto(game, "PlayStation", "NPWR22392_00", "God of War Ragnarök");

            Assert.IsTrue(new MetadataLinkStore(_directory, null).TryGet(game, "playstation", out var record));
            Assert.AreEqual("NPWR22392_00", record.Id);
            Assert.AreEqual("God of War Ragnarök", record.Label);
            Assert.AreEqual(MetadataLinkOrigin.Auto, record.Origin);
        }

        [TestMethod]
        public void AutoLink_DoesNotReplaceUserLink()
        {
            var store = new MetadataLinkStore(_directory, null);
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
            var store = new MetadataLinkStore(_directory, null);
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
        public void UnchangedWrite_RaisesNoEvent()
        {
            var store = new MetadataLinkStore(_directory, null);
            var game = Guid.NewGuid();
            var raised = 0;
            store.LinkChanged += _ => raised++;

            store.SetAuto(game, "Steam", "1");
            store.SetAuto(game, "Steam", "1");

            Assert.AreEqual(1, raised);
        }
    }
}
