using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    public class ThemeIntegrationBindingsTests
    {
        [TestMethod]
        public void SelectedGameBuilder_IncludesStoredFallbackRarityInStats()
        {
            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Selected Game" };
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Rare Unlocked", 8.0, unlocked: true),
                    Achievement("Ultra Unlocked", 2.0, unlocked: true),
                    Achievement("Ultra Locked", 4.0, unlocked: false),
                    Achievement("Uncommon Locked", 25.0, unlocked: false),
                    Achievement("Common Unlocked", 75.0, unlocked: true),
                    Achievement("No Percent", null, unlocked: true),
                    Achievement("Zero Percent", 0.0, unlocked: false)
                }
            };

            var state = SelectedGameRuntimeStateBuilder.Build(
                gameId,
                data);

            AssertStat(state.Common, total: 2, unlocked: 2, locked: 0);
            AssertStat(state.Uncommon, total: 1, unlocked: 0, locked: 1);
            AssertStat(state.Rare, total: 1, unlocked: 1, locked: 0);
            AssertStat(state.UltraRare, total: 3, unlocked: 1, locked: 2);
            AssertStat(state.RareAndUltraRare, total: 4, unlocked: 2, locked: 2);
        }

        [TestMethod]
        public void SelectedGameBuilder_UsesCustomOrderForCanonicalAllAchievements()
        {
            var gameId = Guid.NewGuid();
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = "Custom Ordered Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("First", 80.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 12, 0, 0)),
                    Achievement("Second", 25.0, unlocked: false),
                    Achievement("Third", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 12, 0, 0))
                },
                AchievementOrder = new List<string> { "Third", "First" }
            };

            var state = SelectedGameRuntimeStateBuilder.Build(gameId, data);

            Assert.IsTrue(state.HasCustomAchievementOrder);
            AssertAchievementNames(state.AllAchievements, "Third", "First", "Second");
            AssertAchievementNames(state.AchievementDefaultOrder, "Third", "First", "Second");
        }

        [TestMethod]
        public void SelectedGameBuilder_DefaultCanonicalOrderMatchesSharedDefaultSorting()
        {
            var gameId = Guid.NewGuid();
            var unlockedNoTime = Achievement("Unlocked No Time", null, unlocked: true, unlockTimeUtc: null);
            unlockedNoTime.UnlockTimeUtc = null;
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = "Default Ordered Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Locked", 80.0, unlocked: false),
                    Achievement("Unlocked Older", 25.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                    Achievement("Unlocked Newer", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0)),
                    unlockedNoTime
                }
            };

            var state = SelectedGameRuntimeStateBuilder.Build(gameId, data);

            Assert.IsFalse(state.HasCustomAchievementOrder);
            AssertAchievementNames(
                state.AllAchievements,
                "Unlocked Newer",
                "Unlocked Older",
                "Unlocked No Time",
                "Locked");
            AssertAchievementNames(
                state.AchievementDefaultOrder,
                "Locked",
                "Unlocked Older",
                "Unlocked Newer",
                "Unlocked No Time");
        }

        [TestMethod]
        public void LibraryBuilder_BuildsStatsObjectsAcrossGames()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var gameOneId = Guid.NewGuid();
            var gameTwoId = Guid.NewGuid();
            var allData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = gameOneId,
                    Game = new Game { Id = gameOneId, Name = "Game One" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Game1 Common Unlocked", 75.0, unlocked: true),
                        Achievement("Game1 Uncommon Locked", 25.0, unlocked: false),
                        Achievement("Game1 Rare Unlocked", 8.0, unlocked: true),
                        Achievement("Game1 Ultra Locked", 2.0, unlocked: false)
                    }
                },
                new GameAchievementData
                {
                    PlayniteGameId = gameTwoId,
                    Game = new Game { Id = gameTwoId, Name = "Game Two" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Game2 Common Locked", 90.0, unlocked: false),
                        Achievement("Game2 Uncommon Unlocked", 45.0, unlocked: true),
                        Achievement("Game2 Rare Locked", 7.0, unlocked: false),
                        Achievement("Game2 Ultra Unlocked", 1.0, unlocked: true)
                    }
                }
            };

            var state = LibraryRuntimeStateBuilder.Build(allData, api: null, token: default, includeHeavyAchievementLists: false);

            AssertStat(state.TotalCommon, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.TotalUncommon, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.TotalRare, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.TotalUltraRare, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.TotalRareAndUltraRare, total: 4, unlocked: 2, locked: 2);
            AssertStat(state.TotalOverall, total: 8, unlocked: 4, locked: 4);

            var gameOneSummary = FindSummary(state.AllGamesWithAchievements, gameOneId);
            AssertStat(gameOneSummary.Common, total: 1, unlocked: 1, locked: 0);
            AssertStat(gameOneSummary.Uncommon, total: 1, unlocked: 0, locked: 1);
            AssertStat(gameOneSummary.Rare, total: 1, unlocked: 1, locked: 0);
            AssertStat(gameOneSummary.UltraRare, total: 1, unlocked: 0, locked: 1);
            AssertStat(gameOneSummary.RareAndUltraRare, total: 2, unlocked: 1, locked: 1);
            AssertStat(gameOneSummary.Overall, total: 4, unlocked: 2, locked: 2);

            var gameTwoSummary = FindSummary(state.AllGamesWithAchievements, gameTwoId);
            AssertStat(gameTwoSummary.Common, total: 1, unlocked: 0, locked: 1);
            AssertStat(gameTwoSummary.Uncommon, total: 1, unlocked: 1, locked: 0);
            AssertStat(gameTwoSummary.Rare, total: 1, unlocked: 0, locked: 1);
            AssertStat(gameTwoSummary.UltraRare, total: 1, unlocked: 1, locked: 0);
            AssertStat(gameTwoSummary.RareAndUltraRare, total: 2, unlocked: 1, locked: 1);
            AssertStat(gameTwoSummary.Overall, total: 4, unlocked: 2, locked: 2);
        }

        [TestMethod]
        public void LibraryBuilder_IncludesFallbackAndNormalizedPercentRarityInTotals()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var gameId = Guid.NewGuid();
            var allData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Game = new Game { Id = gameId, Name = "Filtered Game" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Counted Common", 80.0, unlocked: true),
                        Achievement("Counted Rare", 8.0, unlocked: false),
                        Achievement("Missing Percent", null, unlocked: true),
                        Achievement("Zero Percent", 0.0, unlocked: true),
                        Achievement("Negative Percent", -1.0, unlocked: false)
                    }
                }
            };

            var state = LibraryRuntimeStateBuilder.Build(allData, api: null, token: default, includeHeavyAchievementLists: false);

            AssertStat(state.TotalCommon, total: 2, unlocked: 2, locked: 0);
            AssertStat(state.TotalRare, total: 1, unlocked: 0, locked: 1);
            AssertStat(state.TotalUltraRare, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.TotalOverall, total: 5, unlocked: 3, locked: 2);
        }

        [TestMethod]
        public void ThemeRuntimeBuilders_RoundCompletionPercentToNearestInteger()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Rounding Game" };
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Unlocked 1", 8.0, unlocked: true),
                    Achievement("Unlocked 2", 25.0, unlocked: true),
                    Achievement("Locked 1", 75.0, unlocked: false),
                    Achievement("Locked 2", 90.0, unlocked: false),
                    Achievement("Locked 3", 60.0, unlocked: false),
                    Achievement("Locked 4", 50.0, unlocked: false),
                    Achievement("Locked 5", 35.0, unlocked: false),
                    Achievement("Locked 6", 15.0, unlocked: false)
                }
            };

            var selected = SelectedGameRuntimeStateBuilder.Build(
                gameId,
                data);
            var library = LibraryRuntimeStateBuilder.Build(
                new List<GameAchievementData> { data },
                api: null,
                token: default,
                includeHeavyAchievementLists: false);

            var summary = FindSummary(library.AllGamesWithAchievements, gameId);
            Assert.AreEqual(25d, selected.ProgressPercentage, "Selected-game path should round 2/8 to 25%.");
            Assert.AreEqual(25, summary.Progress, "Library path should round 2/8 to 25%.");
        }

        [TestMethod]
        public void ThemeRuntimeBuilders_RoundMidpointUpAtHalfPercent()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Half Percent Game" };
            var achievements = new List<AchievementDetail>();
            achievements.Add(Achievement("Unlocked", 8.0, unlocked: true));
            for (var i = 0; i < 199; i++)
            {
                achievements.Add(Achievement("Locked " + i, 75.0, unlocked: false));
            }

            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                HasAchievements = true,
                Achievements = achievements
            };

            var selected = SelectedGameRuntimeStateBuilder.Build(
                gameId,
                data);
            var library = LibraryRuntimeStateBuilder.Build(
                new List<GameAchievementData> { data },
                api: null,
                token: default,
                includeHeavyAchievementLists: false);

            var summary = FindSummary(library.AllGamesWithAchievements, gameId);
            Assert.AreEqual(1d, selected.ProgressPercentage, "Selected-game path should round 0.5% to 1%.");
            Assert.AreEqual(1, summary.Progress, "Library path should round 0.5% to 1%.");
        }

        [TestMethod]
        public void PopulateSingleGameDataSync_PublishesModernRootAchievementLists()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Binding Game" };
            var first = Achievement("First Unlock", 75.0, unlocked: true);
            first.UnlockTimeUtc = DateTime.SpecifyKind(new DateTime(2026, 3, 1, 9, 0, 0), DateTimeKind.Utc);
            var second = Achievement("Second Unlock", 25.0, unlocked: true);
            second.UnlockTimeUtc = DateTime.SpecifyKind(new DateTime(2026, 3, 2, 9, 0, 0), DateTimeKind.Utc);
            var third = Achievement("Third Unlock", 2.0, unlocked: true);
            third.UnlockTimeUtc = DateTime.SpecifyKind(new DateTime(2026, 3, 3, 9, 0, 0), DateTimeKind.Utc);

            var settings = new PlayniteAchievementsSettings();
            var plugin = new PlayniteAchievementsPlugin
            {
                Settings = settings
            };
            PlayniteAchievementsPlugin.Instance = plugin;

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService();
            achievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail> { first, second, third }
            };

            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();
            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            var changedProperties = TrackPropertyChanges(settings);

            service.PopulateSingleGameDataSync(gameId);

            Assert.AreEqual(3, settings.Achievements.Count);
            Assert.AreEqual(3, settings.AchievementDefaultOrder.Count);
            Assert.AreEqual(3, settings.AchievementsNewestFirst.Count);
            Assert.AreEqual(3, settings.AchievementsOldestFirst.Count);
            Assert.AreEqual(3, settings.AchievementsRarityAsc.Count);
            Assert.AreEqual(3, settings.AchievementsRarityDesc.Count);

            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Achievements)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementDefaultOrder)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsNewestFirst)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsOldestFirst)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsRarityAsc)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsRarityDesc)));
        }

        [TestMethod]
        public void PopulateSingleGameDataSync_UsesCustomOrderForCanonicalAchievementsAndSetsFlag()
        {
            var gameId = Guid.NewGuid();
            var settings = new PlayniteAchievementsSettings();
            var plugin = new PlayniteAchievementsPlugin
            {
                Settings = settings
            };
            PlayniteAchievementsPlugin.Instance = plugin;

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService();
            achievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = "Custom Ordered Binding Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("One", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                    Achievement("Two", 25.0, unlocked: false),
                    Achievement("Three", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0))
                },
                AchievementOrder = new List<string> { "Three", "One" }
            };

            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();
            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            service.PopulateSingleGameDataSync(gameId);

            Assert.IsTrue(settings.ModernTheme.HasCustomAchievementOrder);
            AssertAchievementNames(settings.Achievements, "Three", "One", "Two");
            AssertAchievementNames(settings.AchievementDefaultOrder, "Three", "One", "Two");
        }

        [TestMethod]
        public void SelectedGameBuilder_UsesRarityTieBreakerForEqualUnlockTimes()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var gameId = Guid.NewGuid();
            var unlockTime = DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc);
            var ultra = Achievement("Ultra", 2.0, unlocked: true);
            ultra.UnlockTimeUtc = unlockTime;
            ultra.Points = 10;

            var rare = Achievement("Rare", 8.0, unlocked: true);
            rare.UnlockTimeUtc = unlockTime;
            rare.Points = 50;

            var common = Achievement("Common", 80.0, unlocked: true);
            common.UnlockTimeUtc = unlockTime;
            common.Points = 100;

            var state = SelectedGameRuntimeStateBuilder.Build(
                gameId,
                new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Game = new Game { Id = gameId, Name = "Tie Break Game" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail> { common, rare, ultra }
                });

            CollectionAssert.AreEqual(
                new[] { "Ultra", "Rare", "Common" },
                state.AchievementsNewestFirst.Select(a => a.DisplayName).ToArray());
            CollectionAssert.AreEqual(
                new[] { "Ultra", "Rare", "Common" },
                state.AchievementsOldestFirst.Select(a => a.DisplayName).ToArray());
        }

        [TestMethod]
        public void DynamicSelectedGameBindings_FilterSortAndDirectionPersistAcrossSelectionChanges()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var changedProperties = TrackPropertyChanges(context.Settings);

            var firstGameId = Guid.NewGuid();
            var secondGameId = Guid.NewGuid();

            context.AchievementDataService.GameDataById[firstGameId] = new GameAchievementData
            {
                PlayniteGameId = firstGameId,
                Game = new Game { Id = firstGameId, Name = "First Dynamic Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Alpha Locked", 80.0, unlocked: false),
                    Achievement("Bravo Rare", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0)),
                    Achievement("Charlie Ultra", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 3, 9, 0, 0))
                }
            };

            context.AchievementDataService.GameDataById[secondGameId] = new GameAchievementData
            {
                PlayniteGameId = secondGameId,
                Game = new Game { Id = secondGameId, Name = "Second Dynamic Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Delta Common", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 4, 9, 0, 0)),
                    Achievement("Echo Ultra", 1.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                    Achievement("Foxtrot Locked", 25.0, unlocked: false)
                }
            };

            context.Service.PopulateSingleGameDataSync(firstGameId);

            Assert.AreEqual(DynamicThemeViewKeys.All, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Default, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Descending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Charlie Ultra", "Bravo Rare", "Alpha Locked");
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicAchievements)));

            context.Settings.SetDynamicAchievementsFilterCommand.Execute("uNlOcKeD");
            context.Settings.SortDynamicAchievementsCommand.Execute("rArItY");
            context.Settings.SetDynamicAchievementsSortDirectionCommand.Execute("aScEnDiNg");

            Assert.AreEqual(DynamicThemeViewKeys.Unlocked, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Rarity, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            Assert.AreEqual("Unlocked", context.Settings.DynamicAchievementsFilterLabel);
            Assert.AreEqual("Rarity", context.Settings.DynamicAchievementsSortLabel);
            Assert.AreEqual("Ascending", context.Settings.DynamicAchievementsSortDirectionLabel);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Charlie Ultra", "Bravo Rare");
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterKey)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicAchievementsSortKey)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicAchievementsSortDirectionKey)));

            context.Service.PopulateSingleGameDataSync(secondGameId);

            Assert.AreEqual(DynamicThemeViewKeys.Unlocked, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Rarity, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Echo Ultra", "Delta Common");

            var beforeInvalid = context.Settings.DynamicAchievements.Select(item => item.DisplayName).ToArray();
            changedProperties.Clear();
            context.Logger.DebugMessages.Clear();

            context.Settings.SetDynamicAchievementsFilterCommand.Execute("not-a-filter");

            AssertAchievementNames(context.Settings.DynamicAchievements, beforeInvalid);
            Assert.AreEqual(DynamicThemeViewKeys.Unlocked, context.Settings.DynamicAchievementsFilterKey);
            Assert.IsFalse(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterKey)));
            Assert.IsTrue(context.Logger.DebugMessages.Any(message =>
                message.Contains(nameof(PlayniteAchievementsSettings.SetDynamicAchievementsFilterCommand))));
        }

        [TestMethod]
        public void DynamicLibraryAchievements_FilterByEffectiveProviderAndResortWithoutRebuildingRawData()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var changedProperties = TrackPropertyChanges(context.Settings);

            var battleNetGameId = Guid.NewGuid();
            var steamGameId = Guid.NewGuid();
            context.AchievementDataService.AllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = battleNetGameId,
                    ProviderKey = "Exophase",
                    ProviderPlatformKey = "BattleNet",
                    Game = new Game
                    {
                        Id = battleNetGameId,
                        Name = "BattleNet Game",
                        LastActivity = Utc(2026, 4, 7, 12, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("BN Common", 80.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 5, 10, 0, 0)),
                        Achievement("BN Rare", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 10, 0, 0))
                    }
                },
                new GameAchievementData
                {
                    PlayniteGameId = steamGameId,
                    ProviderKey = "Steam",
                    Game = new Game
                    {
                        Id = steamGameId,
                        Name = "Steam Game",
                        LastActivity = Utc(2026, 4, 9, 12, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Steam Ultra", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 6, 10, 0, 0))
                    }
                }
            };

            context.Settings.OpenAchievementWindow.Execute(null);

            Assert.AreEqual(DynamicThemeViewKeys.All, context.Settings.DynamicLibraryAchievementsProviderKey);
            Assert.AreEqual(DynamicThemeViewKeys.UnlockTime, context.Settings.DynamicLibraryAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Descending, context.Settings.DynamicLibraryAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "Steam Ultra", "BN Common", "BN Rare");
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicLibraryAchievements)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicGameSummaries)));

            changedProperties.Clear();
            context.Settings.FilterDynamicLibraryAchievementsByProviderCommand.Execute("bAtTlEnEt");

            Assert.AreEqual("BattleNet", context.Settings.DynamicLibraryAchievementsProviderKey);
            Assert.AreEqual("BattleNet", context.Settings.DynamicLibraryAchievementsProviderLabel);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "BN Common", "BN Rare");
            CollectionAssert.AreEqual(
                new[] { "BattleNet", "BattleNet" },
                context.Settings.DynamicLibraryAchievements.Select(item => item.ProviderKey).ToArray());
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProviderKey)));

            changedProperties.Clear();
            context.Settings.SortDynamicLibraryAchievementsCommand.Execute("RaRiTy");
            context.Settings.SetDynamicLibraryAchievementsSortDirectionCommand.Execute("ascending");

            Assert.AreEqual(DynamicThemeViewKeys.Rarity, context.Settings.DynamicLibraryAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicLibraryAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "BN Rare", "BN Common");
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortKey)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortDirectionKey)));
        }

        [TestMethod]
        public void DynamicGameSummaries_SortProjectMetadataAndKeepProviderStateIndependent()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var changedProperties = TrackPropertyChanges(context.Settings);

            var battleNetGameId = Guid.NewGuid();
            var steamGameId = Guid.NewGuid();
            var gogGameId = Guid.NewGuid();
            context.AchievementDataService.AllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = battleNetGameId,
                    ProviderKey = "Exophase",
                    ProviderPlatformKey = "BattleNet",
                    Game = new Game
                    {
                        Id = battleNetGameId,
                        Name = "BattleNet Summary",
                        LastActivity = Utc(2026, 4, 7, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("BN Unlocked", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 2, 9, 0, 0)),
                        Achievement("BN Locked", 80.0, unlocked: false)
                    }
                },
                new GameAchievementData
                {
                    PlayniteGameId = steamGameId,
                    ProviderKey = "Steam",
                    Game = new Game
                    {
                        Id = steamGameId,
                        Name = "Steam Summary",
                        LastActivity = Utc(2026, 4, 9, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Steam First", 25.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 30, 9, 0, 0)),
                        Achievement("Steam Latest", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 1, 9, 0, 0)),
                        Achievement("Steam Locked", 75.0, unlocked: false)
                    }
                },
                new GameAchievementData
                {
                    PlayniteGameId = gogGameId,
                    ProviderKey = "GOG",
                    Game = new Game
                    {
                        Id = gogGameId,
                        Name = "GOG Summary",
                        LastActivity = Utc(2026, 4, 1, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("GOG First", 45.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 3, 9, 0, 0)),
                        Achievement("GOG Second", 25.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 4, 9, 0, 0)),
                        Achievement("GOG Latest", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 5, 9, 0, 0)),
                        Achievement("GOG Locked", 80.0, unlocked: false)
                    }
                }
            };

            context.Settings.OpenAchievementWindow.Execute(null);

            Assert.AreEqual(DynamicThemeViewKeys.LastUnlock, context.Settings.DynamicGameSummariesSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Descending, context.Settings.DynamicGameSummariesSortDirectionKey);
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "GOG Summary", "BattleNet Summary", "Steam Summary");

            context.Settings.FilterDynamicLibraryAchievementsByProviderCommand.Execute("BattleNet");
            context.Settings.FilterDynamicGameSummariesByProviderCommand.Execute("sTeAm");

            Assert.AreEqual("BattleNet", context.Settings.DynamicLibraryAchievementsProviderKey);
            Assert.AreEqual("Steam", context.Settings.DynamicGameSummariesProviderKey);
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "Steam Summary");

            context.Settings.FilterDynamicGameSummariesByProviderCommand.Execute("All");

            Assert.AreEqual("BattleNet", context.Settings.DynamicLibraryAchievementsProviderKey);
            Assert.AreEqual(DynamicThemeViewKeys.All, context.Settings.DynamicGameSummariesProviderKey);

            var battleNetSummary = FindSummary(context.Settings.DynamicGameSummaries, battleNetGameId);
            Assert.AreEqual("BattleNet", battleNetSummary.ProviderKey);
            Assert.AreEqual("BattleNet", battleNetSummary.ProviderName);
            Assert.AreEqual(Utc(2026, 4, 7, 8, 0, 0), battleNetSummary.LastPlayed);
            Assert.AreEqual(1, battleNetSummary.UnlockedCount);
            Assert.AreEqual(2, battleNetSummary.AchievementCount);

            changedProperties.Clear();
            context.Settings.SortDynamicGameSummariesCommand.Execute("lAsTpLaYeD");

            AssertSummaryNames(context.Settings.DynamicGameSummaries, "Steam Summary", "BattleNet Summary", "GOG Summary");
            Assert.AreEqual(DynamicThemeViewKeys.LastPlayed, context.Settings.DynamicGameSummariesSortKey);
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortKey)));

            changedProperties.Clear();
            context.Settings.SetDynamicGameSummariesSortDirectionCommand.Execute("ascending");

            AssertSummaryNames(context.Settings.DynamicGameSummaries, "GOG Summary", "BattleNet Summary", "Steam Summary");
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicGameSummariesSortDirectionKey);
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortDirectionKey)));

            changedProperties.Clear();
            context.Settings.SortDynamicGameSummariesCommand.Execute("uNlOcKeDcOuNt");
            context.Settings.SetDynamicGameSummariesSortDirectionCommand.Execute("descending");

            AssertSummaryNames(context.Settings.DynamicGameSummaries, "GOG Summary", "Steam Summary", "BattleNet Summary");
            Assert.AreEqual(DynamicThemeViewKeys.UnlockedCount, context.Settings.DynamicGameSummariesSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Descending, context.Settings.DynamicGameSummariesSortDirectionKey);
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.DynamicGameSummaries)));
        }

        [TestMethod]
        public void ClearSingleGameThemeProperties_ResetsAndPublishesRareAndUltraRare()
        {
            var settings = new PlayniteAchievementsSettings();
            settings.ModernTheme.Common = new AchievementRarityStats { Total = 3, Unlocked = 1, Locked = 2 };
            settings.ModernTheme.Uncommon = new AchievementRarityStats { Total = 2, Unlocked = 1, Locked = 1 };
            settings.ModernTheme.Rare = new AchievementRarityStats { Total = 4, Unlocked = 2, Locked = 2 };
            settings.ModernTheme.UltraRare = new AchievementRarityStats { Total = 1, Unlocked = 1, Locked = 0 };
            settings.ModernTheme.RareAndUltraRare = new AchievementRarityStats { Total = 5, Unlocked = 3, Locked = 2 };
            settings.ModernTheme.AchievementDefaultOrder = new List<AchievementDetail> { Achievement("Default", 75.0, unlocked: true) };
            settings.ModernTheme.AllAchievements = new List<AchievementDetail> { Achievement("All", 75.0, unlocked: true) };
            settings.ModernTheme.AchievementsNewestFirst = new List<AchievementDetail> { Achievement("Newest", 2.0, unlocked: true) };
            settings.ModernTheme.AchievementsOldestFirst = new List<AchievementDetail> { Achievement("Oldest", 25.0, unlocked: true) };
            settings.ModernTheme.AchievementsRarityAsc = new List<AchievementDetail> { Achievement("Rarest", 2.0, unlocked: true) };
            settings.ModernTheme.AchievementsRarityDesc = new List<AchievementDetail> { Achievement("Commonest", 75.0, unlocked: true) };

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService();
            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();
            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            var changedProperties = TrackPropertyChanges(settings);

            service.ClearSingleGameThemeProperties();

            AssertStat(settings.ModernTheme.Common, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.ModernTheme.Uncommon, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.ModernTheme.Rare, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.ModernTheme.UltraRare, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.ModernTheme.RareAndUltraRare, total: 0, unlocked: 0, locked: 0);
            Assert.AreEqual(0, settings.AchievementDefaultOrder.Count);
            Assert.AreEqual(0, settings.Achievements.Count);
            Assert.AreEqual(0, settings.AchievementsNewestFirst.Count);
            Assert.AreEqual(0, settings.AchievementsOldestFirst.Count);
            Assert.AreEqual(0, settings.AchievementsRarityAsc.Count);
            Assert.AreEqual(0, settings.AchievementsRarityDesc.Count);
            Assert.IsFalse(settings.ModernTheme.HasCustomAchievementOrder);

            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Common)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Uncommon)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Rare)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.UltraRare)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.RareAndUltraRare)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementDefaultOrder)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Achievements)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsNewestFirst)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsOldestFirst)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsRarityAsc)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AchievementsRarityDesc)));
        }

        private static ServiceTestContext CreateServiceContext()
        {
            var settings = new PlayniteAchievementsSettings();
            var plugin = new PlayniteAchievementsPlugin
            {
                Settings = settings
            };
            PlayniteAchievementsPlugin.Instance = plugin;

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService();
            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();
            var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            return new ServiceTestContext(settings, achievementDataService, logger, service);
        }

        private static HashSet<string> TrackPropertyChanges(INotifyPropertyChanged source)
        {
            var changedProperties = new HashSet<string>(StringComparer.Ordinal);
            if (source == null)
            {
                return changedProperties;
            }

            source.PropertyChanged += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args?.PropertyName))
                {
                    changedProperties.Add(args.PropertyName);
                }
            };

            return changedProperties;
        }

        private static void AssertAchievementNames(IEnumerable<AchievementDetail> achievements, params string[] expectedDisplayNames)
        {
            CollectionAssert.AreEqual(
                expectedDisplayNames,
                (achievements ?? Enumerable.Empty<AchievementDetail>())
                    .Select(item => item?.DisplayName)
                    .ToArray());
        }

        private static void AssertSummaryNames(IEnumerable<GameAchievementSummary> summaries, params string[] expectedNames)
        {
            CollectionAssert.AreEqual(
                expectedNames,
                (summaries ?? Enumerable.Empty<GameAchievementSummary>())
                    .Select(item => item?.Name)
                    .ToArray());
        }

        private static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
        {
            return DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, second), DateTimeKind.Utc);
        }

        private static AchievementDetail Achievement(
            string name,
            double? percent,
            bool unlocked,
            string providerKey = null,
            DateTime? unlockTimeUtc = null)
        {
            var normalizedPercent = NormalizePercent(percent);
            return new AchievementDetail
            {
                ApiName = name,
                DisplayName = name,
                ProviderKey = providerKey,
                GlobalPercentUnlocked = normalizedPercent,
                Rarity = normalizedPercent.HasValue
                    ? PercentRarityHelper.GetRarityTier(normalizedPercent.Value)
                    : RarityTier.Common,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTimeUtc.HasValue
                    ? DateTime.SpecifyKind(unlockTimeUtc.Value, DateTimeKind.Utc)
                    : unlocked
                        ? Utc(2026, 3, 1, 12, 0, 0)
                        : (DateTime?)null
            };
        }

        private static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        private static void AssertStat(AchievementRarityStats stats, int total, int unlocked, int locked)
        {
            Assert.IsNotNull(stats);
            Assert.AreEqual(total, stats.Total);
            Assert.AreEqual(unlocked, stats.Unlocked);
            Assert.AreEqual(locked, stats.Locked);
        }

        private static GameAchievementSummary FindSummary(IEnumerable<GameAchievementSummary> items, Guid gameId)
        {
            foreach (var item in items)
            {
                if (item != null && item.GameId == gameId)
                {
                    return item;
                }
            }

            Assert.Fail($"Expected summary for game {gameId}.");
            return null;
        }

        private sealed class ServiceTestContext : IDisposable
        {
            public ServiceTestContext(
                PlayniteAchievementsSettings settings,
                AchievementDataService achievementDataService,
                FakeLogger logger,
                ThemeIntegrationService service)
            {
                Settings = settings;
                AchievementDataService = achievementDataService;
                Logger = logger;
                Service = service;
            }

            public PlayniteAchievementsSettings Settings { get; }

            public AchievementDataService AchievementDataService { get; }

            public FakeLogger Logger { get; }

            public ThemeIntegrationService Service { get; }

            public void Dispose()
            {
                Service?.Dispose();
            }
        }

        private sealed class FakeLogger : ILogger
        {
            public List<string> DebugMessages { get; } = new List<string>();

            public void Debug(string message)
            {
                DebugMessages.Add(message);
            }

            public void Debug(Exception exception, string message)
            {
                DebugMessages.Add(message);
            }

            public void Error(string message)
            {
            }

            public void Error(Exception exception, string message)
            {
            }

            public void Info(string message)
            {
            }

            public void Info(Exception exception, string message)
            {
            }

            public void Trace(string message)
            {
            }

            public void Trace(Exception exception, string message)
            {
            }

            public void Warn(string message)
            {
            }

            public void Warn(Exception exception, string message)
            {
            }
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public IMainViewAPI MainView => null;

            public IGameDatabaseAPI Database => null;

            public IDialogsFactory Dialogs => null;

            public IPlaynitePathsAPI Paths => null;

            public INotificationsAPI Notifications => null;

            public IPlayniteInfoAPI ApplicationInfo => null;

            public IWebViewFactory WebViews => null;

            public IResourceProvider Resources => null;

            public IUriHandlerAPI UriHandler => null;

            public IPlayniteSettingsAPI ApplicationSettings => null;

            public IAddons Addons => null;

            public IEmulationAPI Emulation => null;

            public string ExpandGameVariables(Game game, string source)
            {
                return source;
            }

            public string ExpandGameVariables(Game game, string source, string fallbackValue)
            {
                return source ?? fallbackValue;
            }

            public GameAction ExpandGameVariables(Game game, GameAction source)
            {
                return source;
            }

            public void StartGame(Guid id)
            {
            }

            public void InstallGame(Guid id)
            {
            }

            public void UninstallGame(Guid id)
            {
            }

            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args)
            {
            }

            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args)
            {
            }

            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args)
            {
            }
        }
    }
}
