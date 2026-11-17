using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Metadata;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// <see cref="MetadataOverlayRun.LayerByApiName"/>, which puts SteamHunters' grouping of a
    /// provider-only friend game into each row's metadata layer instead of its platform values.
    /// </summary>
    [TestClass]
    public class MetadataLayerByApiNameTests
    {
        private static AchievementDetail SteamRow() => new AchievementDetail
        {
            ApiName = "ACH_1",
            DisplayName = "First",
            Category = "Steam Set",
            CategoryKind = CategoryKindHelper.Base,
            Tags = AchievementTagHelper.Unobtainable
        };

        private static AchievementDetail HuntersRow() => new AchievementDetail
        {
            ApiName = "ach_1",
            DisplayName = "First",
            Category = "Hunters DLC",
            CategoryKind = CategoryKindHelper.Dlc,
            Tags = "Multiplayer"
        };

        [TestMethod]
        public void CategoriesAndTags_GoToTheMetadataLayer_PlatformKeepsSteamValues()
        {
            var row = SteamRow();

            MetadataOverlayRun.LayerByApiName(new[] { row }, new[] { HuntersRow() }, categories: true, tags: true);

            Assert.IsNotNull(row.PlatformLayer);
            Assert.AreEqual("Steam Set", row.PlatformLayer.Category);
            Assert.AreEqual(CategoryKindHelper.Base, row.PlatformLayer.CategoryKind);
            Assert.AreEqual(AchievementTagHelper.Unobtainable, row.PlatformLayer.Tags);

            Assert.IsNotNull(row.MetadataLayer);
            Assert.AreEqual("Hunters DLC", row.MetadataLayer.Category);
            Assert.AreEqual(CategoryKindHelper.Dlc, row.MetadataLayer.CategoryKind);
            Assert.AreEqual("Multiplayer", row.MetadataLayer.Tags);

            // Resolved for in-memory consumers; the archived achievement stays Unobtainable.
            Assert.AreEqual("Hunters DLC", row.Category);
            Assert.AreEqual(CategoryKindHelper.Dlc, row.CategoryKind);
            Assert.IsTrue(AchievementTagHelper.Contains(row.Tags, "Multiplayer"));
            Assert.IsTrue(AchievementTagHelper.IsUnobtainable(row.Tags));

            // What the definitions table stores is Steam's.
            var stored = AchievementLayers.PlatformRow(row);
            Assert.AreEqual("Steam Set", stored.Category);
            Assert.AreEqual(CategoryKindHelper.Base, stored.CategoryKind);
            Assert.AreEqual(AchievementTagHelper.Unobtainable, stored.Tags);
        }

        [TestMethod]
        public void TagsOnly_LeavesTheCategoryToThePlatform()
        {
            var row = SteamRow();

            MetadataOverlayRun.LayerByApiName(new[] { row }, new[] { HuntersRow() }, categories: false, tags: true);

            Assert.IsNull(row.MetadataLayer.Category);
            Assert.AreEqual(AchievementField.None, row.MetadataLayer.Cleared & AchievementField.Categories);
            Assert.AreEqual("Steam Set", row.Category);
            Assert.AreEqual(CategoryKindHelper.Base, row.CategoryKind);
            Assert.IsTrue(AchievementTagHelper.Contains(row.Tags, "Multiplayer"));
        }

        [TestMethod]
        public void SourceWithoutCategory_ClearsIt_LikeTheOverlay()
        {
            var row = SteamRow();
            var source = HuntersRow();
            source.Category = null;
            source.CategoryKind = null;

            MetadataOverlayRun.LayerByApiName(new[] { row }, new[] { source }, categories: true, tags: false);

            Assert.AreEqual(AchievementField.Categories, row.MetadataLayer.Cleared & AchievementField.Categories);
            Assert.AreEqual("Steam Set", row.PlatformLayer.Category);
        }

        [TestMethod]
        public void NoSourceRow_OrNothingChosen_PinsThePlatformWithNoMetadataLayer()
        {
            var unmatched = SteamRow();
            unmatched.ApiName = "OTHER";
            var unchosen = SteamRow();

            MetadataOverlayRun.LayerByApiName(new[] { unmatched }, new[] { HuntersRow() }, categories: true, tags: true);
            MetadataOverlayRun.LayerByApiName(new List<AchievementDetail> { unchosen }, null, categories: false, tags: false);

            foreach (var row in new[] { unmatched, unchosen })
            {
                Assert.IsNotNull(row.PlatformLayer);
                Assert.IsNull(row.MetadataLayer);
                Assert.AreEqual("Steam Set", row.Category);
                Assert.AreEqual(CategoryKindHelper.Base, row.CategoryKind);
            }
        }
    }
}
