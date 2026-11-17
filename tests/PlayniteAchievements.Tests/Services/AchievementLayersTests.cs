using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Tagging;

namespace PlayniteAchievements.Services.Achievements.Tests
{
    [TestClass]
    public class AchievementLayersTests
    {
        [TestMethod]
        public void Resolve_ShowsMetadataOverPlatform_AndClearingMetadataRestoresPlatform()
        {
            var row = new AchievementDetail { ApiName = "A", DisplayName = "Own", Category = "Steam", Points = 10 };
            row.PlatformLayer = AchievementLayers.PlatformOf(row);
            row.MetadataLayer = new AchievementLayerValues(displayName: "Borrowed", category: "Story", points: 50);

            AchievementLayers.Resolve(row);
            Assert.AreEqual("Borrowed", row.DisplayName);
            Assert.AreEqual("Story", row.Category);
            Assert.AreEqual(50, row.Points);

            row.MetadataLayer = null;
            AchievementLayers.Resolve(row);
            Assert.AreEqual("Own", row.DisplayName);
            Assert.AreEqual("Steam", row.Category);
            Assert.AreEqual(10, row.Points);
        }

        [TestMethod]
        public void Resolve_ClearedMetadataCategory_HasNoCategory()
        {
            var row = new AchievementDetail { ApiName = "A", Category = "Steam", CategoryKind = "dlc" };
            row.PlatformLayer = AchievementLayers.PlatformOf(row);
            row.MetadataLayer = new AchievementLayerValues(cleared: AchievementField.Categories);

            AchievementLayers.Resolve(row);

            Assert.IsNull(row.Category);
            Assert.IsNull(row.CategoryKind);
        }

        [TestMethod]
        public void PlatformRow_CarriesPlatformValues_NotResolvedOnes()
        {
            var row = new AchievementDetail { ApiName = "A", DisplayName = "Own", TrophyType = "bronze", ScaledPoints = 3 };
            row.PlatformLayer = AchievementLayers.PlatformOf(row);
            row.MetadataLayer = new AchievementLayerValues(displayName: "Borrowed", trophyType: "gold");
            AchievementLayers.Resolve(row);

            var stored = AchievementLayers.PlatformRow(row);

            Assert.AreEqual("Own", stored.DisplayName);
            Assert.AreEqual("bronze", stored.TrophyType);
            Assert.AreEqual(3, stored.ScaledPoints);
            Assert.AreEqual("Borrowed", row.DisplayName);
        }

        [TestMethod]
        public void PlatformRow_UnlayeredRow_IsTheRowItself()
        {
            var row = new AchievementDetail { ApiName = "A" };

            Assert.AreSame(row, AchievementLayers.PlatformRow(row));
        }

        [TestMethod]
        public void AreasFrom_SplitsShownValuesByLayer()
        {
            var borrowed = new AchievementDetail { ApiName = "A", DisplayName = "Own", UnlockedIconPath = "own.png" };
            borrowed.PlatformLayer = AchievementLayers.PlatformOf(borrowed);
            borrowed.MetadataLayer = new AchievementLayerValues(unlockedIconPath: "borrowed.png", cleared: AchievementField.LockedIcon);
            var own = new AchievementDetail { ApiName = "B", DisplayName = "Mine", UnlockedIconPath = "mine.png" };
            var rows = new[] { borrowed, own };

            var platform = AchievementLayers.AreasFrom(rows, DataLayer.Platform);
            var metadata = AchievementLayers.AreasFrom(rows, DataLayer.Metadata);

            Assert.AreEqual(CustomizationArea.Icons | CustomizationArea.Text, platform);
            Assert.AreEqual(CustomizationArea.Icons, metadata);
        }

        [TestMethod]
        public void CustomLayer_FromOverride_ResolvesOverMetadataAndPlatform()
        {
            var row = new AchievementDetail { ApiName = "A", DisplayName = "Own", Description = "Desc", TrophyType = "gold", Tags = "Hardcore|Missable" };
            row.PlatformLayer = AchievementLayers.PlatformOf(row);
            row.MetadataLayer = new AchievementLayerValues(displayName: "Borrowed", category: "Story");
            row.CustomLayer = new PlayniteAchievements.Models.Settings.AchievementOverride
            {
                DisplayName = "Mine",
                ClearDescription = true,
                TrophyType = "bronze",
                ClearTrophyType = true,
                ClearTags = true,
                Tags = "Challenge"
            }.ToLayer();

            AchievementLayers.Resolve(row);

            Assert.AreEqual("Mine", row.DisplayName);
            Assert.IsNull(row.Description, "a cleared description hides the platform's");
            Assert.AreEqual("bronze", row.TrophyType, "a value wins over its Clear flag");
            Assert.AreEqual("Hardcore", row.Tags, "ClearTags removes every assigned tag, the derived ones stay");
            Assert.AreEqual("Story", row.Category, "metadata shows where the user set nothing");
        }
    }
}
