using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The friends snapshot coordinator, which the theme friend lists, start page widgets and
    /// friend compare still read.
    /// </summary>
    [TestClass]
    public class FriendsOverviewDataCoordinatorTests
    {
        [TestMethod]
        public void FriendsOverviewDataCoordinator_InvalidatedDuringBuild_ReturnsBoundedStaleSnapshotThenRebuilds()
        {
            var staleData = CreateData();
            staleData.Games[0].GameLogo = null;
            staleData.Games[0].GameCoverPath = null;

            var freshData = CreateData();
            freshData.Games[0].GameLogo = "icon.png";
            freshData.Games[0].GameCoverPath = "cover.png";

            using var firstLoadEntered = new ManualResetEventSlim(false);
            using var releaseFirstLoad = new ManualResetEventSlim(false);
            var cache = new StubFriendCache(staleData)
            {
                FirstLoadEntered = firstLoadEntered,
                ReleaseFirstLoad = releaseFirstLoad,
                DataAfterFirstLoad = freshData
            };

            using var coordinator = new FriendsOverviewDataCoordinator(
                cache,
                () => new PersistedSettings());
            var task = coordinator.GetSnapshotAsync(CancellationToken.None);

            Assert.IsTrue(firstLoadEntered.Wait(TimeSpan.FromSeconds(2)));
            coordinator.Invalidate();
            releaseFirstLoad.Set();

            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(2)));
            var snapshot = task.GetAwaiter().GetResult();

            // A mid-build invalidation no longer triggers a rebuild loop: the awaited call
            // returns the bounded-stale result of the single load it joined.
            Assert.AreEqual(1, cache.LoadFriendsOverviewDataCalls);
            Assert.IsNull(snapshot.Games[0].GameLogo);
            Assert.IsFalse(coordinator.TryGetCurrentSnapshot(out _));

            // Convergence happens on the next request (in production, driven by the throttled
            // SnapshotInvalidated fire).
            var fresh = coordinator.GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, cache.LoadFriendsOverviewDataCalls);
            Assert.AreEqual("icon.png", fresh.Games[0].GameLogo);
            Assert.AreEqual("cover.png", fresh.Games[0].GameCoverPath);
        }

        [TestMethod]
        public void FriendsOverviewDataCoordinator_InvalidationBurst_CoalescesEventsToLeadingAndTrailingFire()
        {
            var cache = new StubFriendCache(CreateData());
            using var coordinator = new FriendsOverviewDataCoordinator(
                cache,
                () => new PersistedSettings(),
                invalidationEventThrottleInterval: TimeSpan.FromMilliseconds(250));
            var fires = 0;
            coordinator.SnapshotInvalidated += (_, __) => Interlocked.Increment(ref fires);

            for (var i = 0; i < 10; i++)
            {
                coordinator.Invalidate();
            }

            // Leading fire only, so far.
            Assert.AreEqual(1, Volatile.Read(ref fires));

            // The burst's remaining invalidations fold into exactly one trailing fire.
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref fires) == 2, TimeSpan.FromSeconds(2)));
            Thread.Sleep(400);
            Assert.AreEqual(2, Volatile.Read(ref fires));
        }

        [TestMethod]
        public void FriendsOverviewDataCoordinator_Warm_InvalidatesAndBuildsSnapshot()
        {
            var cache = new StubFriendCache(CreateData());
            using var coordinator = new FriendsOverviewDataCoordinator(
                cache,
                () => new PersistedSettings(),
                warmDebounceInterval: TimeSpan.Zero);
            var invalidated = false;
            coordinator.SnapshotInvalidated += (_, __) => invalidated = true;

            coordinator.Warm();

            Assert.IsTrue(
                SpinWait.SpinUntil(() => cache.LoadFriendsOverviewDataCalls > 0, TimeSpan.FromSeconds(2)));
            Assert.IsTrue(invalidated);
        }

        [TestMethod]
        public void FriendsOverviewDataCoordinator_Warm_WithCurrentSnapshot_InvalidatesWithoutRebuilding()
        {
            var cache = new StubFriendCache(CreateData());
            using var coordinator = new FriendsOverviewDataCoordinator(
                cache,
                () => new PersistedSettings(),
                warmDebounceInterval: TimeSpan.Zero);
            coordinator.GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, cache.LoadFriendsOverviewDataCalls);
            var invalidated = false;
            coordinator.SnapshotInvalidated += (_, __) => invalidated = true;

            coordinator.Warm();

            Assert.IsTrue(SpinWait.SpinUntil(() => invalidated, TimeSpan.FromSeconds(2)));
            Assert.IsFalse(coordinator.TryGetCurrentSnapshot(out _));
            Thread.Sleep(100);
            Assert.AreEqual(1, cache.LoadFriendsOverviewDataCalls);

            coordinator.GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, cache.LoadFriendsOverviewDataCalls);
        }

        [TestMethod]
        public void FriendsOverviewDataCoordinator_Warm_WithBuildInFlight_DoesNotStartSecondBuild()
        {
            using var firstLoadEntered = new ManualResetEventSlim(false);
            using var releaseFirstLoad = new ManualResetEventSlim(false);
            var cache = new StubFriendCache(CreateData())
            {
                FirstLoadEntered = firstLoadEntered,
                ReleaseFirstLoad = releaseFirstLoad
            };
            using var coordinator = new FriendsOverviewDataCoordinator(
                cache,
                () => new PersistedSettings(),
                warmDebounceInterval: TimeSpan.Zero);
            var invalidated = false;
            coordinator.SnapshotInvalidated += (_, __) => invalidated = true;

            var task = coordinator.GetSnapshotAsync(CancellationToken.None);
            Assert.IsTrue(firstLoadEntered.Wait(TimeSpan.FromSeconds(2)));

            coordinator.Warm();
            Thread.Sleep(100);
            releaseFirstLoad.Set();

            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(1, cache.LoadFriendsOverviewDataCalls);
            Assert.IsFalse(invalidated);
            Assert.IsTrue(coordinator.TryGetCurrentSnapshot(out _));
        }

        [TestMethod]
        public void FriendsOverviewDataCoordinator_Invalidate_ReleasesStaleSnapshot()
        {
            var cache = new StubFriendCache(CreateData());
            using var coordinator = new FriendsOverviewDataCoordinator(
                cache,
                () => new PersistedSettings());
            var weakSnapshot = BuildSnapshotAndGetWeakReference(coordinator);

            coordinator.Invalidate();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.IsFalse(coordinator.TryGetCurrentSnapshot(out _));
            Assert.IsFalse(weakSnapshot.IsAlive, "Invalidated snapshot should be collectible.");
        }

        // Builds the snapshot in a separate frame so the test method holds no strong local
        // reference when the collection assertion runs.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference BuildSnapshotAndGetWeakReference(FriendsOverviewDataCoordinator coordinator)
        {
            var snapshot = coordinator.GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsNotNull(snapshot);
            return new WeakReference(snapshot);
        }

        private static FriendsOverviewData CreateData()
        {
            var gameOneId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var gameTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var gogGameId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            var friends = new List<FriendSummaryItem>
            {
                new FriendSummaryItem
                {
                    ProviderKey = "Steam",
                    ExternalUserId = "alice",
                    DisplayName = "Alice",
                    AvatarPath = "https://cdn.example/alice.png",
                    SharedGamesCount = 2,
                    GamesWithUnlocksCount = 2,
                    UnlockedAchievementsCount = 2,
                    CollectionScore = 375,
                    PrestigeScore = 425,
                    RecentUnlockCount = 1,
                    LastUnlockUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                    LastRefreshedUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                    TotalPlaytimeMinutes = 900
                },
                new FriendSummaryItem
                {
                    ProviderKey = "Steam",
                    ExternalUserId = "bob",
                    DisplayName = "Bob",
                    AvatarPath = "https://cdn.example/bob.png",
                    SharedGamesCount = 1,
                    GamesWithUnlocksCount = 1,
                    UnlockedAchievementsCount = 1,
                    CollectionScore = 90,
                    PrestigeScore = 140,
                    RecentUnlockCount = 0,
                    LastUnlockUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    TotalPlaytimeMinutes = 300
                },
                new FriendSummaryItem
                {
                    ProviderKey = "GOG",
                    ExternalUserId = "cora",
                    DisplayName = "Cora",
                    SharedGamesCount = 1,
                    GamesWithUnlocksCount = 1,
                    UnlockedAchievementsCount = 1,
                    CollectionScore = 30,
                    PrestigeScore = 45,
                    RecentUnlockCount = 0,
                    LastUnlockUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    TotalPlaytimeMinutes = 120
                }
            };

            var games = new List<FriendGameSummaryItem>
            {
                new FriendGameSummaryItem
                {
                    ProviderKey = "Steam",
                    AppId = 10,
                    PlayniteGameId = gameOneId,
                    GameName = "Game One",
                    FriendCount = 2,
                    FriendsWithUnlocksCount = 2,
                    FriendUnlockedAchievementsCount = 2,
                    UniqueFriendUnlockedAchievementsCount = 2,
                    TotalAchievements = 4,
                    LastFriendUnlockUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                    TotalFriendPlaytimeMinutes = 1200,
                    AverageFriendPlaytimeMinutes = 600,
                    LastFriendPlayedUtc = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc),
                    LastFriendScrapeStatus = "OK"
                },
                new FriendGameSummaryItem
                {
                    ProviderKey = "Steam",
                    AppId = 20,
                    PlayniteGameId = gameTwoId,
                    GameName = "Game Two",
                    FriendCount = 1,
                    FriendsWithUnlocksCount = 1,
                    FriendUnlockedAchievementsCount = 1,
                    UniqueFriendUnlockedAchievementsCount = 1,
                    TotalAchievements = 2,
                    LastFriendUnlockUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)
                },
                new FriendGameSummaryItem
                {
                    ProviderKey = "GOG",
                    AppId = 30,
                    PlayniteGameId = gogGameId,
                    GameName = "GOG Game",
                    FriendCount = 1,
                    FriendsWithUnlocksCount = 1,
                    FriendUnlockedAchievementsCount = 1,
                    UniqueFriendUnlockedAchievementsCount = 1,
                    TotalAchievements = 1,
                    LastFriendUnlockUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            };

            var allUnlocked = new List<FriendAchievementDisplayItem>
            {
                // Category types use the canonical vocabulary (real rows are canonicalized at the
                // store boundary, and non-canonical tokens are dropped by ParseValues).
                CreateAchievement("Steam", "alice", "Alice", "https://cdn.example/alice.png", 10, gameOneId, "Game One", "Recent Only", "DLC", "Side", new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)),
                CreateAchievement("Steam", "alice", "Alice", "https://cdn.example/alice.png", 20, gameTwoId, "Game Two", "Alice Game Two", "Base", "Main", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
                CreateAchievement("Steam", "bob", "Bob", "https://cdn.example/bob.png", 10, gameOneId, "Game One", "Bob Game One", "Base", "Main", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateAchievement("GOG", "cora", "Cora", 30, gogGameId, "GOG Game", "Cora GOG", "Base", "Main", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            };

            return new FriendsOverviewData
            {
                Friends = friends,
                Games = games,
                RecentUnlocks = new List<FriendAchievementDisplayItem> { allUnlocked[0] },
                AllUnlockedAchievements = allUnlocked,
                FriendGameLinks = new List<FriendGameLinkItem>
                {
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 10,
                        PlayniteGameId = gameOneId,
                        PlaytimeForeverMinutes = 600,
                        LastPlayedUtc = new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 20,
                        PlayniteGameId = gameTwoId,
                        PlaytimeForeverMinutes = 300,
                        LastPlayedUtc = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "bob",
                        AppId = 10,
                        PlayniteGameId = gameOneId,
                        PlaytimeForeverMinutes = 300,
                        LastPlayedUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "GOG",
                        ExternalUserId = "cora",
                        AppId = 30,
                        PlayniteGameId = gogGameId,
                        PlaytimeForeverMinutes = 120,
                        LastPlayedUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
                    }
                }
            };
        }

        private static FriendAchievementDisplayItem CreateAchievement(
            string provider,
            string externalUserId,
            string friendName,
            string friendAvatarPath,
            int appId,
            Guid playniteGameId,
            string gameName,
            string achievementName,
            string categoryType,
            string categoryLabel,
            DateTime unlockTimeUtc)
        {
            return new FriendAchievementDisplayItem
            {
                ProviderKey = provider,
                FriendExternalUserId = externalUserId,
                FriendName = friendName,
                FriendAvatarPath = friendAvatarPath,
                AppId = appId,
                PlayniteGameId = playniteGameId,
                GameName = gameName,
                SortingName = gameName,
                DisplayName = achievementName,
                Description = achievementName + " description",
                CategoryKind = PlayniteAchievements.Services.Achievements.LegacyCategoryTypes.SplitKind(categoryType),
                Tags = PlayniteAchievements.Services.Achievements.AchievementTagHelper.Normalize(categoryType),
                CategoryLabel = categoryLabel,
                UnlockTimeUtc = unlockTimeUtc,
                Unlocked = true
            };
        }

        private static FriendAchievementDisplayItem CreateAchievement(
            string provider,
            string externalUserId,
            string friendName,
            int appId,
            Guid playniteGameId,
            string gameName,
            string achievementName,
            string categoryType,
            string categoryLabel,
            DateTime unlockTimeUtc) =>
            CreateAchievement(
                provider,
                externalUserId,
                friendName,
                null,
                appId,
                playniteGameId,
                gameName,
                achievementName,
                categoryType,
                categoryLabel,
                unlockTimeUtc);

        private sealed class StubFriendCache : IFriendCacheManager
        {
            public StubFriendCache(FriendsOverviewData data)
            {
                Data = data;
            }

            public event EventHandler<FriendCacheInvalidatedEventArgs> FriendCacheInvalidated;

            public FriendsOverviewData Data { get; set; }

            public FriendsOverviewData DataAfterFirstLoad { get; set; }

            public ManualResetEventSlim FirstLoadEntered { get; set; }

            public ManualResetEventSlim ReleaseFirstLoad { get; set; }

            public int LoadFriendsOverviewDataCalls { get; private set; }

            public void RaiseFriendCacheInvalidated()
            {
                FriendCacheInvalidated?.Invoke(this, FriendCacheInvalidatedEventArgs.FullInvalidation);
            }

            public IFriendCacheInvalidationBatch BeginFriendCacheInvalidationBatch() =>
                NullFriendCacheInvalidationBatch.Instance;

            public FriendCacheWriteResult SaveFriendList(string providerKey, IReadOnlyList<FriendIdentity> friends) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveFriendOwnership(
                string providerKey,
                string externalUserId,
                IReadOnlyList<FriendGameOwnership> ownership,
                FriendOwnershipSaveOptions options = null) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveFriendGameDefinition(
                string providerKey,
                FriendGameDefinition definition) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveProviderGameImagePaths(
                string providerKey,
                string providerGameKey,
                int appId,
                string iconAbsolutePath,
                string coverAbsolutePath) =>
                FriendCacheWriteResult.Ok();

            public Dictionary<string, FriendGameDefinitionState> LoadFriendGameDefinitionStates(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);

            public List<string> LoadLegacyKeyedDefinitionGameKeys(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                new List<string>();

            public FriendUnownedCacheStats GetUnownedFriendGameCacheStats() =>
                new FriendUnownedCacheStats();

            public FriendUnownedCacheClearResult ClearUnownedFriendGameData() =>
                new FriendUnownedCacheClearResult { Success = true };

            public FriendCacheWriteResult ClearUnownedFriendGame(string providerKey, int appId, string providerGameKey) =>
                FriendCacheWriteResult.Ok();

            public bool IsProviderGameMappedToPlayniteLibrary(string providerKey, int appId, string providerGameKey) =>
                true;

            public System.Collections.Generic.IReadOnlyList<FriendGameMapping> LoadFriendGameMappings(string providerKey) =>
                new System.Collections.Generic.List<FriendGameMapping>();

            public FriendCacheWriteResult PromoteProviderOnlyGameToPlayniteBacked(
                string providerKey,
                int appId,
                string providerGameKey,
                Guid playniteGameId) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveFriendGameAchievements(
                string providerKey,
                string externalUserId,
                string providerGameKey,
                int appId,
                FriendGameAchievements achievements) =>
                FriendCacheWriteResult.Ok();

            public List<FriendAchievementRow> LoadFriendGameAchievements(
                string providerKey,
                string externalUserId,
                int appId,
                string providerGameKey) =>
                new List<FriendAchievementRow>();

            public FriendCacheWriteResult DeleteFriendData(string providerKey, string externalUserId, bool preserveFriendRecord = false) =>
                FriendCacheWriteResult.Ok();

            public List<FriendIdentity> LoadFriendIdentities(string providerKey) =>
                new List<FriendIdentity>();

            public List<FriendIdentity> LoadCurrentUserIdentities() => new List<FriendIdentity>();

            public bool SaveCurrentUserProfile(string providerKey, FriendIdentity self) => true;

            public DateTime? GetMostRecentFriendLastRefreshedUtc() => null;

            public List<FriendRefreshCandidate> LoadFriendRefreshCandidates(
                string providerKey,
                FriendRefreshOptions options) =>
                new List<FriendRefreshCandidate>();

            public IReadOnlyDictionary<string, FriendOwnershipRecency> LoadFriendOwnershipRecency(
                string providerKey,
                string externalUserId) =>
                new Dictionary<string, FriendOwnershipRecency>();

            public FriendsOverviewData LoadFriendsOverviewData(int recentLimit)
            {
                var data = Data;
                LoadFriendsOverviewDataCalls++;
                if (LoadFriendsOverviewDataCalls == 1 && FirstLoadEntered != null)
                {
                    FirstLoadEntered.Set();
                    ReleaseFirstLoad?.Wait(TimeSpan.FromSeconds(5));
                }

                return LoadFriendsOverviewDataCalls > 1 && DataAfterFirstLoad != null
                    ? DataAfterFirstLoad
                    : data;
            }

            public FriendsOverviewData LoadFriendsOverviewPatchData(IReadOnlyList<FriendCacheChange> reloadScopes) =>
                Data;

            public FriendsOverviewData LoadFriendGameAchievementData(Guid playniteGameId) =>
                Data;

            public IReadOnlyCollection<Guid> LoadFriendDataPlayniteGameIds() =>
                Array.Empty<Guid>();

            public int PairAchievementLoadCalls { get; private set; }

            // When set, blocks the on-demand pair load until released so tests can observe
            // the in-flight (summary-estimated) state deterministically.
            public ManualResetEventSlim PairLoadGate { get; set; }

            // On-demand pair rows (locked included): serves the game-scoped subset of
            // PairAchievements when set, else of Data.AllAchievements.
            public FriendsOverviewData LoadFriendGameAchievementData(FriendCacheChange gameScope)
            {
                PairLoadGate?.Wait(TimeSpan.FromSeconds(5));
                PairAchievementLoadCalls++;
                var source = PairAchievements ?? Data?.AllAchievements ?? new List<FriendAchievementDisplayItem>();
                var rows = source
                    .Where(item => item != null &&
                        string.Equals(item.ProviderKey, gameScope?.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
                        ((gameScope?.AppId > 0 && item.AppId == gameScope.AppId) ||
                         (!string.IsNullOrWhiteSpace(gameScope?.ProviderGameKey) &&
                          string.Equals(item.ProviderGameKey, gameScope.ProviderGameKey, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                return new FriendsOverviewData { AllAchievements = rows };
            }

            public List<FriendAchievementDisplayItem> PairAchievements { get; set; }

            public IReadOnlyList<CurrentUserGameLabel> LoadCurrentUserGameLabels() =>
                new List<CurrentUserGameLabel>();
        }
    }
}
