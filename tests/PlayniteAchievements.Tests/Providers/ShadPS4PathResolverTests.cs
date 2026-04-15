using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.ShadPS4;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ShadPS4PathResolverTests
    {
        [TestMethod]
        public void ShadPS4Settings_DefaultsGameDataPathToAppDataRoot()
        {
            var settings = new ShadPS4Settings();
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "shadPS4");

            Assert.AreEqual(expected, settings.GameDataPath);
        }

        [TestMethod]
        public void Provider_AppDataSetting_DrivesNewFormatPathsFromSettings()
        {
            var root = CreateTempDirectory();

            try
            {
                var userId = "1001";
                var userTrophyPath = Path.Combine(root, "home", userId, "trophy");
                var trophyBasePath = Path.Combine(root, "trophy", "NPWR12345_00", "Icons");
                Directory.CreateDirectory(userTrophyPath);
                Directory.CreateDirectory(trophyBasePath);
                File.WriteAllText(Path.Combine(userTrophyPath, "NPWR12345_00.xml"), "<trophyconf/>");

                var provider = CreateProvider(root);

                Assert.AreEqual(root, provider.GetAppDataPath());
                Assert.AreEqual(userId, provider.GetUserId());
                Assert.AreEqual(userTrophyPath, provider.GetTrophyUserPath());
                Assert.AreEqual(Path.Combine(root, "trophy"), provider.GetTrophyBasePath());
                Assert.IsTrue(provider.IsAuthenticated);
                Assert.IsTrue(ShadPS4PathResolver.HasConfiguredAppDataTrophyData(root));
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void Provider_LegacyGameDataSetting_PreservesLegacyPathAndDisablesAppDataBase()
        {
            var root = CreateTempDirectory();

            try
            {
                var legacyGameDataPath = Path.Combine(root, "user", "game_data");
                Directory.CreateDirectory(legacyGameDataPath);

                var provider = CreateProvider(legacyGameDataPath);

                Assert.AreEqual(legacyGameDataPath, provider.GetGameDataPath());
                Assert.IsNull(provider.GetAppDataPath());
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void Provider_AppDataSetting_FallsBackToGameEmulatorLegacyPath()
        {
            var appDataRoot = CreateTempDirectory();
            var emulatorRoot = CreateTempDirectory();

            try
            {
                var emulatorGameDataPath = Path.Combine(emulatorRoot, "user", "game_data");
                Directory.CreateDirectory(emulatorGameDataPath);

                var emulatorId = Guid.NewGuid();
                var emulator = new Emulator
                {
                    Id = emulatorId,
                    Name = "Custom Emulator",
                    BuiltInConfigId = string.Empty,
                    InstallDir = emulatorRoot
                };

                var api = new FakePlayniteApi(new[] { emulator });
                var provider = CreateProvider(appDataRoot, api);
                var game = new Game
                {
                    GameActions = new ObservableCollection<GameAction>
                    {
                        new GameAction
                        {
                            Type = GameActionType.Emulator,
                            EmulatorId = emulatorId
                        }
                    }
                };

                Assert.AreEqual(emulatorGameDataPath, provider.GetGameDataPath(game));
            }
            finally
            {
                DeleteDirectory(appDataRoot);
                DeleteDirectory(emulatorRoot);
            }
        }

        [TestMethod]
        public void Provider_AppDataSetting_FallsBackToFirstFoundEmulatorLegacyPath()
        {
            var appDataRoot = CreateTempDirectory();
            var emulatorRoot = CreateTempDirectory();

            try
            {
                var emulatorGameDataPath = Path.Combine(emulatorRoot, "user", "game_data");
                Directory.CreateDirectory(emulatorGameDataPath);

                var emulator = new Emulator
                {
                    Id = Guid.NewGuid(),
                    Name = "ShadPS4 Nightly",
                    BuiltInConfigId = string.Empty,
                    InstallDir = emulatorRoot
                };

                var api = new FakePlayniteApi(new[] { emulator });
                var provider = CreateProvider(appDataRoot, api);

                Assert.AreEqual(emulatorGameDataPath, provider.GetGameDataPath());
            }
            finally
            {
                DeleteDirectory(appDataRoot);
                DeleteDirectory(emulatorRoot);
            }
        }

        private static ShadPS4DataProvider CreateProvider(string configuredPath, IPlayniteAPI api = null)
        {
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "ShadPS4" });
            var providerSettings = registry.GetSettings<ShadPS4Settings>();
            providerSettings.GameDataPath = configuredPath;
            registry.Save(providerSettings);

            return new ShadPS4DataProvider(new FakeLogger(), settings, api ?? new FakePlayniteApi(Array.Empty<Emulator>()));
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(ShadPS4PathResolverTests),
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
            public FakePlayniteApi(IEnumerable<Emulator> emulators)
            {
                Database = new FakeGameDatabase(emulators ?? Enumerable.Empty<Emulator>());
            }

            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database { get; }
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

        private sealed class FakeGameDatabase : IGameDatabaseAPI
        {
            public FakeGameDatabase(IEnumerable<Emulator> emulators)
            {
                Emulators = new FakeEmulatorCollection(emulators);
            }

            public IItemCollection<Game> Games => null;
            public IItemCollection<Platform> Platforms => null;
            public IItemCollection<Emulator> Emulators { get; }
            public IItemCollection<Genre> Genres => null;
            public IItemCollection<Company> Companies => null;
            public IItemCollection<Tag> Tags => null;
            public IItemCollection<Category> Categories => null;
            public IItemCollection<Series> Series => null;
            public IItemCollection<AgeRating> AgeRatings => null;
            public IItemCollection<Region> Regions => null;
            public IItemCollection<GameSource> Sources => null;
            public IItemCollection<GameFeature> Features => null;
            public IItemCollection<GameScannerConfig> GameScanners => null;
            public IItemCollection<CompletionStatus> CompletionStatuses => null;
            public IItemCollection<ImportExclusionItem> ImportExclusions => null;
            public IItemCollection<FilterPreset> FilterPresets => null;
            public bool IsOpen => true;
            public string DatabasePath => string.Empty;
            public event EventHandler DatabaseOpened
            {
                add { }
                remove { }
            }

            public string AddFile(string path, Guid parentId) => path;
            public void SaveFile(string path, string sourceFile) { }
            public void RemoveFile(string path) { }
            public IDisposable BufferedUpdate() => NullDisposable.Instance;
            public void BeginBufferUpdate() { }
            public void EndBufferUpdate() { }
            public string GetFileStoragePath(Guid parentId) => string.Empty;
            public string GetFullFilePath(string path) => path;
            public Game ImportGame(GameMetadata gameMetadata) => null;
            public Game ImportGame(GameMetadata gameMetadata, LibraryPlugin libraryPlugin) => null;
            public bool GetGameMatchesFilter(Game game, FilterPresetSettings filter) => false;
            public IEnumerable<Game> GetFilteredGames(FilterPresetSettings filter) => Enumerable.Empty<Game>();
            public bool GetGameMatchesFilter(Game game, FilterPresetSettings filter, bool ignoreHidden) => false;
            public IEnumerable<Game> GetFilteredGames(FilterPresetSettings filter, bool ignoreHidden) => Enumerable.Empty<Game>();
        }

        private sealed class FakeEmulatorCollection : IItemCollection<Emulator>
        {
            private readonly Dictionary<Guid, Emulator> _items;

            public FakeEmulatorCollection(IEnumerable<Emulator> emulators)
            {
                _items = (emulators ?? Enumerable.Empty<Emulator>())
                    .Where(emulator => emulator != null)
                    .ToDictionary(emulator => emulator.Id, emulator => emulator);
            }

            public GameDatabaseCollection CollectionType => GameDatabaseCollection.Emulators;
            public int Count => _items.Count;
            public bool IsReadOnly => false;
            public Emulator this[Guid id]
            {
                get => Get(id);
                set => _items[id] = value;
            }

#pragma warning disable CS0067
            public event EventHandler<ItemCollectionChangedEventArgs<Emulator>> ItemCollectionChanged;
            public event EventHandler<ItemUpdatedEventArgs<Emulator>> ItemUpdated;
#pragma warning restore CS0067

            public bool ContainsItem(Guid id) => _items.ContainsKey(id);
            public Emulator Get(Guid id) => _items.TryGetValue(id, out var emulator) ? emulator : null;
            public List<Emulator> Get(IList<Guid> ids) => ids?.Select(Get).Where(item => item != null).ToList() ?? new List<Emulator>();
            public Emulator Add(string name) => throw new NotSupportedException();
            public Emulator Add(string name, Func<Emulator, string, bool> mergeAction) => throw new NotSupportedException();
            public IEnumerable<Emulator> Add(List<string> items) => throw new NotSupportedException();
            public Emulator Add(MetadataProperty item) => throw new NotSupportedException();
            public IEnumerable<Emulator> Add(IEnumerable<MetadataProperty> items) => throw new NotSupportedException();
            public IEnumerable<Emulator> Add(List<string> items, Func<Emulator, string, bool> mergeAction) => throw new NotSupportedException();
            public void Add(IEnumerable<Emulator> items)
            {
                if (items == null)
                {
                    return;
                }

                foreach (var item in items.Where(item => item != null))
                {
                    _items[item.Id] = item;
                }
            }

            public bool Remove(Guid id) => _items.Remove(id);
            public bool Remove(IEnumerable<Emulator> items)
            {
                var removed = false;
                if (items == null)
                {
                    return false;
                }

                foreach (var item in items.Where(item => item != null))
                {
                    removed |= _items.Remove(item.Id);
                }

                return removed;
            }

            public void Update(Emulator item)
            {
                if (item != null)
                {
                    _items[item.Id] = item;
                }
            }

            public void Update(IEnumerable<Emulator> items)
            {
                if (items == null)
                {
                    return;
                }

                foreach (var item in items.Where(item => item != null))
                {
                    _items[item.Id] = item;
                }
            }

            public IDisposable BufferedUpdate() => NullDisposable.Instance;
            public void BeginBufferUpdate() { }
            public void EndBufferUpdate() { }
            public IEnumerable<Emulator> GetClone() => _items.Values.ToList();
            public void Add(Emulator item) => Update(item);
            public void Clear() => _items.Clear();
            public bool Contains(Emulator item) => item != null && _items.ContainsKey(item.Id);
            public void CopyTo(Emulator[] array, int arrayIndex) => _items.Values.ToList().CopyTo(array, arrayIndex);
            public bool Remove(Emulator item) => item != null && _items.Remove(item.Id);
            public IEnumerator<Emulator> GetEnumerator() => _items.Values.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Dispose() { }
        }

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new NullDisposable();
            public void Dispose() { }
        }
    }
}
