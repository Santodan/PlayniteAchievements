using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services.Metadata;

namespace PlayniteAchievements.Services.Metadata.Tests
{
    [TestClass]
    public class DataLayersTests
    {
        [TestMethod]
        public void EveryAchievementInOneCategory_IsNoCategories()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A", Category = "Game Name" },
                new AchievementDetail { ApiName = "B", Category = "Game Name" }
            };

            Assert.AreEqual(CustomizationArea.None, DataLayers.Present(rows) & CustomizationArea.Categories);
        }

        [TestMethod]
        public void OneCategoryBesideDefault_IsCategorized()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A", Category = "Story" },
                new AchievementDetail { ApiName = "B", Category = "Default" }
            };

            Assert.AreEqual(CustomizationArea.Categories, DataLayers.Present(rows) & CustomizationArea.Categories);
        }

        [TestMethod]
        public void DerivedTags_AreNotTags_AndPlatinumIsACapstone()
        {
            var present = DataLayers.Present(new[]
            {
                new AchievementDetail { ApiName = "A", Tags = "Hardcore", TrophyType = "platinum" }
            });

            Assert.AreEqual(CustomizationArea.None, present & CustomizationArea.Tags);
            Assert.AreEqual(CustomizationArea.Capstones, present & CustomizationArea.Capstones);
        }

        [TestMethod]
        public void DefaultCategory_CountsAsNoCategory()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A", Category = "Default" },
                new AchievementDetail { ApiName = "B", Category = " default " }
            };

            Assert.AreEqual(CustomizationArea.None, DataLayers.Present(rows) & CustomizationArea.Categories);
            Assert.IsNull(DataLayers.NamedCategory("Default"));
            Assert.AreEqual("Story", DataLayers.NamedCategory("Story"));
        }

        [TestMethod]
        public void Present_ReportsAreasWithAnyValue()
        {
            var present = DataLayers.Present(new[]
            {
                new AchievementDetail { ApiName = "A", UnlockedIconPath = "a.png", IsCapstone = true }
            });

            Assert.AreEqual(CustomizationArea.Icons | CustomizationArea.Capstones, present);
        }
    }
}
