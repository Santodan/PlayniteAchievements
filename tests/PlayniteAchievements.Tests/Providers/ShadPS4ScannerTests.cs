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

        [TestMethod]
        public async Task RefreshAsync_NewFormatNoUnlocks_UsesSharedTrophyMetadata()
        {
            var tempDir = CreateTempDirectory();
            var appDataRoot = Path.Combine(tempDir, "shadps4");
            var installDir = Path.Combine(tempDir, "game");
            const string npCommId = "NPWR33333_00";

            try
            {
                WriteNewFormatTrophyData(appDataRoot, "1000", npCommId, BuildNewFormatStateOnlyXml(npCommId));
                WriteSharedTrophyMetadata(
                    appDataRoot,
                    npCommId,
                    BuildSharedTrophyMetadataXml(
                        npCommId,
                        "Shared Trophy",
                        "Shared Description",
                        "Shared DLC",
                        trophyType: "S",
                        hidden: "yes"));
                CreateNewFormatIcon(appDataRoot, npCommId, "1");
                CreateNpbindFile(installDir, npCommId);

                var provider = CreateProvider(appDataRoot);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Metadata Fallback Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);

                var trophy = data.Achievements[0];
                Assert.AreEqual("1", trophy.ApiName);
                Assert.AreEqual("Shared Trophy", trophy.DisplayName);
                Assert.AreEqual("Shared Description", trophy.Description);
                Assert.AreEqual("silver", trophy.TrophyType);
                Assert.IsTrue(trophy.Hidden);
                Assert.IsFalse(trophy.Unlocked);
                Assert.IsNull(trophy.UnlockTimeUtc);
                Assert.AreEqual("DLC", trophy.CategoryType);
                Assert.AreEqual("Shared DLC", trophy.Category);
                Assert.IsFalse(string.IsNullOrWhiteSpace(trophy.UnlockedIconPath));
                Assert.AreEqual(trophy.UnlockedIconPath, trophy.LockedIconPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NewFormatWithUserMetadata_PrefersPerUserValues()
        {
            var tempDir = CreateTempDirectory();
            var appDataRoot = Path.Combine(tempDir, "shadps4");
            var installDir = Path.Combine(tempDir, "game");
            const string npCommId = "NPWR44444_00";

            try
            {
                CreateNewFormatTrophyData(appDataRoot, "1000", npCommId, "Per User Trophy");
                WriteSharedTrophyMetadata(
                    appDataRoot,
                    npCommId,
                    BuildSharedTrophyMetadataXml(
                        npCommId,
                        "Shared Trophy",
                        "Shared Description",
                        "Shared DLC",
                        trophyType: "S",
                        hidden: "yes"));
                CreateNpbindFile(installDir, npCommId);

                var provider = CreateProvider(appDataRoot);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Per User Metadata Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);

                var trophy = data.Achievements[0];
                Assert.AreEqual("Per User Trophy", trophy.DisplayName);
                Assert.AreEqual("Description", trophy.Description);
                Assert.AreEqual("bronze", trophy.TrophyType);
                Assert.IsFalse(trophy.Hidden);
                Assert.IsTrue(trophy.Unlocked);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NewFormatNoUnlocks_UsesFlatSharedMetadataWhenNpCommIdMatches()
        {
            var tempDir = CreateTempDirectory();
            var appDataRoot = Path.Combine(tempDir, "shadps4");
            var installDir = Path.Combine(tempDir, "game");
            const string npCommId = "NPWR55555_00";

            try
            {
                WriteNewFormatTrophyData(appDataRoot, "1000", npCommId, BuildNewFormatStateOnlyXml(npCommId));
                WriteSharedTrophyMetadata(
                    appDataRoot,
                    npCommId,
                    BuildSharedTrophyMetadataXml(
                        npCommId,
                        "Flat Shared Trophy",
                        "Flat Shared Description",
                        "Flat Shared DLC",
                        trophyType: "G",
                        hidden: "no"),
                    flat: true);
                CreateNpbindFile(installDir, npCommId);

                var provider = CreateProvider(appDataRoot);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Flat Metadata Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("Flat Shared Trophy", data.Achievements[0].DisplayName);
                Assert.AreEqual("Flat Shared Description", data.Achievements[0].Description);
                Assert.AreEqual("gold", data.Achievements[0].TrophyType);
                Assert.AreEqual("Flat Shared DLC", data.Achievements[0].Category);
            }
            finally
            {
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
            WriteNewFormatTrophyData(appDataRoot, userId, npCommId, BuildNewFormatXml(trophyName));
        }

        private static void WriteNewFormatTrophyData(string appDataRoot, string userId, string npCommId, string xml)
        {
            var userTrophyDir = Path.Combine(appDataRoot, "home", userId, "trophy");
            Directory.CreateDirectory(userTrophyDir);
            File.WriteAllText(Path.Combine(userTrophyDir, $"{npCommId}.xml"), xml);
        }

        private static void WriteSharedTrophyMetadata(string appDataRoot, string npCommId, string xml, bool flat = false)
        {
            var xmlDir = flat
                ? Path.Combine(appDataRoot, "trophy", "Xml")
                : Path.Combine(appDataRoot, "trophy", npCommId, "Xml");
            Directory.CreateDirectory(xmlDir);
            File.WriteAllText(Path.Combine(xmlDir, "TROP.XML"), xml);
        }

        private static void CreateNewFormatIcon(string appDataRoot, string npCommId, string trophyId)
        {
            var iconDir = Path.Combine(appDataRoot, "trophy", npCommId, "Icons");
            Directory.CreateDirectory(iconDir);
            File.WriteAllBytes(Path.Combine(iconDir, $"TROP{trophyId.PadLeft(3, '0')}.PNG"), new byte[] { 0 });
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

        private static string BuildNewFormatStateOnlyXml(string npCommId)
        {
            return $@"<trophyconf>
  <npcommid>{npCommId}</npcommid>
  <trophy id=""1"" />
</trophyconf>";
        }

        private static string BuildSharedTrophyMetadataXml(
            string npCommId,
            string trophyName,
            string trophyDescription,
            string groupName,
            string trophyType,
            string hidden)
        {
            return $@"<trophyconf>
  <npcommid>{npCommId}</npcommid>
  <group id=""001"">
    <name>{groupName}</name>
    <detail>{groupName} Trophy Set</detail>
  </group>
  <trophy id=""001"" ttype=""{trophyType}"" hidden=""{hidden}"" pid=""000"" gid=""001"">
    <name>{trophyName}</name>
    <detail>{trophyDescription}</detail>
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
