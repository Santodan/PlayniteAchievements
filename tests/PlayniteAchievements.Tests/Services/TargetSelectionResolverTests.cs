using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class TargetSelectionResolverTests
    {
        [TestMethod]
        public void ResolveProviderForGame_XeniaOverrideForcesXeniaProvider()
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
                    XeniaTitleIdOverride = "4D5307E6"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var resolver = new TargetSelectionResolver(
                    new FakePlayniteApi(),
                    new PlayniteAchievementsSettings(),
                    new FakeCacheManager(),
                    logger: null,
                    refreshOrder: new[] { "Xbox", "Xenia" });

                var game = new Game { Id = gameId, Name = "Test Game" };
                var providers = new List<IDataProvider>
                {
                    new FakeProvider("Xbox", _ => true),
                    new FakeProvider("Xenia", _ => false)
                };

                var resolved = resolver.ResolveProviderForGame(game, providers);

                Assert.IsNotNull(resolved);
                Assert.AreEqual("Xenia", resolved.ProviderKey);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveProviderForGame_XeniaOverrideWithoutXeniaProvider_ReturnsNull()
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
                    XeniaTitleIdOverride = "4D5307E6"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var resolver = new TargetSelectionResolver(
                    new FakePlayniteApi(),
                    new PlayniteAchievementsSettings(),
                    new FakeCacheManager(),
                    logger: null,
                    refreshOrder: new[] { "Xbox" });

                var game = new Game { Id = gameId, Name = "Test Game" };
                var providers = new List<IDataProvider>
                {
                    new FakeProvider("Xbox", _ => true)
                };

                var resolved = resolver.ResolveProviderForGame(game, providers);

                Assert.IsNull(resolved);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveProviderForGame_ShadPS4OverrideForcesShadPS4Provider()
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
                    ShadPS4MatchIdOverride = "NPWR12345_00"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var resolver = new TargetSelectionResolver(
                    new FakePlayniteApi(),
                    new PlayniteAchievementsSettings(),
                    new FakeCacheManager(),
                    logger: null,
                    refreshOrder: new[] { "PSN", "ShadPS4" });

                var game = new Game { Id = gameId, Name = "Test Game" };
                var providers = new List<IDataProvider>
                {
                    new FakeProvider("PSN", _ => true),
                    new FakeProvider("ShadPS4", _ => false)
                };

                var resolved = resolver.ResolveProviderForGame(game, providers);

                Assert.IsNotNull(resolved);
                Assert.AreEqual("ShadPS4", resolved.ProviderKey);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveProviderForGame_ShadPS4OverrideWithoutProvider_ReturnsNull()
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
                    ShadPS4MatchIdOverride = "NPWR12345_00"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var resolver = new TargetSelectionResolver(
                    new FakePlayniteApi(),
                    new PlayniteAchievementsSettings(),
                    new FakeCacheManager(),
                    logger: null,
                    refreshOrder: new[] { "PSN" });

                var game = new Game { Id = gameId, Name = "Test Game" };
                var providers = new List<IDataProvider>
                {
                    new FakeProvider("PSN", _ => true)
                };

                var resolved = resolver.ResolveProviderForGame(game, providers);

                Assert.IsNull(resolved);
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
                nameof(TargetSelectionResolverTests),
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

        private sealed class FakeProvider : IDataProvider
        {
            private readonly Func<Game, bool> _isCapable;

            public FakeProvider(string providerKey, Func<Game, bool> isCapable)
            {
                ProviderKey = providerKey;
                _isCapable = isCapable ?? (_ => false);
            }

            public string ProviderName => ProviderKey;
            public string ProviderKey { get; }
            public string ProviderIconKey => ProviderKey;
            public string ProviderColorHex => "#000000";
            public bool IsAuthenticated => true;
            public ISessionManager AuthSession => null;

            public bool IsCapable(Game game) => _isCapable(game);

            public Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Game> gamesToRefresh,
                Action<Game> onGameStarting,
                Func<Game, GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
            {
                return Task.FromResult(new RebuildPayload());
            }

            public IProviderSettings GetSettings() => null;

            public void ApplySettings(IProviderSettings settings)
            {
            }

            public ProviderSettingsViewBase CreateSettingsView() => null;
        }

        private sealed class FakeCacheManager : ICacheManager
        {
#pragma warning disable CS0067
            public event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated;
            public event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated;
            public event EventHandler CacheInvalidated;
#pragma warning restore CS0067

            public void EnsureDiskCacheOrClearMemory() { }
            public bool CacheFileExists() => false;
            public bool IsCacheValid() => true;
            public DateTime? GetMostRecentLastUpdatedUtc() => null;
            public List<string> GetCachedGameIds() => new List<string>();
            public GameAchievementData LoadGameData(string key) => null;
            public CacheWriteResult SaveGameData(string key, GameAchievementData data) => null;
            public void RemoveGameData(Guid playniteGameId) { }
            public void RemoveGameCache(Guid playniteGameId) { }
            public void NotifyCacheInvalidated() { }
            public void ClearCache() { }
            public string ExportDatabaseToCsv(string exportDirectory) => null;
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
