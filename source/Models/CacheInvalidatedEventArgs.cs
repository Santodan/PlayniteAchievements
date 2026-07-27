using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Scope carried by the current-user CacheInvalidated event. IsFull means "everything may
    /// have changed" (bulk operations, resets, or an over-large change set) and ChangedGameIds
    /// is empty; consumers must fall back to their full rebuild path. Scoped invalidations name
    /// the Playnite games whose cached data changed so consumers can patch per game.
    /// </summary>
    public sealed class CacheInvalidatedEventArgs : EventArgs
    {
        /// <summary>
        /// Above this many changed games a scoped invalidation stops paying for itself for the
        /// per-game consumers, so senders collapse to a full invalidation.
        /// </summary>
        public const int MaxScopedGames = 32;

        public static readonly CacheInvalidatedEventArgs FullInvalidation =
            new CacheInvalidatedEventArgs(true, Array.Empty<Guid>());

        private CacheInvalidatedEventArgs(bool isFull, IReadOnlyList<Guid> changedGameIds)
        {
            IsFull = isFull;
            ChangedGameIds = changedGameIds ?? Array.Empty<Guid>();
        }

        public bool IsFull { get; }

        public IReadOnlyList<Guid> ChangedGameIds { get; }

        public static CacheInvalidatedEventArgs Scoped(IReadOnlyList<Guid> changedGameIds)
        {
            return changedGameIds == null ||
                   changedGameIds.Count == 0 ||
                   changedGameIds.Count > MaxScopedGames
                ? FullInvalidation
                : new CacheInvalidatedEventArgs(false, changedGameIds);
        }
    }
}
