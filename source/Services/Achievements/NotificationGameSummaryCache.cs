using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Cache;

namespace PlayniteAchievements.Services.Achievements
{
    // Notifications need game totals, never library-wide recent achievement lists.
    // Keep these rows independent of the bounded overview cache, whose trimming makes
    // it impossible to patch after a game changes.
    internal sealed class NotificationGameSummaryCache
    {
        private readonly object _sync = new object();
        private List<CachedGameSummaryData> _games;
        private readonly HashSet<Guid> _dirty = new HashSet<Guid>();
        private int _generation;

        public void InvalidateDelta(Models.CacheDeltaEventArgs delta)
        {
            Invalidate(delta != null && !delta.IsFullReset &&
                Guid.TryParse(delta.Key, out var gameId) && gameId != Guid.Empty
                ? new[] { gameId } : null);
        }

        public void Invalidate(IReadOnlyList<Guid> gameIds)
        {
            lock (_sync)
            {
                _generation++;
                if (gameIds == null || gameIds.Count == 0)
                {
                    _games = null;
                    _dirty.Clear();
                    return;
                }
                foreach (var id in gameIds) _dirty.Add(id);
                if (_dirty.Count > Models.CacheInvalidatedEventArgs.MaxScopedGames)
                {
                    _games = null;
                    _dirty.Clear();
                }
            }
        }

        public IReadOnlyList<CachedGameSummaryData> Get(
            Func<CachedSummaryData> loadAll, Func<Guid, CachedSummaryData> loadGame)
        {
            List<CachedGameSummaryData> games;
            List<Guid> dirty;
            int generation;
            lock (_sync)
            {
                if (_games != null && _dirty.Count == 0) return _games;
                games = _games;
                dirty = _dirty.ToList();
                generation = _generation;
            }

            if (games != null)
            {
                var replacements = new List<CachedGameSummaryData>();
                foreach (var id in dirty)
                {
                    var slice = loadGame(id);
                    if (slice == null)
                    {
                        games = null; // Fall back to a full read if a scoped read is unavailable.
                        break;
                    }
                    replacements.AddRange(slice.Games ?? new List<CachedGameSummaryData>());
                }
                if (games != null)
                {
                    var ids = new HashSet<Guid>(dirty);
                    games = games.Where(row => row?.PlayniteGameId.HasValue != true ||
                        !ids.Contains(row.PlayniteGameId.Value)).Concat(replacements).ToList();
                }
            }

            if (games == null)
            {
                var all = loadAll();
                if (all == null) return null;
                games = (all.Games ?? new List<CachedGameSummaryData>()).ToList();
            }

            lock (_sync)
            {
                // An invalidation during SQL/hydration must remain dirty for the next caller.
                if (_generation == generation)
                {
                    _games = games;
                    _dirty.Clear();
                }
            }
            return games;
        }
    }
}
