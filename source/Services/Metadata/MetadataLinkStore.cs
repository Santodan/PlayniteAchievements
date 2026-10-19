using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>Who set a game's link to a metadata source.</summary>
    public enum MetadataLinkOrigin
    {
        /// <summary>Found by the source's own matching; replaced only when the user edits or clears it.</summary>
        Auto,

        /// <summary>Entered by the user; never replaced by matching.</summary>
        User
    }

    /// <summary>A game's stored link to one metadata source. A null <see cref="Id"/> is an explicit "no match".</summary>
    public sealed class MetadataLinkRecord
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("origin")]
        public MetadataLinkOrigin Origin { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }

        [JsonIgnore]
        public bool IsNoMatch => string.IsNullOrWhiteSpace(Id);

        public MetadataLinkRecord Clone() => (MetadataLinkRecord)MemberwiseClone();
    }

    /// <summary>What the last metadata overlay applied to a game, read by later steps of the same or a later run.</summary>
    public sealed class MetadataAppliedState
    {
        /// <summary>Key of the metadata provider whose icons replaced the provider's, or null for the provider's own.</summary>
        [JsonProperty("iconSource")]
        public string IconSourceKey { get; set; }

        /// <summary>Image URL an authored auto capstone uses, or null for the game's own art.</summary>
        [JsonProperty("capstoneArt")]
        public string CapstoneArtUrl { get; set; }

        /// <summary>How many of the game's achievements each source matched, by source key.</summary>
        [JsonProperty("coverage")]
        public Dictionary<string, MetadataCoverage> Coverage { get; set; }
    }

    /// <summary>One source's match count for a game at its last overlay.</summary>
    public sealed class MetadataCoverage
    {
        [JsonProperty("matched")]
        public int Matched { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        public bool SameAs(MetadataCoverage other) =>
            other != null && other.Matched == Matched && other.Total == Total;
    }

    /// <summary>
    /// Per-game links to metadata sources (a Steam appId, a PlayStation NPWR id, an Exophase slug),
    /// keyed by game and source key. Both automatic matches and user edits live here so every caller
    /// reads one record per (game, source). Kept outside game custom data: an automatic match written
    /// during a refresh must not count as a user customization or trigger another refresh.
    /// </summary>
    public sealed class MetadataLinkStore
    {
        private const string FileName = "links.json";
        private const int FileVersion = 1;

        private readonly object _sync = new object();
        private readonly object _fileSync = new object();
        private readonly ILogger _logger;
        private readonly string _path;
        private Dictionary<Guid, Dictionary<string, MetadataLinkRecord>> _links =
            new Dictionary<Guid, Dictionary<string, MetadataLinkRecord>>();
        private Dictionary<Guid, MetadataAppliedState> _applied = new Dictionary<Guid, MetadataAppliedState>();

        private sealed class LinkFile
        {
            [JsonProperty("version")]
            public int Version { get; set; } = FileVersion;

            [JsonProperty("games")]
            public Dictionary<Guid, Dictionary<string, MetadataLinkRecord>> Games { get; set; }

            [JsonProperty("applied")]
            public Dictionary<Guid, MetadataAppliedState> Applied { get; set; }
        }

        /// <summary>Raised after a link is added, changed or removed, with the game id.</summary>
        public event Action<Guid> LinkChanged;

        public MetadataLinkStore(string pluginUserDataPath, ILogger logger)
        {
            _logger = logger;
            if (!string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                var directory = Path.Combine(pluginUserDataPath, "metadata");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, FileName);
                Load();
            }
        }

        public bool TryGet(Guid gameId, string sourceKey, out MetadataLinkRecord record)
        {
            record = null;
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey))
            {
                return false;
            }

            lock (_sync)
            {
                if (_links.TryGetValue(gameId, out var bySource) &&
                    bySource.TryGetValue(sourceKey.Trim(), out var stored) &&
                    stored != null)
                {
                    record = stored.Clone();
                    return true;
                }
            }

            return false;
        }

        /// <summary>All of a game's links, by source key.</summary>
        public IReadOnlyDictionary<string, MetadataLinkRecord> GetAll(Guid gameId)
        {
            lock (_sync)
            {
                return _links.TryGetValue(gameId, out var bySource)
                    ? bySource.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, MetadataLinkRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Stores an automatic match unless the user already set this link.</summary>
        public bool SetAuto(Guid gameId, string sourceKey, string id, string label = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            return Write(gameId, sourceKey, existing =>
            {
                if (existing?.Origin == MetadataLinkOrigin.User)
                {
                    return null;
                }

                return new MetadataLinkRecord
                {
                    Id = id.Trim(),
                    Label = NormalizeLabel(label),
                    Origin = MetadataLinkOrigin.Auto
                };
            });
        }

        /// <summary>
        /// Stores the user's link. A blank id stores an explicit "no match", which stops automatic
        /// matching for this source; <see cref="Clear"/> removes the record so matching runs again.
        /// </summary>
        public bool SetUser(Guid gameId, string sourceKey, string id, string label = null)
        {
            return Write(gameId, sourceKey, existing => new MetadataLinkRecord
            {
                Id = string.IsNullOrWhiteSpace(id) ? null : id.Trim(),
                Label = NormalizeLabel(label),
                Origin = MetadataLinkOrigin.User
            });
        }

        public bool Clear(Guid gameId, string sourceKey)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey))
            {
                return false;
            }

            bool removed;
            lock (_sync)
            {
                removed = _links.TryGetValue(gameId, out var bySource) && bySource.Remove(sourceKey.Trim());
                if (removed && bySource.Count == 0)
                {
                    _links.Remove(gameId);
                }
            }

            if (removed)
            {
                Save();
                LinkChanged?.Invoke(gameId);
            }

            return removed;
        }

        /// <summary>The capstone art URL the last overlay recorded for the game, or null.</summary>
        public string GetCapstoneArtUrl(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) ? state?.CapstoneArtUrl : null;
            }
        }

        /// <summary>
        /// Records what an overlay applied to the game. Returns true when the icon source differs
        /// from the one recorded last, so the caller re-downloads icons kept under the same names.
        /// </summary>
        public bool RecordApplied(
            Guid gameId,
            string iconSourceKey,
            string capstoneArtUrl,
            IReadOnlyDictionary<string, MetadataCoverage> coverage = null)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var icon = string.IsNullOrWhiteSpace(iconSourceKey) ? null : iconSourceKey.Trim();
            var art = string.IsNullOrWhiteSpace(capstoneArtUrl) ? null : capstoneArtUrl.Trim();
            var counts = coverage == null || coverage.Count == 0
                ? null
                : coverage.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            bool iconChanged;
            lock (_sync)
            {
                _applied.TryGetValue(gameId, out var previous);
                iconChanged = !string.Equals(previous?.IconSourceKey, icon, StringComparison.OrdinalIgnoreCase);
                if (!iconChanged &&
                    string.Equals(previous?.CapstoneArtUrl, art, StringComparison.Ordinal) &&
                    SameCoverage(previous?.Coverage, counts))
                {
                    return false;
                }

                if (icon == null && art == null && counts == null)
                {
                    _applied.Remove(gameId);
                }
                else
                {
                    _applied[gameId] = new MetadataAppliedState { IconSourceKey = icon, CapstoneArtUrl = art, Coverage = counts };
                }
            }

            Save();
            return iconChanged;
        }

        /// <summary>A source's match count for the game at the last overlay, or null when it did not run.</summary>
        public MetadataCoverage GetCoverage(Guid gameId, string sourceKey)
        {
            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                return null;
            }

            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) &&
                       state?.Coverage != null &&
                       state.Coverage.TryGetValue(sourceKey.Trim(), out var counts)
                    ? new MetadataCoverage { Matched = counts.Matched, Total = counts.Total }
                    : null;
            }
        }

        private static MetadataAppliedState NormalizeLoaded(MetadataAppliedState state)
        {
            if (state.Coverage != null)
            {
                state.Coverage = new Dictionary<string, MetadataCoverage>(state.Coverage, StringComparer.OrdinalIgnoreCase);
            }

            return state;
        }

        private static bool SameCoverage(
            IReadOnlyDictionary<string, MetadataCoverage> previous,
            IReadOnlyDictionary<string, MetadataCoverage> next)
        {
            if ((previous?.Count ?? 0) != (next?.Count ?? 0))
            {
                return false;
            }

            return next == null || next.All(pair =>
                previous.TryGetValue(pair.Key, out var counts) && pair.Value.SameAs(counts));
        }

        private bool Write(Guid gameId, string sourceKey, Func<MetadataLinkRecord, MetadataLinkRecord> build)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey))
            {
                return false;
            }

            var key = sourceKey.Trim();
            lock (_sync)
            {
                if (!_links.TryGetValue(gameId, out var bySource))
                {
                    bySource = new Dictionary<string, MetadataLinkRecord>(StringComparer.OrdinalIgnoreCase);
                }

                bySource.TryGetValue(key, out var existing);
                var next = build(existing);
                if (next == null ||
                    (existing != null &&
                     existing.Origin == next.Origin &&
                     string.Equals(existing.Id, next.Id, StringComparison.Ordinal) &&
                     string.Equals(existing.Label, next.Label, StringComparison.Ordinal)))
                {
                    return false;
                }

                next.UpdatedUtc = DateTime.UtcNow;
                bySource[key] = next;
                _links[gameId] = bySource;
            }

            Save();
            LinkChanged?.Invoke(gameId);
            return true;
        }

        private static string NormalizeLabel(string label)
        {
            return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        }

        private void Load()
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return;
            }

            try
            {
                var parsed = JsonConvert.DeserializeObject<LinkFile>(File.ReadAllText(_path, Encoding.UTF8));
                var loaded = new Dictionary<Guid, Dictionary<string, MetadataLinkRecord>>();
                foreach (var game in parsed?.Games ?? new Dictionary<Guid, Dictionary<string, MetadataLinkRecord>>())
                {
                    if (game.Key == Guid.Empty || game.Value == null)
                    {
                        continue;
                    }

                    var bySource = new Dictionary<string, MetadataLinkRecord>(StringComparer.OrdinalIgnoreCase);
                    foreach (var link in game.Value)
                    {
                        if (!string.IsNullOrWhiteSpace(link.Key) && link.Value != null)
                        {
                            bySource[link.Key.Trim()] = link.Value;
                        }
                    }

                    if (bySource.Count > 0)
                    {
                        loaded[game.Key] = bySource;
                    }
                }

                lock (_sync)
                {
                    _links = loaded;
                    _applied = (parsed?.Applied ?? new Dictionary<Guid, MetadataAppliedState>())
                        .Where(pair => pair.Key != Guid.Empty && pair.Value != null)
                        .ToDictionary(pair => pair.Key, pair => NormalizeLoaded(pair.Value));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to read metadata links; starting empty: {_path}");
            }
        }

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            string json;
            lock (_sync)
            {
                json = JsonConvert.SerializeObject(new LinkFile { Games = _links, Applied = _applied }, Formatting.None);
            }

            var temporaryPath = _path + ".tmp";
            try
            {
                lock (_fileSync)
                {
                    File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                    if (File.Exists(_path))
                    {
                        File.Replace(temporaryPath, _path, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(temporaryPath, _path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to save metadata links: {_path}");
            }
        }
    }
}
