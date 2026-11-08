using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Achievements.Tests
{
    [TestClass]
    public class LayerResolverTests
    {
        [TestMethod]
        public void Resolve_CustomBeatsMetadataBeatsPlatform()
        {
            var platform = new AchievementLayerValues(displayName: "P", description: "Pd", points: 10, trophyType: "bronze");
            var metadata = new AchievementLayerValues(displayName: "M", points: 20);
            var custom = new AchievementLayerValues(displayName: "C");

            var result = LayerResolver.Resolve(platform, metadata, custom);

            Assert.AreEqual("C", result.Values.DisplayName);
            Assert.AreEqual(20, result.Values.Points);
            Assert.AreEqual("Pd", result.Values.Description);
            Assert.AreEqual("bronze", result.Values.TrophyType);
            Assert.AreEqual(DataLayer.Custom, result.OriginOf(AchievementField.DisplayName));
            Assert.AreEqual(DataLayer.Metadata, result.OriginOf(AchievementField.Points));
            Assert.AreEqual(DataLayer.Platform, result.OriginOf(AchievementField.Description));
        }

        [TestMethod]
        public void Resolve_MetadataPointsAndTrophyTypeReplacePlatformValues()
        {
            var platform = new AchievementLayerValues(points: 10, trophyType: "bronze");
            var metadata = new AchievementLayerValues(points: 90, trophyType: "gold");

            var result = LayerResolver.Resolve(platform, metadata, null);

            Assert.AreEqual(90, result.Values.Points);
            Assert.AreEqual("gold", result.Values.TrophyType);
        }

        [TestMethod]
        public void Resolve_ClearedCustomFieldHidesLowerLayers()
        {
            var platform = new AchievementLayerValues(description: "Pd", trophyType: "gold");
            var custom = new AchievementLayerValues(cleared: AchievementField.Description | AchievementField.TrophyType);

            var result = LayerResolver.Resolve(platform, null, custom);

            Assert.IsNull(result.Values.Description);
            Assert.IsNull(result.Values.TrophyType);
            Assert.AreEqual(DataLayer.Custom, result.OriginOf(AchievementField.Description));
        }

        [TestMethod]
        public void Resolve_MetadataIconPairWithoutLockedIcon_DoesNotBorrowPlatformLockedIcon()
        {
            var platform = new AchievementLayerValues(unlockedIconPath: "p-on.png", lockedIconPath: "p-off.png");
            var metadata = new AchievementLayerValues(unlockedIconPath: "m-on.png", cleared: AchievementField.LockedIcon);

            var result = LayerResolver.Resolve(platform, metadata, null);

            Assert.AreEqual("m-on.png", result.Values.UnlockedIconPath);
            Assert.IsNull(result.Values.LockedIconPath);
        }

        [TestMethod]
        public void Resolve_CustomLockedIconOnly_KeepsLowerUnlockedIcon()
        {
            var metadata = new AchievementLayerValues(unlockedIconPath: "m-on.png", cleared: AchievementField.LockedIcon);
            var custom = new AchievementLayerValues(lockedIconPath: "c-off.png");

            var result = LayerResolver.Resolve(null, metadata, custom);

            Assert.AreEqual("m-on.png", result.Values.UnlockedIconPath);
            Assert.AreEqual("c-off.png", result.Values.LockedIconPath);
        }

        [TestMethod]
        public void Resolve_RarityPercentAndTierTravelTogether()
        {
            var platform = new AchievementLayerValues(rarity: RarityTier.Common);
            var metadata = new AchievementLayerValues(globalPercentUnlocked: 2.5, rarity: RarityTier.UltraRare);

            var result = LayerResolver.Resolve(platform, metadata, null);

            Assert.AreEqual(2.5, result.Values.GlobalPercentUnlocked);
            Assert.AreEqual(RarityTier.UltraRare, result.Values.Rarity);
        }

        [TestMethod]
        public void Resolve_UnsuppliedField_HasNoOrigin()
        {
            var result = LayerResolver.Resolve(null, null, null);

            Assert.AreEqual(DataLayer.None, result.OriginOf(AchievementField.Points));
            Assert.IsTrue(result.Values.IsEmpty);
        }

        [TestMethod]
        public void Tags_MetadataKeepsPlatformDerivedAndUnobtainable()
        {
            var platform = new AchievementLayerValues(tags: "Hardcore|Unobtainable|Missable");
            var metadata = new AchievementLayerValues(tags: "Collectable");

            var result = LayerResolver.Resolve(platform, metadata, null);

            CollectionAssert.AreEquivalent(
                new[] { "Collectable", "Hardcore", "Unobtainable" },
                AchievementTagHelper.Parse(result.Values.Tags));
        }

        [TestMethod]
        public void Tags_CustomReplacesUnobtainableButKeepsDerived()
        {
            var platform = new AchievementLayerValues(tags: "Softcore|Unobtainable");
            var metadata = new AchievementLayerValues(tags: "Collectable");
            var custom = new AchievementLayerValues(tags: "Challenge");

            var result = LayerResolver.Resolve(platform, metadata, custom);

            CollectionAssert.AreEquivalent(
                new[] { "Challenge", "Softcore" },
                AchievementTagHelper.Parse(result.Values.Tags));
        }

        [TestMethod]
        public void Tags_ClearedCustomLeavesOnlyDerived()
        {
            var platform = new AchievementLayerValues(tags: "Softcore|Missable");
            var custom = new AchievementLayerValues(cleared: AchievementField.Tags);

            var result = LayerResolver.Resolve(platform, null, custom);

            Assert.AreEqual("Softcore", result.Values.Tags);
        }

        [TestMethod]
        public void LayerValues_BlankStringsAreUnset_AndValueBeatsCleared()
        {
            var values = new AchievementLayerValues(displayName: "  ", description: "d", cleared: AchievementField.Description);

            Assert.IsNull(values.DisplayName);
            Assert.AreEqual(AchievementField.Description, values.Set);
            Assert.AreEqual(AchievementField.None, values.Cleared);
        }
    }
}
