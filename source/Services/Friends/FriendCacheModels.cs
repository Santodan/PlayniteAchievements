using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Friends
{
    internal sealed class FriendCacheWriteResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int IncomingCount { get; set; }
        public int WrittenCount { get; set; }
        public int SkippedCount { get; set; }

        // ApiName renames (old -> new) applied to this game's achievement definitions during the
        // save, plus the mapped Playnite game id they apply to. Consumers rewrite ApiName-keyed
        // per-game custom data with this map; null/empty when nothing was renamed or the game is
        // provider-only (no Playnite id, hence no custom data).
        public Dictionary<string, string> RenamedApiNames { get; set; }
        public Guid? RenamedPlayniteGameId { get; set; }

        public static FriendCacheWriteResult Ok(int incomingCount = 0, int writtenCount = 0, int skippedCount = 0) =>
            new FriendCacheWriteResult
            {
                Success = true,
                IncomingCount = Math.Max(0, incomingCount),
                WrittenCount = Math.Max(0, writtenCount),
                SkippedCount = Math.Max(0, skippedCount)
            };

        public static FriendCacheWriteResult Failed(string message) =>
            new FriendCacheWriteResult { Success = false, ErrorMessage = message };
    }

    internal sealed class FriendRefreshCandidate
    {
        public FriendIdentity Friend { get; set; }
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
        public Guid? PlayniteGameId { get; set; }
        public string GameName { get; set; }
        public int PlaytimeForeverMinutes { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
        public DateTime? LastOwnershipRefreshUtc { get; set; }
        public DateTime? LastScrapedUtc { get; set; }
        public string LastScrapeStatus { get; set; }
    }

    internal interface IFriendCacheInvalidationBatch : IDisposable
    {
        void Flush();
    }

    internal sealed class NullFriendCacheInvalidationBatch : IFriendCacheInvalidationBatch
    {
        public static NullFriendCacheInvalidationBatch Instance { get; } = new NullFriendCacheInvalidationBatch();

        private NullFriendCacheInvalidationBatch()
        {
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// What a friend-cache write touched, so invalidation consumers can patch instead of
    /// rebuilding. Kinds without a friend scope (game definitions, roster) affect all friends.
    /// </summary>
    public enum FriendCacheChangeKind
    {
        /// <summary>One friend's achievement rows for one game changed (the scan hot path).</summary>
        FriendGameAchievements,

        /// <summary>One friend's ownership rows changed (many games; links and summaries affected).</summary>
        FriendOwnership,

        /// <summary>A game's definition or images changed for every friend that owns it.</summary>
        GameDefinition,

        /// <summary>One friend's cached data was deleted.</summary>
        FriendRemoved,

        /// <summary>The provider's friend roster changed (friends added or removed).</summary>
        Roster
    }

    public sealed class FriendCacheChange : IEquatable<FriendCacheChange>
    {
        private FriendCacheChange(
            FriendCacheChangeKind kind,
            string providerKey,
            string externalUserId,
            int appId,
            string providerGameKey)
        {
            Kind = kind;
            ProviderKey = providerKey ?? string.Empty;
            ExternalUserId = externalUserId;
            AppId = appId;
            ProviderGameKey = providerGameKey;
        }

        public FriendCacheChangeKind Kind { get; }

        public string ProviderKey { get; }

        /// <summary>Null for kinds without a friend scope (GameDefinition, Roster).</summary>
        public string ExternalUserId { get; }

        /// <summary>Zero when the change is not game-scoped or the app id is unknown.</summary>
        public int AppId { get; }

        /// <summary>Null when the change is not game-scoped or the key is unknown.</summary>
        public string ProviderGameKey { get; }

        public static FriendCacheChange ForFriendGameAchievements(
            string providerKey, string externalUserId, int appId, string providerGameKey) =>
            new FriendCacheChange(FriendCacheChangeKind.FriendGameAchievements, providerKey, externalUserId, appId, providerGameKey);

        public static FriendCacheChange ForFriendOwnership(string providerKey, string externalUserId) =>
            new FriendCacheChange(FriendCacheChangeKind.FriendOwnership, providerKey, externalUserId, 0, null);

        public static FriendCacheChange ForGameDefinition(string providerKey, int appId, string providerGameKey) =>
            new FriendCacheChange(FriendCacheChangeKind.GameDefinition, providerKey, null, appId, providerGameKey);

        public static FriendCacheChange ForFriendRemoved(string providerKey, string externalUserId) =>
            new FriendCacheChange(FriendCacheChangeKind.FriendRemoved, providerKey, externalUserId, 0, null);

        public static FriendCacheChange ForRoster(string providerKey) =>
            new FriendCacheChange(FriendCacheChangeKind.Roster, providerKey, null, 0, null);

        public bool Equals(FriendCacheChange other)
        {
            if (other is null)
            {
                return false;
            }

            return Kind == other.Kind &&
                   AppId == other.AppId &&
                   string.Equals(ProviderKey, other.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ExternalUserId, other.ExternalUserId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ProviderGameKey, other.ProviderGameKey, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj) => Equals(obj as FriendCacheChange);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Kind;
                hash = (hash * 397) ^ AppId;
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(ProviderKey ?? string.Empty);
                hash = (hash * 397) ^ (ExternalUserId == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(ExternalUserId));
                hash = (hash * 397) ^ (ProviderGameKey == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(ProviderGameKey));
                return hash;
            }
        }
    }

    /// <summary>
    /// Scope carried by <see cref="IFriendCacheManager.FriendCacheInvalidated"/>. IsFull means
    /// "everything may have changed" (unscoped writes, clears, or an overflowing change set) and
    /// Changes is empty; consumers must fall back to a full rebuild.
    /// </summary>
    public sealed class FriendCacheInvalidatedEventArgs : EventArgs
    {
        public static readonly FriendCacheInvalidatedEventArgs FullInvalidation =
            new FriendCacheInvalidatedEventArgs(true, Array.Empty<FriendCacheChange>());

        private FriendCacheInvalidatedEventArgs(bool isFull, IReadOnlyCollection<FriendCacheChange> changes)
        {
            IsFull = isFull;
            Changes = changes ?? Array.Empty<FriendCacheChange>();
        }

        public bool IsFull { get; }

        public IReadOnlyCollection<FriendCacheChange> Changes { get; }

        public static FriendCacheInvalidatedEventArgs Scoped(IReadOnlyCollection<FriendCacheChange> changes)
        {
            return changes == null || changes.Count == 0
                ? FullInvalidation
                : new FriendCacheInvalidatedEventArgs(false, changes);
        }
    }

    /// <summary>
    /// Accumulates the scope of deferred friend-cache invalidations inside a batch window.
    /// Collapses to a full invalidation when an unscoped change arrives or the set overflows
    /// <paramref name="maxChanges"/>. Not thread-safe; the owner synchronizes access.
    /// </summary>
    internal sealed class FriendCacheInvalidationScopeAccumulator
    {
        public const int DefaultMaxChanges = 128;

        private readonly HashSet<FriendCacheChange> _changes = new HashSet<FriendCacheChange>();
        private readonly int _maxChanges;
        private bool _full;

        public FriendCacheInvalidationScopeAccumulator(int maxChanges = DefaultMaxChanges)
        {
            _maxChanges = Math.Max(1, maxChanges);
        }

        /// <summary>True when the pending window has collapsed to a full invalidation.</summary>
        public bool PendingIsFull => _full;

        /// <summary>True when nothing has been recorded since the last drain.</summary>
        public bool IsEmpty => !_full && _changes.Count == 0;

        /// <summary>Records a change; null means "unscoped" and collapses the window to full.</summary>
        public void Add(FriendCacheChange change)
        {
            if (change == null)
            {
                _full = true;
                _changes.Clear();
                return;
            }

            if (_full)
            {
                return;
            }

            _changes.Add(change);
            if (_changes.Count > _maxChanges)
            {
                _full = true;
                _changes.Clear();
            }
        }

        /// <summary>
        /// Takes the accumulated scope out, resetting the accumulator: scope travels with the
        /// emitted event, so a later flush in the same batch starts empty.
        /// </summary>
        public FriendCacheInvalidatedEventArgs Drain()
        {
            var args = _full || _changes.Count == 0
                ? FriendCacheInvalidatedEventArgs.FullInvalidation
                : FriendCacheInvalidatedEventArgs.Scoped(_changes.ToList());
            _changes.Clear();
            _full = false;
            return args;
        }
    }

    internal sealed class FriendOwnershipSaveOptions
    {
        public bool IncludeProviderOnlyGames { get; set; }
        public bool PruneStaleShared { get; set; }
    }

    /// <summary>
    /// A provider game in the current user's Playnite library: its provider identity plus the mapped
    /// Playnite game id. Used by the game-centric friend candidate builder to classify a friend's
    /// freshly-scraped ownership as mapped (library) vs provider-only and to intersect with the
    /// installed-game set, in a single cache read rather than per-game lookups.
    /// </summary>
    internal sealed class FriendGameMapping
    {
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
        public Guid PlayniteGameId { get; set; }
    }

    /// <summary>
    /// Pre-overwrite snapshot of a friend's cached ownership row, used to decide whether the
    /// freshly-fetched owned-games data represents new activity since the last successful scrape.
    /// </summary>
    internal sealed class FriendOwnershipRecency
    {
        public int PlaytimeForeverMinutes { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
        public DateTime? LastScrapedUtc { get; set; }
        public string LastScrapeStatus { get; set; }
    }

    internal sealed class FriendGameDefinitionState
    {
        public string ProviderKey { get; set; }
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
        public string GameName { get; set; }
        public string IconUrl { get; set; }
        public FriendGameDefinitionStatus Status { get; set; } = FriendGameDefinitionStatus.Unavailable;
        public DateTime? LastCheckedUtc { get; set; }
    }

    internal class FriendUnownedCacheStats
    {
        public int Games { get; set; }
        public int Definitions { get; set; }
        public int OwnershipRows { get; set; }
        public int ProgressRows { get; set; }
        public int AchievementRows { get; set; }
        public int DefinitionStates { get; set; }
    }

    internal sealed class FriendUnownedCacheClearResult : FriendUnownedCacheStats
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public static FriendUnownedCacheClearResult Failed(string message) =>
            new FriendUnownedCacheClearResult { Success = false, ErrorMessage = message };
    }

    internal sealed class FriendsOverviewData
    {
        public List<FriendSummaryItem> Friends { get; set; } = new List<FriendSummaryItem>();
        public List<FriendGameSummaryItem> Games { get; set; } = new List<FriendGameSummaryItem>();
        public List<FriendAchievementDisplayItem> RecentUnlocks { get; set; } = new List<FriendAchievementDisplayItem>();
        public List<FriendAchievementDisplayItem> AllAchievements { get; set; } = new List<FriendAchievementDisplayItem>();
        public List<FriendAchievementDisplayItem> AllUnlockedAchievements { get; set; } = new List<FriendAchievementDisplayItem>();
        public List<FriendGameLinkItem> FriendGameLinks { get; set; } = new List<FriendGameLinkItem>();
    }

    internal sealed class FriendGameLinkItem
    {
        public string ProviderKey { get; set; }
        public string ExternalUserId { get; set; }
        public string FriendGroupId { get; set; }
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
        public Guid? PlayniteGameId { get; set; }
        public long PlaytimeForeverMinutes { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
    }

    internal interface IFriendCacheManager
    {
        event EventHandler<FriendCacheInvalidatedEventArgs> FriendCacheInvalidated;

        IFriendCacheInvalidationBatch BeginFriendCacheInvalidationBatch();

        FriendCacheWriteResult SaveFriendList(string providerKey, IReadOnlyList<FriendIdentity> friends);

        FriendCacheWriteResult SaveFriendOwnership(
            string providerKey,
            string externalUserId,
            IReadOnlyList<FriendGameOwnership> ownership,
            FriendOwnershipSaveOptions options = null);

        FriendCacheWriteResult SaveFriendGameDefinition(
            string providerKey,
            FriendGameDefinition definition);

        FriendCacheWriteResult SaveProviderGameImagePaths(
            string providerKey,
            string providerGameKey,
            int appId,
            string iconAbsolutePath,
            string coverAbsolutePath);

        Dictionary<string, FriendGameDefinitionState> LoadFriendGameDefinitionStates(
            string providerKey,
            IReadOnlyCollection<string> providerGameKeys);

        // Provider game cache keys (from the given set) whose cached definitions still carry
        // legacy display-derived Exophase keys and therefore need a definition re-fetch to
        // migrate them to stable ids.
        List<string> LoadLegacyKeyedDefinitionGameKeys(
            string providerKey,
            IReadOnlyCollection<string> providerGameKeys);

        FriendUnownedCacheStats GetUnownedFriendGameCacheStats();

        FriendUnownedCacheClearResult ClearUnownedFriendGameData();

        FriendCacheWriteResult ClearUnownedFriendGame(string providerKey, int appId, string providerGameKey);

        bool IsProviderGameMappedToPlayniteLibrary(string providerKey, int appId, string providerGameKey);

        // All provider games mapped to the current user's Playnite library for this provider (one row
        // per mapped game). The game-centric candidate builder loads this once per provider to classify
        // freshly-scraped friend ownership as mapped vs provider-only and to resolve the Playnite game id
        // for the Installed-scope intersection, avoiding a per-game cache query.
        IReadOnlyList<FriendGameMapping> LoadFriendGameMappings(string providerKey);

        FriendCacheWriteResult PromoteProviderOnlyGameToPlayniteBacked(
            string providerKey,
            int appId,
            string providerGameKey,
            Guid playniteGameId);

        FriendCacheWriteResult SaveFriendGameAchievements(
            string providerKey,
            string externalUserId,
            string providerGameKey,
            int appId,
            FriendGameAchievements achievements);

        List<FriendAchievementRow> LoadFriendGameAchievements(
            string providerKey,
            string externalUserId,
            int appId,
            string providerGameKey);

        // When preserveFriendRecord is true, cached achievement/game/ownership data is cleared but the
        // friend's Users record is kept so the friend stays registered (used by "Clear Friend").
        FriendCacheWriteResult DeleteFriendData(string providerKey, string externalUserId, bool preserveFriendRecord = false);

        List<FriendIdentity> LoadFriendIdentities(string providerKey);

        // Most recent LastRefreshedUtc across all active friends, used by the background updater
        // to decide whether a periodic Recent friends refresh is due (the friend analogue of
        // ICacheManager.GetMostRecentLastUpdatedUtc).
        DateTime? GetMostRecentFriendLastRefreshedUtc();

        List<FriendRefreshCandidate> LoadFriendRefreshCandidates(
            string providerKey,
            FriendRefreshOptions options);

        // Pre-save snapshot of a friend's cached ownership rows (playtime / last-played / last-scrape),
        // keyed by provider-game cache key, used by the Recent-scope recency gate.
        IReadOnlyDictionary<string, FriendOwnershipRecency> LoadFriendOwnershipRecency(
            string providerKey,
            string externalUserId);

        FriendsOverviewData LoadFriendsOverviewData(int recentLimit);

        /// <summary>
        /// Loads the friends/games/links lists in full plus achievement rows for only the given
        /// scopes, for incremental snapshot patching. Derived lists stay empty; the caller
        /// splices and re-derives. See SqlNadoCacheStore.LoadFriendsOverviewPatchData.
        /// </summary>
        FriendsOverviewData LoadFriendsOverviewPatchData(IReadOnlyList<FriendCacheChange> reloadScopes);

        FriendsOverviewData LoadFriendGameAchievementData(Guid playniteGameId);

        /// <summary>
        /// Loads every friend's achievement rows (locked included) for one game identified by
        /// provider scope, supporting provider-only games without a PlayniteGameId. Used by the
        /// friend+game pair comparison to materialize locked rows on demand; the overview
        /// snapshot itself carries unlocked rows only.
        /// </summary>
        FriendsOverviewData LoadFriendGameAchievementData(FriendCacheChange gameScope);

        FriendsOverviewData LoadFriendRecentUnlocksData(int recentLimit);

        // Current-user games (with the servicing provider label stored at scan time) used to resolve a
        // friend's games against the local library without re-deriving platform from Source/Platform.
        IReadOnlyList<CurrentUserGameLabel> LoadCurrentUserGameLabels();
    }
}
