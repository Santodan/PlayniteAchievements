using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcasePersistenceTests
    {
        [TestMethod]
        public void Showcase_SurvivesRepeatedLoadSaveCyclesWithoutGrowingPages()
        {
            var settings = new PersistedSettings();
            var expectedPages = settings.Showcase.Pages.Count;
            var expectedWidgets = settings.Showcase.WidgetInstances.Count;
            var json = JsonConvert.SerializeObject(settings);

            for (var cycle = 0; cycle < 3; cycle++)
            {
                var loaded = JsonConvert.DeserializeObject<PersistedSettings>(json);

                Assert.AreEqual(
                    expectedPages,
                    loaded.Showcase.Pages.Count,
                    $"Showcase pages grew on load cycle {cycle + 1}.");
                Assert.AreEqual(
                    expectedWidgets,
                    loaded.Showcase.WidgetInstances.Count,
                    $"Showcase widgets grew on load cycle {cycle + 1}.");

                json = JsonConvert.SerializeObject(loaded);
            }
        }

        [TestMethod]
        public void Showcase_LoadPreservesUserPagesInsteadOfAppendingSeededDefaults()
        {
            var settings = new PersistedSettings();
            var showcase = settings.Showcase;
            showcase.Pages.Clear();
            showcase.WidgetInstances.Clear();
            var authored = ShowcaseLayoutServiceAccess.AddPage(showcase, ShowcasePageTemplate.Blank, "Mine");
            settings.Showcase = showcase;

            var json = JsonConvert.SerializeObject(settings);
            var loaded = JsonConvert.DeserializeObject<PersistedSettings>(json);

            Assert.AreEqual(1, loaded.Showcase.Pages.Count);
            Assert.AreEqual("Mine", loaded.Showcase.Pages.Single().Name);
            Assert.AreEqual(authored.Blocks.Count, loaded.Showcase.Pages.Single().Blocks.Count);
        }

        [TestMethod]
        public void Showcase_PinCollectionsAndWidgetSelectionsRoundTrip()
        {
            var settings = new PersistedSettings();
            var showcase = settings.Showcase;
            var gameId = Guid.NewGuid();
            var collection = new PinnedGameCollection
            {
                CollectionId = "weekend-games",
                Name = "Weekend",
                GameIds = { gameId }
            };
            showcase.GamePinCollections.Add(collection);
            var widget = PlayniteAchievements.Services.Showcase.ShowcaseLayoutService.CreateWidget(
                showcase,
                ShowcaseWidgetKind.GameSummaries);
            PlayniteAchievements.Models.ShowcaseWidgetOptions.SetPinCollectionId(
                widget,
                collection.CollectionId);

            var json = JsonConvert.SerializeObject(settings);
            var loaded = JsonConvert.DeserializeObject<PersistedSettings>(json);

            var loadedCollection = loaded.Showcase.GamePinCollections.Single(item =>
                item.CollectionId == collection.CollectionId);
            Assert.AreEqual("Weekend", loadedCollection.Name);
            CollectionAssert.AreEqual(new[] { gameId }, loadedCollection.GameIds);
            Assert.AreEqual(
                collection.CollectionId,
                PlayniteAchievements.Models.ShowcaseWidgetOptions.GetPinCollectionId(
                    loaded.Showcase.WidgetInstances.Single(item =>
                        item.InstanceId == widget.InstanceId)));
        }

        [TestMethod]
        public void Showcase_RoundTripKeepsASingleDefaultCollectionPerList()
        {
            // The seeded lists must be replaced, not populated in place, on deserialize;
            // in-place population appended the persisted entries after the seed and grew a
            // duplicate Default collection on every load.
            var settings = new PersistedSettings();
            var json = JsonConvert.SerializeObject(settings);
            var loaded = JsonConvert.DeserializeObject<PersistedSettings>(json);

            Assert.AreEqual(1, loaded.Showcase.AchievementPinCollections.Count);
            Assert.AreEqual(1, loaded.Showcase.GamePinCollections.Count);
        }

        private static class ShowcaseLayoutServiceAccess
        {
            public static ShowcasePageSettings AddPage(
                ShowcaseSettings settings,
                ShowcasePageTemplate template,
                string name) =>
                PlayniteAchievements.Services.Showcase.ShowcaseLayoutService.AddPage(settings, template, name);
        }
    }
}
