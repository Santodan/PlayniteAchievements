using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    public class DynamicCategorySummariesTests
    {
        [TestMethod]
        public void PopulateSingleGameDataSync_PublishesCategorySummariesPerLabel()
        {
            PercentRarityHelper.Configure(5, 10, 50);

            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            SeedGame(
                context,
                gameId,
                Achievement("DLC One", "Frozen Wilds", unlocked: true, percent: 8.0, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                Achievement("DLC Two", "Frozen Wilds", unlocked: true, percent: 2.0, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0)),
                Achievement("Base One", "Base Game", unlocked: true, unlockTimeUtc: Utc(2026, 3, 3, 9, 0, 0)),
                Achievement("Base Two", "Base Game", unlocked: false),
                Achievement("Blank One", null, unlocked: false));

            context.Service.PopulateSingleGameDataSync(gameId);

            var summaries = context.Settings.ModernTheme.DynamicCategorySummaries;
            Assert.IsTrue(context.Settings.ModernTheme.HasCategorySummaries);
            Assert.AreEqual(3, summaries.Count);

            var dlc = FindByName(summaries, "Frozen Wilds");
            Assert.AreEqual(2, dlc.UnlockedCount);
            Assert.AreEqual(2, dlc.AchievementCount);
            Assert.AreEqual(100, dlc.Progress);
            Assert.IsTrue(dlc.IsCompleted);
            Assert.AreEqual(2, dlc.GoldCount, "Gold projects unlocked Rare + UltraRare counts.");

            var baseGame = FindByName(summaries, "Base Game");
            Assert.AreEqual(1, baseGame.UnlockedCount);
            Assert.AreEqual(2, baseGame.AchievementCount);
            Assert.AreEqual(50, baseGame.Progress);
            Assert.IsFalse(baseGame.IsCompleted);

            var blank = FindByName(summaries, "Default");
            Assert.AreEqual(0, blank.UnlockedCount);
            Assert.AreEqual(1, blank.AchievementCount);
            Assert.AreEqual(0, blank.Progress);
            Assert.IsFalse(blank.IsCompleted);
        }

        [TestMethod]
        public void PopulateSingleGameDataSync_CategoryArtFlowsIntoSummaryCoverImage()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            var data = SeedGame(
                context,
                gameId,
                Achievement("DLC One", "Frozen Wilds", unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                Achievement("DLC Two", "Frozen Wilds", unlocked: false),
                Achievement("Base One", "Base Game", unlocked: false));
            data.AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Frozen Wilds"] = new CategoryImageOverrideData { Art = "category-art.png" }
            };

            context.Service.PopulateSingleGameDataSync(gameId);

            var summaries = context.Settings.ModernTheme.DynamicCategorySummaries;
            Assert.AreEqual("category-art.png", FindByName(summaries, "Frozen Wilds").CoverImagePath);
            Assert.AreEqual(string.Empty, FindByName(summaries, "Base Game").CoverImagePath);
        }

        [TestMethod]
        public void PopulateSingleGameDataSync_SingleCategoryPublishesNoCategorySummaries()
        {
            using var context = CreateServiceContext();
            var singleLabelGameId = Guid.NewGuid();
            SeedGame(
                context,
                singleLabelGameId,
                Achievement("One", "Base Game", unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                Achievement("Two", "Base Game", unlocked: false));

            context.Service.PopulateSingleGameDataSync(singleLabelGameId);

            Assert.IsFalse(context.Settings.ModernTheme.HasCategorySummaries);
            Assert.AreEqual(0, context.Settings.ModernTheme.DynamicCategorySummaries.Count);

            var blankLabelGameId = Guid.NewGuid();
            SeedGame(
                context,
                blankLabelGameId,
                Achievement("Three", null, unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0)),
                Achievement("Four", "   ", unlocked: false));

            context.Service.PopulateSingleGameDataSync(blankLabelGameId);

            Assert.IsFalse(context.Settings.ModernTheme.HasCategorySummaries);
            Assert.AreEqual(0, context.Settings.ModernTheme.DynamicCategorySummaries.Count);
        }

        [TestMethod]
        public void DynamicCategorySummaries_DefaultSortUsesCustomOrderAndNameSortReSorts()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            var data = SeedGame(
                context,
                gameId,
                Achievement("C1", "Charlie", unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                Achievement("A1", "Alpha", unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0)),
                Achievement("B1", "Bravo", unlocked: false));
            data.AchievementCategoryOrder = new List<string> { "Charlie", "Alpha", "Bravo" };

            context.Service.PopulateSingleGameDataSync(gameId);

            AssertSummaryNames(
                context.Settings.ModernTheme.DynamicCategorySummaries,
                "Charlie",
                "Alpha",
                "Bravo");

            context.Settings.SortDynamicCategorySummariesCommand.Execute("Name");

            AssertSummaryNames(
                context.Settings.ModernTheme.DynamicCategorySummaries,
                "Charlie",
                "Bravo",
                "Alpha");

            context.Settings.SetDynamicCategorySummariesSortDirectionCommand.Execute("Ascending");

            AssertSummaryNames(
                context.Settings.ModernTheme.DynamicCategorySummaries,
                "Alpha",
                "Bravo",
                "Charlie");
        }

        [TestMethod]
        public void DynamicCategorySummaries_CompletedFilterKeepsOnlyCompletedCategories()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            SeedGame(
                context,
                gameId,
                Achievement("Done One", "Done", unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                Achievement("Done Two", "Done", unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0)),
                Achievement("Partial One", "Partial", unlocked: true, unlockTimeUtc: Utc(2026, 3, 3, 9, 0, 0)),
                Achievement("Partial Two", "Partial", unlocked: false));

            context.Service.PopulateSingleGameDataSync(gameId);
            Assert.AreEqual(2, context.Settings.ModernTheme.DynamicCategorySummaries.Count);

            context.Settings.SetDynamicCategorySummariesFilterCommand.Execute("Completed");

            AssertSummaryNames(context.Settings.ModernTheme.DynamicCategorySummaries, "Done");
            Assert.IsTrue(context.Settings.ModernTheme.HasCategorySummaries);
        }

        [TestMethod]
        public void DynamicCategorySummaries_HasCategorySummariesStaysTrueWhenFilterEmptiesList()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            SeedGame(
                context,
                gameId,
                Achievement("Partial A", "Alpha", unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0)),
                Achievement("Partial A Locked", "Alpha", unlocked: false),
                Achievement("Partial B", "Bravo", unlocked: false));

            context.Service.PopulateSingleGameDataSync(gameId);
            Assert.AreEqual(2, context.Settings.ModernTheme.DynamicCategorySummaries.Count);

            context.Settings.SetDynamicCategorySummariesFilterCommand.Execute("Completed");

            Assert.AreEqual(0, context.Settings.ModernTheme.DynamicCategorySummaries.Count);
            Assert.IsTrue(context.Settings.ModernTheme.HasCategorySummaries);
        }

        [TestMethod]
        public void PopulateSingleGameDataSync_ProjectsGroupTypeFlagsPerCategory()
        {
            using var context = CreateServiceContext();
            var gameId = Guid.NewGuid();
            SeedGame(
                context,
                gameId,
                Achievement("Base One", "Base", unlocked: true, unlockTimeUtc: Utc(2026, 3, 1, 9, 0, 0), categoryType: "Base"),
                Achievement("Base Two", "Base", unlocked: false, categoryType: "Base"),
                Achievement("Bonus One", "Bonus Set", unlocked: true, unlockTimeUtc: Utc(2026, 3, 2, 9, 0, 0), categoryType: "Subset"),
                Achievement("DLC One", "Frozen Wilds", unlocked: false, categoryType: "DLC"),
                Achievement("Plain One", "Extras", unlocked: false));

            context.Service.PopulateSingleGameDataSync(gameId);

            var summaries = context.Settings.ModernTheme.DynamicCategorySummaries;

            var baseCard = FindByName(summaries, "Base");
            Assert.IsTrue(baseCard.IsBaseCategory);
            Assert.IsFalse(baseCard.IsDlcCategory);
            Assert.IsFalse(baseCard.IsSubsetCategory);
            Assert.IsFalse(baseCard.IsUpdateCategory);

            var subsetCard = FindByName(summaries, "Bonus Set");
            Assert.IsTrue(subsetCard.IsSubsetCategory);
            Assert.IsFalse(subsetCard.IsBaseCategory);

            var dlcCard = FindByName(summaries, "Frozen Wilds");
            Assert.IsTrue(dlcCard.IsDlcCategory);
            Assert.IsFalse(dlcCard.IsBaseCategory);

            var plainCard = FindByName(summaries, "Extras");
            Assert.IsFalse(plainCard.IsBaseCategory);
            Assert.IsFalse(plainCard.IsDlcCategory);
            Assert.IsFalse(plainCard.IsSubsetCategory);
            Assert.IsFalse(plainCard.IsUpdateCategory);
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
            var service = new ThemeIntegrationService(
                api,
                refreshRuntime,
                achievementDataService,
                refreshCoordinator,
                settings,
                windowService,
                new FakeLogger());

            return new ServiceTestContext(settings, achievementDataService, service);
        }

        private static GameAchievementData SeedGame(
            ServiceTestContext context,
            Guid gameId,
            params AchievementDetail[] achievements)
        {
            var data = new GameAchievementData
            {
                PlayniteGameId = gameId,
                Game = new Game { Id = gameId, Name = "Category Summary Game" },
                HasAchievements = true,
                Achievements = achievements.ToList()
            };
            context.AchievementDataService.GameDataById[gameId] = data;
            return data;
        }

        private static AchievementDetail Achievement(
            string name,
            string category,
            bool unlocked,
            double? percent = null,
            DateTime? unlockTimeUtc = null,
            string categoryType = null)
        {
            return new AchievementDetail
            {
                ApiName = name,
                DisplayName = name,
                Category = category,
                CategoryType = categoryType,
                Unlocked = unlocked,
                GlobalPercentUnlocked = percent,
                Rarity = percent.HasValue
                    ? PercentRarityHelper.GetRarityTier(percent.Value)
                    : RarityTier.Common,
                UnlockTimeUtc = unlockTimeUtc
            };
        }

        private static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
        {
            return DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, second), DateTimeKind.Utc);
        }

        private static GameAchievementSummary FindByName(IEnumerable<GameAchievementSummary> items, string name)
        {
            foreach (var item in items)
            {
                if (item != null && item.Name == name)
                {
                    return item;
                }
            }

            Assert.Fail($"Expected category summary named '{name}'.");
            return null;
        }

        private static void AssertSummaryNames(IEnumerable<GameAchievementSummary> summaries, params string[] expectedNames)
        {
            CollectionAssert.AreEqual(
                expectedNames,
                summaries.Select(item => item.Name).ToArray());
        }

        private sealed class ServiceTestContext : IDisposable
        {
            public ServiceTestContext(
                PlayniteAchievementsSettings settings,
                AchievementDataService achievementDataService,
                ThemeIntegrationService service)
            {
                Settings = settings;
                AchievementDataService = achievementDataService;
                Service = service;
            }

            public PlayniteAchievementsSettings Settings { get; }

            public AchievementDataService AchievementDataService { get; }

            public ThemeIntegrationService Service { get; }

            public void Dispose()
            {
                Service?.Dispose();
            }
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
