using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class XeniaScannerTests
    {
        [TestMethod]
        public void ResolveTitleId_ManualOverrideBeatsCacheAndUpdatesCache()
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
                    XeniaTitleIdOverride = "0x4d5307e6"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var cacheDir = Path.Combine(tempDir, "xenia");
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(
                    Path.Combine(cacheDir, "titleID_cache.json"),
                    JsonConvert.SerializeObject(new List<KeyValuePair<Guid, string>>
                    {
                        new KeyValuePair<Guid, string>(gameId, "11111111")
                    }));

                var scanner = new XeniaScanner(
                    logger: null,
                    playniteApi: new FakePlayniteApi(),
                    providerSettings: new XeniaSettings { AccountPath = tempDir },
                    pluginUserDataPath: tempDir);

                var resolved = scanner.ResolveTitleID(new Game
                {
                    Id = gameId,
                    Name = "Test Game"
                }, out var titleId);

                Assert.IsTrue(resolved);
                Assert.AreEqual("4D5307E6", titleId);
                Assert.IsTrue(scanner.TryGetCachedTitleId(gameId, out var cachedTitleId));
                Assert.AreEqual("4D5307E6", cachedTitleId);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_UninstalledGameWithTitleIdOverride_ProducesData()
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
                    XeniaTitleIdOverride = "0x4d5307e6"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var fakeApi = new FakePlayniteApi();
                var scanner = new XeniaScanner(
                    logger: new FakeLogger(),
                    playniteApi: fakeApi,
                    providerSettings: new XeniaSettings { AccountPath = tempDir },
                    pluginUserDataPath: tempDir);

                GameAchievementData completedData = null;
                var payload = await scanner.RefreshAsync(
                    new List<Game>
                    {
                        new Game
                        {
                            Id = gameId,
                            Name = "Override Game",
                            IsInstalled = false
                        }
                    },
                    onGameStarting: _ => { },
                    onGameCompleted: (game, data) =>
                    {
                        completedData = data;
                        return Task.CompletedTask;
                    },
                    cancel: CancellationToken.None);

                Assert.IsNotNull(completedData);
                Assert.AreEqual(0x4D5307E6, completedData.AppId);
                Assert.IsFalse(completedData.HasAchievements);
                Assert.AreEqual(1, payload?.Summary?.GamesRefreshed ?? 0);

                var hasInstallErrorNotification = fakeApi.TestNotifications.Messages.Any(message =>
                    message != null &&
                    message.Type == NotificationType.Error &&
                    (message.Text ?? string.Empty).IndexOf("isn't installed", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.IsFalse(hasInstallErrorNotification);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(XeniaScannerTests),
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
            public FakeNotificationsApi TestNotifications { get; } = new FakeNotificationsApi();

            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database => null;
            public IDialogsFactory Dialogs => null;
            public IPlaynitePathsAPI Paths => null;
            public INotificationsAPI Notifications => TestNotifications;
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

        private sealed class FakeNotificationsApi : INotificationsAPI
        {
            public ObservableCollection<NotificationMessage> Messages { get; } = new ObservableCollection<NotificationMessage>();

            public int Count => Messages.Count;

            public void Add(NotificationMessage message)
            {
                if (message != null)
                {
                    Messages.Add(message);
                }
            }

            public void Add(string id, string text, NotificationType type)
            {
                Messages.Add(new NotificationMessage(id, text, type));
            }

            public void Remove(string id)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return;
                }

                var message = Messages.FirstOrDefault(item => string.Equals(item?.Id, id, StringComparison.OrdinalIgnoreCase));
                if (message != null)
                {
                    Messages.Remove(message);
                }
            }

            public void RemoveAll()
            {
                Messages.Clear();
            }
        }
    }
}
