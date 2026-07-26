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
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class RefreshRequestPlannerFriendModeTests
    {
        [DataTestMethod]
        [DataRow(RefreshModeType.FriendsRecent, FriendRefreshScope.Recent)]
        [DataRow(RefreshModeType.FriendsFull, FriendRefreshScope.Full)]
        [DataRow(RefreshModeType.FriendsShared, FriendRefreshScope.Shared)]
        public void Resolve_FriendModes_CreateFriendOptionsAndFriendProviderScope(
            RefreshModeType mode,
            FriendRefreshScope expectedScope)
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));
            var nonFriendProvider = new FakeProvider("Epic", friendsProvider: null);

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = mode },
                new IDataProvider[] { friendProvider, nonFriendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.AreEqual(RefreshSubjects.Friends, resolved.Options.Subjects);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(expectedScope, resolved.FriendOptions.Scope);
            Assert.AreEqual(1, resolved.ProviderScope.Count);
            Assert.AreSame(friendProvider, resolved.ProviderScope[0]);
        }

        [TestMethod]
        public void Resolve_FriendsInstalled_UsesInstalledGameIds()
        {
            var installedId = Guid.NewGuid();
            var notInstalledId = Guid.NewGuid();
            var planner = CreatePlanner(new[]
            {
                new Game { Id = installedId, Name = "Installed", IsInstalled = true },
                new Game { Id = notInstalledId, Name = "Not Installed", IsInstalled = false }
            });
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = RefreshModeType.FriendsInstalled },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.Installed, resolved.FriendOptions.Scope);
            CollectionAssert.AreEquivalent(
                new[] { installedId },
                resolved.FriendOptions.PlayniteGameIds.ToList());
        }

        [TestMethod]
        public void Resolve_FriendsInstalled_WithNoInstalledGames_DoesNotExecute()
        {
            var planner = CreatePlanner(new[]
            {
                new Game { Id = Guid.NewGuid(), Name = "Not Installed", IsInstalled = false }
            });
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = RefreshModeType.FriendsInstalled },
                new IDataProvider[] { friendProvider });

            Assert.IsFalse(resolved.ShouldExecute);
            Assert.AreEqual("No installed games found for friends refresh.", resolved.EmptySelectionLogMessage);
        }

        [TestMethod]
        public void Resolve_FriendsSelectedGame_UsesSelectedGameId()
        {
            var gameId = Guid.NewGuid();
            var planner = CreatePlanner(Array.Empty<Game>());
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsSelectedGame,
                    SingleGameId = gameId
                },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.SelectedGame, resolved.FriendOptions.Scope);
            Assert.IsTrue(resolved.FriendOptions.ForceDefinitionRefresh);
            CollectionAssert.AreEqual(
                new[] { gameId },
                resolved.FriendOptions.PlayniteGameIds.ToList());
        }

        [TestMethod]
        public void Resolve_FriendsSelectedGame_WithNoGame_DoesNotExecute()
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = RefreshModeType.FriendsSelectedGame },
                new IDataProvider[] { friendProvider });

            Assert.IsFalse(resolved.ShouldExecute);
            Assert.AreEqual(
                "No selected game provided for friends selected-game refresh.",
                resolved.EmptySelectionLogMessage);
        }

        [DataTestMethod]
        [DataRow(RefreshModeType.FriendsRecent, FriendRefreshScope.Recent)]
        [DataRow(RefreshModeType.FriendsFull, FriendRefreshScope.Full)]
        [DataRow(RefreshModeType.FriendsShared, FriendRefreshScope.Shared)]
        public void Resolve_BroadFriendModes_MapToFriendScope(
            RefreshModeType mode,
            FriendRefreshScope expectedScope)
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = mode },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.AreEqual(expectedScope, resolved.FriendOptions.Scope);
        }

        [DataTestMethod]
        [DataRow(RefreshModeType.FriendsFull, true)]
        [DataRow(RefreshModeType.FriendsShared, false)]
        [DataRow(RefreshModeType.FriendsRecent, false)]
        public void Resolve_FullFriendMode_ForcesDefinitionRefresh(
            RefreshModeType mode,
            bool expectedForce)
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = mode },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(expectedForce, resolved.FriendOptions.ForceDefinitionRefresh);
        }

        [DataTestMethod]
        [DataRow(FriendRefreshScope.Full, true)]
        [DataRow(FriendRefreshScope.Recent, false)]
        [DataRow(FriendRefreshScope.Shared, false)]
        [DataRow(FriendRefreshScope.Installed, false)]
        [DataRow(FriendRefreshScope.SelectedGame, false)]
        public void FriendRefreshPolicy_DiscoversProviderOnlyGames_OnlyForFullScope(
            FriendRefreshScope scope,
            bool expected)
        {
            var options = new FriendRefreshOptions { Scope = scope };

            Assert.AreEqual(expected, options.DiscoversProviderOnlyGames());
        }

        [TestMethod]
        public void FriendRefreshPolicy_DiscoversProviderOnlyGames_AllowsExplicitProviderAppIds()
        {
            var options = new FriendRefreshOptions
            {
                Scope = FriendRefreshScope.Custom,
                ProviderAppIds = new[] { 10 }
            };

            Assert.IsTrue(options.DiscoversProviderOnlyGames());
        }

        [TestMethod]
        public void Resolve_FriendsCustom_FiltersProvidersAndUsesSelectedGameScope()
        {
            var gameId = Guid.NewGuid();
            var planner = CreatePlanner(Array.Empty<Game>());
            var steamProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));
            var epicProvider = new FakeProvider("Epic", new FakeFriendsProvider("Epic"));

            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = new[] { " steam " },
                        Scope = FriendRefreshScope.SelectedGame,
                        PlayniteGameIds = new[] { Guid.Empty, gameId, gameId }
                    })
                },
                new IDataProvider[] { steamProvider, epicProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.AreEqual(1, resolved.ProviderScope.Count);
            Assert.AreSame(steamProvider, resolved.ProviderScope[0]);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.SelectedGame, resolved.FriendOptions.Scope);
            Assert.IsTrue(resolved.FriendOptions.ForceDefinitionRefresh);
            CollectionAssert.AreEqual(
                new[] { gameId },
                resolved.FriendOptions.PlayniteGameIds.ToList());
        }

        [TestMethod]
        public void Resolve_FriendsCustom_PreferCachedDefinitions_SuppressesForcedDefinitionRefresh()
        {
            var gameId = Guid.NewGuid();
            var planner = CreatePlanner(Array.Empty<Game>());
            var steamProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            // The in-game poller path: selected-game scope normally forces a schema re-download,
            // but PreferCachedDefinitions keeps polling ticks on the cached Ok schema.
            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = new[] { "Steam" },
                        Scope = FriendRefreshScope.SelectedGame,
                        PlayniteGameIds = new[] { gameId },
                        PreferCachedDefinitions = true
                    })
                },
                new IDataProvider[] { steamProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.SelectedGame, resolved.FriendOptions.Scope);
            Assert.IsFalse(resolved.FriendOptions.ForceDefinitionRefresh);
        }

        [TestMethod]
        public void Resolve_FriendsCustom_CarriesProviderScopedFriendAccounts()
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var steamProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));
            var exophaseProvider = new FakeProvider("Exophase", new FakeFriendsProvider("Exophase"));

            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = new[] { "Steam", "Exophase" },
                        Scope = FriendRefreshScope.Full,
                        FriendAccounts = new[]
                        {
                            FriendAccountRef.From(" Steam ", " alice "),
                            FriendAccountRef.From("Steam", "alice"),
                            FriendAccountRef.From("Exophase", "exo-alice")
                        },
                        FriendExternalUserIds = new[] { "alice", "exo-alice" }
                    })
                },
                new IDataProvider[] { steamProvider, exophaseProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            var accounts = resolved.FriendOptions.FriendAccounts.ToList();
            Assert.AreEqual(2, accounts.Count);
            Assert.IsTrue(accounts.Any(account => account.Matches("Steam", "alice")));
            Assert.IsTrue(accounts.Any(account => account.Matches("Exophase", "exo-alice")));
            CollectionAssert.AreEquivalent(
                new[] { "alice", "exo-alice" },
                resolved.FriendOptions.FriendExternalUserIds.ToList());
        }

        [TestMethod]
        public void Resolve_FriendsCustom_Installed_UsesInstalledGameIds()
        {
            var installedId = Guid.NewGuid();
            var staleId = Guid.NewGuid();
            var planner = CreatePlanner(new[]
            {
                new Game { Id = installedId, Name = "Installed", IsInstalled = true },
                new Game { Id = Guid.NewGuid(), Name = "Not Installed", IsInstalled = false }
            });
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        Scope = FriendRefreshScope.Installed,
                        PlayniteGameIds = new[] { staleId }
                    })
                },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.Installed, resolved.FriendOptions.Scope);
            CollectionAssert.AreEquivalent(
                new[] { installedId },
                resolved.FriendOptions.PlayniteGameIds.ToList());
        }

        [TestMethod]
        public void Resolve_UnifiedOptions_ResolvesCurrentAndFriendOptions()
        {
            var gameId = Guid.NewGuid();
            var planner = CreatePlanner(new[]
            {
                new Game { Id = gameId, Name = "Current" }
            });
            var steamProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));
            var epicProvider = new FakeProvider("Epic", new FakeFriendsProvider("Epic"));

            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Custom,
                    Options = new RefreshOptions
                    {
                        Subjects = RefreshSubjects.All,
                        ProviderKeys = new[] { "Steam" },
                        Scope = RefreshGameScope.SelectedGame,
                        PlayniteGameIds = new[] { gameId },
                        ForceIconRefresh = true,
                        ForceDefinitionRefresh = true,
                        RunProvidersInParallelOverride = false
                    }
                },
                new IDataProvider[] { steamProvider, epicProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.AreEqual(RefreshModeType.Custom, resolved.Mode);
            Assert.IsTrue(resolved.Options.ForceIconRefresh);
            Assert.IsNotNull(resolved.CurrentUserOptions);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(false, resolved.RunProvidersInParallelOverride);
            CollectionAssert.AreEqual(
                new[] { gameId },
                resolved.CurrentUserOptions.PlayniteGameIds.ToList());
            CollectionAssert.AreEqual(
                new[] { gameId },
                resolved.FriendOptions.PlayniteGameIds.ToList());
            Assert.IsTrue(resolved.FriendOptions.ForceDefinitionRefresh);
            CollectionAssert.AreEqual(
                new IDataProvider[] { steamProvider },
                resolved.CurrentProviderScope.ToList());
            CollectionAssert.AreEqual(
                new IDataProvider[] { steamProvider },
                resolved.FriendProviderScope.ToList());
            CollectionAssert.AreEqual(
                new IDataProvider[] { steamProvider },
                resolved.ProviderScope.ToList());
        }

        [TestMethod]
        public void Resolve_UnifiedOptions_AllowsCurrentOnlyBranch()
        {
            var gameId = Guid.NewGuid();
            var planner = CreatePlanner(new[]
            {
                new Game { Id = gameId, Name = "Current" }
            });
            var provider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
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
                new IDataProvider[] { provider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.CurrentUserOptions);
            Assert.IsNull(resolved.FriendOptions);
            CollectionAssert.AreEqual(
                new IDataProvider[] { provider },
                resolved.CurrentProviderScope.ToList());
            CollectionAssert.AreEqual(
                new IDataProvider[] { provider },
                resolved.ProviderScope.ToList());
        }

        [TestMethod]
        public void Resolve_UnifiedOptions_AllowsFriendOnlyBranch()
        {
            var planner = CreatePlanner(Array.Empty<Game>());
            var provider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = new[] { "Steam" },
                        Scope = FriendRefreshScope.Full
                    })
                },
                new IDataProvider[] { provider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNull(resolved.CurrentUserOptions);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.Full, resolved.FriendOptions.Scope);
            Assert.IsTrue(resolved.FriendOptions.ForceDefinitionRefresh);
            CollectionAssert.AreEqual(
                new IDataProvider[] { provider },
                resolved.FriendProviderScope.ToList());
            CollectionAssert.AreEqual(
                new IDataProvider[] { provider },
                resolved.ProviderScope.ToList());
        }

        [TestMethod]
        public void Resolve_FriendsFull_WithUnownedGamesExcluded_ClampsScopeToShared()
        {
            var planner = CreatePlanner(Array.Empty<Game>(), includeUnownedFriendGames: false);
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = RefreshModeType.FriendsFull },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.Shared, resolved.FriendOptions.Scope);
            Assert.IsFalse(resolved.FriendOptions.ForceDefinitionRefresh);
            Assert.IsFalse(resolved.FriendOptions.DiscoversProviderOnlyGames());
        }

        [TestMethod]
        public void Resolve_FriendsCustomFullScope_WithUnownedGamesExcluded_ClampsScopeToShared()
        {
            // The per-friend context-menu "Refresh friend" path issues a FriendsCustom request
            // with Full scope; the global toggle must clamp it to Shared for that friend.
            var planner = CreatePlanner(Array.Empty<Game>(), includeUnownedFriendGames: false);
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = new[] { "Steam" },
                        Scope = FriendRefreshScope.Full,
                        FriendAccounts = new[] { FriendAccountRef.From("Steam", "alice") },
                        FriendExternalUserIds = new[] { "alice" }
                    })
                },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.IsNotNull(resolved.FriendOptions);
            Assert.AreEqual(FriendRefreshScope.Shared, resolved.FriendOptions.Scope);
            CollectionAssert.AreEquivalent(
                new[] { "alice" },
                resolved.FriendOptions.FriendExternalUserIds.ToList());
        }

        [DataTestMethod]
        [DataRow(RefreshModeType.FriendsRecent, FriendRefreshScope.Recent)]
        [DataRow(RefreshModeType.FriendsShared, FriendRefreshScope.Shared)]
        [DataRow(RefreshModeType.FriendsInstalled, FriendRefreshScope.Installed)]
        public void Resolve_NonFullFriendModes_WithUnownedGamesExcluded_KeepScope(
            RefreshModeType mode,
            FriendRefreshScope expectedScope)
        {
            var planner = CreatePlanner(
                new[] { new Game { Id = Guid.NewGuid(), Name = "Installed", IsInstalled = true } },
                includeUnownedFriendGames: false);
            var friendProvider = new FakeProvider("Steam", new FakeFriendsProvider("Steam"));

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = mode },
                new IDataProvider[] { friendProvider });

            Assert.IsTrue(resolved.ShouldExecute);
            Assert.AreEqual(expectedScope, resolved.FriendOptions.Scope);
        }

        [TestMethod]
        public void Resolve_FriendMode_WithNoFriendProviders_DoesNotExecute()
        {
            var planner = CreatePlanner(Array.Empty<Game>());

            var resolved = planner.Resolve(
                new RefreshRequest { Mode = RefreshModeType.FriendsRecent },
                new IDataProvider[] { new FakeProvider("Epic", friendsProvider: null) });

            Assert.IsFalse(resolved.ShouldExecute);
            Assert.IsFalse(string.IsNullOrWhiteSpace(resolved.UserMessage));
        }

        private static RefreshRequestPlanner CreatePlanner(
            IEnumerable<Game> games,
            bool includeUnownedFriendGames = true)
        {
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.IncludeUnplayedGames = true;
            settings.Persisted.IncludeUnownedFriendGames = includeUnownedFriendGames;
            var api = new FakePlayniteApi(games ?? Enumerable.Empty<Game>());
            var resolver = new TargetSelectionResolver(
                api,
                settings,
                new FakeCacheManager(),
                logger: null,
                refreshOrder: new[] { "Steam", "Epic" });

            return new RefreshRequestPlanner(api, settings, logger: null, resolver);
        }

        private sealed class FakeProvider : IDataProvider
        {
            public FakeProvider(string providerKey, IFriendsProvider friendsProvider)
            {
                ProviderKey = providerKey;
                Friends = friendsProvider;
            }

            public string ProviderName => ProviderKey;
            public string ProviderKey { get; }
            public string ProviderIconKey => ProviderKey;
            public string ProviderColorHex => "#000000";
            public bool IsAuthenticated => true;
            public ISessionManager AuthSession => null;
            public IFriendsProvider Friends { get; }

            public bool IsCapable(Game game) => true;

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
