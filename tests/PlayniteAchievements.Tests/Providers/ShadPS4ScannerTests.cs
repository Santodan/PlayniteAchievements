using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ShadPS4ScannerTests
    {
        [TestMethod]
        public async Task RefreshAsync_NpwrOverride_BeatsNpbindDetection()
        {
            var tempDir = CreateTempDirectory();
            var appDataRoot = Path.Combine(tempDir, "shadps4");
            var installDir = Path.Combine(tempDir, "game");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateNewFormatTrophyData(appDataRoot, "1000", "NPWR11111_00", "Override Trophy");
                CreateNewFormatTrophyData(appDataRoot, "1000", "NPWR22222_00", "Detected Trophy");
                CreateNpbindFile(installDir, "NPWR22222_00");

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ShadPS4MatchIdOverride = "npwr11111_00"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(appDataRoot);
                var game = new Game
                {
                    Id = gameId,
                    Name = "ShadPS4 Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Override Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_TitleIdOverride_BeatsInstallPathTitleDetection()
        {
            var tempDir = CreateTempDirectory();
            var legacyGameDataPath = Path.Combine(tempDir, "user", "game_data");
            var installDir = Path.Combine(tempDir, "Games", "CUSA22222");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateLegacyTrophyData(legacyGameDataPath, "CUSA11111", "Override Legacy Trophy");
                CreateLegacyTrophyData(legacyGameDataPath, "CUSA22222", "Detected Legacy Trophy");

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ShadPS4MatchIdOverride = "cusa11111"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(legacyGameDataPath);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Legacy Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Override Legacy Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_OverrideMissingData_DoesNotFallBackToAutoDetection()
        {
            var tempDir = CreateTempDirectory();
            var appDataRoot = Path.Combine(tempDir, "shadps4");
            var installDir = Path.Combine(tempDir, "game");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateNewFormatTrophyData(appDataRoot, "1000", "NPWR22222_00", "Detected Trophy");
                CreateNpbindFile(installDir, "NPWR22222_00");

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ShadPS4MatchIdOverride = "NPWR11111_00"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(appDataRoot);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Missing Override Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("ShadPS4", data.ProviderKey);
                Assert.IsFalse(data.HasAchievements);
                Assert.AreEqual(0, data.Achievements.Count);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        private static async Task<GameAchievementData> RefreshSingleGameAsync(ShadPS4DataProvider provider, Game game)
        {
            GameAchievementData captured = null;

            await provider.RefreshAsync(
                new[] { game },
                onGameStarting: null,
                onGameCompleted: (completedGame, data) =>
                {
                    captured = data;
                    return Task.CompletedTask;
                },
                cancel: CancellationToken.None).ConfigureAwait(false);

            return captured;
        }

        private static ShadPS4DataProvider CreateProvider(string configuredPath)
        {
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "ShadPS4" });
            var providerSettings = registry.GetSettings<ShadPS4Settings>();
            providerSettings.GameDataPath = configuredPath;
            registry.Save(providerSettings);

            return new ShadPS4DataProvider(new FakeLogger(), settings, new FakePlayniteApi());
        }

        private static void CreateNewFormatTrophyData(string appDataRoot, string userId, string npCommId, string trophyName)
        {
            var userTrophyDir = Path.Combine(appDataRoot, "home", userId, "trophy");
            Directory.CreateDirectory(userTrophyDir);
            File.WriteAllText(
                Path.Combine(userTrophyDir, $"{npCommId}.xml"),
                BuildNewFormatXml(trophyName));
        }

        private static void CreateLegacyTrophyData(string legacyGameDataPath, string titleId, string trophyName)
        {
            var xmlDir = Path.Combine(legacyGameDataPath, titleId, "trophyfiles", "trophy00", "Xml");
            Directory.CreateDirectory(xmlDir);
            File.WriteAllText(
                Path.Combine(xmlDir, "TROP.XML"),
                BuildLegacyFormatXml(trophyName));
        }

        private static void CreateNpbindFile(string installDir, string npCommId)
        {
            var sceSysDir = Path.Combine(installDir, "sce_sys");
            Directory.CreateDirectory(sceSysDir);
            File.WriteAllText(Path.Combine(sceSysDir, "npbind.dat"), $"prefix-{npCommId}-suffix");
        }

        private static string BuildNewFormatXml(string trophyName)
        {
            return $@"<trophyconf>
  <trophy id=""1"" ttype=""B"" hidden=""no"" unlockstate=""true"" timestamp=""1710000000"">
    <name>{trophyName}</name>
    <detail>Description</detail>
  </trophy>
</trophyconf>";
        }

        private static string BuildLegacyFormatXml(string trophyName)
        {
            return $@"<trophyconf>
  <trophy id=""1"" ttype=""B"" hidden=""no"" unlockstate=""1"" timestamp=""0"">
    <name>{trophyName}</name>
    <detail>Description</detail>
  </trophy>
</trophyconf>";
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(ShadPS4ScannerTests),
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private sealed class FakeLogger : ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
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

            public string ExpandGameVariables(Game game, string source) => source;
            public string ExpandGameVariables(Game game, string source, string fallbackValue) => source ?? fallbackValue;
            public GameAction ExpandGameVariables(Game game, GameAction source) => source;
            public void StartGame(Guid id) { }
            public void InstallGame(Guid id) { }
            public void UninstallGame(Guid id) { }
            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args) { }
            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args) { }
            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args) { }
        }
    }
}
