using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Where each game's TrueGaming link leads on each site: the page found there, or that the site
    /// has none. Local and rederivable, so it stays out of game data files; a game's record belongs
    /// to its link name, and a different name starts it again. Saved at the end of a refresh run.
    /// </summary>
    internal sealed class TrueGamingPageStore
    {
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private Dictionary<Guid, GameRecord> _games;
        private bool _dirty;

        public TrueGamingPageStore(string path, ILogger logger)
        {
            _path = path;
            _logger = logger;
        }

        /// <summary>
        /// True when the site was looked at for <paramref name="name"/>; <paramref name="pageSlug"/>
        /// is then its page, or null for a site without one.
        /// </summary>
        public bool TryGet(Guid gameId, string name, TrueGamingSite site, out string pageSlug)
        {
            pageSlug = null;
            lock (_sync)
            {
                return Games().TryGetValue(gameId, out var record) &&
                       string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase) &&
                       record.Pages != null &&
                       record.Pages.TryGetValue(site.Prefix, out pageSlug);
            }
        }

        /// <summary>Records the site's page for <paramref name="name"/>, null for none; a new name clears the game's other sites.</summary>
        public void Set(Guid gameId, string name, TrueGamingSite site, string pageSlug)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            lock (_sync)
            {
                var games = Games();
                if (!games.TryGetValue(gameId, out var record) ||
                    !string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    record = new GameRecord { Name = name };
                    games[gameId] = record;
                    _dirty = true;
                }

                record.Pages = record.Pages ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!record.Pages.TryGetValue(site.Prefix, out var existing) || !string.Equals(existing, pageSlug, StringComparison.Ordinal))
                {
                    record.Pages[site.Prefix] = pageSlug;
                    _dirty = true;
                }
            }
        }

        /// <summary>Each site looked at for <paramref name="name"/>, in lookup order, with its page or null for none.</summary>
        public IReadOnlyList<(TrueGamingSite Site, string PageSlug)> GetPages(Guid gameId, string name)
        {
            var pages = new List<(TrueGamingSite Site, string PageSlug)>();
            foreach (var site in TrueGamingSite.LookupOrder)
            {
                if (TryGet(gameId, name, site, out var pageSlug))
                {
                    pages.Add((site, pageSlug));
                }
            }

            return pages;
        }

        /// <summary>Forgets the game's pages, so every site is looked at again.</summary>
        public void Clear(Guid gameId)
        {
            lock (_sync)
            {
                _dirty |= Games().Remove(gameId);
            }
        }

        public void Save()
        {
            string json;
            lock (_sync)
            {
                if (!_dirty || _games == null || string.IsNullOrWhiteSpace(_path))
                {
                    return;
                }

                json = JsonConvert.SerializeObject(_games, Formatting.None);
                _dirty = false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[TrueGamingMetadata] Saving {_path} failed: {ex.Message}");
            }
        }

        private Dictionary<Guid, GameRecord> Games()
        {
            if (_games != null)
            {
                return _games;
            }

            _games = new Dictionary<Guid, GameRecord>();
            try
            {
                if (!string.IsNullOrWhiteSpace(_path) && File.Exists(_path))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<Guid, GameRecord>>(File.ReadAllText(_path, Encoding.UTF8));
                    if (loaded != null)
                    {
                        _games = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[TrueGamingMetadata] Reading {_path} failed: {ex.Message}");
            }

            return _games;
        }

        private sealed class GameRecord
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            /// <summary>Site prefix to page slug; a null slug is a site without the game.</summary>
            [JsonProperty("pages")]
            public Dictionary<string, string> Pages { get; set; }
        }
    }
}
