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

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    public class ThemeIntegrationBindingsTests
    {
        [TestMethod]
        public void SelectedGameBuilder_CombinesRareAndUltraRareStats()
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

            AssertStat(state.Common, total: 1, unlocked: 1, locked: 0);
            AssertStat(state.Uncommon, total: 1, unlocked: 0, locked: 1);
            AssertStat(state.Rare, total: 1, unlocked: 1, locked: 0);
            AssertStat(state.UltraRare, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.RareAndUltraRare, total: 3, unlocked: 2, locked: 1);
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
        public void LibraryBuilder_ExcludesMissingAndNonPositivePercentsFromTotals()
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

            AssertStat(state.TotalCommon, total: 1, unlocked: 1, locked: 0);
            AssertStat(state.TotalRare, total: 1, unlocked: 0, locked: 1);
            AssertStat(state.TotalUltraRare, total: 0, unlocked: 0, locked: 0);
            AssertStat(state.TotalOverall, total: 2, unlocked: 1, locked: 1);
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
        public void ClearSingleGameThemeProperties_ResetsAndPublishesRareAndUltraRare()
        {
            var settings = new PlayniteAchievementsSettings();
            settings.Theme.Common = new AchievementRarityStats { Total = 3, Unlocked = 1, Locked = 2 };
            settings.Theme.Uncommon = new AchievementRarityStats { Total = 2, Unlocked = 1, Locked = 1 };
            settings.Theme.Rare = new AchievementRarityStats { Total = 4, Unlocked = 2, Locked = 2 };
            settings.Theme.UltraRare = new AchievementRarityStats { Total = 1, Unlocked = 1, Locked = 0 };
            settings.Theme.RareAndUltraRare = new AchievementRarityStats { Total = 5, Unlocked = 3, Locked = 2 };

            var api = new FakePlayniteApi();
            var refreshRuntime = new RefreshRuntime();
            var achievementDataService = new AchievementDataService();
            var refreshCoordinator = new RefreshEntryPoint(refreshRuntime, logger: null, providerRegistry: new ProviderRegistry());
            var windowService = new FullscreenWindowService(api, settings, _ => { });
            var logger = new FakeLogger();
            using var service = new ThemeIntegrationService(api, refreshRuntime, achievementDataService, refreshCoordinator, settings, windowService, logger);

            var changedProperties = new HashSet<string>(StringComparer.Ordinal);
            settings.PropertyChanged += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args?.PropertyName))
                {
                    changedProperties.Add(args.PropertyName);
                }
            };

            service.ClearSingleGameThemeProperties();

            AssertStat(settings.Common, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.Uncommon, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.Rare, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.UltraRare, total: 0, unlocked: 0, locked: 0);
            AssertStat(settings.RareAndUltraRare, total: 0, unlocked: 0, locked: 0);

            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Common)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Uncommon)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.Rare)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.UltraRare)));
            Assert.IsTrue(changedProperties.Contains(nameof(PlayniteAchievementsSettings.RareAndUltraRare)));
        }

        private static AchievementDetail Achievement(string name, double? percent, bool unlocked)
        {
            return new AchievementDetail
            {
                ApiName = name,
                DisplayName = name,
                GlobalPercentUnlocked = percent,
                Unlocked = unlocked,
                UnlockTimeUtc = unlocked
                    ? DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc)
                    : (DateTime?)null
            };
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

        private sealed class FakeLogger : ILogger
        {
            public void Debug(string message)
            {
            }

            public void Debug(Exception exception, string message)
            {
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
