using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.IO;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ShadPS4DataProviderTests
    {
        [TestMethod]
        public void IsCapable_WithOverride_ReturnsTrueWithoutAutoDetection()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ShadPS4MatchIdOverride = "npwr12345_00"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(configuredPath: null);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Test Game",
                    InstallDirectory = @"C:\Games\Test"
                };

                Assert.IsTrue(provider.IsCapable(game));
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveNpCommIdForGame_ReadsNpbindBesideRomEboot()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameDir = Path.Combine(tempDir, "Dump", "CUSA03749");
                Directory.CreateDirectory(Path.Combine(gameDir, "sce_sys"));
                File.WriteAllBytes(Path.Combine(gameDir, "sce_sys", "npbind.dat"),
                    System.Text.Encoding.ASCII.GetBytes("..UP0001-NPWR10146_00.."));
                File.WriteAllBytes(Path.Combine(gameDir, "eboot.bin"), new byte[4]);

                var provider = CreateProvider(configuredPath: null);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Rom Game",
                    Roms = new System.Collections.ObjectModel.ObservableCollection<GameRom>
                    {
                        new GameRom("Rom Game", Path.Combine(gameDir, "eboot.bin"))
                    }
                };

                Assert.AreEqual("NPWR10146_00", provider.ResolveNpCommIdForGame(game));
                Assert.AreEqual("CUSA03749", provider.ExtractTitleIdFromGame(game));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveNpCommIdForGame_ReadsNpbindInsideZarRom()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var archivePath = Path.Combine(tempDir, "CUSA01623.zar");
                File.WriteAllBytes(archivePath, ZArchiveTestWriter.Build(new System.Collections.Generic.Dictionary<string, byte[]>
                {
                    ["eboot.bin"] = new byte[64],
                    ["sce_sys/npbind.dat"] = System.Text.Encoding.ASCII.GetBytes("..EP9000-NPWR08268_00..")
                }));

                var provider = CreateProvider(configuredPath: null);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Zar Game",
                    InstallDirectory = tempDir,
                    Roms = new System.Collections.ObjectModel.ObservableCollection<GameRom>
                    {
                        new GameRom("Zar Game", archivePath)
                    }
                };

                Assert.AreEqual("NPWR08268_00", provider.ResolveNpCommIdForGame(game));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static ShadPS4DataProvider CreateProvider(string configuredPath)
        {
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "ShadPS4" });
            var providerSettings = registry.GetSettings<ShadPS4Settings>();
            providerSettings.GameDataPaths = PlayniteAchievements.Providers.Settings.ProviderPathList.FromLegacy(configuredPath);
            registry.Save(providerSettings);

            return new ShadPS4DataProvider(new FakeLogger(), settings, new FakePlayniteApi());
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(ShadPS4DataProviderTests),
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
