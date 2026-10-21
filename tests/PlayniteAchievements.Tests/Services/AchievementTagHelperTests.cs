using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class AchievementTagHelperTests
    {
        [TestCleanup]
        public void ResetRegistry()
        {
            AchievementTagRegistry.SetGlobal(AchievementTagHelper.CreateDefaultDefinitions());
            AchievementTagRegistry.SetGameResolver(null);
        }

        [TestMethod]
        public void Normalize_CanonicalizesBuiltInAliasesInListOrder()
        {
            Assert.AreEqual("Progression|SideQuest|WinCondition", AchievementTagHelper.Normalize("win condition|side progression|story"));
            Assert.AreEqual("Collectable|Missable", AchievementTagHelper.Normalize("miss-able, collectible"));
            Assert.AreEqual("Softcore", AchievementTagHelper.Normalize("casual"));
        }

        [TestMethod]
        public void Parse_KeepsUserKeysAndDropsKindsAndDefault()
        {
            CollectionAssert.AreEqual(
                new[] { "Missable", "u-12ab34cd" },
                AchievementTagHelper.Parse("u-12ab34cd|Base|Default|Missable"));
        }

        [TestMethod]
        public void WithTag_IgnoresDerivedKeys()
        {
            Assert.AreEqual("Missable", AchievementTagHelper.WithTag("Missable", "Hardcore", include: true));
            Assert.AreEqual("Missable|Softcore", AchievementTagHelper.WithTag("Missable|Softcore", "Softcore", include: false));
            Assert.IsNull(AchievementTagHelper.WithTag("Missable", "Missable", include: false));
        }

        [TestMethod]
        public void ApplyOverride_TakesDerivedKeysFromTheProviderOnly()
        {
            Assert.AreEqual("Missable|Softcore", AchievementTagHelper.ApplyOverride("Progression|Softcore", "Missable|Hardcore"));
            Assert.AreEqual("Hardcore", AchievementTagHelper.ApplyOverride("Progression|Hardcore", string.Empty), "a cleared override keeps the unlock mode");
            Assert.AreEqual("Progression|Hardcore", AchievementTagHelper.ApplyOverride("Progression|Hardcore", null));
        }

        [TestMethod]
        public void OverrideOrNull_StoresEmptyForAClearAndNullForNoChange()
        {
            Assert.IsNull(AchievementTagHelper.OverrideOrNull("Progression|Hardcore", "progression"));
            Assert.AreEqual(string.Empty, AchievementTagHelper.OverrideOrNull(null, "Progression"), "removing the provider's tags is a real override");
            Assert.AreEqual("Missable", AchievementTagHelper.OverrideOrNull("Missable|Softcore", "Softcore"));
        }

        [TestMethod]
        public void NormalizeDefinitions_DedupesAndKeepsSystemTags()
        {
            var normalized = AchievementTagHelper.NormalizeDefinitions(
                new[]
                {
                    new AchievementTagDefinition { Key = "u-1", Name = " Boss ", Color = "#ff0000" },
                    new AchievementTagDefinition { Key = "U-1", Name = "Duplicate" },
                    new AchievementTagDefinition { Key = "Base" },
                    new AchievementTagDefinition { Key = "Hardcore" },
                    new AchievementTagDefinition { Key = "bad|key" },
                    new AchievementTagDefinition { Key = "u-2", Color = "red" }
                },
                ensureSystemTags: true);

            CollectionAssert.AreEqual(
                new[] { "u-1", "u-2", "Missable", "Unobtainable" },
                normalized.Select(definition => definition.Key).ToList());
            Assert.AreEqual("Boss", normalized[0].Name);
            Assert.AreEqual("#FF0000", normalized[0].Color);
            Assert.IsNull(normalized[1].Color, "a malformed color falls back to the default");
        }

        [TestMethod]
        public void Registry_ResolvesGameTagsBeforeGlobalOnesAndHidesDeletedKeys()
        {
            var gameId = Guid.NewGuid();
            AchievementTagRegistry.SetGlobal(new[]
            {
                new AchievementTagDefinition { Key = "Missable", Name = "Can Miss" },
                new AchievementTagDefinition { Key = "u-global", Name = "Global" }
            });
            AchievementTagRegistry.SetGameResolver(id => id == gameId
                ? new[] { new AchievementTagDefinition { Key = "u-game", Name = "Game Only" } }
                : Array.Empty<AchievementTagDefinition>());

            Assert.AreEqual("Can Miss, Global, Game Only", AchievementTagHelper.ToDisplayText("u-game|u-global|Missable|u-deleted|Hardcore", gameId));
            Assert.AreEqual("Can Miss, Global", AchievementTagHelper.ToDisplayText("u-game|u-global|Missable"), "another game's tag does not resolve");
        }

        [TestMethod]
        public void ClassificationFilter_MatchesKindsAndTags()
        {
            Assert.IsTrue(AchievementClassificationFilter.Matches("DLC|Update", null, "Update"));
            Assert.IsTrue(AchievementClassificationFilter.Matches(null, "SideQuest", "SideProgression"), "the shipped alias still filters");
            Assert.IsFalse(AchievementClassificationFilter.Matches("Base", "Missable", "DLC"));
            Assert.IsTrue(AchievementClassificationFilter.IsFilterKey("u-anything"));
            Assert.IsFalse(AchievementClassificationFilter.IsFilterKey("Unlocked"));
        }

        [TestMethod]
        public void LegacyCategoryTypes_KeepsTagsAndDropsKinds()
        {
            var entry = new AchievementOverride();
            LegacyCategoryTypes.ApplyTo(entry, "Base|Missable|Hardcore");
            Assert.IsNull(entry.LegacyCategoryType);
            Assert.AreEqual("Missable", entry.Tags);
            Assert.IsFalse(entry.ClearTags);

            var cleared = new AchievementOverride();
            LegacyCategoryTypes.ApplyTo(cleared, "Default");
            Assert.IsTrue(cleared.ClearTags, "a Default override cleared every tag");

            var unlockModeOnly = new AchievementOverride();
            LegacyCategoryTypes.ApplyTo(unlockModeOnly, "Hardcore");
            Assert.IsTrue(unlockModeOnly.IsEmpty, "an unlock mode alone overrode nothing");
        }
    }
}
