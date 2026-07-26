using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.ThemeIntegration;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.ThemeIntegration.Base;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    public class ThemeIntegrationBindingsTests
    {
        // Percent assertions (e.g. FriendCompletionText) depend on the plugin formatting culture.
        [TestInitialize]
        public void PinFormattingCulture()
        {
            PlayniteAchievements.Common.FormattingCulture.Initialize(() => "english");
        }

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
        public void SelectedGameBuilder_CarriesCategoryImagesIntoThemeDisplayItems()
        {
            var gameId = Guid.NewGuid();
            var achievement = Achievement("DLC Achievement", 12.0, unlocked: true);
            achievement.Category = "DLC";
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = "Category Image Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail> { achievement },
                AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>
                {
                    ["DLC"] = new CategoryImageOverrideData
                    {
                        Art = "category-art.png"
                    }
                }
            };

            var state = SelectedGameRuntimeStateBuilder.Build(gameId, data);
            var detail = state.AllAchievements.Single();

            Assert.AreEqual("category-art.png", detail.CategoryArtPath);

            var displayItem = new AchievementDisplayItem();
            displayItem.UpdateFrom(
                detail,
                "Category Image Game",
                gameId,
                showHiddenIcon: false,
                showHiddenTitle: false,
                showHiddenDescription: false,
                showHiddenSuffix: true,
                showLockedIcon: true,
                useSeparateLockedIconsWhenAvailable: false,
                showRarityBar: true);

            Assert.AreEqual("category-art.png", displayItem.CategoryArtPath);
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
        public void LibraryBuilder_BuildsLightStateFromCachedSummaryData()
        {
            var steamGameId = Guid.NewGuid();
            var battleNetGameId = Guid.NewGuid();
            var summaryData = new CachedSummaryData
            {
                Games = new List<CachedGameSummaryData>
                {
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = steamGameId,
                        ProviderKey = "Steam",
                        GameName = "Steam Cached",
                        HasAchievements = true,
                        TotalAchievements = 4,
                        UnlockedAchievements = 2,
                        CollectionScore = 105,
                        PrestigeScore = 220,
                        CommonCount = 1,
                        RareCount = 1,
                        TotalCommonPossible = 2,
                        TotalRarePossible = 2
                    },
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = battleNetGameId,
                        ProviderKey = "Exophase",
                        ProviderPlatformKey = "BattleNet",
                        GameName = "BattleNet Cached",
                        HasAchievements = true,
                        TotalAchievements = 2,
                        UnlockedAchievements = 2,
                        CollectionScore = 210,
                        PrestigeScore = 480,
                        UncommonCount = 1,
                        UltraRareCount = 1,
                        TotalUncommonPossible = 1,
                        TotalUltraRarePossible = 1,
                        IsCompleted = true
                    }
                },
                UnlockCountsByDateByGame = new Dictionary<Guid, Dictionary<DateTime, int>>
                {
                    [steamGameId] = new Dictionary<DateTime, int>
                    {
                        [Utc(2026, 4, 1, 0, 0, 0)] = 2
                    },
                    [battleNetGameId] = new Dictionary<DateTime, int>
                    {
                        [Utc(2026, 4, 2, 0, 0, 0)] = 2
                    }
                },
                RecentUnlocks = new List<CachedRecentUnlockData>
                {
                    new CachedRecentUnlockData
                    {
                        PlayniteGameId = steamGameId,
                        ProviderKey = "Steam",
                        GameName = "Steam Cached",
                        ApiName = "steam_recent",
                        DisplayName = "Steam Recent",
                        Rarity = RarityTier.Rare,
                        GlobalPercentUnlocked = 8.0,
                        UnlockTimeUtc = Utc(2026, 4, 1, 9, 0, 0)
                    },
                    new CachedRecentUnlockData
                    {
                        PlayniteGameId = battleNetGameId,
                        ProviderKey = "Exophase",
                        ProviderPlatformKey = "BattleNet",
                        GameName = "BattleNet Cached",
                        ApiName = "bn_recent",
                        DisplayName = "BattleNet Recent",
                        Rarity = RarityTier.UltraRare,
                        GlobalPercentUnlocked = 2.0,
                        UnlockTimeUtc = Utc(2026, 4, 2, 9, 0, 0)
                    }
                }
            };

            var state = LibraryRuntimeStateBuilder.BuildFromCachedSummary(summaryData, api: null, token: default);

            Assert.IsFalse(state.HeavyListsBuilt);
            Assert.AreEqual(0, state.AllAchievements.Count);
            AssertSummaryNames(state.AllGamesWithAchievements, "BattleNet Cached", "Steam Cached");
            AssertSummaryNames(state.SteamGames, "Steam Cached");
            AssertSummaryNames(state.BattleNetGames, "BattleNet Cached");
            Assert.AreEqual(1, state.PlatinumTrophies);
            Assert.AreEqual(2, state.GoldTrophies);
            Assert.AreEqual(1, state.SilverTrophies);
            Assert.AreEqual(1, state.BronzeTrophies);
            Assert.AreEqual(5, state.TotalTrophies);
            AssertStat(state.TotalCommon, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.TotalUncommon, total: 1, unlocked: 1, locked: 0);
            AssertStat(state.TotalRare, total: 2, unlocked: 1, locked: 1);
            AssertStat(state.TotalUltraRare, total: 1, unlocked: 1, locked: 0);
            Assert.AreEqual(315, state.CollectorScore);
            Assert.AreEqual(700, state.PrestigeScore);
            Assert.AreEqual(AchievementScoreCalculator.CalculateLegacyScore(1, 2, 1, 1), state.Score);
            AssertAchievementNames(state.MostRecentUnlocksTop3, "BattleNet Recent", "Steam Recent");
            Assert.AreEqual("BattleNet", state.MostRecentUnlocksTop3[0].ProviderKey);
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

            context.Service.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: false);

            Assert.AreEqual(1, context.AchievementDataService.CachedSummaryDataForThemeCalls);
            Assert.AreEqual(0, context.AchievementDataService.VisibleAllGameDataForThemeCalls);
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

            context.Service.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: true);

            Assert.AreEqual(0, context.AchievementDataService.CachedSummaryDataForThemeCalls);
            Assert.AreEqual(1, context.AchievementDataService.VisibleAllGameDataForThemeCalls);
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

            context.Service.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: false);
            Assert.AreEqual(1, context.AchievementDataService.CachedSummaryDataForThemeCalls);

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

            Assert.AreEqual(2, context.AchievementDataService.CachedSummaryDataForThemeCalls);
            Assert.AreEqual(0, context.AchievementDataService.VisibleAllGameDataForThemeCalls);
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
            await WaitForConditionAsync(() => context.Settings.Achievements.Count == 1);
            AssertAchievementNames(context.Settings.Achievements, "Visible One");

            await WaitForConditionAsync(() => context.Settings.DynamicLibraryAchievements.Count == 1);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "Visible One");

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
            Assert.AreEqual(DynamicThemeViewKeys.UnlockTime, context.Settings.DynamicAchievementsSortKey);
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
            context.AchievementDataService.GameDataById[gameId].Achievements[0].AchievementNote = "Important";

            context.Settings.DynamicAchievementsDefaultFilterKey = DynamicThemeViewKeys.Unlocked;
            context.Settings.DynamicAchievementsDefaultSortKey = DynamicThemeViewKeys.Rarity;
            context.Settings.DynamicAchievementsDefaultSortDirectionKey = DynamicThemeViewKeys.Ascending;
            context.Service.PopulateSingleGameDataSync(gameId);

            Assert.AreEqual(DynamicThemeViewKeys.Unlocked, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Rarity, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Visible Unlocked Ultra");

            context.Settings.DynamicAchievementsFilterKey = DynamicThemeViewKeys.Locked;
            context.Settings.DynamicAchievementsDefaultFilterKey = DynamicThemeViewKeys.HasNotes;

            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.HasNotes, context.Settings.DynamicAchievementsDefaultFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Locked Rare", "Visible Locked Common");

            context.Settings.ResetDynamicAchievementsCommand.Execute(null);

            Assert.AreEqual(DynamicThemeViewKeys.HasNotes, context.Settings.DynamicAchievementsFilterKey);
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
            AssertAchievementNames(context.Settings.DynamicAchievements, "Ultra Locked", "Rare Locked");
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
            hiddenRare.AchievementNote = "Important";
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
                DynamicThemeViewKeys.HasNotes);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicAchievementCustomizationFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Visible);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicAchievementCustomizationFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Hidden);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicAchievementCustomizationFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.NoNotes);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicGameProgressFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.Started);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicGameActivityFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.HasLastUnlock);
            CollectionAssert.DoesNotContain(
                context.Settings.DynamicGameActivityFilterOptions.Select(item => item.Key).ToList(),
                DynamicThemeViewKeys.NoLastUnlock);

            context.Settings.DynamicAchievementsStatusFilterKey = DynamicThemeViewKeys.Locked;
            context.Settings.DynamicAchievementsRarityFilterKey = DynamicThemeViewKeys.Rare;

            Assert.AreEqual("Locked+Rare", context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicAchievementsStatusFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Rare, context.Settings.DynamicAchievementsRarityFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Rare Locked");

            context.Settings.DynamicAchievementsRarityFilterKey = DynamicThemeViewKeys.All;

            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicAchievementsFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Rare Locked", "Common Locked");

            context.Settings.DynamicAchievementsCustomizationFilterKey = DynamicThemeViewKeys.HasNotes;

            Assert.AreEqual("Locked+HasNotes", context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.HasNotes, context.Settings.DynamicAchievementsCustomizationFilterKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "Hidden Rare Locked");
        }

        [TestMethod]
        public void DynamicCommands_AcceptObjectParametersAndAttachItemLocalCommands()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var steamGameId = Guid.NewGuid();
            var gogGameId = Guid.NewGuid();
            var steamData = new GameAchievementData
            {
                PlayniteGameId = steamGameId,
                ProviderKey = "Steam",
                Game = new Game { Id = steamGameId, Name = "Steam Command Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Steam Unlocked", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 1, 9, 0, 0))
                }
            };
            var gogData = new GameAchievementData
            {
                PlayniteGameId = gogGameId,
                ProviderKey = "GOG",
                Game = new Game { Id = gogGameId, Name = "GOG Command Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("GOG Locked Rare", 8.0, unlocked: false)
                }
            };

            context.AchievementDataService.GameDataById[steamGameId] = steamData;
            context.AchievementDataService.GameDataById[gogGameId] = gogData;
            context.AchievementDataService.AllGameData = new List<GameAchievementData> { steamData, gogData };
            context.Settings.OpenAchievementWindow.Execute(null);

            var gogSummary = context.Settings.DynamicGameSummaries.Single(item => item.GameId == gogGameId);
            context.Settings.SetDynamicAchievementsGameCommand.Execute(gogSummary);
            Assert.AreEqual(gogGameId.ToString("D"), context.Settings.DynamicAchievementsGameKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "GOG Locked Rare");

            var achievementRow = context.Settings.DynamicAchievements.Single();
            Assert.IsNotNull(achievementRow.SetDynamicAchievementsGameCommand);
            Assert.IsNotNull(achievementRow.OpenViewAchievementsWindow);
            achievementRow.SetDynamicAchievementsGameCommand.Execute(null);
            Assert.AreEqual(gogGameId.ToString("D"), context.Settings.DynamicAchievementsGameKey);

            context.Settings.FilterDynamicLibraryAchievementsByProviderCommand.Execute(achievementRow);
            Assert.AreEqual("GOG", context.Settings.DynamicLibraryAchievementsProviderKey);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "GOG Locked Rare");

            var steamSummary = context.Settings.DynamicGameSummaries.Single(item => item.GameId == steamGameId);
            Assert.IsNotNull(steamSummary.SetDynamicAchievementsGameCommand);
            Assert.IsNotNull(steamSummary.FilterDynamicGameSummariesByProviderCommand);
            steamSummary.FilterDynamicGameSummariesByProviderCommand.Execute(null);
            Assert.AreEqual("Steam", context.Settings.DynamicGameSummariesProviderKey);
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "Steam Command Game");

            var displayItem = new AchievementDisplayItem
            {
                PlayniteGameId = steamGameId,
                ProviderKey = "Steam",
                DisplayName = "Display Object"
            };
            context.Settings.SetDynamicAchievementsGameCommand.Execute(displayItem);
            Assert.AreEqual(steamGameId.ToString("D"), context.Settings.DynamicAchievementsGameKey);

            var gameSummaryItem = new GameSummaryItem
            {
                PlayniteGameId = gogGameId,
                ProviderKey = "GOG",
                GameName = "GOG Command Game"
            };
            context.Settings.SetDynamicAchievementsGameCommand.Execute(gameSummaryItem);
            Assert.AreEqual(gogGameId.ToString("D"), context.Settings.DynamicAchievementsGameKey);
        }

        [TestMethod]
        public void DynamicRunningGameCommands_FilterSelectedLibraryAndSummariesPreserveState()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var steamGameId = Guid.NewGuid();
            var gogGameId = Guid.NewGuid();
            using var context = CreateServiceContext(
                resolveRunningGameTarget: () => AchievementHotkeyTargetResolution.ForGame(gogGameId));

            var steamData = new GameAchievementData
            {
                PlayniteGameId = steamGameId,
                ProviderKey = "Steam",
                Game = new Game { Id = steamGameId, Name = "Steam Running Baseline" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Steam Complete", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 1, 9, 0, 0))
                }
            };
            var gogData = new GameAchievementData
            {
                PlayniteGameId = gogGameId,
                ProviderKey = "GOG",
                Game = new Game { Id = gogGameId, Name = "GOG Running Game" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("GOG Locked Rare", 8.0, unlocked: false),
                    Achievement("GOG Unlocked Common", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 2, 9, 0, 0))
                }
            };

            context.AchievementDataService.GameDataById[steamGameId] = steamData;
            context.AchievementDataService.GameDataById[gogGameId] = gogData;
            context.AchievementDataService.AllGameData = new List<GameAchievementData> { steamData, gogData };
            context.Service.PopulateSingleGameDataSync(steamGameId);
            context.Settings.OpenAchievementWindow.Execute(null);

            context.Settings.SetDynamicAchievementsFilterCommand.Execute(DynamicThemeViewKeys.Locked);
            context.Settings.SortDynamicAchievementsCommand.Execute(DynamicThemeViewKeys.Name);
            context.Settings.SetDynamicAchievementsSortDirectionCommand.Execute(DynamicThemeViewKeys.Ascending);
            context.Settings.FilterDynamicLibraryAchievementsByProviderCommand.Execute("Steam");
            context.Settings.SetDynamicLibraryAchievementsFilterCommand.Execute(DynamicThemeViewKeys.Locked);
            context.Settings.SortDynamicLibraryAchievementsCommand.Execute(DynamicThemeViewKeys.Name);
            context.Settings.SetDynamicLibraryAchievementsSortDirectionCommand.Execute(DynamicThemeViewKeys.Ascending);
            context.Settings.FilterDynamicGameSummariesByProviderCommand.Execute("Steam");
            context.Settings.SetDynamicGameSummariesFilterCommand.Execute(DynamicThemeViewKeys.Incomplete);
            context.Settings.SortDynamicGameSummariesCommand.Execute(DynamicThemeViewKeys.Name);
            context.Settings.SetDynamicGameSummariesSortDirectionCommand.Execute(DynamicThemeViewKeys.Ascending);

            context.Settings.FilterDynamicAchievementsByRunningGameCommand.Execute(null);

            Assert.AreEqual(gogGameId.ToString("D"), context.Settings.DynamicAchievementsGameKey);
            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Name, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicAchievements, "GOG Locked Rare");

            context.Settings.FilterDynamicLibraryAchievementsByRunningGameCommand.Execute(null);

            Assert.AreEqual(gogGameId.ToString("D"), context.Settings.DynamicLibraryAchievementsGameKey);
            Assert.AreEqual("GOG Running Game", context.Settings.DynamicLibraryAchievementsGameLabel);
            Assert.AreEqual("GOG", context.Settings.DynamicLibraryAchievementsProviderKey);
            Assert.AreEqual(DynamicThemeViewKeys.Locked, context.Settings.DynamicLibraryAchievementsFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Name, context.Settings.DynamicLibraryAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicLibraryAchievementsSortDirectionKey);
            AssertAchievementNames(context.Settings.DynamicLibraryAchievements, "GOG Locked Rare");

            context.Settings.FilterDynamicGameSummariesByRunningGameCommand.Execute(null);

            Assert.AreEqual(gogGameId.ToString("D"), context.Settings.DynamicGameSummariesGameKey);
            Assert.AreEqual("GOG Running Game", context.Settings.DynamicGameSummariesGameLabel);
            Assert.AreEqual("GOG", context.Settings.DynamicGameSummariesProviderKey);
            Assert.AreEqual(DynamicThemeViewKeys.Incomplete, context.Settings.DynamicGameSummariesFilterKey);
            Assert.AreEqual(DynamicThemeViewKeys.Name, context.Settings.DynamicGameSummariesSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicGameSummariesSortDirectionKey);
            AssertSummaryNames(context.Settings.DynamicGameSummaries, "GOG Running Game");
        }

        [TestMethod]
        public void DynamicRunningGameCommands_NoRunningGameNoOpForAllLists()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var friendData = CreateFriendOverviewData();
            var gameId = friendData.Games[0].PlayniteGameId.Value;
            using var context = CreateServiceContext(
                friendCache: new FakeFriendCache(friendData),
                resolveRunningGameTarget: () => AchievementHotkeyTargetResolution.NoTarget);
            WaitForFriendThemeData(context.Settings);
            var gameData = new GameAchievementData
            {
                PlayniteGameId = gameId,
                ProviderKey = "Steam",
                Game = new Game { Id = gameId, Name = "No Running Baseline" },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("No Running Achievement", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 3, 9, 0, 0))
                }
            };

            context.AchievementDataService.GameDataById[gameId] = gameData;
            context.AchievementDataService.AllGameData = new List<GameAchievementData> { gameData };
            context.Service.PopulateSingleGameDataSync(gameId);
            context.Settings.OpenAchievementWindow.Execute(null);
            context.Settings.SetDynamicFriendScopeGameCommand.Execute(FriendOverviewProjection.GetGameScopeKey(friendData.Games[0]));

            var selectedGameKey = context.Settings.DynamicAchievementsGameKey;
            var libraryGameKey = context.Settings.DynamicLibraryAchievementsGameKey;
            var summaryGameKey = context.Settings.DynamicGameSummariesGameKey;
            var friendGameKey = context.Settings.DynamicFriendScopeGameKey;
            var selectedNames = context.Settings.DynamicAchievements.Select(item => item.DisplayName).ToArray();
            var libraryNames = context.Settings.DynamicLibraryAchievements.Select(item => item.DisplayName).ToArray();
            var summaryNames = context.Settings.DynamicGameSummaries.Select(item => item.Name).ToArray();
            var friendGameNames = context.Settings.DynamicFriendGameSummaries.Select(item => item.GameName).ToArray();

            context.Settings.FilterDynamicAchievementsByRunningGameCommand.Execute(null);
            context.Settings.FilterDynamicLibraryAchievementsByRunningGameCommand.Execute(null);
            context.Settings.FilterDynamicGameSummariesByRunningGameCommand.Execute(null);
            context.Settings.FilterDynamicFriendSummariesByRunningGameCommand.Execute(null);
            context.Settings.FilterDynamicFriendGameSummariesByRunningGameCommand.Execute(null);
            context.Settings.FilterDynamicFriendAchievementsByRunningGameCommand.Execute(null);

            Assert.AreEqual(selectedGameKey, context.Settings.DynamicAchievementsGameKey);
            Assert.AreEqual(libraryGameKey, context.Settings.DynamicLibraryAchievementsGameKey);
            Assert.AreEqual(summaryGameKey, context.Settings.DynamicGameSummariesGameKey);
            Assert.AreEqual(friendGameKey, context.Settings.DynamicFriendScopeGameKey);
            CollectionAssert.AreEqual(selectedNames, context.Settings.DynamicAchievements.Select(item => item.DisplayName).ToArray());
            CollectionAssert.AreEqual(libraryNames, context.Settings.DynamicLibraryAchievements.Select(item => item.DisplayName).ToArray());
            CollectionAssert.AreEqual(summaryNames, context.Settings.DynamicGameSummaries.Select(item => item.Name).ToArray());
            CollectionAssert.AreEqual(friendGameNames, context.Settings.DynamicFriendGameSummaries.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void DynamicRunningGameCommands_AreExposedOnSettingsAndThemePreviewContext()
        {
            using var context = CreateServiceContext();
            var commandNames = new[]
            {
                nameof(PlayniteAchievementsSettings.FilterDynamicAchievementsByRunningGameCommand),
                nameof(PlayniteAchievementsSettings.FilterDynamicLibraryAchievementsByRunningGameCommand),
                nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByRunningGameCommand),
                nameof(PlayniteAchievementsSettings.FilterDynamicFriendSummariesByRunningGameCommand),
                nameof(PlayniteAchievementsSettings.FilterDynamicFriendGameSummariesByRunningGameCommand),
                nameof(PlayniteAchievementsSettings.FilterDynamicFriendAchievementsByRunningGameCommand)
            };
            var stateNames = new[]
            {
                nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsGameKey),
                nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsGameLabel),
                nameof(PlayniteAchievementsSettings.DynamicGameSummariesGameKey),
                nameof(PlayniteAchievementsSettings.DynamicGameSummariesGameLabel)
            };

            foreach (var name in commandNames)
            {
                Assert.IsNotNull(typeof(PlayniteAchievementsSettings).GetProperty(name));
                Assert.IsNotNull(typeof(ThemePreviewContext).GetProperty(name));
                Assert.IsNotNull(typeof(PlayniteAchievementsSettings).GetProperty(name).GetValue(context.Settings));
            }

            foreach (var name in stateNames)
            {
                Assert.IsNotNull(typeof(PlayniteAchievementsSettings).GetProperty(name));
                Assert.IsNotNull(typeof(ThemePreviewContext).GetProperty(name));
            }
        }

        [TestMethod]
        public void DynamicOptions_ApplyCommandsUpdateKeysPreserveGroupsAndRefreshSelectedState()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            var friendData = CreateFriendOverviewData();
            using var context = CreateServiceContext(friendCache: new FakeFriendCache(friendData));
            WaitForFriendThemeData(context.Settings);
            var firstGameId = Guid.NewGuid();
            var secondGameId = Guid.NewGuid();
            var firstData = new GameAchievementData
            {
                PlayniteGameId = firstGameId,
                ProviderKey = "Steam",
                Game = new Game { Id = firstGameId, Name = "First Option Game", LastActivity = Utc(2026, 5, 1, 9, 0, 0) },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("First Complete", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 1, 10, 0, 0))
                }
            };
            var secondData = new GameAchievementData
            {
                PlayniteGameId = secondGameId,
                ProviderKey = "GOG",
                Game = new Game { Id = secondGameId, Name = "Second Option Game", LastActivity = Utc(2026, 5, 2, 9, 0, 0) },
                HasAchievements = true,
                Achievements = new List<AchievementDetail>
                {
                    Achievement("Second Locked Rare", 8.0, unlocked: false),
                    Achievement("Second Unlocked Common", 75.0, unlocked: true, unlockTimeUtc: Utc(2026, 5, 2, 10, 0, 0))
                }
            };

            context.AchievementDataService.GameDataById[firstGameId] = firstData;
            context.AchievementDataService.GameDataById[secondGameId] = secondData;
            context.AchievementDataService.AllGameData = new List<GameAchievementData> { firstData, secondData };
            context.Service.PopulateSingleGameDataSync(firstGameId);
            context.Settings.OpenAchievementWindow.Execute(null);

            context.Settings.DynamicAchievementGameOptions
                .Single(option => option.Key == secondGameId.ToString("D"))
                .ApplyCommand.Execute(null);
            Assert.AreEqual(secondGameId.ToString("D"), context.Settings.DynamicAchievementsGameKey);
            AssertOptionSelected(context.Settings.DynamicAchievementGameOptions, secondGameId.ToString("D"));

            context.Settings.DynamicAchievementsSortOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Name)
                .ApplyCommand.Execute(null);
            context.Settings.DynamicAchievementsSortDirectionOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Ascending)
                .ApplyCommand.Execute(null);
            Assert.AreEqual(DynamicThemeViewKeys.Name, context.Settings.DynamicAchievementsSortKey);
            Assert.AreEqual(DynamicThemeViewKeys.Ascending, context.Settings.DynamicAchievementsSortDirectionKey);
            AssertOptionSelected(context.Settings.DynamicAchievementsSortOptions, DynamicThemeViewKeys.Name);
            AssertOptionSelected(context.Settings.DynamicAchievementsSortDirectionOptions, DynamicThemeViewKeys.Ascending);

            context.Settings.DynamicAchievementStatusFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Locked)
                .ApplyCommand.Execute(null);
            context.Settings.DynamicAchievementRarityFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Rare)
                .ApplyCommand.Execute(null);
            Assert.AreEqual("Locked+Rare", context.Settings.DynamicAchievementsFilterKey);
            AssertOptionSelected(context.Settings.DynamicAchievementStatusFilterOptions, DynamicThemeViewKeys.Locked);
            AssertOptionSelected(context.Settings.DynamicAchievementRarityFilterOptions, DynamicThemeViewKeys.Rare);

            context.Settings.DynamicLibraryAchievementsProviderOptions
                .Single(option => option.Key == "GOG")
                .ApplyCommand.Execute(null);
            context.Settings.DynamicLibraryAchievementStatusFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Unlocked)
                .ApplyCommand.Execute(null);
            context.Settings.DynamicLibraryAchievementRarityFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Common)
                .ApplyCommand.Execute(null);
            Assert.AreEqual("GOG", context.Settings.DynamicLibraryAchievementsProviderKey);
            Assert.AreEqual("Unlocked+Common", context.Settings.DynamicLibraryAchievementsFilterKey);
            AssertOptionSelected(context.Settings.DynamicLibraryAchievementsProviderOptions, "GOG");
            AssertOptionSelected(context.Settings.DynamicLibraryAchievementRarityFilterOptions, DynamicThemeViewKeys.Common);

            context.Settings.DynamicGameProgressFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Completed)
                .ApplyCommand.Execute(null);
            context.Settings.DynamicGameActivityFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Played)
                .ApplyCommand.Execute(null);
            Assert.AreEqual("Completed+Played", context.Settings.DynamicGameSummariesFilterKey);
            AssertOptionSelected(context.Settings.DynamicGameProgressFilterOptions, DynamicThemeViewKeys.Completed);
            AssertOptionSelected(context.Settings.DynamicGameActivityFilterOptions, DynamicThemeViewKeys.Played);

            var aliceKey = FriendOverviewProjection.GetFriendScopeKey(friendData.Friends[0]);
            var gameOneKey = FriendOverviewProjection.GetGameScopeKey(friendData.Games[0]);
            context.Settings.DynamicFriendScopeUserOptions
                .Single(option => option.Key == aliceKey)
                .ApplyCommand.Execute(null);
            context.Settings.DynamicFriendScopeGameOptions
                .Single(option => option.Key == gameOneKey)
                .ApplyCommand.Execute(null);
            Assert.AreEqual(aliceKey, context.Settings.DynamicFriendScopeUserKey);
            Assert.AreEqual(gameOneKey, context.Settings.DynamicFriendScopeGameKey);
            AssertFriendGameNames(context.Settings.DynamicFriendGameSummaries, "Game One");
            AssertOptionSelected(context.Settings.DynamicFriendScopeUserOptions, aliceKey);
            AssertOptionSelected(context.Settings.DynamicFriendScopeGameOptions, gameOneKey);

            context.Settings.DynamicFriendGameProgressFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Incomplete)
                .ApplyCommand.Execute(null);
            context.Settings.DynamicFriendGameActivityFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Played)
                .ApplyCommand.Execute(null);
            Assert.AreEqual("Incomplete+Played", context.Settings.DynamicFriendGameSummariesFilterKey);

            context.Settings.DynamicFriendAchievementStatusFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Unlocked)
                .ApplyCommand.Execute(null);
            context.Settings.DynamicFriendAchievementRarityFilterOptions
                .Single(option => option.Key == DynamicThemeViewKeys.Common)
                .ApplyCommand.Execute(null);
            Assert.AreEqual("Unlocked+Common", context.Settings.DynamicFriendAchievementsFilterKey);
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
        public void FriendDynamicLists_LoadFriendCacheOffConstructionThread()
        {
            var data = CreateFriendOverviewData();
            var cache = new FakeFriendCache(data);
            var constructionThreadId = Thread.CurrentThread.ManagedThreadId;

            using var context = CreateServiceContext(friendCache: cache);
            WaitForFriendThemeData(context.Settings);

            Assert.AreEqual(1, cache.LoadFriendsOverviewDataCalls);
            Assert.AreNotEqual(constructionThreadId, cache.FirstLoadThreadId);
        }

        [TestMethod]
        public void FriendDynamicLists_NoConsumerSkipsSnapshotBuild()
        {
            var cache = new FakeFriendCache(CreateFriendOverviewData());
            using var context = CreateServiceContext(friendCache: cache);

            // No friend theme property has been read, so neither construction nor an
            // invalidation may build the friends overview snapshot.
            cache.RaiseFriendCacheInvalidated();
            Task.Delay(250).GetAwaiter().GetResult();
            Assert.AreEqual(0, cache.LoadFriendsOverviewDataCalls);

            // The first read registers demand and builds once, even after the invalidation.
            WaitForFriendThemeData(context.Settings);
            Assert.AreEqual(1, cache.LoadFriendsOverviewDataCalls);
        }

        [TestMethod]
        public void FriendDynamicLists_InvalidationAfterConsumptionRebuildsOnce()
        {
            var cache = new FakeFriendCache(CreateFriendOverviewData());
            using var context = CreateServiceContext(friendCache: cache);

            WaitForFriendThemeData(context.Settings);
            Assert.AreEqual(1, cache.LoadFriendsOverviewDataCalls);

            cache.RaiseFriendCacheInvalidated();
            WaitForConditionAsync(
                () => cache.LoadFriendsOverviewDataCalls == 2,
                timeoutMs: 3000).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void FriendDynamicLists_LoadFromFriendCacheAndSwitchToSelectedFriendGames()
        {
            var data = CreateFriendOverviewData();
            using var context = CreateServiceContext(friendCache: new FakeFriendCache(data));
            WaitForFriendThemeData(context.Settings);
            var settings = context.Settings;

            AssertFriendNames(settings.DynamicFriendSummaries, "Alice", "Bob", "Cora");
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One", "Game Two", "GOG Game");
            AssertFriendAchievementNames(settings.DynamicFriendAchievements, "Recent Only");

            var alice = settings.DynamicFriendSummaries.Single(item => item.DisplayName == "Alice");
            var aliceKey = FriendOverviewProjection.GetFriendScopeKey(data.Friends[0]);
            Assert.AreEqual("avatars/alice.png", alice.AvatarPath);
            Assert.AreEqual(111, alice.CollectionScore);
            Assert.AreEqual(222, alice.PrestigeScore);
            Assert.AreEqual(aliceKey, alice.FriendScopeKey);
            Assert.AreEqual(aliceKey, alice.Key);
            Assert.AreEqual("Alice", alice.Label);
            Assert.AreEqual(2, alice.Count);

            var aggregateGameOne = settings.DynamicFriendGameSummaries.Single(item => item.AppId == 10);
            Assert.AreEqual(333, aggregateGameOne.CollectionScore);
            Assert.AreEqual(444, aggregateGameOne.CollectionScoreTotal);
            Assert.AreEqual(555, aggregateGameOne.PrestigeScore);
            Assert.AreEqual(666, aggregateGameOne.PrestigeScoreTotal);
            Assert.AreEqual(777, aggregateGameOne.Points);
            Assert.AreEqual(2, aggregateGameOne.BronzeCount);
            Assert.AreEqual(1, aggregateGameOne.SilverCount);
            Assert.AreEqual(1, aggregateGameOne.GoldCount);
            Assert.AreEqual(2, aggregateGameOne.TrophyBronzeCount);
            Assert.AreEqual(4, aggregateGameOne.TrophyBronzeTotal);

            settings.DynamicFriendScopeUserKey = alice.FriendScopeKey;

            Assert.AreEqual(aliceKey, settings.DynamicFriendScopeUserKey);
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One", "Game Two");
            AssertFriendAchievementNames(settings.DynamicFriendAchievements, "Recent Only", "Alice Game Two");
            var gameOne = settings.DynamicFriendGameSummaries.Single(item => item.AppId == 10);
            Assert.AreEqual(FriendOverviewProjection.GetGameScopeKey(data.Games[0]), gameOne.FriendGameScopeKey);
            Assert.AreEqual(gameOne.FriendGameScopeKey, gameOne.Key);
            Assert.AreEqual("Game One", gameOne.Label);
            Assert.AreEqual(1, gameOne.Count);
            Assert.AreEqual(1, gameOne.FriendUnlockedAchievementsCount);
            Assert.AreEqual(1, gameOne.UniqueFriendUnlockedAchievementsCount);
            Assert.AreEqual(1, gameOne.UnlockedCount);
            Assert.AreEqual(4, gameOne.AchievementCount);
            Assert.AreEqual(25, gameOne.FriendCompletionPercent);
            Assert.AreEqual(25, gameOne.Progress);
            Assert.AreEqual(1, gameOne.BronzeCount);
            Assert.AreEqual(0, gameOne.SilverCount);
            Assert.AreEqual(0, gameOne.GoldCount);
            Assert.AreEqual(1, gameOne.TrophyBronzeCount);
            Assert.AreEqual("25%", gameOne.FriendCompletionText);
            Assert.AreEqual(600, gameOne.TotalFriendPlaytimeMinutes);
            Assert.AreEqual(600UL * 60UL, gameOne.TotalFriendPlaytimeSeconds);
            Assert.AreEqual(Utc(2026, 1, 7, 0, 0, 0), gameOne.LastPlayed);
            Assert.AreEqual(Utc(2026, 1, 4, 0, 0, 0), gameOne.LastFriendUnlockUtc);
            Assert.IsNotNull(gameOne.OpenViewAchievementsWindow);
            Assert.IsNotNull(gameOne.Rare);
            Assert.IsNotNull(gameOne.Overall);

            settings.DynamicFriendScopeGameKey = gameOne.FriendGameScopeKey;

            Assert.AreEqual(gameOne.FriendGameScopeKey, settings.DynamicFriendScopeGameKey);
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One");
        }

        [TestMethod]
        public void FriendDynamicLists_RowLocalCommandsUpdateFriendGameSummariesDirectly()
        {
            var data = CreateFriendOverviewData();
            using var context = CreateServiceContext(friendCache: new FakeFriendCache(data));
            WaitForFriendThemeData(context.Settings);
            var settings = context.Settings;

            Assert.IsNull(typeof(ModernThemeBindings).GetProperty("DynamicFriendGameSummaryRows"));
            Assert.IsNull(typeof(PlayniteAchievementsSettings).GetProperty("DynamicFriendGameSummaryRows"));

            var alice = settings.DynamicFriendSummaries.Single(item => item.DisplayName == "Alice");
            Assert.IsNotNull(alice.SetDynamicFriendScopeUserCommand);
            alice.SetDynamicFriendScopeUserCommand.Execute(null);

            var aliceKey = FriendOverviewProjection.GetFriendScopeKey(data.Friends[0]);
            Assert.AreEqual(aliceKey, settings.DynamicFriendScopeUserKey);
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One", "Game Two");

            var gameOne = settings.DynamicFriendGameSummaries.Single(item => item.AppId == 10);
            Assert.IsNotNull(gameOne.SetDynamicFriendScopeGameCommand);
            Assert.IsNotNull(gameOne.SetDynamicFriendScopeProviderCommand);
            gameOne.SetDynamicFriendScopeGameCommand.Execute(null);

            Assert.AreEqual(FriendOverviewProjection.GetGameScopeKey(data.Games[0]), settings.DynamicFriendScopeGameKey);
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One");
            AssertFriendAchievementNames(settings.DynamicFriendAchievements, "Recent Only");

            settings.ResetDynamicFriendScopeCommand.Execute(null);
            var recent = settings.DynamicFriendAchievements.Single(item => item.DisplayName == "Recent Only");
            Assert.IsNotNull(recent.SetDynamicFriendScopeUserCommand);
            Assert.IsNotNull(recent.SetDynamicFriendScopeGameCommand);

            settings.SetDynamicFriendScopeUserCommand.Execute(recent);
            Assert.AreEqual(aliceKey, settings.DynamicFriendScopeUserKey);
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One", "Game Two");

            settings.SetDynamicFriendScopeGameCommand.Execute(recent);
            Assert.AreEqual(FriendOverviewProjection.GetGameScopeKey(data.Games[0]), settings.DynamicFriendScopeGameKey);
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One");
        }

        [TestMethod]
        public void FriendDynamicLists_GameCommandRejectsGamesOutsideSelectedFriendScope()
        {
            var data = CreateFriendOverviewData();
            using var context = CreateServiceContext(friendCache: new FakeFriendCache(data));
            WaitForFriendThemeData(context.Settings);
            var settings = context.Settings;
            var aliceKey = FriendOverviewProjection.GetFriendScopeKey(data.Friends[0]);
            var gogGameKey = FriendOverviewProjection.GetGameScopeKey(data.Games[2]);

            settings.SetDynamicFriendScopeUserCommand.Execute(aliceKey);
            settings.SetDynamicFriendScopeGameCommand.Execute(gogGameKey);

            Assert.AreEqual(aliceKey, settings.DynamicFriendScopeUserKey);
            Assert.AreEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeGameKey);
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One", "Game Two");
            AssertFriendAchievementNames(settings.DynamicFriendAchievements, "Recent Only", "Alice Game Two");
            CollectionAssert.AreEqual(
                new[]
                {
                    DynamicThemeViewKeys.All,
                    FriendOverviewProjection.GetGameScopeKey(data.Games[0]),
                    FriendOverviewProjection.GetGameScopeKey(data.Games[1])
                },
                settings.DynamicFriendScopeGameOptions.Select(option => option.Key).ToArray());
        }

        [TestMethod]
        public void FriendDynamicLists_FriendSummarySortUsesSharedGamesKeyAndAcceptsLegacyAchievementCount()
        {
            var data = CreateFriendOverviewData();
            data.Friends[0].SharedGamesCount = 1;
            data.Friends[1].SharedGamesCount = 3;
            data.Friends[2].SharedGamesCount = 2;
            using var context = CreateServiceContext(friendCache: new FakeFriendCache(data));
            WaitForFriendThemeData(context.Settings);
            var settings = context.Settings;

            var sortKeys = settings.DynamicFriendSummariesSortOptions
                .Select(option => option.Key)
                .ToList();
            CollectionAssert.Contains(sortKeys, DynamicThemeViewKeys.SharedGamesCount);
            CollectionAssert.DoesNotContain(sortKeys, DynamicThemeViewKeys.AchievementCount);

            settings.SortDynamicFriendSummariesCommand.Execute(DynamicThemeViewKeys.SharedGamesCount);

            Assert.AreEqual(DynamicThemeViewKeys.SharedGamesCount, settings.DynamicFriendSummariesSortKey);
            AssertFriendNames(settings.DynamicFriendSummaries, "Bob", "Cora", "Alice");

            settings.SortDynamicFriendSummariesCommand.Execute(DynamicThemeViewKeys.AchievementCount);

            Assert.AreEqual(DynamicThemeViewKeys.SharedGamesCount, settings.DynamicFriendSummariesSortKey);
            AssertFriendNames(settings.DynamicFriendSummaries, "Bob", "Cora", "Alice");
        }

        [TestMethod]
        public void FriendDynamicLists_ProviderAndGameScopesConstrainListsAndOptions()
        {
            var data = CreateFriendOverviewData();
            using var context = CreateServiceContext(friendCache: new FakeFriendCache(data));
            WaitForFriendThemeData(context.Settings);
            var settings = context.Settings;

            settings.SetDynamicFriendScopeProviderCommand.Execute("GOG");

            Assert.AreEqual("GOG", settings.DynamicFriendScopeProviderKey);
            AssertFriendNames(settings.DynamicFriendSummaries, "Cora");
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "GOG Game");
            AssertFriendAchievementNames(settings.DynamicFriendAchievements, "Cora GOG");
            CollectionAssert.AreEqual(
                new[] { DynamicThemeViewKeys.All, FriendOverviewProjection.GetFriendScopeKey(data.Friends[2]) },
                settings.DynamicFriendScopeUserOptions.Select(option => option.Key).ToArray());
            CollectionAssert.AreEqual(
                new[] { DynamicThemeViewKeys.All, FriendOverviewProjection.GetGameScopeKey(data.Games[2]) },
                settings.DynamicFriendScopeGameOptions.Select(option => option.Key).ToArray());

            settings.SetDynamicFriendScopeGameCommand.Execute(FriendOverviewProjection.GetGameScopeKey(data.Games[0]));

            Assert.AreEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeGameKey);

            settings.ResetDynamicFriendScopeCommand.Execute(null);
            settings.SetDynamicFriendScopeGameCommand.Execute(FriendOverviewProjection.GetGameScopeKey(data.Games[0]));

            AssertFriendNames(settings.DynamicFriendSummaries, "Alice", "Bob");
            AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One");
            AssertFriendAchievementNames(settings.DynamicFriendAchievements, "Recent Only", "Bob Game One");
        }

        [TestMethod]
        public void FriendDynamicRunningGameCommands_AdjustConflictingScopeAndPreserveListState()
        {
            var data = CreateFriendOverviewData();
            var runningGameId = data.Games[0].PlayniteGameId.Value;
            using var context = CreateServiceContext(
                friendCache: new FakeFriendCache(data),
                resolveRunningGameTarget: () => AchievementHotkeyTargetResolution.ForGame(runningGameId));
            WaitForFriendThemeData(context.Settings);
            var settings = context.Settings;
            var coraKey = FriendOverviewProjection.GetFriendScopeKey(data.Friends[2]);
            var gameOneKey = FriendOverviewProjection.GetGameScopeKey(data.Games[0]);

            settings.SetDynamicFriendSummariesFilterCommand.Execute(DynamicThemeViewKeys.HasLastUnlock);
            settings.SortDynamicFriendSummariesCommand.Execute(DynamicThemeViewKeys.Name);
            settings.SetDynamicFriendSummariesSortDirectionCommand.Execute(DynamicThemeViewKeys.Ascending);
            settings.SetDynamicFriendGameSummariesFilterCommand.Execute(DynamicThemeViewKeys.Incomplete);
            settings.SortDynamicFriendGameSummariesCommand.Execute(DynamicThemeViewKeys.Name);
            settings.SetDynamicFriendGameSummariesSortDirectionCommand.Execute(DynamicThemeViewKeys.Ascending);
            settings.SetDynamicFriendAchievementsFilterCommand.Execute(DynamicThemeViewKeys.Unlocked);
            settings.SortDynamicFriendAchievementsCommand.Execute(DynamicThemeViewKeys.Name);
            settings.SetDynamicFriendAchievementsSortDirectionCommand.Execute(DynamicThemeViewKeys.Ascending);

            void ResetConflictingScope()
            {
                settings.ResetDynamicFriendScopeCommand.Execute(null);
                settings.SetDynamicFriendScopeProviderCommand.Execute("GOG");
                settings.SetDynamicFriendScopeUserCommand.Execute(coraKey);

                Assert.AreEqual("GOG", settings.DynamicFriendScopeProviderKey);
                Assert.AreEqual(coraKey, settings.DynamicFriendScopeUserKey);
                Assert.AreEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeGameKey);
            }

            void AssertRunningGameScopeApplied()
            {
                Assert.AreEqual("Steam", settings.DynamicFriendScopeProviderKey);
                Assert.AreEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeUserKey);
                Assert.AreEqual(gameOneKey, settings.DynamicFriendScopeGameKey);
                Assert.AreEqual(DynamicThemeViewKeys.HasLastUnlock, settings.DynamicFriendSummariesFilterKey);
                Assert.AreEqual(DynamicThemeViewKeys.Name, settings.DynamicFriendSummariesSortKey);
                Assert.AreEqual(DynamicThemeViewKeys.Ascending, settings.DynamicFriendSummariesSortDirectionKey);
                Assert.AreEqual(DynamicThemeViewKeys.Incomplete, settings.DynamicFriendGameSummariesFilterKey);
                Assert.AreEqual(DynamicThemeViewKeys.Name, settings.DynamicFriendGameSummariesSortKey);
                Assert.AreEqual(DynamicThemeViewKeys.Ascending, settings.DynamicFriendGameSummariesSortDirectionKey);
                Assert.AreEqual(DynamicThemeViewKeys.Unlocked, settings.DynamicFriendAchievementsFilterKey);
                Assert.AreEqual(DynamicThemeViewKeys.Name, settings.DynamicFriendAchievementsSortKey);
                Assert.AreEqual(DynamicThemeViewKeys.Ascending, settings.DynamicFriendAchievementsSortDirectionKey);
                AssertFriendNames(settings.DynamicFriendSummaries, "Alice", "Bob");
                AssertFriendGameNames(settings.DynamicFriendGameSummaries, "Game One");
                AssertFriendAchievementNames(settings.DynamicFriendAchievements, "Bob Game One", "Recent Only");
            }

            ResetConflictingScope();
            settings.FilterDynamicFriendSummariesByRunningGameCommand.Execute(null);
            AssertRunningGameScopeApplied();

            ResetConflictingScope();
            settings.FilterDynamicFriendGameSummariesByRunningGameCommand.Execute(null);
            AssertRunningGameScopeApplied();

            ResetConflictingScope();
            settings.FilterDynamicFriendAchievementsByRunningGameCommand.Execute(null);
            AssertRunningGameScopeApplied();
        }

        [TestMethod]
        public void FriendDynamicLists_ResetStaleScopesOnRefresh()
        {
            var data = CreateFriendOverviewData();
            var friendCache = new FakeFriendCache(data);
            using var context = CreateServiceContext(friendCache: friendCache);
            WaitForFriendThemeData(context.Settings);
            var settings = context.Settings;

            settings.SetDynamicFriendScopeUserCommand.Execute(FriendOverviewProjection.GetFriendScopeKey(data.Friends[0]));
            Assert.AreNotEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeUserKey);

            friendCache.Data = new FriendsOverviewData();
            var previousLoadCount = friendCache.LoadFriendsOverviewDataCalls;
            friendCache.RaiseFriendCacheInvalidated();
            WaitForConditionAsync(
                () => friendCache.LoadFriendsOverviewDataCalls > previousLoadCount &&
                      settings.DynamicFriendScopeProviderKey == DynamicThemeViewKeys.All &&
                      settings.DynamicFriendScopeUserKey == DynamicThemeViewKeys.All &&
                      settings.DynamicFriendScopeGameKey == DynamicThemeViewKeys.All &&
                      settings.DynamicFriendSummaries.Count == 0,
                timeoutMs: 3000).GetAwaiter().GetResult();

            Assert.AreEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeProviderKey);
            Assert.AreEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeUserKey);
            Assert.AreEqual(DynamicThemeViewKeys.All, settings.DynamicFriendScopeGameKey);
            Assert.AreEqual(0, settings.DynamicFriendSummaries.Count);
            Assert.AreEqual(0, settings.DynamicFriendGameSummaries.Count);
            Assert.AreEqual(0, settings.DynamicFriendAchievements.Count);
            CollectionAssert.AreEqual(
                new[] { DynamicThemeViewKeys.All },
                settings.DynamicFriendScopeProviderOptions.Select(option => option.Key).ToArray());
        }

        [TestMethod]
        public void FriendDynamicLists_WhenFriendCacheUnavailableExposeOnlyAllScopeOptions()
        {
            using var context = CreateServiceContext();
            var settings = context.Settings;

            Assert.AreEqual(0, settings.DynamicFriendSummaries.Count);
            Assert.AreEqual(0, settings.DynamicFriendGameSummaries.Count);
            Assert.AreEqual(0, settings.DynamicFriendAchievements.Count);
            CollectionAssert.AreEqual(
                new[] { DynamicThemeViewKeys.All },
                settings.DynamicFriendScopeProviderOptions.Select(option => option.Key).ToArray());
            CollectionAssert.AreEqual(
                new[] { DynamicThemeViewKeys.All },
                settings.DynamicFriendScopeUserOptions.Select(option => option.Key).ToArray());
            CollectionAssert.AreEqual(
                new[] { DynamicThemeViewKeys.All },
                settings.DynamicFriendScopeGameOptions.Select(option => option.Key).ToArray());
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

        private static ServiceTestContext CreateServiceContext(
            Dispatcher dispatcher = null,
            IFriendCacheManager friendCache = null,
            Func<AchievementHotkeyTargetResolution> resolveRunningGameTarget = null)
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
            var service = new ThemeIntegrationService(
                api,
                refreshRuntime,
                achievementDataService,
                refreshCoordinator,
                settings,
                windowService,
                logger,
                friendCache: friendCache,
                resolveRunningGameTarget: resolveRunningGameTarget);

            return new ServiceTestContext(settings, achievementDataService, logger, service);
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

        private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 1500)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            Assert.IsTrue(condition(), "Timed out waiting for asynchronous theme binding update.");
        }

        private static void WaitForFriendThemeData(PlayniteAchievementsSettings settings, string expectedFriendName = "Alice")
        {
            WaitForConditionAsync(
                () => settings.DynamicFriendSummaries.Any(item => item.DisplayName == expectedFriendName),
                timeoutMs: 3000).GetAwaiter().GetResult();
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

        private static FriendsOverviewData CreateFriendOverviewData()
        {
            var gameOneId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var gameTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var gogGameId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            var friends = new List<FriendSummaryItem>
            {
                new FriendSummaryItem
                {
                    ProviderKey = "Steam",
                    ExternalUserId = "alice",
                    DisplayName = "Alice",
                    AvatarPath = "avatars/alice.png",
                    GamesWithUnlocksCount = 2,
                    UnlockedAchievementsCount = 2,
                    CollectionScore = 111,
                    PrestigeScore = 222,
                    LastUnlockUtc = Utc(2026, 1, 4, 0, 0, 0),
                    TotalPlaytimeMinutes = 900
                },
                new FriendSummaryItem
                {
                    ProviderKey = "Steam",
                    ExternalUserId = "bob",
                    DisplayName = "Bob",
                    GamesWithUnlocksCount = 1,
                    UnlockedAchievementsCount = 1,
                    LastUnlockUtc = Utc(2026, 1, 2, 0, 0, 0),
                    TotalPlaytimeMinutes = 300
                },
                new FriendSummaryItem
                {
                    ProviderKey = "GOG",
                    ExternalUserId = "cora",
                    DisplayName = "Cora",
                    GamesWithUnlocksCount = 1,
                    UnlockedAchievementsCount = 1,
                    LastUnlockUtc = Utc(2026, 1, 1, 0, 0, 0),
                    TotalPlaytimeMinutes = 120
                }
            };

            var games = new List<FriendGameSummaryItem>
            {
                new FriendGameSummaryItem
                {
                    ProviderKey = "Steam",
                    AppId = 10,
                    PlayniteGameId = gameOneId,
                    GameName = "Game One",
                    FriendCount = 2,
                    FriendsWithUnlocksCount = 2,
                    FriendUnlockedAchievementsCount = 2,
                    UniqueFriendUnlockedAchievementsCount = 2,
                    TotalAchievements = 4,
                    CollectionScore = 333,
                    CollectionScoreTotal = 444,
                    PrestigeScore = 555,
                    PrestigeScoreTotal = 666,
                    Points = 777,
                    CommonCount = 2,
                    UncommonCount = 1,
                    RareCount = 1,
                    TotalCommonPossible = 4,
                    TotalUncommonPossible = 3,
                    TotalRarePossible = 2,
                    TrophyBronzeCount = 2,
                    TrophyBronzeTotal = 4,
                    LastFriendUnlockUtc = Utc(2026, 1, 4, 0, 0, 0),
                    TotalFriendPlaytimeMinutes = 1200,
                    LastFriendPlayedUtc = Utc(2026, 1, 6, 0, 0, 0)
                },
                new FriendGameSummaryItem
                {
                    ProviderKey = "Steam",
                    AppId = 20,
                    PlayniteGameId = gameTwoId,
                    GameName = "Game Two",
                    FriendCount = 1,
                    FriendsWithUnlocksCount = 1,
                    FriendUnlockedAchievementsCount = 1,
                    UniqueFriendUnlockedAchievementsCount = 1,
                    TotalAchievements = 2,
                    LastFriendUnlockUtc = Utc(2026, 1, 3, 0, 0, 0)
                },
                new FriendGameSummaryItem
                {
                    ProviderKey = "GOG",
                    AppId = 30,
                    PlayniteGameId = gogGameId,
                    GameName = "GOG Game",
                    FriendCount = 1,
                    FriendsWithUnlocksCount = 1,
                    FriendUnlockedAchievementsCount = 1,
                    UniqueFriendUnlockedAchievementsCount = 1,
                    TotalAchievements = 1,
                    LastFriendUnlockUtc = Utc(2026, 1, 1, 0, 0, 0)
                }
            };

            var allUnlocked = new List<FriendAchievementDisplayItem>
            {
                FriendAchievement("Steam", "alice", "Alice", 10, gameOneId, "Game One", "Recent Only", Utc(2026, 1, 4, 0, 0, 0), RarityTier.Common, "bronze", 75, 5),
                FriendAchievement("Steam", "alice", "Alice", 20, gameTwoId, "Game Two", "Alice Game Two", Utc(2026, 1, 3, 0, 0, 0), RarityTier.Uncommon, "silver", 35, 10),
                FriendAchievement("Steam", "bob", "Bob", 10, gameOneId, "Game One", "Bob Game One", Utc(2026, 1, 2, 0, 0, 0), RarityTier.Rare, "gold", 12.5, 15),
                FriendAchievement("GOG", "cora", "Cora", 30, gogGameId, "GOG Game", "Cora GOG", Utc(2026, 1, 1, 0, 0, 0), RarityTier.UltraRare, "platinum", 2.5, 20)
            };

            return new FriendsOverviewData
            {
                Friends = friends,
                Games = games,
                RecentUnlocks = new List<FriendAchievementDisplayItem> { allUnlocked[0] },
                AllUnlockedAchievements = allUnlocked,
                FriendGameLinks = new List<FriendGameLinkItem>
                {
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 10,
                        PlayniteGameId = gameOneId,
                        PlaytimeForeverMinutes = 600,
                        LastPlayedUtc = Utc(2026, 1, 7, 0, 0, 0)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 20,
                        PlayniteGameId = gameTwoId,
                        PlaytimeForeverMinutes = 300,
                        LastPlayedUtc = Utc(2026, 1, 6, 0, 0, 0)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "bob",
                        AppId = 10,
                        PlayniteGameId = gameOneId,
                        PlaytimeForeverMinutes = 300,
                        LastPlayedUtc = Utc(2026, 1, 5, 0, 0, 0)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "GOG",
                        ExternalUserId = "cora",
                        AppId = 30,
                        PlayniteGameId = gogGameId,
                        PlaytimeForeverMinutes = 120,
                        LastPlayedUtc = Utc(2026, 1, 2, 0, 0, 0)
                    }
                }
            };
        }

        private static FriendAchievementDisplayItem FriendAchievement(
            string providerKey,
            string externalUserId,
            string friendName,
            int appId,
            Guid playniteGameId,
            string gameName,
            string achievementName,
            DateTime unlockTimeUtc,
            RarityTier rarity = RarityTier.Common,
            string trophyType = null,
            double? globalPercentUnlocked = null,
            int? points = null)
        {
            return new FriendAchievementDisplayItem
            {
                ProviderKey = providerKey,
                FriendExternalUserId = externalUserId,
                FriendName = friendName,
                AppId = appId,
                PlayniteGameId = playniteGameId,
                GameName = gameName,
                SortingName = gameName,
                ApiName = achievementName,
                DisplayName = achievementName,
                GlobalPercentUnlocked = globalPercentUnlocked,
                Rarity = rarity,
                TrophyType = trophyType,
                PointsValue = points,
                Unlocked = true,
                UnlockTimeUtc = unlockTimeUtc
            };
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

        private static void AssertFriendNames(IEnumerable<FriendSummaryItem> friends, params string[] expectedNames)
        {
            CollectionAssert.AreEqual(
                expectedNames,
                (friends ?? Enumerable.Empty<FriendSummaryItem>())
                    .Select(item => item?.DisplayName)
                    .ToArray());
        }

        private static void AssertFriendGameNames(IEnumerable<FriendGameAchievementSummary> games, params string[] expectedNames)
        {
            CollectionAssert.AreEqual(
                expectedNames,
                (games ?? Enumerable.Empty<FriendGameAchievementSummary>())
                    .Select(item => item?.GameName)
                    .ToArray());
        }

        private static void AssertFriendAchievementNames(
            IEnumerable<FriendAchievementDisplayItem> achievements,
            params string[] expectedNames)
        {
            CollectionAssert.AreEqual(
                expectedNames,
                (achievements ?? Enumerable.Empty<FriendAchievementDisplayItem>())
                    .Select(item => item?.DisplayName)
                    .ToArray());
        }

        private static void AssertOptionSelected(IEnumerable<DynamicThemeOption> options, string key)
        {
            var option = (options ?? Enumerable.Empty<DynamicThemeOption>())
                .Single(item => item.Key == key);
            Assert.IsTrue(option.IsSelected, $"Expected option '{key}' to be selected.");
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

        private sealed class FakeFriendCache : IFriendCacheManager
        {
            public FakeFriendCache(FriendsOverviewData data)
            {
                Data = data;
            }

            public event EventHandler<FriendCacheInvalidatedEventArgs> FriendCacheInvalidated;

            public FriendsOverviewData Data { get; set; }

            public int LoadFriendsOverviewDataCalls { get; private set; }

            public int FirstLoadThreadId { get; private set; }

            public void RaiseFriendCacheInvalidated()
            {
                FriendCacheInvalidated?.Invoke(this, FriendCacheInvalidatedEventArgs.FullInvalidation);
            }

            public IFriendCacheInvalidationBatch BeginFriendCacheInvalidationBatch() =>
                NullFriendCacheInvalidationBatch.Instance;

            public FriendCacheWriteResult SaveFriendList(string providerKey, IReadOnlyList<FriendIdentity> friends) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveFriendOwnership(
                string providerKey,
                string externalUserId,
                IReadOnlyList<FriendGameOwnership> ownership,
                FriendOwnershipSaveOptions options = null) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveFriendGameDefinition(
                string providerKey,
                FriendGameDefinition definition) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveProviderGameImagePaths(
                string providerKey,
                string providerGameKey,
                int appId,
                string iconAbsolutePath,
                string coverAbsolutePath) =>
                FriendCacheWriteResult.Ok();

            public Dictionary<string, FriendGameDefinitionState> LoadFriendGameDefinitionStates(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);

            public List<string> LoadLegacyKeyedDefinitionGameKeys(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                new List<string>();

            public FriendUnownedCacheStats GetUnownedFriendGameCacheStats() =>
                new FriendUnownedCacheStats();

            public FriendUnownedCacheClearResult ClearUnownedFriendGameData() =>
                new FriendUnownedCacheClearResult { Success = true };

            public FriendCacheWriteResult ClearUnownedFriendGame(string providerKey, int appId, string providerGameKey) =>
                FriendCacheWriteResult.Ok();

            public bool IsProviderGameMappedToPlayniteLibrary(string providerKey, int appId, string providerGameKey) =>
                true;

            public System.Collections.Generic.IReadOnlyList<FriendGameMapping> LoadFriendGameMappings(string providerKey) =>
                new System.Collections.Generic.List<FriendGameMapping>();

            public FriendCacheWriteResult PromoteProviderOnlyGameToPlayniteBacked(
                string providerKey,
                int appId,
                string providerGameKey,
                Guid playniteGameId) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveFriendGameAchievements(
                string providerKey,
                string externalUserId,
                string providerGameKey,
                int appId,
                FriendGameAchievements achievements) =>
                FriendCacheWriteResult.Ok();

            public System.Collections.Generic.List<FriendAchievementRow> LoadFriendGameAchievements(
                string providerKey,
                string externalUserId,
                int appId,
                string providerGameKey) =>
                new System.Collections.Generic.List<FriendAchievementRow>();

            public FriendCacheWriteResult DeleteFriendData(string providerKey, string externalUserId, bool preserveFriendRecord = false) =>
                FriendCacheWriteResult.Ok();

            public List<FriendIdentity> LoadFriendIdentities(string providerKey) =>
                new List<FriendIdentity>();

            public DateTime? GetMostRecentFriendLastRefreshedUtc() => null;

            public List<FriendRefreshCandidate> LoadFriendRefreshCandidates(
                string providerKey,
                FriendRefreshOptions options) =>
                new List<FriendRefreshCandidate>();

            public IReadOnlyDictionary<string, FriendOwnershipRecency> LoadFriendOwnershipRecency(
                string providerKey,
                string externalUserId) =>
                new Dictionary<string, FriendOwnershipRecency>();

            public FriendsOverviewData LoadFriendsOverviewData(int recentLimit)
            {
                LoadFriendsOverviewDataCalls++;
                if (FirstLoadThreadId == 0)
                {
                    FirstLoadThreadId = Thread.CurrentThread.ManagedThreadId;
                }

                return Data;
            }

            public FriendsOverviewData LoadFriendsOverviewPatchData(IReadOnlyList<FriendCacheChange> reloadScopes) =>
                Data;

            public FriendsOverviewData LoadFriendGameAchievementData(FriendCacheChange gameScope) =>
                new FriendsOverviewData();

            public FriendsOverviewData LoadFriendGameAchievementData(Guid playniteGameId) =>
                Data;

            public FriendsOverviewData LoadFriendRecentUnlocksData(int recentLimit) =>
                Data;

            public IReadOnlyList<CurrentUserGameLabel> LoadCurrentUserGameLabels() =>
                new List<CurrentUserGameLabel>();
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
