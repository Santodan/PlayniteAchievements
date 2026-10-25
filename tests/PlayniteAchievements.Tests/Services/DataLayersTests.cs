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
        public void Resolve_PlatformValueOverwrittenByMetadata_IsMetadataOnly()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A", Category = "Steam" },
                new AchievementDetail { ApiName = "B", Category = "Steam" }
            };
            var before = DataLayers.Take(rows);
            rows[0].Category = "Story";
            rows[1].Category = "Side";

            var layers = Resolve(before, rows);

            Assert.AreEqual(CustomizationArea.Categories, layers.Metadata & CustomizationArea.Categories);
            Assert.AreEqual(CustomizationArea.None, layers.Platform & CustomizationArea.Categories);
        }

        [TestMethod]
        public void Resolve_EveryAchievementInOneCategory_IsNoCategories()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A" },
                new AchievementDetail { ApiName = "B" }
            };
            var before = DataLayers.Take(rows);
            rows[0].Category = "Librarian: Tidy Up The Arcane Library";
            rows[1].Category = "Librarian: Tidy Up The Arcane Library";

            var layers = Resolve(before, rows);

            Assert.AreEqual(CustomizationArea.None, (layers.Platform | layers.Metadata) & CustomizationArea.Categories);
            Assert.AreEqual(CustomizationArea.None, DataLayers.Present(rows) & CustomizationArea.Categories);
        }

        [TestMethod]
        public void Resolve_GameNameAndDlcCategories_IsMetadata()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A" },
                new AchievementDetail { ApiName = "B" }
            };
            var before = DataLayers.Take(rows);
            rows[0].Category = "Game";
            rows[1].Category = "Game|Expansion";

            var layers = Resolve(before, rows);

            Assert.AreEqual(CustomizationArea.Categories, layers.Metadata & CustomizationArea.Categories);
            Assert.AreEqual(CustomizationArea.Categories, DataLayers.Present(rows) & CustomizationArea.Categories);
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
        public void Resolve_MetadataFillsOnlyBlanks_IsBothLayers()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A", TrophyType = "gold" },
                new AchievementDetail { ApiName = "B" }
            };
            var before = DataLayers.Take(rows);
            rows[1].TrophyType = "bronze";

            var layers = Resolve(before, rows);

            Assert.IsTrue((layers.Platform & CustomizationArea.TrophyTypes) != 0);
            Assert.IsTrue((layers.Metadata & CustomizationArea.TrophyTypes) != 0);
        }

        [TestMethod]
        public void Resolve_CarriedForwardPlatformValue_StaysPlatform()
        {
            var rows = new List<AchievementDetail> { new AchievementDetail { ApiName = "A", Points = 10 } };
            var before = DataLayers.Take(rows);
            rows[0].Points = 10;

            var layers = Resolve(before, rows);

            Assert.AreEqual(CustomizationArea.Points, layers.Platform & CustomizationArea.Points);
            Assert.AreEqual(CustomizationArea.None, layers.Metadata & CustomizationArea.Points);
        }

        [TestMethod]
        public void Resolve_MetadataBlankingAValue_IsNeitherLayer()
        {
            var rows = new List<AchievementDetail> { new AchievementDetail { ApiName = "A", Category = "Steam" } };
            var before = DataLayers.Take(rows);
            rows[0].Category = null;

            var layers = Resolve(before, rows);

            Assert.AreEqual(CustomizationArea.None, (layers.Platform | layers.Metadata) & CustomizationArea.Categories);
        }

        [TestMethod]
        public void Resolve_ProviderDeclaredNames_AreMetadataNotPlatform()
        {
            var rows = new List<AchievementDetail> { new AchievementDetail { ApiName = "A", Tags = "Missable" } };
            var before = DataLayers.Take(rows);
            var changed = DataLayers.ChangedSince(before, rows);
            changed[CustomizationArea.Tags].Add("A");

            var layers = DataLayers.Resolve(before, rows, changed, metadataCapstone: false);

            Assert.AreEqual(CustomizationArea.Tags, layers.Metadata & CustomizationArea.Tags);
            Assert.AreEqual(CustomizationArea.None, layers.Platform & CustomizationArea.Tags);
        }

        [TestMethod]
        public void Resolve_SourceChangingOnlyADescription_IsMetadataText()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A", DisplayName = "Dragon Slayer", Description = "kill dragon" },
                new AchievementDetail { ApiName = "B", DisplayName = "Untouched" }
            };
            var before = DataLayers.Take(rows);
            rows[0].Description = "Defeat the dragon.";

            var layers = Resolve(before, rows);

            Assert.AreEqual(CustomizationArea.Text, layers.Metadata & CustomizationArea.Text);
            Assert.AreEqual(CustomizationArea.Text, layers.Platform & CustomizationArea.Text, "B keeps the platform's text");
        }

        [TestMethod]
        public void Resolve_SourceTextEqualToThePlatforms_StaysPlatform()
        {
            var rows = new List<AchievementDetail> { new AchievementDetail { ApiName = "A", DisplayName = "Dragon Slayer " } };
            var before = DataLayers.Take(rows);
            rows[0].DisplayName = "Dragon Slayer";

            var layers = Resolve(before, rows);

            Assert.AreEqual(CustomizationArea.None, layers.Metadata & CustomizationArea.Text);
            Assert.AreEqual(CustomizationArea.Text, layers.Platform & CustomizationArea.Text);
        }

        [TestMethod]
        public void Take_IgnoresDerivedTags_AndCountsPlatinumAsCapstone()
        {
            var rows = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "A", Tags = "Hardcore", TrophyType = "platinum" }
            };

            var before = DataLayers.Take(rows);
            var layers = DataLayers.Resolve(before, rows, DataLayers.ChangedSince(before, rows), metadataCapstone: true);

            Assert.AreEqual(0, before.Values[CustomizationArea.Tags].Count);
            Assert.IsTrue((layers.Platform & CustomizationArea.Capstones) != 0);
            Assert.IsTrue((layers.Metadata & CustomizationArea.Capstones) != 0);
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

        private static (CustomizationArea Platform, CustomizationArea Metadata) Resolve(
            DataLayers.Snapshot before,
            List<AchievementDetail> after)
        {
            return DataLayers.Resolve(before, after, DataLayers.ChangedSince(before, after), metadataCapstone: false);
        }
    }
}
