using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Friends
{
    public enum FriendRefreshScope
    {
        Recent,
        Full,
        Shared,
        Installed,
        SelectedGame,
        Custom
    }

    public static class FriendRefreshPolicy
    {
        /// <summary>
        /// True when the refresh scope discovers games the friend owns that are not present in the
        /// current user's Playnite library (provider-only games). Only the Full scope does this.
        /// </summary>
        public static bool DiscoversProviderOnlyGames(FriendRefreshScope scope)
        {
            return scope == FriendRefreshScope.Full;
        }

        /// <summary>
        /// True when the refresh options discover provider-only games: either the scope does so
        /// (Full), or the request explicitly targets provider game ids/keys (a selected-game refresh
        /// of a friend-owned game that is not in the current user's library).
        /// </summary>
        public static bool DiscoversProviderOnlyGames(this FriendRefreshOptions options)
        {
            return options != null &&
                   (DiscoversProviderOnlyGames(options.Scope) ||
                    options.ProviderAppIds?.Any(id => id > 0) == true ||
                    options.ProviderGameKeys?.Any(key => !string.IsNullOrWhiteSpace(key)) == true);
        }
    }

    internal static class FriendRefreshOptionNormalizer
    {
        public static List<FriendAccountRef> NormalizeFriendAccounts(IEnumerable<FriendAccountRef> accounts)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<FriendAccountRef>();
            foreach (var account in accounts ?? Enumerable.Empty<FriendAccountRef>())
            {
                var next = account?.Clone()?.Normalize();
                if (string.IsNullOrWhiteSpace(next?.Key) || !seen.Add(next.Key))
                {
                    continue;
                }

                normalized.Add(next);
            }

            return normalized.Count == 0 ? null : normalized;
        }
    }

    public sealed class FriendRefreshOptions
    {
        public FriendRefreshScope Scope { get; set; } = FriendRefreshScope.Recent;
        public IReadOnlyCollection<Guid> PlayniteGameIds { get; set; }
        public IReadOnlyCollection<int> ProviderAppIds { get; set; }
        public IReadOnlyCollection<string> ProviderGameKeys { get; set; }
        public IReadOnlyCollection<FriendAccountRef> FriendAccounts { get; set; }
        public IReadOnlyCollection<string> FriendExternalUserIds { get; set; }
        public bool ForceDefinitionRefresh { get; set; }

        public FriendRefreshOptions Clone()
        {
            return new FriendRefreshOptions
            {
                Scope = Scope,
                PlayniteGameIds = PlayniteGameIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList(),
                ProviderAppIds = ProviderAppIds?
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList(),
                ProviderGameKeys = ProviderGameKeys?
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                FriendAccounts = FriendRefreshOptionNormalizer.NormalizeFriendAccounts(FriendAccounts),
                FriendExternalUserIds = FriendExternalUserIds?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ForceDefinitionRefresh = ForceDefinitionRefresh
            };
        }
    }

    public sealed class FriendCustomRefreshOptions
    {
        public IReadOnlyCollection<string> ProviderKeys { get; set; }
        public FriendRefreshScope Scope { get; set; } = FriendRefreshScope.Recent;
        public IReadOnlyCollection<Guid> PlayniteGameIds { get; set; }
        public IReadOnlyCollection<int> ProviderAppIds { get; set; }
        public IReadOnlyCollection<string> ProviderGameKeys { get; set; }
        public IReadOnlyCollection<FriendAccountRef> FriendAccounts { get; set; }
        public IReadOnlyCollection<string> FriendExternalUserIds { get; set; }
        public bool ForceDefinitionRefresh { get; set; }

        // Reuse cached Ok schemas even for scopes that normally force a definition re-fetch
        // (SelectedGame/Full). Set by latency-sensitive programmatic callers such as the
        // in-game poller; user-initiated refreshes leave this false.
        public bool PreferCachedDefinitions { get; set; }

        public FriendCustomRefreshOptions Clone()
        {
            return new FriendCustomRefreshOptions
            {
                ProviderKeys = ProviderKeys?
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Scope = Scope,
                PlayniteGameIds = PlayniteGameIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList(),
                ProviderAppIds = ProviderAppIds?
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList(),
                ProviderGameKeys = ProviderGameKeys?
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                FriendAccounts = FriendRefreshOptionNormalizer.NormalizeFriendAccounts(FriendAccounts),
                FriendExternalUserIds = FriendExternalUserIds?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ForceDefinitionRefresh = ForceDefinitionRefresh,
                PreferCachedDefinitions = PreferCachedDefinitions
            };
        }

    }

    public sealed class FriendsProviderResult<T>
    {
        public bool Success { get; set; }
        public bool AuthRequired { get; set; }
        public bool TransientFailure { get; set; }
        public string ErrorMessage { get; set; }
        public T Data { get; set; }

        public static FriendsProviderResult<T> FromData(T data) =>
            new FriendsProviderResult<T> { Success = true, Data = data };

        public static FriendsProviderResult<T> Failed(string message, bool authRequired = false, bool transientFailure = false) =>
            new FriendsProviderResult<T>
            {
                Success = false,
                AuthRequired = authRequired,
                TransientFailure = transientFailure,
                ErrorMessage = message
            };
    }

    public sealed class FriendIdentity
    {
        public string ProviderKey { get; set; }
        public string ExternalUserId { get; set; }
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
        public string AvatarPath { get; set; }
        public DateTime? LastRefreshedUtc { get; set; }
    }

    public sealed class FriendGameOwnership
    {
        public string ProviderKey { get; set; }
        public string ExternalUserId { get; set; }
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
        public string ProviderPlatformKey { get; set; }
        public Guid? PlayniteGameId { get; set; }
        public string GameName { get; set; }
        public string IconUrl { get; set; }
        public string CoverUrl { get; set; }
        public int PlaytimeForeverMinutes { get; set; }
        public int? Playtime2WeeksMinutes { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
        public int? AchievementUnlocksHint { get; set; }
        public int? AchievementTotalHint { get; set; }
    }

    public sealed class FriendAchievementRow
    {
        /// <summary>
        /// Stable, language-independent achievement identifier (e.g. the Steam api name) when the
        /// provider can resolve one. Preferred over display text when matching to canonical
        /// achievement definitions. Null for providers that expose no stable key.
        /// </summary>
        public string ApiName { get; set; }

        /// <summary>
        /// The servicing platform's native achievement key (e.g. the Steam apiname, or a PSN
        /// trophy id) when the source can supply one. Bridges unlock rows sourced from an
        /// aggregator onto definitions keyed by the platform provider's own scheme for mapped
        /// games. Null when the source has no native key.
        /// </summary>
        public string ProviderNativeKey { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string IconUrl { get; set; }
        public string UnlockedIconUrl { get; set; }
        public string LockedIconUrl { get; set; }
        public int? Points { get; set; }
        public int? ScaledPoints { get; set; }
        public string Category { get; set; }
        public string CategoryType { get; set; }
        public string TrophyType { get; set; }
        public bool Hidden { get; set; }
        public bool IsCapstone { get; set; }
        public double? GlobalPercentUnlocked { get; set; }
        public RarityTier? Rarity { get; set; }
        public bool Unlocked { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public int? ProgressNum { get; set; }
        public int? ProgressDenom { get; set; }
    }

    public sealed class FriendGameAchievements
    {
        public FriendIdentity Friend { get; set; }
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
        public bool StatsUnavailable { get; set; }
        public bool TransientFailure { get; set; }
        public SteamScrapeDetail DetailCode { get; set; }

        // Game header banner scraped from the same achievement page, used as the provider-only friend
        // game's icon and cover when definitions are seeded from this scrape (no separate definition fetch).
        public string IconUrl { get; set; }
        public List<FriendAchievementRow> Rows { get; set; } = new List<FriendAchievementRow>();
    }

    public enum FriendGameDefinitionStatus
    {
        Ok,
        NoAchievements,
        Unavailable,
        Transient
    }

    public sealed class FriendGameDefinition
    {
        public string ProviderKey { get; set; }
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
        public string ProviderPlatformKey { get; set; }
        public string GameName { get; set; }
        public string IconUrl { get; set; }
        public FriendGameDefinitionStatus Status { get; set; } = FriendGameDefinitionStatus.Unavailable;
        public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
        public List<AchievementDetail> Achievements { get; set; } = new List<AchievementDetail>();
    }

    public sealed class FriendsRefreshPreparation
    {
        public bool CanRefreshAchievements { get; set; } = true;
    }

    public interface IFriendsProvider
    {
        string ProviderKey { get; }

        Task<FriendsProviderResult<FriendsRefreshPreparation>> BeginRefreshAsync(CancellationToken cancel);

        void EndRefresh();

        Task<FriendsProviderResult<IReadOnlyList<FriendIdentity>>> GetFriendsAsync(CancellationToken cancel);

        Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetOwnedGamesAsync(
            FriendIdentity friend,
            CancellationToken cancel);

        Task<FriendsProviderResult<FriendGameAchievements>> GetFriendGameAchievementsAsync(
            FriendIdentity friend,
            string providerGameKey,
            int appId,
            string gameName,
            CancellationToken cancel);

        Task<FriendsProviderResult<FriendGameDefinition>> GetFriendGameDefinitionAsync(
            string providerGameKey,
            int appId,
            string gameName,
            CancellationToken cancel);
    }

    /// <summary>
    /// A current-user game as recorded in the plugin cache: the Playnite game id plus the servicing
    /// provider label the plugin stored at scan time. Supplied to friend providers that map a friend's
    /// games to the local library by name, so they match on the stored platform label rather than
    /// re-deriving platform from Playnite Source/Platform strings.
    /// </summary>
    public sealed class CurrentUserGameLabel
    {
        public Guid PlayniteGameId { get; set; }
        public string GameName { get; set; }
        public string ProviderKey { get; set; }
        public string ProviderPlatformKey { get; set; }
        public int AppId { get; set; }
        public string ProviderGameKey { get; set; }
    }

    /// <summary>
    /// Optional capability for a friend provider that resolves friend games against the current user's
    /// local library. The refresh runtime supplies the current user's cached game labels once per
    /// refresh; the provider owns how it indexes and matches them.
    /// </summary>
    public interface ICurrentUserGameLabelReceiver
    {
        void SetCurrentUserGameLabels(IReadOnlyList<CurrentUserGameLabel> labels);
    }

    /// <summary>
    /// Optional capability for a provider that can supplement Steam friend ownership with Steam-family
    /// ownership data from another source. The refresh runtime still saves the translated rows under
    /// Steam so definitions and achievement scrapes remain Steam-backed.
    /// </summary>
    public interface ISteamFriendOwnershipSupplementSource
    {
        Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetSteamOwnedGamesAsync(
            string externalUserId,
            IReadOnlyList<CurrentUserGameLabel> currentUserLabels,
            IReadOnlyList<FriendGameOwnership> knownSteamOwnership,
            CancellationToken cancel);
    }
}
