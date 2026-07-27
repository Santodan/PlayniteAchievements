using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AuthProbeCandidateTests
    {
        [TestMethod]
        public void ResolveAuthProbeCandidates_NullRequest_ReturnsAllProviders()
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var steam = new FakeProvider("Steam", _ => true);
            var psn = new FakeProvider("PSN", _ => false);

            var candidates = planner.ResolveAuthProbeCandidates(
                null,
                new IDataProvider[] { steam, psn });

            CollectionAssert.AreEquivalent(
                new IDataProvider[] { steam, psn },
                candidates.ToList());
        }

        [TestMethod]
        public void ResolveAuthProbeCandidates_ExplicitGameIds_ReturnsOnlyCapableProviders()
        {
            var gameId = Guid.NewGuid();
            var planner = CreatePlanner(new[]
            {
                new Game { Id = gameId, Name = "Steam Game" }
            });
            var steam = new FakeProvider("Steam", _ => true);
            var psn = new FakeProvider("PSN", _ => false);

            var candidates = planner.ResolveAuthProbeCandidates(
                new RefreshRequest { GameIds = new List<Guid> { gameId } },
                new IDataProvider[] { steam, psn });

            Assert.AreEqual(1, candidates.Count);
            Assert.AreSame(steam, candidates[0]);
        }

        [TestMethod]
        public void ResolveAuthProbeCandidates_ForcedOverride_IncludesOverrideProviderDespiteCapabilityAndAuth()
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
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Xenia",
                        Value = "4D5307E6"
                    }
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var planner = CreatePlanner(new[]
                {
                    new Game { Id = gameId, Name = "Override Game" }
                });
                var steam = new FakeProvider("Steam", _ => true);
                var xenia = new FakeProvider("Xenia", _ => false, isAuthenticated: false);

                var candidates = planner.ResolveAuthProbeCandidates(
                    new RefreshRequest { GameIds = new List<Guid> { gameId } },
                    new IDataProvider[] { steam, xenia });

                Assert.AreEqual(1, candidates.Count);
                Assert.AreSame(xenia, candidates[0]);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveAuthProbeCandidates_FriendsMode_ReturnsFriendCapableProviders()
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var steam = new FakeProvider("Steam", _ => false, friendsProvider: new FakeFriendsProvider("Steam"));
            var epic = new FakeProvider("Epic", _ => true);

            var candidates = planner.ResolveAuthProbeCandidates(
                new RefreshRequest { Mode = RefreshModeType.FriendsRecent },
                new IDataProvider[] { steam, epic });

            Assert.AreEqual(1, candidates.Count);
            Assert.AreSame(steam, candidates[0]);
        }

        [TestMethod]
        public void ResolveAuthProbeCandidates_ProviderKeys_RestrictCandidates()
        {
            var gameId = Guid.NewGuid();
            var planner = CreatePlanner(new[]
            {
                new Game { Id = gameId, Name = "Shared Game" }
            });
            var steam = new FakeProvider("Steam", _ => true);
            var epic = new FakeProvider("Epic", _ => true);

            var candidates = planner.ResolveAuthProbeCandidates(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Custom,
                    Options = RefreshOptions.FromCustom(new CustomRefreshOptions
                    {
                        ProviderKeys = new[] { "Steam" },
                        Scope = CustomGameScope.Explicit,
                        IncludeGameIds = new[] { gameId }
                    })
                },
                new IDataProvider[] { steam, epic });

            Assert.AreEqual(1, candidates.Count);
            Assert.AreSame(steam, candidates[0]);
        }

        [TestMethod]
        public void ResolveAuthProbeCandidates_RecentMode_IgnoresRecentLimit()
        {
            var newerGameId = Guid.NewGuid();
            var olderGameId = Guid.NewGuid();
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.RecentRefreshGamesCount = 1;
            var planner = CreatePlanner(new[]
            {
                new Game { Id = newerGameId, Name = "Newer", LastActivity = DateTime.Now },
                new Game { Id = olderGameId, Name = "Older", LastActivity = DateTime.Now.AddDays(-7) }
            }, settings);
            var steam = new FakeProvider("Steam", game => game?.Id == newerGameId);
            var epic = new FakeProvider("Epic", game => game?.Id == olderGameId);

            var candidates = planner.ResolveAuthProbeCandidates(
                new RefreshRequest { Mode = RefreshModeType.Recent },
                new IDataProvider[] { steam, epic });

            CollectionAssert.AreEquivalent(
                new IDataProvider[] { steam, epic },
                candidates.ToList());
        }

        [TestMethod]
        public void ResolveAuthProbeCandidates_SingleModeWithoutGameId_ReturnsEmpty()
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var steam = new FakeProvider("Steam", _ => true);

            var candidates = planner.ResolveAuthProbeCandidates(
                new RefreshRequest { Mode = RefreshModeType.Single },
                new IDataProvider[] { steam });

            Assert.AreEqual(0, candidates.Count);
        }

        [TestMethod]
        public async Task GetRefreshAuthContextAsync_FilteredRequest_ProbesOnlyCapableProviders()
        {
            var gameId = Guid.NewGuid();
            var steam = new FakeProvider("Steam", _ => true);
            var psn = new FakeProvider("PSN", _ => false);
            var runtime = CreateRuntime(
                new[] { new Game { Id = gameId, Name = "Steam Game" } },
                steam,
                psn);

            var context = await runtime.GetRefreshAuthContextAsync(
                new RefreshRequest { GameIds = new List<Guid> { gameId } },
                CancellationToken.None);

            Assert.AreEqual(1, context.AuthenticatedProviders.Count);
            Assert.AreSame(steam, context.AuthenticatedProviders[0]);
            Assert.AreEqual(1, steam.AuthReadCount);
            Assert.AreEqual(0, psn.AuthReadCount);
        }

        [TestMethod]
        public async Task GetRefreshAuthContextAsync_SecondChance_ProbesRemainingProvidersWhenFilteredSetUnauthenticated()
        {
            var gameId = Guid.NewGuid();
            var steam = new FakeProvider("Steam", _ => true, isAuthenticated: false);
            var psn = new FakeProvider("PSN", _ => false, isAuthenticated: true);
            var runtime = CreateRuntime(
                new[] { new Game { Id = gameId, Name = "Steam Game" } },
                steam,
                psn);

            var context = await runtime.GetRefreshAuthContextAsync(
                new RefreshRequest { GameIds = new List<Guid> { gameId } },
                CancellationToken.None);

            Assert.AreEqual(1, context.AuthenticatedProviders.Count);
            Assert.AreSame(psn, context.AuthenticatedProviders[0]);
            Assert.AreEqual(1, steam.AuthReadCount);
            Assert.AreEqual(1, psn.AuthReadCount);
        }

        [TestMethod]
        public async Task GetRefreshAuthContextAsync_PopulatesTargetSelectionCacheForReuse()
        {
            var gameId = Guid.NewGuid();
            var steam = new FakeProvider("Steam", _ => true);
            var runtime = CreateRuntime(
                new[] { new Game { Id = gameId, Name = "Steam Game" } },
                steam);

            var context = await runtime.GetRefreshAuthContextAsync(
                new RefreshRequest { GameIds = new List<Guid> { gameId } },
                CancellationToken.None);

            Assert.IsNotNull(context.TargetSelectionCache);
            Assert.IsTrue(context.TargetSelectionCache.TryGetCapability(gameId, "Steam", out var capable));
            Assert.IsTrue(capable);
        }

        private static RefreshRequestPlanner CreatePlanner(
            IEnumerable<Game> games,
            PlayniteAchievementsSettings settings = null)
        {
            settings = settings ?? new PlayniteAchievementsSettings();
            var api = new FakePlayniteApi(games ?? Enumerable.Empty<Game>());
            var resolver = new TargetSelectionResolver(
                api,
                settings,
                new FakeCacheManager(),
                logger: null,
                refreshOrder: new[] { "Steam", "Epic", "PSN", "Xenia" });

            return new RefreshRequestPlanner(api, settings, logger: null, resolver);
        }

        private static RefreshRuntime CreateRuntime(
            IEnumerable<Game> games,
            params IDataProvider[] providers)
        {
            var api = new FakePlayniteApi(games ?? Enumerable.Empty<Game>());
            return new RefreshRuntime(
                new RefreshRuntime.TestRuntimeCache(),
                new PlayniteAchievementsSettings(),
                api,
                providers);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(AuthProbeCandidateTests),
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
            private readonly bool _isAuthenticated;

            public FakeProvider(
                string providerKey,
                Func<Game, bool> isCapable,
                bool isAuthenticated = true,
                IFriendsProvider friendsProvider = null)
            {
                ProviderKey = providerKey;
                _isCapable = isCapable ?? (_ => false);
                _isAuthenticated = isAuthenticated;
                Friends = friendsProvider;
            }

            public int AuthReadCount { get; private set; }

            public string ProviderName => ProviderKey;
            public string ProviderKey { get; }
            public string ProviderIconKey => ProviderKey;
            public string ProviderColorHex => "#000000";

            public bool IsAuthenticated
            {
                get
                {
                    AuthReadCount++;
                    return _isAuthenticated;
                }
            }

            public ISessionManager AuthSession => null;
            public IFriendsProvider Friends { get; }

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
            public void ApplySettings(IProviderSettings settings) { }
            public ProviderSettingsViewBase CreateSettingsView() => null;
        }

        private sealed class FakeFriendsProvider : IFriendsProvider
        {
            public FakeFriendsProvider(string providerKey)
            {
                ProviderKey = providerKey;
            }

            public string ProviderKey { get; }

            public Task<FriendsProviderResult<FriendsRefreshPreparation>> BeginRefreshAsync(CancellationToken cancel) =>
                Task.FromResult(FriendsProviderResult<FriendsRefreshPreparation>.FromData(new FriendsRefreshPreparation()));

            public void EndRefresh() { }

            public Task<FriendsProviderResult<IReadOnlyList<FriendIdentity>>> GetFriendsAsync(CancellationToken cancel) =>
                Task.FromResult(FriendsProviderResult<IReadOnlyList<FriendIdentity>>.FromData(Array.Empty<FriendIdentity>()));

            public Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetOwnedGamesAsync(
                FriendIdentity friend,
                CancellationToken cancel) =>
                Task.FromResult(FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.FromData(Array.Empty<FriendGameOwnership>()));

            public Task<FriendsProviderResult<FriendGameAchievements>> GetFriendGameAchievementsAsync(
                FriendIdentity friend,
                string providerGameKey,
                int appId,
                string gameName,
                CancellationToken cancel) =>
                Task.FromResult(FriendsProviderResult<FriendGameAchievements>.FromData(new FriendGameAchievements()));

            public Task<FriendsProviderResult<FriendGameDefinition>> GetFriendGameDefinitionAsync(
                string providerGameKey,
                int appId,
                string gameName,
                CancellationToken cancel) =>
                Task.FromResult(FriendsProviderResult<FriendGameDefinition>.FromData(new FriendGameDefinition
                {
                    ProviderKey = ProviderKey,
                    AppId = appId,
                    GameName = gameName,
                    Status = FriendGameDefinitionStatus.NoAchievements
                }));
        }

        private sealed class FakeCacheManager : ICacheManager
        {
#pragma warning disable CS0067
            public event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated;
            public event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated;
            public event EventHandler<CacheInvalidatedEventArgs> CacheInvalidated;
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
            public void NotifyCacheInvalidated(IReadOnlyList<Guid> changedGameIds) { }
            public void ClearCache() { }
            public string ExportDatabaseToCsv(string exportDirectory) => null;
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public FakePlayniteApi(IEnumerable<Game> games)
            {
                Database = new FakeGameDatabase(games);
                MainView = new FakeMainView();
            }

            public IMainViewAPI MainView { get; }
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
            public FakeGameDatabase(IEnumerable<Game> games)
            {
                Games = new FakeGameCollection(games);
            }

            public IItemCollection<Game> Games { get; }
            public IItemCollection<Platform> Platforms => null;
            public IItemCollection<Emulator> Emulators => null;
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
            public event EventHandler DatabaseOpened { add { } remove { } }
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

        private sealed class FakeMainView : IMainViewAPI
        {
            public DesktopView ActiveDesktopView { get; set; }
            public FullscreenView ActiveFullscreenView { get; set; }
            public SortOrder SortOrder { get; set; }
            public SortOrderDirection SortOrderDirection { get; set; }
            public GroupableField Grouping { get; set; }
            public Dispatcher UIDispatcher => Dispatcher.CurrentDispatcher;
            public IEnumerable<Game> SelectedGames => Enumerable.Empty<Game>();
            public List<Game> FilteredGames => new List<Game>();
            public bool OpenPluginSettings(Guid pluginId) => false;
            public void SwitchToLibraryView() { }
            public void SelectGame(Guid gameId) { }
            public void SelectGames(IEnumerable<Guid> gameIds) { }
            public void ApplyFilterPreset(Guid filterId) { }
            public void ApplyFilterPreset(FilterPreset preset) { }
            public Guid GetActiveFilterPreset() => Guid.Empty;
            public FilterPresetSettings GetCurrentFilterSettings() => null;
            public void OpenSearch(string searchTerm) { }
            public void OpenSearch(SearchContext context, string searchTerm) { }
            public bool? OpenEditDialog(Guid gameId) => null;
            public bool? OpenEditDialog(List<Guid> gameIds) => null;
            public List<FilterPreset> GetSortedFilterPresets() => new List<FilterPreset>();
            public List<FilterPreset> GetSortedFilterFullscreenPresets() => new List<FilterPreset>();
            public void ToggleFullscreenView() { }
        }

        private sealed class FakeGameCollection : IItemCollection<Game>
        {
            private readonly Dictionary<Guid, Game> _items;

            public FakeGameCollection(IEnumerable<Game> games)
            {
                _items = (games ?? Enumerable.Empty<Game>())
                    .Where(game => game != null)
                    .ToDictionary(game => game.Id, game => game);
            }

            public GameDatabaseCollection CollectionType => GameDatabaseCollection.Games;
            public int Count => _items.Count;
            public bool IsReadOnly => false;
            public Game this[Guid id] { get => Get(id); set => _items[id] = value; }
#pragma warning disable CS0067
            public event EventHandler<ItemCollectionChangedEventArgs<Game>> ItemCollectionChanged;
            public event EventHandler<ItemUpdatedEventArgs<Game>> ItemUpdated;
#pragma warning restore CS0067
            public bool ContainsItem(Guid id) => _items.ContainsKey(id);
            public Game Get(Guid id) => _items.TryGetValue(id, out var game) ? game : null;
            public List<Game> Get(IList<Guid> ids) => ids?.Select(Get).Where(item => item != null).ToList() ?? new List<Game>();
            public Game Add(string name) => throw new NotSupportedException();
            public Game Add(string name, Func<Game, string, bool> mergeAction) => throw new NotSupportedException();
            public IEnumerable<Game> Add(List<string> items) => throw new NotSupportedException();
            public Game Add(MetadataProperty item) => throw new NotSupportedException();
            public IEnumerable<Game> Add(IEnumerable<MetadataProperty> items) => throw new NotSupportedException();
            public IEnumerable<Game> Add(List<string> items, Func<Game, string, bool> mergeAction) => throw new NotSupportedException();
            public void Add(IEnumerable<Game> items) => Update(items);
            public bool Remove(Guid id) => _items.Remove(id);
            public bool Remove(IEnumerable<Game> items)
            {
                var removed = false;
                foreach (var item in items ?? Enumerable.Empty<Game>())
                {
                    removed |= item != null && _items.Remove(item.Id);
                }

                return removed;
            }

            public void Update(Game item)
            {
                if (item != null)
                {
                    _items[item.Id] = item;
                }
            }

            public void Update(IEnumerable<Game> items)
            {
                foreach (var item in items ?? Enumerable.Empty<Game>())
                {
                    Update(item);
                }
            }

            public IDisposable BufferedUpdate() => NullDisposable.Instance;
            public void BeginBufferUpdate() { }
            public void EndBufferUpdate() { }
            public IEnumerable<Game> GetClone() => _items.Values.ToList();
            public void Add(Game item) => Update(item);
            public void Clear() => _items.Clear();
            public bool Contains(Game item) => item != null && _items.ContainsKey(item.Id);
            public void CopyTo(Game[] array, int arrayIndex) => _items.Values.ToList().CopyTo(array, arrayIndex);
            public bool Remove(Game item) => item != null && _items.Remove(item.Id);
            public IEnumerator<Game> GetEnumerator() => _items.Values.GetEnumerator();
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
