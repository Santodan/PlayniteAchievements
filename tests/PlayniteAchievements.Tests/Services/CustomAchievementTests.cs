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
                            CategoryType = "dlc",
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
            Assert.AreEqual("DLC", normalized.CustomAchievements[0].CategoryType);
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
                        CategoryType = "mp",
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
            Assert.AreEqual("Multiplayer", achievement.CategoryType);
            Assert.AreEqual("silver", achievement.TrophyType);
            Assert.AreEqual(RarityTier.UltraRare, achievement.Rarity);
            Assert.AreEqual(4.5, achievement.GlobalPercentUnlocked);
            Assert.AreEqual(1, achievement.ProgressNum);
            Assert.AreEqual(3, achievement.ProgressDenom);
        }

        [TestMethod]
        public void TextImport_ParsesQuotedCsvAndReportsInvalidRows()
        {
            var service = new CustomAchievementTextImportService();
            var result = service.Import(
                "Title,Description,Unlocked,Unlocked At,Points,Percent,Progress,Total\r\n" +
                "\"First, Win\",\"Uses a comma\",yes,2026-01-02T03:04:05Z,10,50%,1,2\r\n" +
                ",Missing title,no,,0,10,0,1\r\n" +
                "Bad Percent,,,,0,150,0,1");

            Assert.AreEqual(1, result.Definitions.Count);
            Assert.IsTrue(result.HasErrors);
            Assert.IsTrue(result.Errors.Any(error => error.Contains("title/name is required")));
            Assert.IsTrue(result.Errors.Any(error => error.Contains("percent must be between 0 and 100")));

            var definition = result.Definitions[0];
            Assert.AreEqual("first-win", definition.Id);
            Assert.AreEqual("First, Win", definition.DisplayName);
            Assert.AreEqual("Uses a comma", definition.Description);
            Assert.IsTrue(definition.Unlocked);
            Assert.AreEqual(DateTimeKind.Utc, definition.UnlockTimeUtc.Value.Kind);
            Assert.AreEqual(10, definition.Points);
            Assert.AreEqual(50, definition.GlobalPercentUnlocked);
            Assert.AreEqual(1, definition.ProgressNum);
            Assert.AreEqual(2, definition.ProgressDenom);
        }

        [TestMethod]
        public void SummaryMerger_AppendsCustomAchievementsToExistingAndCustomOnlyGames()
        {
            var existingGameId = Guid.NewGuid();
            var customOnlyGameId = Guid.NewGuid();
            var excludedGameId = Guid.NewGuid();
            var unlockTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            var summary = new CachedSummaryData();
            summary.Games.Add(new CachedGameSummaryData
            {
                PlayniteGameId = existingGameId,
                CacheKey = "steam:1",
                ProviderKey = "Steam",
                GameName = "Existing",
                HasAchievements = true,
                TotalAchievements = 2,
                UnlockedAchievements = 1,
                TotalCommonPossible = 2,
                CommonCount = 1
            });

            var customData = new Dictionary<Guid, GameCustomDataFile>
            {
                [existingGameId] = new GameCustomDataFile
                {
                    PlayniteGameId = existingGameId,
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition { Id = "a", DisplayName = "A", Unlocked = true, UnlockTimeUtc = unlockTime, Rarity = "Rare", Points = 10, TrophyType = "gold" },
                        new CustomAchievementDefinition { Id = "b", DisplayName = "B" },
                        new CustomAchievementDefinition { Id = "c", DisplayName = "C", Unlocked = true }
                    },
                    SummaryFilteredAchievementApiNames = new List<string> { CustomAchievementProjectionService.BuildApiName("c") }
                },
                [customOnlyGameId] = new GameCustomDataFile
                {
                    PlayniteGameId = customOnlyGameId,
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition { Id = "solo", DisplayName = "Solo", Unlocked = true, UnlockTimeUtc = unlockTime.AddDays(1) }
                    }
                },
                [excludedGameId] = new GameCustomDataFile
                {
                    PlayniteGameId = excludedGameId,
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition { Id = "x", DisplayName = "X", Unlocked = true }
                    }
                }
            };

            CustomAchievementSummaryMerger.Merge(
                summary,
                customData,
                new HashSet<Guid> { excludedGameId },
                recentAchievementDetailLimit: 1,
                resolveGameName: id => id == customOnlyGameId ? "Solo Game" : null,
                managedCustomIconService: null);

            var existing = summary.Games.Single(g => g.PlayniteGameId == existingGameId);
            Assert.AreEqual(4, existing.TotalAchievements, "two stored plus a and b; c is filtered from summaries");
            Assert.AreEqual(2, existing.UnlockedAchievements);
            Assert.AreEqual(1, existing.TotalRarePossible);
            Assert.AreEqual(1, existing.RareCount);
            Assert.AreEqual(3, existing.TotalCommonPossible);
            Assert.AreEqual(1, existing.TrophyGoldTotal);
            Assert.AreEqual(1, existing.TrophyGoldCount);
            Assert.AreEqual(10, existing.Points);
            Assert.AreEqual(unlockTime, existing.LastUnlockUtc);
            Assert.IsFalse(existing.IsCompleted);

            var solo = summary.Games.Single(g => g.PlayniteGameId == customOnlyGameId);
            Assert.AreEqual("Solo Game", solo.GameName);
            Assert.AreEqual(CustomAchievementProjectionService.ProviderKey, solo.ProviderKey);
            Assert.IsTrue(solo.HasAchievements);
            Assert.IsTrue(solo.IsCompleted);
            Assert.IsFalse(summary.Games.Any(g => g.PlayniteGameId == excludedGameId));

            Assert.IsTrue(summary.HasMoreRecentUnlocks);
            Assert.AreEqual(1, summary.RecentUnlocks.Count);
            Assert.AreEqual(CustomAchievementProjectionService.BuildApiName("solo"), summary.RecentUnlocks[0].ApiName, "newest unlock first");
            // Timeline keys are local calendar days, so the expected key goes through the same helper.
            var unlockDay = PlayniteAchievements.Services.Overview.UnlockDayCounts.DayOf(unlockTime);
            Assert.AreEqual(1, summary.GlobalUnlockCountsByDate[unlockDay]);
            Assert.AreEqual(1, summary.GlobalUnlockCountsByDate[unlockDay.AddDays(1)]);
            Assert.AreEqual(1, summary.UnlockCountsByDateByGame[existingGameId][unlockDay]);
        }

        [TestMethod]
        public void CustomAchievementTotals_UseTheUsersOverrides()
        {
            var gameId = Guid.NewGuid();
            var apiName = CustomAchievementProjectionService.BuildApiName("a");
            var summary = new CachedSummaryData();
            var customData = new Dictionary<Guid, GameCustomDataFile>
            {
                [gameId] = new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition
                        {
                            Id = "a",
                            DisplayName = "A",
                            Unlocked = true,
                            UnlockTimeUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                            Points = 10,
                            TrophyType = "bronze"
                        }
                    },
                    AchievementOverrides = new Dictionary<string, AchievementOverride>
                    {
                        [apiName] = new AchievementOverride { Points = 50, TrophyType = "gold" }
                    }
                }
            };

            CustomAchievementSummaryMerger.Merge(
                summary,
                customData,
                new HashSet<Guid>(),
                recentAchievementDetailLimit: 0,
                resolveGameName: _ => "Game",
                managedCustomIconService: null);

            var game = summary.Games.Single();
            Assert.AreEqual(50, game.Points, "The game's points follow the override, as its row does.");
            Assert.AreEqual(1, game.TrophyGoldTotal);
            Assert.AreEqual(1, game.TrophyGoldCount);
            Assert.AreEqual(0, game.TrophyBronzeTotal);
        }

        [TestMethod]
        public void CustomCapstones_CountTowardTheFinishBadgeAndItsPlatinumIdentity()
        {
            var gameId = Guid.NewGuid();
            var summary = new CachedSummaryData
            {
                Games = new List<CachedGameSummaryData>
                {
                    // What the summary query found: two ordinary stored achievements.
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = gameId,
                        CacheKey = gameId.ToString("D"),
                        TotalAchievements = 2,
                        UnlockedAchievements = 2
                    }
                }
            };

            var customData = new Dictionary<Guid, GameCustomDataFile>
            {
                [gameId] = new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition
                        {
                            Id = "mastery",
                            DisplayName = "Mastery",
                            IsCapstone = true,
                            Unlocked = false
                        }
                    }
                }
            };

            CustomAchievementSummaryMerger.Merge(
                summary,
                customData,
                new HashSet<Guid>(),
                recentAchievementDetailLimit: 0,
                resolveGameName: _ => "Game",
                managedCustomIconService: null);

            var game = summary.Games.Single();
            Assert.AreEqual(1, game.CapstoneTotal, "A custom capstone is a capstone.");
            Assert.AreEqual(0, game.CapstoneUnlocked);
            Assert.IsFalse(
                game.IsCompleted,
                "Every stored achievement is unlocked, but the custom capstone that stands for the game is not.");
            Assert.IsFalse(
                game.CapstonesMatchPlatinums,
                "A capstone that is not a platinum must not hand the finish badge to one.");
        }

        [TestMethod]
        public void CustomPlatinumCapstone_KeepsTheFinishBadgeOnThePlatinum()
        {
            var gameId = Guid.NewGuid();
            var summary = new CachedSummaryData();
            var customData = new Dictionary<Guid, GameCustomDataFile>
            {
                [gameId] = new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition
                        {
                            Id = "plat",
                            DisplayName = "Platinum",
                            TrophyType = "Platinum",
                            IsCapstone = true,
                            Unlocked = true
                        },
                        new CustomAchievementDefinition { Id = "gold", DisplayName = "Gold", TrophyType = "Gold" }
                    }
                }
            };

            CustomAchievementSummaryMerger.Merge(
                summary,
                customData,
                new HashSet<Guid>(),
                recentAchievementDetailLimit: 0,
                resolveGameName: _ => "Game",
                managedCustomIconService: null);

            var game = summary.Games.Single();
            Assert.AreEqual(1, game.CapstoneTotal);
            Assert.AreEqual(1, game.CapstoneUnlocked);
            Assert.IsTrue(game.IsCompleted, "The only capstone is earned.");
            Assert.IsTrue(game.CapstonesMatchPlatinums);
            Assert.AreEqual(
                CustomAchievementProjectionService.BuildApiName("plat"),
                game.PlatinumApiNames,
                "The overlay needs the custom platinum by name, not only as a count.");
        }

        [TestMethod]
        public void CustomAchievementsPackage_RoundTripsDefinitionsAndHeaderOnlyTemplate()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var store = new GameCustomDataStore(Path.Combine(tempDirectory, "store"));
                var gameId = Guid.NewGuid();
                var packagePath = Path.Combine(tempDirectory, "custom.pa");
                store.ExportCustomAchievementsPackage(
                    gameId,
                    new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition
                        {
                            Id = "first-win",
                            DisplayName = "First, Win",
                            Description = "Uses a \"quote\"",
                            Unlocked = true,
                            UnlockTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                            Points = 10,
                            TrophyType = "gold",
                            Hidden = true,
                            Rarity = "Rare",
                            GlobalPercentUnlocked = 12.5,
                            ProgressNum = 1,
                            ProgressDenom = 2
                        },
                        new CustomAchievementDefinition
                        {
                            Id = "second",
                            DisplayName = "Second"
                        }
                    },
                    packagePath);

                Assert.IsTrue(store.IsCustomAchievementsPackage(packagePath));
                string csvText;
                using (var archive = System.IO.Compression.ZipFile.OpenRead(packagePath))
                using (var reader = new StreamReader(archive.GetEntry(GameCustomDataStore.CustomAchievementsPackageCsvEntryName).Open()))
                {
                    csvText = reader.ReadToEnd();
                }

                StringAssert.DoesNotMatch(csvText, new System.Text.RegularExpressions.Regex(@"(?i)unlocked,|unlock time|2026-01-02|progress,"));
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
                Assert.AreEqual("Rare", first.Rarity);
                Assert.AreEqual(12.5, first.GlobalPercentUnlocked);
                Assert.IsNull(first.ProgressNum, "A package never carries progress.");
                Assert.AreEqual(2, first.ProgressDenom);
                Assert.IsNull(first.UnlockedIconPath);
                Assert.AreEqual("second", result.Definitions[1].Id);

                var templatePath = Path.Combine(tempDirectory, "template.pa");
                store.ExportCustomAchievementsPackage(gameId, new List<CustomAchievementDefinition>(), templatePath);
                Assert.IsTrue(store.IsCustomAchievementsPackage(templatePath));
                var templateResult = store.ImportCustomAchievementsPackage(gameId, templatePath);
                Assert.AreEqual(0, templateResult.Definitions.Count);
                Assert.IsTrue(templateResult.HasErrors, "A header-only template imports as no rows.");
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
        public void Merge_CustomOnlyGame_CarriesResolvedCustomProviderKeyIntoSummaryAndRecentUnlocks()
        {
            var gameId = Guid.NewGuid();
            var unlockTime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            Dictionary<Guid, GameCustomDataFile> BuildCustomData() => new Dictionary<Guid, GameCustomDataFile>
            {
                [gameId] = new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    CustomProviderId = "abc",
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition { Id = "solo", DisplayName = "Solo", Unlocked = true, UnlockTimeUtc = unlockTime }
                    }
                }
            };

            var resolved = new CachedSummaryData();
            CustomAchievementSummaryMerger.Merge(
                resolved,
                BuildCustomData(),
                new HashSet<Guid>(),
                10,
                _ => "Solo Game",
                null,
                id => id == "abc" ? "Custom:abc" : null);

            var game = resolved.Games.Single();
            Assert.AreEqual(CustomAchievementProjectionService.ProviderKey, game.ProviderKey);
            Assert.AreEqual("Custom:abc", game.ProviderPlatformKey);
            Assert.AreEqual("Custom:abc", resolved.RecentUnlocks.Single().ProviderPlatformKey);

            var unresolved = new CachedSummaryData();
            CustomAchievementSummaryMerger.Merge(
                unresolved,
                BuildCustomData(),
                new HashSet<Guid>(),
                10,
                _ => "Solo Game",
                null,
                _ => null);

            Assert.IsNull(unresolved.Games.Single().ProviderPlatformKey);
        }
    }
}
