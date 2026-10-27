using Playnite.SDK;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database.Rows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Where one source's one-name links led for each game: the page found on each site, or that a
    /// site has none (a null slug). A game's record belongs to its link name; a different name
    /// starts it again. Kept in the cache database and rederived by refreshes; read from memory,
    /// loaded on first use, and each change writes the game's rows.
    /// </summary>
    internal sealed class MetadataSourcePageStore
    {
        private readonly string _sourceKey;
        private readonly Func<IMetadataStateDatabase> _database;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private Dictionary<Guid, GameRecord> _games;

        public MetadataSourcePageStore(string sourceKey, Func<IMetadataStateDatabase> database, ILogger logger)
        {
            _sourceKey = sourceKey;
            _database = database;
            _logger = logger;
        }

        /// <summary>
        /// True when the site was looked at for <paramref name="name"/>; <paramref name="pageSlug"/>
        /// is then its page, or null for a site without one.
        /// </summary>
        public bool TryGet(Guid gameId, string name, string site, out string pageSlug)
        {
            pageSlug = null;
            lock (_sync)
            {
                return Games().TryGetValue(gameId, out var record) &&
                       string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase) &&
                       record.Pages.TryGetValue(site ?? string.Empty, out pageSlug);
            }
        }

        /// <summary>Records the site's page for <paramref name="name"/>, null for none; a new name clears the game's other sites.</summary>
        public void Set(Guid gameId, string name, string site, string pageSlug)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(site))
            {
                return;
            }

            List<MetadataSourcePageRow> rows;
            lock (_sync)
            {
                var games = Games();
                if (!games.TryGetValue(gameId, out var record) ||
                    !string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    record = new GameRecord(name);
                    games[gameId] = record;
                }
                else if (record.Pages.TryGetValue(site, out var existing) && string.Equals(existing, pageSlug, StringComparison.Ordinal))
                {
                    return;
                }

                record.Pages[site] = pageSlug;
                rows = ToRows(gameId, record);
            }

            Write(gameId, rows);
        }

        /// <summary>Each site looked at for <paramref name="name"/>, in the order recorded, with its page or null for none.</summary>
        public IReadOnlyList<(string Site, string PageSlug)> GetPages(Guid gameId, string name)
        {
            lock (_sync)
            {
                return Games().TryGetValue(gameId, out var record) &&
                       string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase)
                    ? record.Pages.Select(pair => (pair.Key, pair.Value)).ToList()
                    : new List<(string Site, string PageSlug)>();
            }
        }

        /// <summary>Forgets the sites of <paramref name="name"/>'s record other than <paramref name="sites"/>.</summary>
        public void Keep(Guid gameId, string name, IEnumerable<string> sites)
        {
            var keep = new HashSet<string>(sites ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            List<MetadataSourcePageRow> rows;
            lock (_sync)
            {
                if (!Games().TryGetValue(gameId, out var record) ||
                    !string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var stale = record.Pages.Keys.Where(site => !keep.Contains(site)).ToList();
                if (stale.Count == 0)
                {
                    return;
                }

                foreach (var site in stale)
                {
                    record.Pages.Remove(site);
                }

                rows = ToRows(gameId, record);
            }

            Write(gameId, rows);
        }

        /// <summary>Forgets the game's pages, so every site is looked at again.</summary>
        public void Clear(Guid gameId)
        {
            lock (_sync)
            {
                if (!Games().Remove(gameId))
                {
                    return;
                }
            }

            Write(gameId, new List<MetadataSourcePageRow>());
        }

        /// <summary>Drops a removed game from memory; its rows went with the game's cache.</summary>
        internal void ForgetCached(Guid gameId)
        {
            lock (_sync)
            {
                _games?.Remove(gameId);
            }
        }

        /// <summary>Drops everything from memory after the cache database was cleared.</summary>
        internal void DropCached()
        {
            lock (_sync)
            {
                _games = null;
            }
        }

        private Dictionary<Guid, GameRecord> Games()
        {
            if (_games != null)
            {
                return _games;
            }

            var games = new Dictionary<Guid, GameRecord>();
            var database = _database?.Invoke();
            if (database == null)
            {
                // Nothing to read yet; not kept, so the database is read once it is attached.
                return games;
            }

            try
            {
                foreach (var row in database.LoadMetadataSourcePages(_sourceKey) ?? new List<MetadataSourcePageRow>())
                {
                    if (!Guid.TryParse(row?.PlayniteGameId, out var gameId) || string.IsNullOrWhiteSpace(row.Site))
                    {
                        continue;
                    }

                    if (!games.TryGetValue(gameId, out var record))
                    {
                        record = new GameRecord(row.LinkName);
                        games[gameId] = record;
                    }

                    record.Pages[row.Site] = row.PageSlug;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to read the {_sourceKey} pages; starting empty.");
            }

            _games = games;
            return _games;
        }

        private List<MetadataSourcePageRow> ToRows(Guid gameId, GameRecord record) =>
            record.Pages.Select(pair => new MetadataSourcePageRow
            {
                PlayniteGameId = gameId.ToString(),
                SourceKey = _sourceKey,
                LinkName = record.Name,
                Site = pair.Key,
                PageSlug = pair.Value
            }).ToList();

        private void Write(Guid gameId, List<MetadataSourcePageRow> rows)
        {
            var database = _database?.Invoke();
            if (database == null)
            {
                return;
            }

            try
            {
                database.ReplaceMetadataSourcePages(gameId.ToString(), _sourceKey, rows);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to save the {_sourceKey} pages of {gameId}.");
            }
        }

        private sealed class GameRecord
        {
            public GameRecord(string name)
            {
                Name = name;
            }

            public string Name { get; }

            /// <summary>Site to page slug; a null slug is a site without the game.</summary>
            public Dictionary<string, string> Pages { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
