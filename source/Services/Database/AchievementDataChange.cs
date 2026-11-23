using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Friends;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// What one write batch changed in the achievement data, as <see cref="AchievementDataFeed"/>
    /// raises it: everything, or the current user's changed games and the friend changes.
    /// Immutable.
    /// </summary>
    internal sealed class AchievementDataChange : EventArgs
    {
        public static readonly AchievementDataChange Full =
            new AchievementDataChange(true, Array.Empty<Guid>(), false, Array.Empty<FriendCacheChange>());

        private AchievementDataChange(
            bool isFull,
            IReadOnlyCollection<Guid> gameIds,
            bool allFriends,
            IReadOnlyCollection<FriendCacheChange> friendChanges)
        {
            IsFull = isFull;
            GameIds = gameIds;
            AllFriends = allFriends;
            FriendChanges = friendChanges;
        }

        /// <summary>Every row of every user may have changed.</summary>
        public bool IsFull { get; }

        /// <summary>
        /// Playnite games whose current-user data or custom layer changed. Friend rows of these
        /// games change too, since they carry the current user's state and the custom layer.
        /// </summary>
        public IReadOnlyCollection<Guid> GameIds { get; }

        /// <summary>Every friend row may have changed; current-user rows did not.</summary>
        public bool AllFriends { get; }

        /// <summary>The friend writes of the batch, when <see cref="AllFriends"/> is false.</summary>
        public IReadOnlyCollection<FriendCacheChange> FriendChanges { get; }

        /// <summary>An empty or oversized set is full, as <see cref="CacheInvalidatedEventArgs.Scoped"/> makes it.</summary>
        public static AchievementDataChange ForGames(IEnumerable<Guid> gameIds) =>
            Of(CacheInvalidatedEventArgs.Scoped(gameIds?.ToList()), null);

        /// <summary>
        /// The change one raise of the two cache events describes; either may be null. A full
        /// current-user invalidation is full for everyone, as today's consumers treat it.
        /// <paramref name="sharedGames"/> are library games whose definitions a friend write changed.
        /// </summary>
        public static AchievementDataChange Of(
            CacheInvalidatedEventArgs games,
            FriendCacheInvalidatedEventArgs friends,
            IEnumerable<Guid> sharedGames = null)
        {
            if (games?.IsFull == true)
            {
                return Full;
            }

            return new AchievementDataChange(
                false,
                (games?.ChangedGameIds ?? Enumerable.Empty<Guid>()).Concat(sharedGames ?? Enumerable.Empty<Guid>())
                    .Where(id => id != Guid.Empty).Distinct().ToList(),
                friends?.IsFull == true,
                friends == null || friends.IsFull ? Array.Empty<FriendCacheChange>() : friends.Changes);
        }

        /// <summary>Whether the change can alter what <paramref name="query"/> reads.</summary>
        /// <remarks>
        /// Changed games touch a query whose games are all or include one of them; a query
        /// narrowed to one provider game counts as touched, since its Playnite id is not known
        /// here. Friend changes touch friend queries only: roster changes by provider, ownership
        /// and removal by friend, game definitions by game, and one friend's game by both.
        /// </remarks>
        public bool Touches(AchievementQuery query)
        {
            if (IsFull)
            {
                return true;
            }

            if (GameIds.Count > 0 &&
                (query.GameIds == null || query.GameIds.Any(GameIds.Contains)))
            {
                return true;
            }

            if (!query.Friends)
            {
                return false;
            }

            return AllFriends || FriendChanges.Any(change => Touches(change, query));
        }

        private static bool Touches(FriendCacheChange change, AchievementQuery query)
        {
            switch (change.Kind)
            {
                case FriendCacheChangeKind.Roster:
                    return SameProvider(change, query);
                case FriendCacheChangeKind.FriendOwnership:
                case FriendCacheChangeKind.FriendRemoved:
                    return SameFriend(change, query);
                case FriendCacheChangeKind.GameDefinition:
                    return SameProvider(change, query) && SameGame(change, query);
                case FriendCacheChangeKind.FriendGameAchievements:
                    return SameFriend(change, query) && SameGame(change, query);
                default:
                    return true;
            }
        }

        // A query naming accounts reads its own rows and the compare accounts' state, so a change
        // to either touches it.
        private static bool SameProvider(FriendCacheChange change, AchievementQuery query) =>
            (string.IsNullOrWhiteSpace(query.FriendProviderKey) ||
             string.Equals(change.ProviderKey, query.FriendProviderKey.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (query.FriendAccounts == null ||
             NamedAccounts(query).Any(account => string.Equals(change.ProviderKey, account.ProviderKey, StringComparison.OrdinalIgnoreCase)));

        private static bool SameFriend(FriendCacheChange change, AchievementQuery query) =>
            SameProvider(change, query) &&
            (string.IsNullOrWhiteSpace(query.FriendExternalUserId) ||
             string.Equals(change.ExternalUserId, query.FriendExternalUserId.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (query.FriendAccounts == null ||
             NamedAccounts(query).Any(account =>
                 string.Equals(change.ProviderKey, account.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(change.ExternalUserId, account.ExternalUserId, StringComparison.OrdinalIgnoreCase)));

        private static IEnumerable<Models.Settings.FriendAccountRef> NamedAccounts(AchievementQuery query) =>
            query.FriendAccounts.Concat(query.CompareAccounts ?? Enumerable.Empty<Models.Settings.FriendAccountRef>());

        // A query narrowed to Playnite games cannot be matched against a provider identity here,
        // so it counts as touched.
        private static bool SameGame(FriendCacheChange change, AchievementQuery query)
        {
            if (string.IsNullOrWhiteSpace(query.GameProviderKey))
            {
                return true;
            }

            if (!string.Equals(change.ProviderKey, query.GameProviderKey.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var keyKnown = !string.IsNullOrWhiteSpace(change.ProviderGameKey);
            if (change.AppId <= 0 && !keyKnown)
            {
                return true;
            }

            return (change.AppId > 0 && change.AppId == query.AppId) ||
                   (keyKnown && string.Equals(change.ProviderGameKey, query.ProviderGameKey?.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
