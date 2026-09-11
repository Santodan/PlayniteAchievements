using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers the schema 7 -> 8 fold of the per-achievement parallel maps into one
    /// <see cref="AchievementOverride"/> record. This migration rewrites authorship that cannot be
    /// re-fetched, so every branch of the fold is pinned here.
    /// </summary>
    [TestClass]
    public class AchievementOverrideMigrationTests
    {
        private static readonly Guid GameId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private static GameCustomDataFile Normalize(GameCustomDataFile data) =>
            GameCustomDataNormalizer.NormalizeInternal(data, GameId);

        [TestMethod]
        public void Normalize_EmptyRecord_LeavesNoOverrides()
        {
            var result = Normalize(new GameCustomDataFile());

            Assert.IsNull(result.AchievementOverrides);
        }

        [TestMethod]
        public void Normalize_StampsCurrentSchemaVersion()
        {
            var result = Normalize(new GameCustomDataFile { SchemaVersion = 7 });

            Assert.AreEqual(GameCustomDataNormalizer.CurrentSchemaVersion, result.SchemaVersion);
            Assert.AreEqual(8, GameCustomDataNormalizer.CurrentSchemaVersion);
        }

        [TestMethod]
        public void Normalize_LegacyNoteOnly_FoldsIntoRecord()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "remember this" }
            });

            Assert.IsNotNull(result.AchievementOverrides);
            Assert.AreEqual("remember this", result.AchievementOverrides["ach_one"].Note);
        }

        [TestMethod]
        public void Normalize_LegacyCategoryOnly_FoldsIntoRecord()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementCategoryOverrides = new Dictionary<string, string> { ["ach_one"] = "Story" }
            });

            Assert.AreEqual("Story", result.AchievementOverrides["ach_one"].Category);
        }

        [TestMethod]
        public void Normalize_LegacyCategoryTypeOnly_FoldsIntoRecord()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementCategoryTypeOverrides = new Dictionary<string, string> { ["ach_one"] = "DLC" }
            });

            Assert.AreEqual(
                AchievementCategoryTypeHelper.Normalize("DLC"),
                result.AchievementOverrides["ach_one"].CategoryType);
        }

        [TestMethod]
        public void Normalize_LegacyIconOverridesOnly_FoldIntoRecord()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementUnlockedIconOverrides = new Dictionary<string, string> { ["ach_one"] = "unlocked.png" },
                AchievementLockedIconOverrides = new Dictionary<string, string> { ["ach_one"] = "locked.png" }
            });

            var entry = result.AchievementOverrides["ach_one"];
            Assert.AreEqual("unlocked.png", entry.UnlockedIconPath);
            Assert.AreEqual("locked.png", entry.LockedIconPath);
        }

        [TestMethod]
        public void Normalize_AllLegacyMapsPopulated_ProduceOneRecordPerAchievement()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementCategoryOverrides = new Dictionary<string, string> { ["ach_one"] = "Story" },
                AchievementCategoryTypeOverrides = new Dictionary<string, string> { ["ach_one"] = "DLC" },
                AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "note" },
                AchievementUnlockedIconOverrides = new Dictionary<string, string> { ["ach_one"] = "unlocked.png" },
                AchievementLockedIconOverrides = new Dictionary<string, string> { ["ach_one"] = "locked.png" }
            });

            Assert.AreEqual(1, result.AchievementOverrides.Count);
            var entry = result.AchievementOverrides["ach_one"];
            Assert.AreEqual("Story", entry.Category);
            Assert.AreEqual("note", entry.Note);
            Assert.AreEqual("unlocked.png", entry.UnlockedIconPath);
            Assert.AreEqual("locked.png", entry.LockedIconPath);
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.CategoryType));
        }

        [TestMethod]
        public void Normalize_LegacyMapsDisagreeOnCase_CollapseToOneEntry()
        {
            // The legacy maps are OrdinalIgnoreCase, so the folded record must not split an
            // achievement into two rows that later resolve inconsistently.
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementNotes = new Dictionary<string, string> { ["ACH_ONE"] = "note" },
                AchievementCategoryOverrides = new Dictionary<string, string> { ["ach_one"] = "Story" }
            });

            Assert.AreEqual(1, result.AchievementOverrides.Count);
            var entry = result.AchievementOverrides["aCh_OnE"];
            Assert.AreEqual("note", entry.Note);
            Assert.AreEqual("Story", entry.Category);
        }

        [TestMethod]
        public void Normalize_ExistingRecordWins_OverLegacyMirror()
        {
            // A record written directly must not be clobbered by a stale legacy value.
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride { Note = "from record" }
                },
                AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "from legacy" }
            });

            Assert.AreEqual("from record", result.AchievementOverrides["ach_one"].Note);
        }

        [TestMethod]
        public void Normalize_LegacyFillsOnlyFieldsTheRecordLacks()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride { Note = "from record" }
                },
                AchievementCategoryOverrides = new Dictionary<string, string> { ["ach_one"] = "Story" }
            });

            var entry = result.AchievementOverrides["ach_one"];
            Assert.AreEqual("from record", entry.Note);
            Assert.AreEqual("Story", entry.Category);
        }

        [TestMethod]
        public void Normalize_NewlyEditableFields_SurviveNormalization()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride
                    {
                        DisplayName = "Renamed",
                        Description = "Rewritten",
                        Points = 42,
                        TrophyType = "Gold"
                    }
                }
            });

            var entry = result.AchievementOverrides["ach_one"];
            Assert.AreEqual("Renamed", entry.DisplayName);
            Assert.AreEqual("Rewritten", entry.Description);
            Assert.AreEqual(42, entry.Points);
            Assert.AreEqual("gold", entry.TrophyType);
        }

        [TestMethod]
        public void Normalize_InvalidTrophyTypeAndNegativePoints_AreDropped()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride
                    {
                        DisplayName = "Renamed",
                        Points = -5,
                        TrophyType = "diamond"
                    }
                }
            });

            var entry = result.AchievementOverrides["ach_one"];
            Assert.IsNull(entry.Points);
            Assert.IsNull(entry.TrophyType);
            Assert.AreEqual("Renamed", entry.DisplayName);
        }

        [TestMethod]
        public void Normalize_EmptyRecordEntry_IsPruned()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride(),
                    ["ach_two"] = new AchievementOverride { Note = "kept" }
                }
            });

            Assert.AreEqual(1, result.AchievementOverrides.Count);
            Assert.IsTrue(result.AchievementOverrides.ContainsKey("ach_two"));
        }

        [TestMethod]
        public void Normalize_LongNote_IsTruncatedToTheNoteCap()
        {
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementNotes = new Dictionary<string, string>
                {
                    ["ach_one"] = new string('x', AchievementNoteHelper.MaxNoteLength + 500)
                }
            });

            Assert.AreEqual(
                AchievementNoteHelper.MaxNoteLength,
                result.AchievementOverrides["ach_one"].Note.Length);
        }

        [TestMethod]
        public void Normalize_UnicodeText_IsPreserved()
        {
            const string note = "日本語のメモ — ok";
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementNotes = new Dictionary<string, string> { ["ach_one"] = note },
                AchievementCategoryOverrides = new Dictionary<string, string> { ["ach_one"] = "История" }
            });

            var entry = result.AchievementOverrides["ach_one"];
            Assert.AreEqual(note, entry.Note);
            Assert.AreEqual("История", entry.Category);
        }

        [TestMethod]
        public void Normalize_IsIdempotent()
        {
            var once = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 7,
                AchievementCategoryOverrides = new Dictionary<string, string> { ["ach_one"] = "Story" },
                AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "note" },
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_two"] = new AchievementOverride { DisplayName = "Renamed", Points = 10 }
                }
            });

            var twice = Normalize(once);

            Assert.AreEqual(once.AchievementOverrides.Count, twice.AchievementOverrides.Count);
            foreach (var pair in once.AchievementOverrides)
            {
                var second = twice.AchievementOverrides[pair.Key];
                Assert.AreEqual(pair.Value.Category, second.Category);
                Assert.AreEqual(pair.Value.CategoryType, second.CategoryType);
                Assert.AreEqual(pair.Value.Note, second.Note);
                Assert.AreEqual(pair.Value.DisplayName, second.DisplayName);
                Assert.AreEqual(pair.Value.Description, second.Description);
                Assert.AreEqual(pair.Value.Points, second.Points);
                Assert.AreEqual(pair.Value.TrophyType, second.TrophyType);
                Assert.AreEqual(pair.Value.UnlockedIconPath, second.UnlockedIconPath);
                Assert.AreEqual(pair.Value.LockedIconPath, second.LockedIconPath);
            }
        }

        [TestMethod]
        public void Normalize_KeepsLegacyMapsMirroredForNotYetRepointedConsumers()
        {
            // Until every consumer reads the record, both shapes must agree; a consumer still
            // reading the legacy map would otherwise see the value disappear.
            var result = Normalize(new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride
                    {
                        Category = "Story",
                        Note = "note",
                        UnlockedIconPath = "unlocked.png",
                        LockedIconPath = "locked.png"
                    }
                }
            });

            Assert.AreEqual("Story", result.AchievementCategoryOverrides["ach_one"]);
            Assert.AreEqual("note", result.AchievementNotes["ach_one"]);
            Assert.AreEqual("unlocked.png", result.AchievementUnlockedIconOverrides["ach_one"]);
            Assert.AreEqual("locked.png", result.AchievementLockedIconOverrides["ach_one"]);
        }

        [TestMethod]
        public void HasVisibleCustomization_RecordOnlyFieldEdit_ReturnsTrue()
        {
            // Title, description, points and trophy type live only on the record, so the
            // Customized tag would be wrong if the predicates ignored it.
            var data = new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride { DisplayName = "Renamed" }
                }
            };

            Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(data));
            Assert.IsTrue(GameCustomDataNormalizer.HasInternalData(data));
        }

        [TestMethod]
        public void Clone_CopiesOverridesDeeply()
        {
            var source = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride { Note = "original" }
                }
            };

            var clone = source.Clone();
            clone.AchievementOverrides["ach_one"].Note = "mutated";

            Assert.AreEqual("original", source.AchievementOverrides["ach_one"].Note);
        }

        [TestMethod]
        public void PortableRoundTrip_PreservesOverrides()
        {
            var source = new GameCustomDataFile
            {
                SchemaVersion = 8,
                AchievementOverrides = new Dictionary<string, AchievementOverride>
                {
                    ["ach_one"] = new AchievementOverride
                    {
                        DisplayName = "Renamed",
                        Description = "Rewritten",
                        Points = 7,
                        TrophyType = "silver",
                        Category = "Story",
                        Note = "note",
                        UnlockedIconPath = "unlocked.png",
                        LockedIconPath = "locked.png"
                    }
                }
            };

            var restored = GameCustomDataFile.FromPortable(
                source.ToPortable(),
                GameId,
                excludedFromRefreshes: null,
                excludedFromSummaries: null);

            var entry = restored.AchievementOverrides["ach_one"];
            Assert.AreEqual("Renamed", entry.DisplayName);
            Assert.AreEqual("Rewritten", entry.Description);
            Assert.AreEqual(7, entry.Points);
            Assert.AreEqual("silver", entry.TrophyType);
            Assert.AreEqual("Story", entry.Category);
            Assert.AreEqual("note", entry.Note);
            Assert.AreEqual("unlocked.png", entry.UnlockedIconPath);
            Assert.AreEqual("locked.png", entry.LockedIconPath);
        }

        [TestMethod]
        public void NormalizePortable_FoldsLegacySchemaSevenPackage()
        {
            var portable = new GameCustomDataPortableFile
            {
                SchemaVersion = 7,
                AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "imported note" },
                AchievementCategoryOverrides = new Dictionary<string, string> { ["ach_one"] = "Story" }
            };

            var result = GameCustomDataNormalizer.NormalizePortable(portable, GameId);

            Assert.AreEqual(GameCustomDataNormalizer.CurrentSchemaVersion, result.SchemaVersion);
            var entry = result.AchievementOverrides["ach_one"];
            Assert.AreEqual("imported note", entry.Note);
            Assert.AreEqual("Story", entry.Category);
        }
    }
}
