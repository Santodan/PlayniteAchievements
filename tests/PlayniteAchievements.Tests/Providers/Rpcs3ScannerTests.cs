using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class Rpcs3ScannerTests
    {
        [TestMethod]
        public async Task RefreshAsync_NameFallback_ExactTitleMatchWinsBeforeFuzzyMatch()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR33333_00", "God of War III", "Wrong Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "God of War", "Exact Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "God of War",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Exact Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NpwrOverride_BeatsAutomaticNameMatching()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "Override Game", "Override Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "Detected Game", "Detected Trophy");

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "RPCS3",
                        Value = "npwr11111_00"
                    }
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Detected Game",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
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
        public async Task RefreshAsync_MissingNpwrOverride_DoesNotFallBackToAutomaticNameMatching()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "Detected Game", "Detected Trophy");

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "RPCS3",
                        Value = "NPWR99999_00"
                    }
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Detected Game",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("RPCS3", data.ProviderKey);
                Assert.IsFalse(data.HasAchievements);
                Assert.AreEqual(0, data.Achievements.Count);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        private static async Task<GameAchievementData> RefreshSingleGameAsync(Rpcs3DataProvider provider, Game game)
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

        private static Rpcs3DataProvider CreateProvider(string rpcs3Root)
        {
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "RPCS3" });
            var providerSettings = registry.GetSettings<Rpcs3Settings>();
            providerSettings.ExecutablePath = Path.Combine(rpcs3Root, "rpcs3.exe");
            registry.Save(providerSettings);

            return new Rpcs3DataProvider(new FakeLogger(), settings, new FakePlayniteApi());
        }

        private static void CreateRpcs3TrophyData(string rpcs3Root, string npCommId, string titleName, string trophyName)
        {
            File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

            var trophyDir = Path.Combine(rpcs3Root, "dev_hdd0", "home", "00000001", "trophy", npCommId);
            Directory.CreateDirectory(trophyDir);
            File.WriteAllText(
                Path.Combine(trophyDir, "TROPCONF.SFM"),
                BuildTropconfXml(npCommId, titleName, trophyName));
        }

        private static string CreateRpcs3Root(string rpcs3Root)
        {
            Directory.CreateDirectory(rpcs3Root);
            Directory.CreateDirectory(Path.Combine(rpcs3Root, "dev_hdd0", "home", "00000001", "trophy"));
            return rpcs3Root;
        }

        private static string BuildTropconfXml(string npCommId, string titleName, string trophyName)
        {
            return $@"<trophyconf>
  <npcommid>{npCommId}</npcommid>
  <title-name>{titleName}</title-name>
  <trophy id=""0"" ttype=""B"" hidden=""no"">
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
                nameof(Rpcs3ScannerTests),
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
