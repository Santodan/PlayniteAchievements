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
        public void Registry_ShowsSourceKeysNoListDefines_AsTheGamesOwn()
        {
            var gameId = Guid.NewGuid();
            AchievementTagRegistry.SetGlobal(new[] { new AchievementTagDefinition { Key = "Missable" } });
            AchievementTagRegistry.SetGameResolver(id => id == gameId
                ? new[] { new AchievementTagDefinition { Key = "Cooperative", Name = "Co-op" } }
                : Array.Empty<AchievementTagDefinition>());

            // Collectable was deleted from the global list; its key stays on the achievement, and
            // shows after the global and game tags with the other keys only the data defines.
            Assert.AreEqual("Missable, Co-op, Collectable, Buggy+", AchievementTagHelper.ToDisplayText("BuggyPlus|Cooperative|Collectable|Missable|u-deleted", gameId));
            Assert.IsTrue(AchievementTagRegistry.IsImplicit("BuggyPlus", gameId));
            Assert.IsFalse(AchievementTagRegistry.IsImplicit("Cooperative", gameId), "the game defines it");
            Assert.IsFalse(AchievementTagRegistry.IsImplicit("u-deleted", gameId), "a deleted user tag stays deleted");
            Assert.IsFalse(AchievementTagRegistry.IsImplicit("Hardcore", gameId));
            CollectionAssert.AreEqual(
                new[] { "Missable", "Cooperative", "BuggyPlus" },
                AchievementTagRegistry.GetAssignableKeys(gameId, new[] { "BuggyPlus", "Hardcore", "u-deleted" }).ToList());
            Assert.IsFalse(AchievementClassificationFilter.IsFilterKey("Unlocked"), "an unknown key is not a filter key");
        }

        [TestMethod]
        public void Registry_PrefersTheGlobalDefinition_OverAGamesCopy()
        {
            var gameId = Guid.NewGuid();
            AchievementTagRegistry.SetGlobal(new[] { new AchievementTagDefinition { Key = "Collectable", Name = "Global" } });
            AchievementTagRegistry.SetGameResolver(id => new[] { new AchievementTagDefinition { Key = "Collectable", Name = "Game copy" } });

            Assert.AreEqual("Global", AchievementTagRegistry.GetName("Collectable", gameId));
            Assert.AreEqual("Global", AchievementTagHelper.ToDisplayText("Collectable", gameId));
        }

        [TestMethod]
        public void Registry_LeavesHiddenTagsOutOfViews_ButNotTheEditor()
        {
            var gameId = Guid.NewGuid();
            AchievementTagRegistry.SetGlobal(new[]
            {
                new AchievementTagDefinition { Key = "Missable", Hidden = true },
                new AchievementTagDefinition { Key = "u-global", Name = "Global" }
            });
            AchievementTagRegistry.SetGameResolver(id => id == gameId
                ? new[] { new AchievementTagDefinition { Key = "u-game", Name = "Game Only", Hidden = true } }
                : Array.Empty<AchievementTagDefinition>());

            const string tags = "Missable|u-global|u-game";
            Assert.AreEqual("Global", AchievementTagHelper.ToDisplayText(tags, gameId));
            Assert.AreEqual("Missable, Global, Game Only", AchievementTagHelper.ToDisplayText(tags, gameId, includeHidden: true));
            Assert.IsTrue(AchievementTagHelper.IsMissable(tags), "hiding the tag keeps its behavior");
            Assert.IsTrue(AchievementTagRegistry.IsHidden("u-game", gameId));
            Assert.IsFalse(AchievementTagRegistry.IsHidden("u-global", gameId));

            CollectionAssert.AreEqual(
                new[] { "Missable", "u-global", "u-game" },
                AchievementTagRegistry.GetAssignableKeys(gameId).ToList());
            CollectionAssert.AreEqual(new[] { "u-global" }, AchievementTagRegistry.GetShownKeys(gameId).ToList());
            CollectionAssert.AreEqual(
                new[] { "u-global" },
                AchievementClassificationFilter.GetPresentKeys(new[] { tags }, value => value, gameId));
            Assert.IsFalse(AchievementClassificationFilter.GetFilterKeys(gameId).Contains("Missable"));
        }

        [TestMethod]
        public void Registry_RaisesChanged_WhenOnlyVisibilityChanges()
        {
            AchievementTagRegistry.SetGlobal(new[] { new AchievementTagDefinition { Key = "Missable" } });
            var raised = 0;
            EventHandler<AchievementTagsChangedEventArgs> handler = (_, __) => raised++;
            AchievementTagRegistry.Changed += handler;
            try
            {
                AchievementTagRegistry.SetGlobal(new[] { new AchievementTagDefinition { Key = "Missable", Hidden = true } });
            }
            finally
            {
                AchievementTagRegistry.Changed -= handler;
            }

            Assert.AreEqual(1, raised);
            Assert.IsTrue(AchievementTagHelper.NormalizeDefinitions(AchievementTagRegistry.Global, ensureSystemTags: true)[0].Hidden,
                "normalizing keeps the flag");
        }

        [TestMethod]
        public void DefaultName_SpellsOutSourceKeys()
        {
            Assert.AreEqual("Buggy+", AchievementTagHelper.GetDefaultName("BuggyPlus"));
            Assert.AreEqual("Cumulative-", AchievementTagHelper.GetDefaultName("CumulativeMinus"));
            Assert.AreEqual("Partly Discontinued", AchievementTagHelper.GetDefaultName("PartlyDiscontinued"));
            Assert.AreEqual("Online Offline", AchievementTagHelper.GetDefaultName("OnlineOffline"));
            Assert.AreEqual("u-1a2b3c4d", AchievementTagHelper.GetDefaultName("u-1a2b3c4d"));
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
