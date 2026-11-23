using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class CustomAchievementTests
    {
        [TestMethod]
        public void NormalizeInternal_CustomAchievements_NormalizesAndDropsInvalidRows()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition
                        {
                            Id = " custom:first-win ",
                            DisplayName = " First Win ",
                            Description = "  Start the game.  ",
                            Unlocked = true,
                            UnlockTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
                            Points = -10,
                            ScaledPoints = 25,
                            Category = " Main Story ",
                            // Stored under the pre-v9 type field, which the normalizer splits.
                            CategoryKind = "dlc",
                            TrophyType = "GOLD",
                            Rarity = "rare",
                            GlobalPercentUnlocked = 101,
                            ProgressNum = 3,
                            ProgressDenom = 2
                        },
                        new CustomAchievementDefinition
                        {
                            Id = "FIRST-WIN",
                            DisplayName = "Duplicate ID"
                        },
                        new CustomAchievementDefinition
                        {
                            DisplayName = " Boss Fight! ",
                            ProgressNum = 1,
                            ProgressDenom = 2
                        },
                        new CustomAchievementDefinition
                        {
                            DisplayName = " "
                        }
                    }
                },
                gameId);

            Assert.AreEqual(2, normalized.CustomAchievements.Count);
            Assert.AreEqual("first-win", normalized.CustomAchievements[0].Id);
            Assert.AreEqual("First Win", normalized.CustomAchievements[0].DisplayName);
            Assert.AreEqual("Start the game.", normalized.CustomAchievements[0].Description);
            Assert.AreEqual(DateTimeKind.Utc, normalized.CustomAchievements[0].UnlockTimeUtc.Value.Kind);
            Assert.IsNull(normalized.CustomAchievements[0].Points);
            Assert.AreEqual(25, normalized.CustomAchievements[0].ScaledPoints);
            Assert.AreEqual("Main Story", normalized.CustomAchievements[0].Category);
            Assert.AreEqual("DLC", normalized.CustomAchievements[0].CategoryKind);
            Assert.IsNull(normalized.CustomAchievements[0].Tags);
            Assert.AreEqual("gold", normalized.CustomAchievements[0].TrophyType);
            Assert.AreEqual("Rare", normalized.CustomAchievements[0].Rarity);
            Assert.IsNull(normalized.CustomAchievements[0].GlobalPercentUnlocked);
            Assert.IsNull(normalized.CustomAchievements[0].ProgressNum);
            Assert.IsNull(normalized.CustomAchievements[0].ProgressDenom);

            Assert.AreEqual("boss-fight", normalized.CustomAchievements[1].Id);
            Assert.AreEqual(1, normalized.CustomAchievements[1].ProgressNum);
            Assert.AreEqual(2, normalized.CustomAchievements[1].ProgressDenom);
            Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(normalized));
        }

        [TestMethod]
        public void ProjectAchievements_UsesCustomApiNamesAndRuntimeFlags()
        {
            var achievements = CustomAchievementProjectionService.ProjectAchievements(
                Guid.Empty,
                new[]
                {
                    new CustomAchievementDefinition
                    {
                        DisplayName = " Boss Fight! ",
                        Description = "Win the fight.",
                        Unlocked = true,
                        UnlockTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                        Points = 10,
                        Tags = "mp",
                        TrophyType = "Silver",
                        Rarity = "UltraRare",
                        GlobalPercentUnlocked = 4.5,
                        ProgressNum = 1,
                        ProgressDenom = 3
                    }
                });

            Assert.AreEqual(1, achievements.Count);
            var achievement = achievements[0];
            Assert.AreEqual("custom:boss-fight", achievement.ApiName);
            Assert.AreEqual("Boss Fight!", achievement.DisplayName);
            Assert.IsTrue(achievement.IsCustom);
            Assert.AreEqual(CustomAchievementProjectionService.ProviderKey, achievement.ProviderKey);
            Assert.IsTrue(achievement.Unlocked);
            Assert.AreEqual(10, achievement.Points);
            Assert.IsNull(achievement.CategoryKind);
            Assert.AreEqual("Multiplayer", achievement.Tags);
            Assert.AreEqual("silver", achievement.TrophyType);
            Assert.AreEqual(RarityTier.UltraRare, achievement.Rarity);
            Assert.AreEqual(4.5, achievement.GlobalPercentUnlocked);
            Assert.AreEqual(1, achievement.ProgressNum);
            Assert.AreEqual(3, achievement.ProgressDenom);
        }

        [TestMethod]
        public void TextImport_ParsesQuotedCsvIntoDefinitions()
        {
            var service = new CustomAchievementTextImportService();
            var result = service.Import(
                "Title,Description,Unlocked,Unlock Time,Points,Rarity,Progress,Progress Total\r\n" +
                "\"First, Win\",\"Uses a comma\",yes,2026-01-02T03:04:05Z,10,50%,1,2\r\n");

            Assert.IsFalse(result.HasErrors, string.Join("; ", result.Errors));
            var definition = result.Definitions.Single();
            Assert.AreEqual("first-win", definition.Id);
            Assert.AreEqual("First, Win", definition.DisplayName);
            Assert.AreEqual("Uses a comma", definition.Description);
            Assert.IsTrue(definition.Unlocked);
            Assert.AreEqual(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), definition.UnlockTimeUtc);
            Assert.AreEqual(DateTimeKind.Utc, definition.UnlockTimeUtc.Value.Kind);
            Assert.AreEqual(10, definition.Points);
            Assert.AreEqual(50, definition.GlobalPercentUnlocked);
            Assert.AreEqual(1, definition.ProgressNum);
            Assert.AreEqual(2, definition.ProgressDenom);
        }

        [TestMethod]
        public void TextImport_AnyBadValueRejectsTheWholeFile()
        {
            var result = new CustomAchievementTextImportService().Import(
                "Title,Trophy Type,Rarity\r\n" +
                "Good,gold,Rare\r\n" +
                "Bad Trophy,Diamond,\r\n" +
                "Bad Rarity,,150%\r\n" +
                ",,Common");

            Assert.AreEqual(0, result.Definitions.Count, "Nothing is imported while any row is bad.");
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 3, Trophy Type: \"Diamond\"")), string.Join("; ", result.Errors));
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 4, Rarity: \"150%\"")), string.Join("; ", result.Errors));
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 5: an ID or a Title is required")), string.Join("; ", result.Errors));
        }

        [TestMethod]
        public void CsvParse_KeepsOnlyFilledCellsAndAllowsBlankTitleWithAnId()
        {
            var result = new CustomAchievementTextImportService().Parse(
                "﻿ID,Title,Description,Hidden,Notes\r\n" +
                "ACH_ONE,,New text,,whatever\r\n");

            Assert.IsFalse(result.HasErrors, string.Join("; ", result.Errors));
            var row = result.Rows.Single();
            Assert.AreEqual("ACH_ONE", row.Id, "A leading byte order mark does not hide the ID column.");
            Assert.IsNull(row.DisplayName, "A blank Title means leave the title alone.");
            Assert.AreEqual("New text", row.Description);
            Assert.IsNull(row.Hidden, "A blank cell is not false.");
            CollectionAssert.AreEqual(new[] { "Notes" }, result.IgnoredColumns);
        }

        [TestMethod]
        public void CsvParse_NormalizesUnambiguousSpellings()
        {
            var result = new CustomAchievementTextImportService().Parse(
                "ID,Trophy Type,Rarity,Category\r\n" +
                "a,Gold,ultra-rare,Story > Act 2\r\n" +
                "b,PLATINUM,Ultra Rare,\r\n" +
                "c,,12.5,\r\n");

            Assert.IsFalse(result.HasErrors, string.Join("; ", result.Errors));
            Assert.AreEqual("gold", result.Rows[0].TrophyType);
            Assert.AreEqual("UltraRare", result.Rows[0].RarityTier);
            Assert.AreEqual("Story" + CategoryPathHelper.Separator + "Act 2", result.Rows[0].Category);
            Assert.AreEqual("platinum", result.Rows[1].TrophyType);
            Assert.AreEqual("UltraRare", result.Rows[1].RarityTier);
            Assert.AreEqual(12.5, result.Rows[2].RarityPercent);
            Assert.IsNull(result.Rows[2].RarityTier);
        }

        [TestMethod]
        public void CsvParse_RejectsContradictionsAndDuplicates()
        {
            var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = new CustomAchievementTextImportService().Parse(
                "ID,Unlocked,Unlock Time,Progress,Progress Total\r\n" +
                "a,false,2026-01-01 10:00:00,,\r\n" +
                "b,,2027-01-01 10:00:00,,\r\n" +
                "c,,,5,3\r\n" +
                "A,,,,\r\n",
                now);

            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 2, Unlock Time: set while Unlocked is false")), string.Join("; ", result.Errors));
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 3, Unlock Time: is in the future")), string.Join("; ", result.Errors));
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 4, Progress: is greater than Progress Total")), string.Join("; ", result.Errors));
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 5, ID: \"A\" is used by an earlier row")), string.Join("; ", result.Errors));
        }

        [TestMethod]
        public void CsvFormat_RoundTripsThroughTheParser()
        {
            var unlock = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var lines = CustomAchievementCsvFormat.BuildLines(
                new[]
                {
                    new CustomAchievementCsvRow
                    {
                        Id = "ACH_ONE",
                        DisplayName = "First, \"Win\"",
                        Description = "Line one\nLine two",
                        Points = 10,
                        TrophyType = "gold",
                        Hidden = true,
                        RarityPercent = 12.5,
                        Category = "Story" + CategoryPathHelper.Separator + "Act 2",
                        ProgressNum = 1,
                        ProgressDenom = 3,
                        Unlocked = true,
                        UnlockTimeUtc = unlock
                    },
                    new CustomAchievementCsvRow { Id = "two", DisplayName = "Two", RarityTier = "Rare", Unlocked = false }
                });

            Assert.AreEqual(CustomAchievementCsvFormat.Header, lines[0]);
            Assert.IsTrue(lines[1].Contains("12.5%"), lines[1]);
            Assert.IsTrue(lines[1].Contains("Story > Act 2"), lines[1]);

            var result = new CustomAchievementTextImportService().Parse(string.Join("\r\n", lines), unlock.AddDays(1));
            Assert.IsFalse(result.HasErrors, string.Join("; ", result.Errors));
            var first = result.Rows[0];
            Assert.AreEqual("ACH_ONE", first.Id);
            Assert.AreEqual("First, \"Win\"", first.DisplayName);
            Assert.AreEqual("Line one\nLine two", first.Description);
            Assert.AreEqual(10, first.Points);
            Assert.AreEqual("gold", first.TrophyType);
            Assert.AreEqual(true, first.Hidden);
            Assert.AreEqual(12.5, first.RarityPercent);
            Assert.AreEqual("Story" + CategoryPathHelper.Separator + "Act 2", first.Category);
            Assert.AreEqual(1, first.ProgressNum);
            Assert.AreEqual(3, first.ProgressDenom);
            Assert.AreEqual(true, first.Unlocked);
            Assert.AreEqual(unlock, first.UnlockTimeUtc, "Written in local time and read back as the same instant.");
            Assert.AreEqual("Rare", result.Rows[1].RarityTier);
            Assert.AreEqual(false, result.Rows[1].Unlocked);
        }

        [TestMethod]
        public void CsvParse_ResolvesIconCellsAgainstTheFilesFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "icons"));
            try
            {
                File.WriteAllBytes(Path.Combine(folder, "icons", "a.png"), new byte[] { 1 });
                var text =
                    CustomAchievementCsvFormat.Header + "\r\n" +
                    "a,,,,,,,,,,,,icons/a.png,https://example.com/a-locked.png\r\n";

                var fromFile = new CustomAchievementTextImportService().Parse(text, iconBaseDirectory: folder);
                Assert.IsFalse(fromFile.HasErrors, string.Join("; ", fromFile.Errors));
                Assert.AreEqual(Path.Combine(folder, "icons", "a.png"), fromFile.Rows[0].UnlockedIconPath);
                Assert.AreEqual("https://example.com/a-locked.png", fromFile.Rows[0].LockedIconPath);

                var missing = new CustomAchievementTextImportService().Parse(
                    CustomAchievementCsvFormat.Header + "\r\na,,,,,,,,,,,,icons/missing.png,\r\n",
                    iconBaseDirectory: folder);
                Assert.IsTrue(
                    missing.Errors.Any(error => error.StartsWith("Row 2, Unlocked Icon: \"icons/missing.png\" is not a web address or a file that exists")),
                    string.Join("; ", missing.Errors));

                var fromPackage = new CustomAchievementTextImportService().Parse(
                    CustomAchievementCsvFormat.Header + "\r\na,,,,,,,,,,,,images/a.png,\r\n");
                Assert.AreEqual("images/a.png", fromPackage.Rows[0].UnlockedIconPath, "A package's reader resolves its own entries.");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [TestMethod]
        public void CsvParse_ReadsSemicolonAndTabFiles()
        {
            var service = new CustomAchievementTextImportService();

            var semicolon = service.Parse("ID;Title;Description\r\na;First, win;\"Uses; a semicolon\"\r\n");
            Assert.IsFalse(semicolon.HasErrors, string.Join("; ", semicolon.Errors));
            Assert.AreEqual("First, win", semicolon.Rows[0].DisplayName, "A comma is plain text in a semicolon file.");
            Assert.AreEqual("Uses; a semicolon", semicolon.Rows[0].Description);

            var tab = service.Parse("ID\tTitle\r\na\tFirst, win\r\n");
            Assert.IsFalse(tab.HasErrors, string.Join("; ", tab.Errors));
            Assert.AreEqual("First, win", tab.Rows[0].DisplayName);
        }

        [TestMethod]
        public void CsvParse_HonoursExcelsSeparatorLineWithoutCountingItAsARow()
        {
            var result = new CustomAchievementTextImportService().Parse(
                "sep=;\r\nID;Title;Trophy Type\r\na;One;gold\r\nb;Two;Diamond\r\n");

            Assert.IsTrue(
                result.Errors.Any(error => error.StartsWith("Row 3, Trophy Type")),
                "Rows are numbered as Excel shows them, with the hidden sep= line not counted: " + string.Join("; ", result.Errors));
            Assert.AreEqual("One", result.Rows.Single().DisplayName);
        }

        [TestMethod]
        public void CsvParse_RejectsRowsWithAnotherSeparatorOrExtraCells()
        {
            var result = new CustomAchievementTextImportService().Parse(
                "ID,Title,Description\r\n" +
                "a,One,\r\n" +
                "b;Two;Text\r\n" +
                "c,Three,Has, an unquoted comma\r\n");

            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 3: uses a different separator than the header (\",\")")), string.Join("; ", result.Errors));
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("Row 4: has more cells than the header")), string.Join("; ", result.Errors));
            Assert.AreEqual("a", result.Rows.Single().Id);
        }

        [TestMethod]
        public void CsvFormat_WritesTheGivenSeparatorAndReadsItBack()
        {
            var lines = CustomAchievementCsvFormat.BuildLines(
                new[] { new CustomAchievementCsvRow { Id = "a", DisplayName = "One, two", Description = "Three; four", RarityPercent = 12.5 } },
                ';');

            Assert.AreEqual(CustomAchievementCsvFormat.Header.Replace(',', ';'), lines[0]);
            var result = new CustomAchievementTextImportService().Parse(string.Join("\r\n", lines));
            Assert.IsFalse(result.HasErrors, string.Join("; ", result.Errors));
            Assert.AreEqual("One, two", result.Rows[0].DisplayName);
            Assert.AreEqual("Three; four", result.Rows[0].Description);
            Assert.AreEqual(12.5, result.Rows[0].RarityPercent);
        }

        [TestMethod]
        public void CsvFormat_WriteFileEmitsByteOrderMarkAndCrlf()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N") + ".csv");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                CustomAchievementCsvFormat.WriteFile(path, new[] { "a,b", "c,d" });
                var bytes = File.ReadAllBytes(path);
                CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
                Assert.AreEqual("a,b\r\nc,d\r\n", System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void CustomAchievementsPackage_ImportsDefinitionsWithoutPersonalState()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var store = new GameCustomDataStore(Path.Combine(tempDirectory, "store"));
                var gameId = Guid.NewGuid();
                var packagePath = Path.Combine(tempDirectory, "custom.pa");
                using (var archive = System.IO.Compression.ZipFile.Open(packagePath, System.IO.Compression.ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry(GameCustomDataStore.CustomAchievementsPackageCsvEntryName).Open()))
                {
                    writer.WriteLine(CustomAchievementCsvFormat.Header);
                    writer.WriteLine("first-win,\"First, Win\",\"Uses a \"\"quote\"\"\",10,gold,true,12.5%,,1,2,true,2026-01-02 03:04:05,,");
                    writer.WriteLine("second,Second,,,,,,,,,,,,");
                }

                Assert.IsTrue(store.IsCustomAchievementsPackage(packagePath));
                Assert.ThrowsException<InvalidOperationException>(
                    () => store.ImportReplacePortable(gameId, packagePath),
                    "A custom-achievements package must not replace the game's custom data.");

                var result = store.ImportCustomAchievementsPackage(gameId, packagePath);
                Assert.IsFalse(result.HasErrors, string.Join("; ", result.Errors));
                Assert.AreEqual(2, result.Definitions.Count);

                var first = result.Definitions[0];
                Assert.AreEqual("first-win", first.Id);
                Assert.AreEqual("First, Win", first.DisplayName);
                Assert.AreEqual("Uses a \"quote\"", first.Description);
                Assert.IsFalse(first.Unlocked, "A package never carries unlock state.");
                Assert.IsNull(first.UnlockTimeUtc);
                Assert.AreEqual(10, first.Points);
                Assert.AreEqual("gold", first.TrophyType);
                Assert.IsTrue(first.Hidden);
                Assert.AreEqual(
                    PercentRarityHelper.GetRarityTier(12.5).ToString(),
                    first.Rarity,
                    "A percent is written in the one Rarity column, and the tier is derived from it.");
                Assert.AreEqual(12.5, first.GlobalPercentUnlocked);
                Assert.IsNull(first.ProgressNum, "A package never carries progress.");
                Assert.AreEqual(2, first.ProgressDenom);
                Assert.IsNull(first.UnlockedIconPath);
                Assert.AreEqual("second", result.Definitions[1].Id);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }

        [TestMethod]
        public void CreateSyntheticGameData_StampsProviderPlatformKeyFromAssignedCustomProvider()
        {
            var gameId = Guid.NewGuid();
            var definitions = new List<CustomAchievementDefinition>
            {
                new CustomAchievementDefinition { DisplayName = "Solo" }
            };

            var assigned = CustomAchievementProjectionService.CreateSyntheticGameData(gameId, null, definitions, null, "Custom:abc");
            Assert.AreEqual(CustomAchievementProjectionService.ProviderKey, assigned.ProviderKey);
            Assert.AreEqual("Custom:abc", assigned.ProviderPlatformKey);
            Assert.AreEqual("Custom:abc", assigned.EffectiveProviderKey);

            var unassigned = CustomAchievementProjectionService.CreateSyntheticGameData(gameId, null, definitions);
            Assert.IsNull(unassigned.ProviderPlatformKey);
            Assert.AreEqual(CustomAchievementProjectionService.ProviderKey, unassigned.EffectiveProviderKey);
        }

        [TestMethod]
        public void CreateSyntheticGameData_RecordsNoPlatformOrMetadataLayer()
        {
            var data = CustomAchievementProjectionService.CreateSyntheticGameData(
                Guid.NewGuid(),
                null,
                new List<CustomAchievementDefinition> { new CustomAchievementDefinition { DisplayName = "Solo" } });

            Assert.AreEqual(PlayniteAchievements.Models.Tagging.CustomizationArea.None, data.PlatformFields);
            Assert.AreEqual(PlayniteAchievements.Models.Tagging.CustomizationArea.None, data.MetadataFields);
        }

    }
}
