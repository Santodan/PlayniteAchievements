using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

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
        public void PopulateSingleGameDataSync_UsesVisibleAchievementProjection()
        {
            var gameId = Guid.NewGuid();
            var settings = new PlayniteAchievementsSettings();
            var plugin = new PlayniteAchievementsPlugin
            {
                Settings = settings
            };
            PlayniteAchievementsPlugin.Instance = plugin;

            var rawVisible = Achievement("Visible", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0));
            var rawAlsoVisible = Achievement("Visible Too", 25.0, unlocked: false);
            var rawIgnored = Achievement("Filtered Entry", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0));
            rawIgnored.IsFiltered = true;

            var visibleData = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = "Visible Projection Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail> { rawVisible, rawAlsoVisible }
            };

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService();
            achievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = visibleData.Game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail> { rawVisible, rawAlsoVisible, rawIgnored }
            };
            achievementDataService.VisibleGameDataById[gameId] = visibleData;

            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();
            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            service.PopulateSingleGameDataSync(gameId);

            Assert.AreEqual(2, settings.Achievements.Count);
            AssertAchievementNames(settings.Achievements, "Visible", "Visible Too");
            Assert.AreEqual(50d, settings.ProgressPercentage);
        }

        [TestMethod]
        public void RequestUpdate_BeforeSynchronousPopulate_DoesNotClearPublishedCachedSelection()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var context = CreateServiceContext(dispatcher);
            var gameId = Guid.NewGuid();
            var hasAchievementStates = new List<bool>();

            context.Settings.PropertyChanged += (_, args) =>
            {
                if (args?.PropertyName == nameof(PlayniteAchievementsSettings.HasAchievements))
                {
                    hasAchievementStates.Add(context.Settings.HasAchievements);
                }
            };

            context.AchievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = "Queued Reconcile Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Ready One", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 1, 9, 0, 0)),
                    Achievement("Ready Two", 25.0, unlocked: false)
                }
            };

            context.Service.RequestUpdate(gameId);
            context.Service.PopulateSingleGameDataSync(gameId);

            Assert.IsTrue(context.Settings.HasAchievements);
            Assert.AreEqual(2, context.Settings.Achievements.Count);

            DrainDispatcher(dispatcher);

            Assert.IsTrue(context.Settings.HasAchievements);
            Assert.AreEqual(2, context.Settings.Achievements.Count);
            CollectionAssert.DoesNotContain(hasAchievementStates, false);
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
        public void OpenAchievementWindow_UsesVisibleThemeLibraryProjection()
        {
            using var context = CreateServiceContext();

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Library Projection Game",
                LastActivity = Utc(2026, 4, 9, 8, 0, 0)
            };

            var visibleUnlocked = Achievement("Visible Unlock", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 1, 9, 0, 0));
            var visibleLocked = Achievement("Visible Locked", 75.0, unlocked: false);
            var ignored = Achievement("Filtered Unlock", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 2, 9, 0, 0));
            ignored.IsFiltered = true;

            context.AchievementDataService.AllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    ProviderKey = "Steam",
                    Game = game,
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail> { visibleUnlocked, visibleLocked, ignored }
                }
            };
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    ProviderKey = "Steam",
                    Game = game,
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail> { visibleUnlocked, visibleLocked }
                }
            };

            context.Settings.OpenAchievementWindow.Execute(null);

            Assert.AreEqual(2, context.Settings.DynamicLibraryAchievements.Count);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "Visible Unlock", "Visible Locked");

            var summary = FindSummary(context.Settings.DynamicGameSummaries, gameId);
            Assert.AreEqual(1, summary.UnlockedCount);
            Assert.AreEqual(2, summary.AchievementCount);
        }

        [TestMethod]
        public void OpenAchievementWindow_PublishesModernScoresWithoutChangingLegacyScore()
        {
            using var context = CreateServiceContext();

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Modern Score Game",
                LastActivity = Utc(2026, 4, 9, 8, 0, 0)
            };
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                ProviderKey = "Steam",
                Game = game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Common Unlock", 75.0, unlocked: true),
                    Achievement("Uncommon Unlock", 35.0, unlocked: true),
                    Achievement("Rare Unlock", 8.0, unlocked: true),
                    Achievement("Ultra Unlock", 2.5, unlocked: true),
                    Achievement("Ultra Locked", 2.0, unlocked: false)
                }
            };
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData> { data };

            var expectedModernScores = AchievementScoreCalculator.CalculateModernScores(context.AchievementDataService.VisibleAllGameData);
            var expectedLegacyLevel = AchievementLevelCalculator.CalculateLegacy(225);

            context.Settings.OpenAchievementWindow.Execute(null);

            Assert.AreEqual(315, context.Settings.CollectorScore);
            Assert.AreEqual(expectedModernScores.PrestigeScore, context.Settings.PrestigeScore);
            Assert.AreEqual(expectedModernScores.CollectorLevel.DisplayLevel, context.Settings.CollectorLevel);
            Assert.AreEqual(expectedModernScores.CollectorLevel.LevelProgress, context.Settings.CollectorLevelProgress);
            Assert.AreEqual(expectedModernScores.CollectorLevel.Rank, context.Settings.CollectorRank);
            Assert.AreEqual(expectedModernScores.PrestigeLevel.DisplayLevel, context.Settings.PrestigeLevel);
            Assert.AreEqual(expectedModernScores.PrestigeLevel.LevelProgress, context.Settings.PrestigeLevelProgress);
            Assert.AreEqual(expectedModernScores.PrestigeLevel.Rank, context.Settings.PrestigeRank);

            Assert.AreEqual("225", context.Settings.GSScore);
            Assert.AreEqual(expectedLegacyLevel.Level.ToString(), context.Settings.GSLevel);
            Assert.AreEqual(expectedLegacyLevel.LevelProgress, context.Settings.GSLevelProgress);
            Assert.AreEqual(expectedLegacyLevel.Rank, context.Settings.GSRank);
            Assert.AreEqual(expectedLegacyLevel.Level, context.Settings.Level);
            Assert.AreEqual(expectedLegacyLevel.LevelProgress, context.Settings.LevelProgress);
            Assert.AreEqual(expectedLegacyLevel.Rank, context.Settings.Rank);
        }

        [TestMethod]
        public void OpenAchievementWindow_DuringRebuildPreservesExistingScoresForEmptyProjection()
        {
            using var context = CreateServiceContext();
            context.RefreshRuntime.IsRebuilding = true;

            context.Settings.ModernTheme.CollectorScore = 1234;
            context.Settings.ModernTheme.CollectorLevel = 7;
            context.Settings.ModernTheme.CollectorLevelProgress = 42.5;
            context.Settings.ModernTheme.CollectorRank = "Silver1";
            context.Settings.ModernTheme.PrestigeScore = 5678;
            context.Settings.ModernTheme.PrestigeLevel = 11;
            context.Settings.ModernTheme.PrestigeLevelProgress = 66.25;
            context.Settings.ModernTheme.PrestigeRank = "Gold2";
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData>();

            context.Settings.OpenAchievementWindow.Execute(null);

            Assert.AreEqual(1234, context.Settings.CollectorScore);
            Assert.AreEqual(7, context.Settings.CollectorLevel);
            Assert.AreEqual(42.5, context.Settings.CollectorLevelProgress);
            Assert.AreEqual("Silver1", context.Settings.CollectorRank);
            Assert.AreEqual(5678, context.Settings.PrestigeScore);
            Assert.AreEqual(11, context.Settings.PrestigeLevel);
            Assert.AreEqual(66.25, context.Settings.PrestigeLevelProgress);
            Assert.AreEqual("Gold2", context.Settings.PrestigeRank);
        }

        [TestMethod]
        public void OpenAchievementWindow_DuringRebuildAppliesPopulatedProjectionEvenWhenScoresDecrease()
        {
            using var context = CreateServiceContext();
            context.RefreshRuntime.IsRebuilding = true;

            context.Settings.ModernTheme.CollectorScore = 1234;
            context.Settings.ModernTheme.CollectorLevel = 7;
            context.Settings.ModernTheme.CollectorLevelProgress = 42.5;
            context.Settings.ModernTheme.CollectorRank = "Silver1";
            context.Settings.ModernTheme.PrestigeScore = 5678;
            context.Settings.ModernTheme.PrestigeLevel = 11;
            context.Settings.ModernTheme.PrestigeLevelProgress = 66.25;
            context.Settings.ModernTheme.PrestigeRank = "Gold2";
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = Guid.NewGuid(),
                    Game = new Game { Id = Guid.NewGuid(), Name = "Partial Rebuild Game" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Partial Common", 75.0, unlocked: true)
                    }
                }
            };

            var expectedScores = AchievementScoreCalculator.CalculateModernScores(context.AchievementDataService.VisibleAllGameData);

            context.Settings.OpenAchievementWindow.Execute(null);

            Assert.AreEqual(expectedScores.CollectorScore, context.Settings.CollectorScore);
            Assert.AreEqual(expectedScores.CollectorLevel.DisplayLevel, context.Settings.CollectorLevel);
            Assert.AreEqual(expectedScores.CollectorLevel.LevelProgress, context.Settings.CollectorLevelProgress);
            Assert.AreEqual(expectedScores.CollectorLevel.Rank, context.Settings.CollectorRank);
            Assert.AreEqual(expectedScores.PrestigeScore, context.Settings.PrestigeScore);
            Assert.AreEqual(expectedScores.PrestigeLevel.DisplayLevel, context.Settings.PrestigeLevel);
            Assert.AreEqual(expectedScores.PrestigeLevel.LevelProgress, context.Settings.PrestigeLevelProgress);
            Assert.AreEqual(expectedScores.PrestigeLevel.Rank, context.Settings.PrestigeRank);
        }

        [TestMethod]
        public void RefreshCompleted_RebuildsAllGamesScoresWithoutRestart()
        {
            using var context = CreateServiceContext();
            context.Settings.ModernTheme.CollectorScore = 15;
            context.Settings.ModernTheme.PrestigeScore = 15;

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Refresh Completed Game" };
            var refreshedData = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Common", 75.0, unlocked: true),
                    Achievement("Uncommon", 25.0, unlocked: true),
                    Achievement("Rare", 8.0, unlocked: true),
                    Achievement("Ultra", 2.0, unlocked: true)
                }
            };
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData> { refreshedData };
            var expectedScores = AchievementScoreCalculator.CalculateModernScores(context.AchievementDataService.VisibleAllGameData);

            context.RefreshCoordinator.ExecuteAsync(new RefreshRequest()).GetAwaiter().GetResult();

            Assert.AreEqual(expectedScores.CollectorScore, context.Settings.CollectorScore);
            Assert.AreEqual(expectedScores.CollectorLevel.DisplayLevel, context.Settings.CollectorLevel);
            Assert.AreEqual(expectedScores.CollectorLevel.Rank, context.Settings.CollectorRank);
            Assert.AreEqual(expectedScores.PrestigeScore, context.Settings.PrestigeScore);
            Assert.AreEqual(expectedScores.PrestigeLevel.DisplayLevel, context.Settings.PrestigeLevel);
            Assert.AreEqual(expectedScores.PrestigeLevel.Rank, context.Settings.PrestigeRank);
        }

        [TestMethod]
        public void CacheInvalidated_WhenNotRebuilding_RebuildsAllGamesScoresWithoutRestart()
        {
            using var context = CreateServiceContext();
            context.Settings.ModernTheme.CollectorScore = 15;
            context.Settings.ModernTheme.PrestigeScore = 15;

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Cache Invalidated Game" };
            var updatedData = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Common", 75.0, unlocked: true),
                    Achievement("Exact Ultra", 0.1, unlocked: true)
                }
            };
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData> { updatedData };
            var expectedScores = AchievementScoreCalculator.CalculateModernScores(context.AchievementDataService.VisibleAllGameData);

            context.RefreshRuntime.RaiseCacheInvalidated();

            Assert.AreEqual(expectedScores.CollectorScore, context.Settings.CollectorScore);
            Assert.AreEqual(expectedScores.CollectorLevel.DisplayLevel, context.Settings.CollectorLevel);
            Assert.AreEqual(expectedScores.CollectorLevel.Rank, context.Settings.CollectorRank);
            Assert.AreEqual(expectedScores.PrestigeScore, context.Settings.PrestigeScore);
            Assert.AreEqual(expectedScores.PrestigeLevel.DisplayLevel, context.Settings.PrestigeLevel);
            Assert.AreEqual(expectedScores.PrestigeLevel.Rank, context.Settings.PrestigeRank);
        }

        [TestMethod]
        public void Constructor_SeedsModernScoresFromPersistedSnapshotBeforeAllGamesLoad()
        {
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.LastAllGamesCollectorScore = 1234;
            settings.Persisted.LastAllGamesCollectorLevel = 7;
            settings.Persisted.LastAllGamesCollectorLevelProgress = 42.5;
            settings.Persisted.LastAllGamesCollectorRank = "Silver1";
            settings.Persisted.LastAllGamesPrestigeScore = 5678;
            settings.Persisted.LastAllGamesPrestigeLevel = 11;
            settings.Persisted.LastAllGamesPrestigeLevelProgress = 66.25;
            settings.Persisted.LastAllGamesPrestigeRank = "Gold2";
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

            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            Assert.AreEqual(1234, settings.CollectorScore);
            Assert.AreEqual(7, settings.CollectorLevel);
            Assert.AreEqual(42.5, settings.CollectorLevelProgress);
            Assert.AreEqual("Silver1", settings.CollectorRank);
            Assert.AreEqual(5678, settings.PrestigeScore);
            Assert.AreEqual(11, settings.PrestigeLevel);
            Assert.AreEqual(66.25, settings.PrestigeLevelProgress);
            Assert.AreEqual("Gold2", settings.PrestigeRank);
        }

        [TestMethod]
        public void Constructor_DoesNotOverwritePersistedSnapshotWithCachedDataSeed()
        {
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.LastAllGamesCollectorScore = 1234;
            settings.Persisted.LastAllGamesCollectorLevel = 7;
            settings.Persisted.LastAllGamesCollectorLevelProgress = 42.5;
            settings.Persisted.LastAllGamesCollectorRank = "Silver1";
            settings.Persisted.LastAllGamesPrestigeScore = 5678;
            settings.Persisted.LastAllGamesPrestigeLevel = 11;
            settings.Persisted.LastAllGamesPrestigeLevelProgress = 66.25;
            settings.Persisted.LastAllGamesPrestigeRank = "Gold2";
            var plugin = new PlayniteAchievementsPlugin
            {
                Settings = settings
            };
            PlayniteAchievementsPlugin.Instance = plugin;

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService
            {
                VisibleAllGameData = new List<GameAchievementData>
                {
                    new GameAchievementData
                    {
                        PlayniteGameId = Guid.NewGuid(),
                        Game = new Game { Id = Guid.NewGuid(), Name = "Different Cached Seed Game" },
                        HasAchievements = true,
                        Achievements = new List<AchievementDetail>
                        {
                            Achievement("Different Ultra", 0.1, unlocked: true)
                        }
                    }
                }
            };
            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();

            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            Assert.AreEqual(1234, settings.CollectorScore);
            Assert.AreEqual(7, settings.CollectorLevel);
            Assert.AreEqual(42.5, settings.CollectorLevelProgress);
            Assert.AreEqual("Silver1", settings.CollectorRank);
            Assert.AreEqual(5678, settings.PrestigeScore);
            Assert.AreEqual(11, settings.PrestigeLevel);
            Assert.AreEqual(66.25, settings.PrestigeLevelProgress);
            Assert.AreEqual("Gold2", settings.PrestigeRank);
        }

        [TestMethod]
        public void OpenAchievementWindow_PersistsFinalAllGamesScoreSnapshot()
        {
            using var context = CreateServiceContext();

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Persisted Score Game" };
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Common", 75.0, unlocked: true),
                    Achievement("Exact Ultra", 0.1, unlocked: true)
                }
            };
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData> { data };
            var expectedScores = AchievementScoreCalculator.CalculateModernScores(context.AchievementDataService.VisibleAllGameData);

            context.Settings.OpenAchievementWindow.Execute(null);

            Assert.AreEqual(expectedScores.CollectorScore, context.Settings.Persisted.LastAllGamesCollectorScore);
            Assert.AreEqual(expectedScores.CollectorLevel.DisplayLevel, context.Settings.Persisted.LastAllGamesCollectorLevel);
            Assert.AreEqual(expectedScores.CollectorLevel.LevelProgress, context.Settings.Persisted.LastAllGamesCollectorLevelProgress);
            Assert.AreEqual(expectedScores.CollectorLevel.Rank, context.Settings.Persisted.LastAllGamesCollectorRank);
            Assert.AreEqual(expectedScores.PrestigeScore, context.Settings.Persisted.LastAllGamesPrestigeScore);
            Assert.AreEqual(expectedScores.PrestigeLevel.DisplayLevel, context.Settings.Persisted.LastAllGamesPrestigeLevel);
            Assert.AreEqual(expectedScores.PrestigeLevel.LevelProgress, context.Settings.Persisted.LastAllGamesPrestigeLevelProgress);
            Assert.AreEqual(expectedScores.PrestigeLevel.Rank, context.Settings.Persisted.LastAllGamesPrestigeRank);
            Assert.AreSame(context.Settings, PlayniteAchievementsPlugin.Instance.Settings);
        }

        [TestMethod]
        public void Constructor_SeedsModernScoresFromCachedDataBeforeAllGamesLoad()
        {
            var settings = new PlayniteAchievementsSettings();
            var plugin = new PlayniteAchievementsPlugin
            {
                Settings = settings
            };
            PlayniteAchievementsPlugin.Instance = plugin;

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService
            {
                VisibleAllGameData = new List<GameAchievementData>
                {
                    new GameAchievementData
                    {
                        PlayniteGameId = Guid.NewGuid(),
                        Game = new Game { Id = Guid.NewGuid(), Name = "Seed Score Game" },
                        HasAchievements = true,
                        Achievements = new List<AchievementDetail>
                        {
                            Achievement("Exact Common", 75.0, unlocked: true),
                            Achievement("Exact Uncommon", 25.0, unlocked: true),
                            Achievement("Exact Rare", 8.0, unlocked: true),
                            Achievement("Exact Ultra", 0.1, unlocked: true)
                        }
                    }
                }
            };
            var expectedScores = AchievementScoreCalculator.CalculateModernScores(achievementDataService.VisibleAllGameData);
            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();

            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            Assert.AreEqual(expectedScores.CollectorScore, settings.CollectorScore);
            Assert.AreEqual(expectedScores.CollectorLevel.DisplayLevel, settings.CollectorLevel);
            Assert.AreEqual(expectedScores.CollectorLevel.Rank, settings.CollectorRank);
            Assert.AreEqual(expectedScores.PrestigeScore, settings.PrestigeScore);
            Assert.AreEqual(expectedScores.PrestigeLevel.DisplayLevel, settings.PrestigeLevel);
            Assert.AreEqual(expectedScores.PrestigeLevel.Rank, settings.PrestigeRank);
        }

        [TestMethod]
        public void EnsureAllGamesThemeDataLoaded_LightRequestUsesCachedSummaryData()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            context.AchievementDataService.CachedSummaryDataForTheme = new CachedSummaryData
            {
                Games = new List<CachedGameSummaryData>
                {
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = gameId,
                        ProviderKey = "Steam",
                        GameName = "Light Cached",
                        HasAchievements = true,
                        TotalAchievements = 2,
                        UnlockedAchievements = 1,
                        CommonCount = 1,
                        TotalCommonPossible = 2,
                        CollectionScore = 15,
                        PrestigeScore = 5
                    }
                },
                UnlockCountsByDateByGame = new Dictionary<Guid, Dictionary<DateTime, int>>
                {
                    [gameId] = new Dictionary<DateTime, int>
                    {
                        [Utc(2026, 4, 1, 0, 0, 0)] = 1
                    }
                }
            };
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = Guid.NewGuid(),
                    ProviderKey = "GOG",
                    Game = new Game { Name = "Hydrated Should Not Load" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Hydrated", 75.0, unlocked: true)
                    }
                }
            };

            var cachedCallsBefore = context.AchievementDataService.CachedSummaryDataForThemeCalls;
            var visibleCallsBefore = context.AchievementDataService.VisibleAllGameDataForThemeCalls;
            context.Service.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: false);

            Assert.AreEqual(cachedCallsBefore + 1, context.AchievementDataService.CachedSummaryDataForThemeCalls);
            Assert.AreEqual(visibleCallsBefore, context.AchievementDataService.VisibleAllGameDataForThemeCalls);
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "Light Cached");
            Assert.AreEqual(0, context.Settings.DynamicLibraryAchievements.Count);
            Assert.AreEqual(1, context.Settings.GameSummariesDesc.Count);
        }

        [TestMethod]
        public void EnsureAllGamesThemeDataLoaded_HeavyRequestUsesHydratedAchievementData()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            context.AchievementDataService.CachedSummaryDataForTheme = new CachedSummaryData
            {
                Games = new List<CachedGameSummaryData>
                {
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = Guid.NewGuid(),
                        ProviderKey = "Steam",
                        GameName = "Cached Should Not Load",
                        HasAchievements = true,
                        TotalAchievements = 1,
                        UnlockedAchievements = 1,
                        CommonCount = 1,
                        TotalCommonPossible = 1
                    }
                }
            };
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    ProviderKey = "GOG",
                    Game = new Game { Id = gameId, Name = "Heavy Hydrated" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Hydrated Achievement", 75.0, unlocked: true)
                    }
                }
            };

            var cachedCallsBefore = context.AchievementDataService.CachedSummaryDataForThemeCalls;
            var visibleCallsBefore = context.AchievementDataService.VisibleAllGameDataForThemeCalls;
            context.Service.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: true);

            Assert.AreEqual(cachedCallsBefore, context.AchievementDataService.CachedSummaryDataForThemeCalls);
            Assert.AreEqual(visibleCallsBefore + 1, context.AchievementDataService.VisibleAllGameDataForThemeCalls);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "Hydrated Achievement");
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "Heavy Hydrated");
        }

        [TestMethod]
        public async Task NotifyCustomDataChanged_CoalescesLightLibraryRefreshes()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            context.AchievementDataService.CachedSummaryDataForTheme = new CachedSummaryData
            {
                Games = new List<CachedGameSummaryData>
                {
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = gameId,
                        ProviderKey = "Steam",
                        GameName = "Before Coalesce",
                        HasAchievements = true,
                        TotalAchievements = 2,
                        UnlockedAchievements = 1,
                        CommonCount = 1,
                        TotalCommonPossible = 2
                    }
                }
            };

            var cachedCallsBefore = context.AchievementDataService.CachedSummaryDataForThemeCalls;
            var visibleCallsBefore = context.AchievementDataService.VisibleAllGameDataForThemeCalls;
            context.Service.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: false);
            Assert.AreEqual(cachedCallsBefore + 1, context.AchievementDataService.CachedSummaryDataForThemeCalls);

            context.AchievementDataService.CachedSummaryDataForTheme = new CachedSummaryData
            {
                Games = new List<CachedGameSummaryData>
                {
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = gameId,
                        ProviderKey = "Steam",
                        GameName = "After Coalesce",
                        HasAchievements = true,
                        TotalAchievements = 3,
                        UnlockedAchievements = 2,
                        RareCount = 2,
                        TotalRarePossible = 3
                    }
                }
            };

            context.Service.NotifyCustomDataChanged(gameId);
            context.Service.NotifyCustomDataChanged(gameId);
            context.Service.NotifyCustomDataChanged(gameId);

            await Task.Delay(700);

            Assert.AreEqual(cachedCallsBefore + 2, context.AchievementDataService.CachedSummaryDataForThemeCalls);
            Assert.AreEqual(visibleCallsBefore, context.AchievementDataService.VisibleAllGameDataForThemeCalls);
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "After Coalesce");
        }

        [TestMethod]
        public async Task NotifyCustomDataChanged_RefreshesLoadedThemeBindings()
        {
            using var context = CreateServiceContext();

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Immediate Theme Refresh Game",
                LastActivity = Utc(2026, 4, 9, 8, 0, 0)
            };

            var initialVisibleOne = Achievement("Visible One", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 1, 9, 0, 0));
            var initialVisibleTwo = Achievement("Visible Two", 75.0, unlocked: false);
            var initialSelected = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                ProviderKey = "Steam",
                HasAchievements = true,
                Achievements = new List<AchievementDetail> { initialVisibleOne, initialVisibleTwo }
            };

            context.AchievementDataService.VisibleGameDataById[gameId] = initialSelected;
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData> { initialSelected };

            context.Settings.OpenAchievementWindow.Execute(null);
            context.Service.PopulateSingleGameDataSync(gameId);

            Assert.AreEqual(2, context.Settings.DynamicLibraryAchievements.Count);
            Assert.AreEqual(2, context.Settings.Achievements.Count);

            var updatedVisible = Achievement("Visible One", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 1, 9, 0, 0));
            var updatedSelected = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = game,
                ProviderKey = "Steam",
                HasAchievements = true,
                Achievements = new List<AchievementDetail> { updatedVisible }
            };

            context.AchievementDataService.VisibleGameDataById[gameId] = updatedSelected;
            context.AchievementDataService.VisibleAllGameData = new List<GameAchievementData> { updatedSelected };

            context.Service.NotifyCustomDataChanged(gameId);

            Assert.AreEqual(2, context.Settings.DynamicLibraryAchievements.Count);
            await WaitForConditionAsync(() => context.Settings.DynamicLibraryAchievements.Count == 1);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "Visible One");
            await WaitForConditionAsync(() => context.Settings.Achievements.Count == 1);
            AssertAchievementNames(context.Settings.Achievements, "Visible One");

            var summary = FindSummary(context.Settings.DynamicGameSummaries, gameId);
            Assert.AreEqual(1, summary.UnlockedCount);
            Assert.AreEqual(1, summary.AchievementCount);
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
            AssertAchievementNames(context.Settings.DynamicAchievements, "Alpha Locked", "Bravo Rare", "Charlie Ultra");
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
        public void DynamicBindings_ExposeOptionsAndWritableKeysForComboBoxes()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            context.AchievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                ProviderKey = "Steam",
                Game = new Game { Id = gameId, Name = "Writable Dynamic Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Bravo Hidden", 8.0, unlocked: false),
                    Achievement("Alpha Unlocked", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0)),
                    Achievement("Charlie Locked", 25.0, unlocked: false)
                }
            };
            context.AchievementDataService.GameDataById[gameId].Achievements[0].Hidden = true;
            context.AchievementDataService.GameDataById[gameId].Achievements[2].ProgressNum = 3;
            context.AchievementDataService.GameDataById[gameId].Achievements[2].ProgressDenom = 10;

            context.Service.PopulateSingleGameDataSync(gameId);

            CollectionAssert.Contains(
                context.Settings.DynamicAchievementsFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.InProgress);
            CollectionAssert.Contains(
                context.Settings.DynamicAchievementsSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Name);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicAchievementsSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Game);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicAchievementsSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Provider);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicAchievementsSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.RarityPercent);
            CollectionAssert.Contains(
                context.Settings.DynamicAchievementsSortDirectionOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Ascending);
            Assert.AreEqual(
                "Name",
                context.Settings.DynamicAchievementsSortOptions
                    .Single(item => item.Key == DynamicThemeViewKeys.Name)
                    .Label);

            context.Settings.DynamicAchievementsFilterKey = DynamicThemeViewKeys.InProgress;
            context.Settings.DynamicAchievementsSortKey = DynamicThemeViewKeys.Name;
            context.Settings.DynamicAchievementsSortDirectionKey = DynamicThemeViewKeys.Ascending;

            Assert.AreEqual(DynamicThemeViewKeys.InProgress, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Name, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Charlie Locked");
        }

        [TestMethod]
        public void DynamicBindings_ApplyDefaultsUntilUserSelectionAndResetToDefaults()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            context.AchievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                ProviderKey = "Steam",
                Game = new Game { Id = gameId, Name = "Default Dynamic Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Hidden Locked Rare", 8.0, unlocked: false),
                    Achievement("Visible Locked Common", 80.0, unlocked: false),
                    Achievement("Visible Unlocked Ultra", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 1, 9, 0, 0))
                }
            };
            context.AchievementDataService.GameDataById[gameId].Achievements[0].Hidden = true;

            context.Settings.DynamicAchievementsDefaultFilterKey = DynamicThemeViewKeys.Unlocked;
            context.Settings.DynamicAchievementsDefaultSortKey = DynamicThemeViewKeys.Rarity;
            context.Settings.DynamicAchievementsDefaultSortDirectionKey = DynamicThemeViewKeys.Ascending;
            context.Service.PopulateSingleGameDataSync(gameId);

            Assert.AreEqual(DynamicThemeViewKeys.Unlocked, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Rarity, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Visible Unlocked Ultra");

            context.Settings.DynamicAchievementsFilterKey = DynamicThemeViewKeys.Locked;
            context.Settings.DynamicAchievementsDefaultFilterKey = DynamicThemeViewKeys.Hidden;

            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Hidden, context.Settings.DynamicAchievementsDefaultFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Locked Rare", "Visible Locked Common");

            context.Settings.ResetDynamicAchievementsCommand.Execute(null);

            Assert.AreEqual(DynamicThemeViewKeys.Hidden, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Rarity, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Locked Rare");
        }

        [TestMethod]
        public void DynamicBindings_AcceptCompositeFilterKeys()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            context.AchievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                ProviderKey = "Steam",
                Game = new Game { Id = gameId, Name = "Composite Filter Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Common Locked", 80.0, unlocked: false),
                    Achievement("Rare Locked", 8.0, unlocked: false),
                    Achievement("Ultra Locked", 2.0, unlocked: false),
                    Achievement("Rare Unlocked", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 2, 9, 0, 0))
                }
            };

            context.Service.PopulateSingleGameDataSync(gameId);
            context.Settings.DynamicAchievementsFilterKey = "locked, rare, ultrarare";

            Assert.AreEqual("Locked+Rare+UltraRare", context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual("Locked + Rare + Ultra Rare", context.Settings.DynamicAchievementsFilterLabel);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Rare Locked", "Ultra Locked");
            CollectionAssert.Contains(
                context.Settings.DynamicAchievementsFilterOptions.Select(item => item.Key).ToList(),
                "Locked+Rare+UltraRare");
            Assert.AreEqual(
                "Locked + Rare + Ultra Rare",
                context.Settings.DynamicAchievementsFilterOptions
                    .Single(item => item.Key == "Locked+Rare+UltraRare")
                    .Label);
        }

        [TestMethod]
        public void DynamicBindings_GroupedFilterKeysComposeCompositeFilters()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            var hiddenRare = Achievement("Hidden Rare Locked", 8.0, unlocked: false);
            hiddenRare.Hidden = true;
            var commonLocked = Achievement("Common Locked", 80.0, unlocked: false);
            var rareUnlocked = Achievement("Rare Unlocked", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 2, 9, 0, 0));

            context.AchievementDataService.GameDataById[gameId] = new GameAchievementData
            {
                PlayniteGameId = gameId,
                ProviderKey = "Steam",
                Game = new Game { Id = gameId, Name = "Grouped Filter Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail> { hiddenRare, commonLocked, rareUnlocked }
            };

            context.Service.PopulateSingleGameDataSync(gameId);

            CollectionAssert.Contains(
                context.Settings.DynamicAchievementRarityFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Rare);
            CollectionAssert.Contains(
                context.Settings.DynamicAchievementCustomizationFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Hidden);

            context.Settings.DynamicAchievementsStatusFilterKey = DynamicThemeViewKeys.Locked;
            context.Settings.DynamicAchievementsRarityFilterKey = DynamicThemeViewKeys.Rare;

            Assert.AreEqual("Locked+Rare", context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicAchievementsStatusFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Rare, context.Settings.DynamicAchievementsRarityFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Rare Locked");

            context.Settings.DynamicAchievementsRarityFilterKey = DynamicThemeViewKeys.All;

            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicAchievementsFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Rare Locked", "Common Locked");

            context.Settings.DynamicAchievementsCustomizationFilterKey = DynamicThemeViewKeys.Hidden;

            Assert.AreEqual("Locked+Hidden", context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Hidden, context.Settings.DynamicAchievementsCustomizationFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Rare Locked");
        }

        [TestMethod]
        public void DynamicBindings_WritableGameKeySelectsDynamicAchievementGame()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var firstGameId = Guid.NewGuid();
            var secondGameId = Guid.NewGuid();
            var firstGame = new Game { Id = firstGameId, Name = "First Dynamic Game" };
            var secondGame = new Game { Id = secondGameId, Name = "Second Dynamic Game" };
            var firstData = new GameAchievementData
            {
                PlayniteGameId = firstGameId,
                ProviderKey = "Steam",
                Game = firstGame,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("First Game Achievement", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 1, 9, 0, 0))
                }
            };
            var secondData = new GameAchievementData
            {
                PlayniteGameId = secondGameId,
                ProviderKey = "GOG",
                Game = secondGame,
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Second Game Achievement", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 2, 9, 0, 0))
                }
            };

            context.AchievementDataService.GameDataById[firstGameId] = firstData;
            context.AchievementDataService.GameDataById[secondGameId] = secondData;
            context.AchievementDataService.AllGameData = new List<GameAchievementData> { firstData, secondData };

            context.Settings.OpenAchievementWindow.Execute(null);
            context.Settings.DynamicAchievementsGameKey = secondGameId.ToString("D");

            Assert.AreEqual(secondGameId.ToString("D"), context.Settings.DynamicAchievementsGameKey);
            Assert.AreEqual("Second Dynamic Game", context.Settings.DynamicAchievementsGameLabel);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Second Game Achievement");
            CollectionAssert.Contains(
                context.Settings.DynamicAchievementGameOptions.Select(item => item.Key).ToList(),
                firstGameId.ToString("D"));
            CollectionAssert.Contains(
                context.Settings.DynamicAchievementGameOptions.Select(item => item.Key).ToList(),
                secondGameId.ToString("D"));
        }

        [TestMethod]
        public void DynamicAllGamesBindings_ExposeProviderOptionsAndWritableFilters()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
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
                        Name = "BattleNet Dynamic",
                        LastActivity = Utc(2026, 4, 7, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("BN Rare", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 2, 9, 0, 0)),
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
                        Name = "Steam Dynamic",
                        LastActivity = Utc(2026, 4, 9, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Steam Complete One", 25.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 1, 9, 0, 0)),
                        Achievement("Steam Complete Two", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 3, 9, 0, 0))
                    }
                }
            };

            context.Settings.OpenAchievementWindow.Execute(null);

            CollectionAssert.Contains(
                context.Settings.DynamicLibraryAchievementsProviderOptions.Select(item => item.Key).ToList(),
                "BattleNet");
            CollectionAssert.Contains(
                context.Settings.DynamicLibraryAchievementsProviderOptions.Select(item => item.Key).ToList(),
                "Steam");
            CollectionAssert.Contains(
                context.Settings.DynamicLibraryAchievementsFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Locked);
            CollectionAssert.Contains(
                context.Settings.DynamicGameSummariesFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Completed);
            CollectionAssert.Contains(
                context.Settings.DynamicGameSummariesSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Progress);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicLibraryAchievementsSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.RarityPercent);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicLibraryAchievementsSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Hidden);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicLibraryAchievementsSortOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Capstone);

            context.Settings.DynamicLibraryAchievementsProviderKey = "BattleNet";
            context.Settings.DynamicLibraryAchievementsFilterKey = DynamicThemeViewKeys.Locked;
            context.Settings.DynamicLibraryAchievementsSortKey = DynamicThemeViewKeys.Name;
            context.Settings.DynamicLibraryAchievementsSortDirectionKey = DynamicThemeViewKeys.Ascending;

            Assert.AreEqual("BattleNet", context.Settings.DynamicLibraryAchievementsProviderKey);
            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicLibraryAchievementsFilterKey);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "BN Locked");
            CollectionAssert.Contains(
                context.Settings.DynamicLibraryAchievementsProviderOptions.Select(item => item.Key).ToList(),
                "Steam");

            context.Settings.DynamicGameSummariesFilterKey = DynamicThemeViewKeys.Completed;
            context.Settings.DynamicGameSummariesSortKey = DynamicThemeViewKeys.Progress;
            context.Settings.DynamicGameSummariesSortDirectionKey = DynamicThemeViewKeys.Descending;

            Assert.AreEqual(DynamicThemeViewKeys.Completed, context.Settings.DynamicGameSummariesFilterKey);
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "Steam Dynamic");
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
        public void OpenAchievementWindow_PublishesAppleGooglePlayAndUbisoftProviderLists()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var changedProperties = TrackPropertyChanges(context.Settings);

            var appleGameId = Guid.NewGuid();
            var googlePlayGameId = Guid.NewGuid();
            var ubisoftGameId = Guid.NewGuid();

            context.AchievementDataService.AllGameData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = appleGameId,
                    ProviderKey = "Exophase",
                    ProviderPlatformKey = "Apple",
                    Game = new Game
                    {
                        Id = appleGameId,
                        Name = "Apple Summary",
                        LastActivity = Utc(2026, 4, 10, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Apple Unlock", 8.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 1, 9, 0, 0))
                    }
                },
                new GameAchievementData
                {
                    PlayniteGameId = googlePlayGameId,
                    ProviderKey = "GooglePlay",
                    Game = new Game
                    {
                        Id = googlePlayGameId,
                        Name = "Google Play Summary",
                        LastActivity = Utc(2026, 4, 11, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Google Play Unlock", 25.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 2, 9, 0, 0))
                    }
                },
                new GameAchievementData
                {
                    PlayniteGameId = ubisoftGameId,
                    ProviderKey = "Exophase",
                    ProviderPlatformKey = "Ubisoft",
                    Game = new Game
                    {
                        Id = ubisoftGameId,
                        Name = "Ubisoft Summary",
                        LastActivity = Utc(2026, 4, 12, 8, 0, 0)
                    },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement("Ubisoft Unlock", 2.0, unlocked: true, unlockTimeUtc: Utc(2026, 4, 3, 9, 0, 0))
                    }
                }
            };

            context.Settings.OpenAchievementWindow.Execute(null);

            AssertSummaryNames(context.Settings.AppleGames, "Apple Summary");
            AssertSummaryNames(context.Settings.GooglePlayGames, "Google Play Summary");
            AssertSummaryNames(context.Settings.UbisoftGames, "Ubisoft Summary");
            Assert.AreEqual("Apple", context.Settings.AppleGames.Single().ProviderKey);
            Assert.AreEqual("GooglePlay", context.Settings.GooglePlayGames.Single().ProviderKey);
            Assert.AreEqual("Ubisoft", context.Settings.UbisoftGames.Single().ProviderKey);
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.AppleGames)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.GooglePlayGames)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.UbisoftGames)));
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

        private static ServiceTestContext CreateServiceContext(Dispatcher dispatcher = null)
        {
            var settings = new PlayniteAchievementsSettings();
            var plugin = new PlayniteAchievementsPlugin
            {
                Settings = settings
            };
            PlayniteAchievementsPlugin.Instance = plugin;

            var api = new FakePlayniteApi(dispatcher != null ? new FakeMainView(dispatcher) : null);
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService();
            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null);
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();
            var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            return new ServiceTestContext(settings, refreshRuntime, refreshCoordinator, achievementDataService, logger, service);
        }

        private static void DrainDispatcher(Dispatcher dispatcher)
        {
            if (dispatcher == null)
            {
                return;
            }

            dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
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

        private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 1000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.IsTrue(condition(), "Condition was not met before the timeout.");
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
                RefreshRuntime refreshRuntime,
                RefreshEntryPoint refreshCoordinator,
                AchievementDataService achievementDataService,
                FakeLogger logger,
                ThemeIntegrationService service)
            {
                Settings = settings;
                RefreshRuntime = refreshRuntime;
                RefreshCoordinator = refreshCoordinator;
                AchievementDataService = achievementDataService;
                Logger = logger;
                Service = service;
            }

            public PlayniteAchievementsSettings Settings { get; }

            public RefreshRuntime RefreshRuntime { get; }

            public RefreshEntryPoint RefreshCoordinator { get; }

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
            private readonly IMainViewAPI _mainView;

            public FakePlayniteApi(IMainViewAPI mainView = null)
            {
                _mainView = mainView;
            }

            public IMainViewAPI MainView => _mainView;

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

        private sealed class FakeMainView : IMainViewAPI
        {
            private readonly Dispatcher _dispatcher;

            public FakeMainView(Dispatcher dispatcher)
            {
                _dispatcher = dispatcher;
            }

            public DesktopView ActiveDesktopView { get; set; }

            public FullscreenView ActiveFullscreenView { get; set; }

            public SortOrder SortOrder { get; set; }

            public SortOrderDirection SortOrderDirection { get; set; }

            public GroupableField Grouping { get; set; }

            public Dispatcher UIDispatcher => _dispatcher;

            public IEnumerable<Game> SelectedGames => Enumerable.Empty<Game>();

            public List<Game> FilteredGames => new List<Game>();

            public bool OpenPluginSettings(Guid pluginId)
            {
                return false;
            }

            public void SwitchToLibraryView()
            {
            }

            public void SelectGame(Guid gameId)
            {
            }

            public void SelectGames(IEnumerable<Guid> gameIds)
            {
            }

            public void ApplyFilterPreset(Guid filterId)
            {
            }

            public void ApplyFilterPreset(FilterPreset preset)
            {
            }

            public Guid GetActiveFilterPreset()
            {
                return Guid.Empty;
            }

            public FilterPresetSettings GetCurrentFilterSettings()
            {
                return null;
            }

            public void OpenSearch(string searchTerm)
            {
            }

            public void OpenSearch(SearchContext context, string searchTerm)
            {
            }

            public bool? OpenEditDialog(Guid gameId)
            {
                return null;
            }

            public bool? OpenEditDialog(List<Guid> gameIds)
            {
                return null;
            }

            public List<FilterPreset> GetSortedFilterPresets()
            {
                return new List<FilterPreset>();
            }

            public List<FilterPreset> GetSortedFilterFullscreenPresets()
            {
                return new List<FilterPreset>();
            }

            public void ToggleFullscreenView()
            {
            }
        }
    }
}
