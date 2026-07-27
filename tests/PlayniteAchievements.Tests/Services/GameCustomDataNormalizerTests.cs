using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class GameCustomDataNormalizerTests
    {
        [TestMethod]
        public void HasVisibleCustomization_EmptyData_ReturnsFalse()
        {
            Assert.IsFalse(GameCustomDataNormalizer.HasVisibleCustomization(new GameCustomDataFile()));
        }

        [TestMethod]
        public void HasVisibleCustomization_ExclusionsOnly_ReturnsFalse()
        {
            var data = new GameCustomDataFile
            {
                ExcludedFromRefreshes = true,
                ExcludedFromSummaries = true
            };

            Assert.IsFalse(GameCustomDataNormalizer.HasVisibleCustomization(data));
        }

        [TestMethod]
        public void HasVisibleCustomization_VisibleCustomizationKinds_ReturnTrue()
        {
            var cases = new[]
            {
                new GameCustomDataFile { UseSeparateLockedIconsOverride = true },
                new GameCustomDataFile { ManualCapstoneApiName = "capstone" },
                new GameCustomDataFile { AchievementOrder = new List<string> { "ach_one" } },
                new GameCustomDataFile
                {
                    AchievementCategoryOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "Category"
                    }
                },
                new GameCustomDataFile
                {
                    AchievementCategoryTypeOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "DLC"
                    }
                },
                new GameCustomDataFile
                {
                    AchievementCategoryOrder = new List<string> { "DLC" }
                },
                new GameCustomDataFile
                {
                    AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>
                    {
                        ["DLC"] = new CategoryImageOverrideData
                        {
                            Art = "https://example.com/art.png"
                        }
                    }
                },
                new GameCustomDataFile
                {
                    GameSummaryCategory = new GameSummaryCategoryData
                    {
                        Label = "DLC"
                    }
                },
                new GameCustomDataFile
                {
                    FilteredAchievementApiNames = new List<string> { "ach_one" }
                },
                new GameCustomDataFile
                {
                    SummaryFilteredAchievementApiNames = new List<string> { "ach_one" }
                },
                new GameCustomDataFile
                {
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "https://example.com/unlocked.png"
                    }
                },
                new GameCustomDataFile
                {
                    AchievementLockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "https://example.com/locked.png"
                    }
                },
                new GameCustomDataFile
                {
                    AchievementNotes = new Dictionary<string, string>
                    {
                        ["ach_one"] = "Use this route"
                    }
                },
                new GameCustomDataFile { RetroAchievementsGameIdOverride = 42 },
                new GameCustomDataFile { XeniaTitleIdOverride = "4d5307e6" },
                new GameCustomDataFile { ShadPS4MatchIdOverride = "npwr12345_00" },
                new GameCustomDataFile { ForceUseExophase = true },
                new GameCustomDataFile { ExophaseSlugOverride = "game-slug" },
                new GameCustomDataFile
                {
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Steam",
                        Value = "480"
                    }
                },
                new GameCustomDataFile
                {
                    ManualLink = new ManualAchievementLink
                    {
                        SourceKey = "Steam",
                        SourceGameId = "123"
                    }
                }
            };

            foreach (var item in cases)
            {
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(item));
            }
        }

        [TestMethod]
        public void NormalizeInternal_CanonicalProviderOverride_NormalizesAndClearsLegacyFields()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "steam",
                        Value = " 480 "
                    },
                    RetroAchievementsGameIdOverride = 12345,
                    XeniaTitleIdOverride = "0x4d5307e6",
                    ShadPS4MatchIdOverride = "npwr12345_00",
                    ForceUseExophase = true,
                    ExophaseSlugOverride = "legacy-slug"
                },
                gameId);

            Assert.AreEqual(5, normalized.SchemaVersion);
            AssertProviderOverride(normalized, "Steam", "480");
            AssertLegacyProviderFieldsCleared(normalized);
        }

        [TestMethod]
        public void NormalizeInternal_AchievementNotes_NormalizesAndCapsValues()
        {
            var gameId = Guid.NewGuid();
            var longNote = new string('x', AchievementNoteHelper.MaxNoteLength + 50);
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementNotes = new Dictionary<string, string>
                    {
                        [" ach_one "] = " first note ",
                        ["ACH_ONE"] = " second note\r\nline ",
                        ["blank"] = " ",
                        [" "] = "ignored",
                        ["ach_long"] = longNote
                    }
                },
                gameId);

            Assert.AreEqual(2, normalized.AchievementNotes.Count);
            Assert.AreEqual("second note\nline", normalized.AchievementNotes["ach_one"]);
            Assert.AreEqual(AchievementNoteHelper.MaxNoteLength, normalized.AchievementNotes["ach_long"].Length);
            Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(normalized));
        }

        [TestMethod]
        public void NormalizeInternal_CategoryMetadata_NormalizesOrderAndImages()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementCategoryOrder = new List<string>
                    {
                        " DLC ",
                        "Base",
                        "dlc"
                    },
                    AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>
                    {
                        [" DLC "] = new CategoryImageOverrideData
                        {
                            Art = " https://example.com/art.png "
                        },
                        ["Base"] = new CategoryImageOverrideData
                        {
                            Art = "managed://art.png"
                        },
                        ["Empty"] = new CategoryImageOverrideData
                        {
                            Art = " "
                        }
                    }
                },
                gameId);

            CollectionAssert.AreEqual(
                new[] { "DLC", "Base" },
                normalized.AchievementCategoryOrder);
            Assert.AreEqual(2, normalized.AchievementCategoryImageOverrides.Count);
            Assert.AreEqual("https://example.com/art.png", normalized.AchievementCategoryImageOverrides["DLC"].Art);
            Assert.AreEqual("managed://art.png", normalized.AchievementCategoryImageOverrides["Base"].Art);
            Assert.IsFalse(normalized.AchievementCategoryImageOverrides.ContainsKey("Empty"));
            Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(normalized));
        }

        [TestMethod]
        public void HasInternalData_GameSummaryCategoryOnly_ReturnsTrue()
        {
            var data = new GameCustomDataFile
            {
                GameSummaryCategory = new GameSummaryCategoryData
                {
                    Label = "DLC",
                    ProviderLabel = "Phantom Liberty"
                }
            };

            Assert.IsTrue(GameCustomDataNormalizer.HasInternalData(data));
            Assert.IsTrue(GameCustomDataNormalizer.HasPortableData(data));
        }

        [TestMethod]
        public void NormalizeInternal_GameSummaryCategory_NormalizesLabelsAndDefaultsProviderLabel()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    GameSummaryCategory = new GameSummaryCategoryData
                    {
                        Label = " DLC ",
                        ProviderLabel = " "
                    }
                },
                gameId);

            Assert.IsNotNull(normalized.GameSummaryCategory);
            Assert.AreEqual("DLC", normalized.GameSummaryCategory.Label);
            Assert.AreEqual("DLC", normalized.GameSummaryCategory.ProviderLabel);
        }

        [TestMethod]
        public void NormalizeInternal_GameSummaryCategory_BlankLabel_DropsSelection()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    GameSummaryCategory = new GameSummaryCategoryData
                    {
                        Label = " ",
                        ProviderLabel = "Phantom Liberty"
                    }
                },
                gameId);

            Assert.IsNull(normalized.GameSummaryCategory);
        }

        [TestMethod]
        public void NormalizePortable_GameSummaryCategory_KeepsDistinctProviderLabel()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizePortable(
                new GameCustomDataPortableFile
                {
                    PlayniteGameId = gameId,
                    GameSummaryCategory = new GameSummaryCategoryData
                    {
                        Label = "My Renamed DLC",
                        ProviderLabel = " Phantom Liberty "
                    }
                },
                gameId);

            Assert.IsNotNull(normalized.GameSummaryCategory);
            Assert.AreEqual("My Renamed DLC", normalized.GameSummaryCategory.Label);
            Assert.AreEqual("Phantom Liberty", normalized.GameSummaryCategory.ProviderLabel);
        }

        [TestMethod]
        public void MergePreferExisting_GameSummaryCategory_PrefersExistingThenLegacy()
        {
            var existing = new GameCustomDataFile
            {
                GameSummaryCategory = new GameSummaryCategoryData
                {
                    Label = "Existing",
                    ProviderLabel = "Existing"
                }
            };
            var legacy = new GameCustomDataFile
            {
                GameSummaryCategory = new GameSummaryCategoryData
                {
                    Label = "Legacy",
                    ProviderLabel = "Legacy"
                }
            };

            var merged = GameCustomDataNormalizer.MergePreferExisting(existing, legacy);
            Assert.AreEqual("Existing", merged.GameSummaryCategory.Label);

            var mergedFromLegacy = GameCustomDataNormalizer.MergePreferExisting(new GameCustomDataFile(), legacy);
            Assert.AreEqual("Legacy", mergedFromLegacy.GameSummaryCategory.Label);
        }

        [TestMethod]
        public void GameCustomDataFiles_CloneAndPortableRoundTrip_DeepCopyGameSummaryCategory()
        {
            var internalData = new GameCustomDataFile
            {
                PlayniteGameId = Guid.NewGuid(),
                GameSummaryCategory = new GameSummaryCategoryData
                {
                    Label = "DLC",
                    ProviderLabel = "Phantom Liberty"
                }
            };

            var internalClone = internalData.Clone();
            internalClone.GameSummaryCategory.Label = "Changed";
            Assert.AreEqual("DLC", internalData.GameSummaryCategory.Label);

            var portable = internalData.ToPortable();
            portable.GameSummaryCategory.Label = "Portable";
            Assert.AreEqual("DLC", internalData.GameSummaryCategory.Label);

            var portableClone = portable.Clone();
            portableClone.GameSummaryCategory.Label = "Clone";
            Assert.AreEqual("Portable", portable.GameSummaryCategory.Label);

            var imported = GameCustomDataFile.FromPortable(portable, Guid.NewGuid(), false, false);
            Assert.AreEqual("Portable", imported.GameSummaryCategory.Label);
            Assert.AreEqual("Phantom Liberty", imported.GameSummaryCategory.ProviderLabel);
            imported.GameSummaryCategory.Label = "Import";
            Assert.AreEqual("Portable", portable.GameSummaryCategory.Label);
        }

        [TestMethod]
        public void GameCustomDataFiles_CloneAndPortableRoundTrip_DeepCopyCategoryMetadata()
        {
            var gameId = Guid.NewGuid();
            var internalData = new GameCustomDataFile
            {
                PlayniteGameId = gameId,
                AchievementCategoryOrder = new List<string> { "DLC" },
                AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>
                {
                    ["DLC"] = new CategoryImageOverrideData
                    {
                        Art = "art.png"
                    }
                }
            };

            var internalClone = internalData.Clone();
            internalClone.AchievementCategoryOrder[0] = "Base";
            internalClone.AchievementCategoryImageOverrides["DLC"].Art = "changed.png";
            Assert.AreEqual("DLC", internalData.AchievementCategoryOrder[0]);
            Assert.AreEqual("art.png", internalData.AchievementCategoryImageOverrides["DLC"].Art);

            var portable = internalData.ToPortable();
            portable.AchievementCategoryOrder[0] = "Portable";
            portable.AchievementCategoryImageOverrides["DLC"].Art = "portable.png";
            Assert.AreEqual("DLC", internalData.AchievementCategoryOrder[0]);
            Assert.AreEqual("art.png", internalData.AchievementCategoryImageOverrides["DLC"].Art);

            var portableClone = portable.Clone();
            portableClone.AchievementCategoryOrder[0] = "Clone";
            portableClone.AchievementCategoryImageOverrides["DLC"].Art = "clone.png";
            Assert.AreEqual("Portable", portable.AchievementCategoryOrder[0]);
            Assert.AreEqual("portable.png", portable.AchievementCategoryImageOverrides["DLC"].Art);

            var imported = GameCustomDataFile.FromPortable(portable, Guid.NewGuid(), false, false);
            imported.AchievementCategoryOrder[0] = "Import";
            imported.AchievementCategoryImageOverrides["DLC"].Art = "import.png";
            Assert.AreEqual("Portable", portable.AchievementCategoryOrder[0]);
            Assert.AreEqual("portable.png", portable.AchievementCategoryImageOverrides["DLC"].Art);
        }

        [TestMethod]
        public void GameCustomDataFiles_CloneAndPortableRoundTrip_DeepCopyAchievementNotes()
        {
            var gameId = Guid.NewGuid();
            var internalData = new GameCustomDataFile
            {
                PlayniteGameId = gameId,
                AchievementNotes = new Dictionary<string, string>
                {
                    ["ach_one"] = "note one"
                }
            };

            var internalClone = internalData.Clone();
            internalClone.AchievementNotes["ach_one"] = "changed";
            Assert.AreEqual("note one", internalData.AchievementNotes["ach_one"]);

            var portable = internalData.ToPortable();
            portable.AchievementNotes["ach_one"] = "portable changed";
            Assert.AreEqual("note one", internalData.AchievementNotes["ach_one"]);

            var portableClone = portable.Clone();
            portableClone.AchievementNotes["ach_one"] = "clone changed";
            Assert.AreEqual("portable changed", portable.AchievementNotes["ach_one"]);

            var imported = GameCustomDataFile.FromPortable(portable, Guid.NewGuid(), false, false);
            imported.AchievementNotes["ach_one"] = "import changed";
            Assert.AreEqual("portable changed", portable.AchievementNotes["ach_one"]);
        }

        [TestMethod]
        public void AchievementDetailHydrator_AppliesNotesWithoutChangingCountsOrFilters()
        {
            var gameId = Guid.NewGuid();
            var details = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "ach_one", Unlocked = true },
                new AchievementDetail { ApiName = "ach_two", Unlocked = false }
            };
            var customData = new ResolvedGameCustomData
            {
                AchievementNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ACH_ONE"] = "route note"
                }
            };

            var hydrator = new AchievementDetailHydrator(new PersistedSettings());
            hydrator.HydrateAllWithCapstoneOverride(details, gameId, "Steam", customData);

            Assert.AreEqual("route note", details[0].AchievementNote);
            Assert.IsNull(details[1].AchievementNote);
            Assert.AreEqual(2, details.Count);
            Assert.AreEqual(1, details.Count(a => a.Unlocked));
            Assert.IsFalse(details.Any(a => a.IsFiltered));
            Assert.IsFalse(details.Any(a => a.IsFilteredFromSummaries));
        }

        [TestMethod]
        public void AchievementDetailHydrator_KeepsProviderCategoryStableAcrossRenames()
        {
            var gameId = Guid.NewGuid();
            var details = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "dlc_ach", Category = "Phantom Liberty", CategoryType = "DLC" }
            };
            var renamed = new ResolvedGameCustomData
            {
                AchievementCategoryOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dlc_ach"] = "My Renamed DLC"
                }
            };

            var hydrator = new AchievementDetailHydrator(new PersistedSettings());
            hydrator.HydrateAllWithCapstoneOverride(details, gameId, "Steam", renamed);

            Assert.AreEqual("My Renamed DLC", details[0].Category);
            Assert.AreEqual("Phantom Liberty", details[0].ProviderCategory);

            // Re-hydrating the same mutated instance without the rename override must
            // restore the provider label rather than treat the rename as provider data.
            hydrator.HydrateAllWithCapstoneOverride(details, gameId, "Steam", new ResolvedGameCustomData());

            Assert.AreEqual("Phantom Liberty", details[0].Category);
            Assert.AreEqual("Phantom Liberty", details[0].ProviderCategory);
        }

        [TestMethod]
        public void NormalizeInternal_ExtractsLegacyFilterCategoryTypes()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    FilteredAchievementApiNames = new List<string> { "existing_filtered" },
                    SummaryFilteredAchievementApiNames = new List<string> { "existing_summary" },
                    AchievementCategoryTypeOverrides = new Dictionary<string, string>
                    {
                        ["ach_filtered"] = "dlc | ignored",
                        ["ach_summary"] = "summary ignored",
                        ["ach_both"] = "ignored | summaryignored",
                        ["ach_normal"] = "base | stackable"
                    }
                },
                gameId);

            CollectionAssert.AreEquivalent(
                new[] { "existing_filtered", "ach_filtered", "ach_both" },
                normalized.FilteredAchievementApiNames);
            CollectionAssert.AreEquivalent(
                new[] { "existing_summary", "ach_summary" },
                normalized.SummaryFilteredAchievementApiNames);
            Assert.AreEqual("DLC", normalized.AchievementCategoryTypeOverrides["ach_filtered"]);
            Assert.AreEqual("Base|Stackable", normalized.AchievementCategoryTypeOverrides["ach_normal"]);
            Assert.IsFalse(normalized.AchievementCategoryTypeOverrides.ContainsKey("ach_summary"));
            Assert.IsFalse(normalized.AchievementCategoryTypeOverrides.ContainsKey("ach_both"));
        }

        [TestMethod]
        public void NormalizeInternal_InvalidCanonicalProviderOverride_DropsOverride()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Steam",
                        Value = "0"
                    }
                },
                gameId);

            Assert.IsNull(normalized.ProviderOverride);
        }

        [TestMethod]
        public void NormalizeInternal_LegacyProviderFields_NormalizeIntoCanonicalOverride()
        {
            AssertLegacyProviderOverride(
                new GameCustomDataFile { RetroAchievementsGameIdOverride = 42 },
                "RetroAchievements",
                "42");
            AssertLegacyProviderOverride(
                new GameCustomDataFile { XeniaTitleIdOverride = "0x4d5307e6" },
                "Xenia",
                "4D5307E6");
            AssertLegacyProviderOverride(
                new GameCustomDataFile { ShadPS4MatchIdOverride = "npwr12345_00" },
                "ShadPS4",
                "NPWR12345_00");
            AssertLegacyProviderOverride(
                new GameCustomDataFile { ForceUseExophase = true },
                "Exophase",
                null);
            AssertLegacyProviderOverride(
                new GameCustomDataFile { ExophaseSlugOverride = " exophase-slug " },
                "Exophase",
                "exophase-slug");
        }

        [TestMethod]
        public void NormalizeInternal_ExophaseProviderOverride_AllowsEmptyValue()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Exophase",
                        Value = " "
                    }
                },
                gameId);

            AssertProviderOverride(normalized, "Exophase", null);
        }

        [TestMethod]
        public void NormalizeInternal_FfxivProviderOverride_PreservesKeyWithNullValue()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "ffxiv",
                        Value = null
                    }
                },
                gameId);

            AssertProviderOverride(normalized, "FFXIV", null);
        }

        [TestMethod]
        public void NormalizeInternal_Rpcs3ProviderOverride_NormalizesCanonicalValue()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "rpcs3",
                        Value = " npwr12345_00 "
                    }
                },
                gameId);

            AssertProviderOverride(normalized, "RPCS3", "NPWR12345_00");
        }

        private static void AssertLegacyProviderOverride(
            GameCustomDataFile data,
            string providerKey,
            string value)
        {
            var gameId = Guid.NewGuid();
            data.PlayniteGameId = gameId;

            var normalized = GameCustomDataNormalizer.NormalizeInternal(data, gameId);

            AssertProviderOverride(normalized, providerKey, value);
            AssertLegacyProviderFieldsCleared(normalized);
        }

        private static void AssertProviderOverride(
            GameCustomDataFile data,
            string providerKey,
            string value)
        {
            Assert.IsNotNull(data.ProviderOverride);
            Assert.AreEqual(providerKey, data.ProviderOverride.ProviderKey);
            Assert.AreEqual(value, data.ProviderOverride.Value);
        }

        private static void AssertLegacyProviderFieldsCleared(GameCustomDataFile data)
        {
            Assert.IsNull(data.RetroAchievementsGameIdOverride);
            Assert.IsNull(data.XeniaTitleIdOverride);
            Assert.IsNull(data.ShadPS4MatchIdOverride);
            Assert.IsNull(data.ForceUseExophase);
            Assert.IsNull(data.ExophaseSlugOverride);
        }
    }
}
