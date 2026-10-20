using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>One row of a metadata source's list for a game, as Manage Achievements shows it.</summary>
    public sealed class MetadataSourceRow
    {
        [JsonProperty("apiName")]
        public string ApiName { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        [JsonProperty("icon", NullValueHandling = NullValueHandling.Ignore)]
        public string Icon { get; set; }
    }

    /// <summary>A source's rows for a game at its last overlay, and which of the game's achievements each matched.</summary>
    public sealed class MetadataSourceSnapshot
    {
        /// <summary>The link the rows came from; a snapshot for another link is stale.</summary>
        [JsonProperty("linkId")]
        public string LinkId { get; set; }

        [JsonProperty("rows")]
        public List<MetadataSourceRow> Rows { get; set; } = new List<MetadataSourceRow>();

        /// <summary>The game's ApiName to the matched source row's ApiName.</summary>
        [JsonProperty("matches")]
        public Dictionary<string, string> Matches { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Each metadata source's list and matches for a game at its last overlay, one file per game
    /// under <c>metadata/sources</c>, so Manage Achievements can show and fix the pairings without
    /// fetching anything. Local state: it is rebuilt by the next refresh and never travels.
    /// </summary>
    public sealed class MetadataSourceSnapshotStore
    {
        private readonly object _sync = new object();
        private readonly string _directory;
        private readonly ILogger _logger;

        public MetadataSourceSnapshotStore(string metadataDirectory, ILogger logger)
        {
            _logger = logger;
            if (!string.IsNullOrWhiteSpace(metadataDirectory))
            {
                _directory = Path.Combine(metadataDirectory, "sources");
            }
        }

        /// <summary>The source's snapshot for the game when it was taken from <paramref name="linkId"/>, else null.</summary>
        public MetadataSourceSnapshot Get(Guid gameId, string sourceKey, string linkId)
        {
            if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(linkId))
            {
                return null;
            }

            lock (_sync)
            {
                return Read(gameId).TryGetValue(sourceKey.Trim(), out var snapshot) &&
                       string.Equals(snapshot?.LinkId, linkId.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? snapshot
                    : null;
            }
        }

        /// <summary>Records a source's rows and matches for the game; the file is rewritten only when they changed.</summary>
        public void Record(
            Guid gameId,
            string sourceKey,
            string linkId,
            IEnumerable<AchievementDetail> rows,
            IEnumerable<KeyValuePair<string, string>> matches)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(linkId) || _directory == null)
            {
                return;
            }

            var snapshot = new MetadataSourceSnapshot
            {
                LinkId = linkId.Trim(),
                Rows = (rows ?? Enumerable.Empty<AchievementDetail>())
                    .Where(row => !string.IsNullOrWhiteSpace(row?.ApiName))
                    .GroupBy(row => row.ApiName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Select(row => new MetadataSourceRow
                    {
                        ApiName = row.ApiName,
                        Name = row.DisplayName,
                        Description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description,
                        Icon = string.IsNullOrWhiteSpace(row.UnlockedIconPath) ? row.LockedIconPath : row.UnlockedIconPath
                    })
                    .ToList()
            };
            foreach (var pair in matches ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    snapshot.Matches[pair.Key] = pair.Value;
                }
            }

            lock (_sync)
            {
                var all = Read(gameId);
                all[sourceKey.Trim()] = snapshot;
                Write(gameId, all);
            }
        }

        /// <summary>Drops a source's snapshot, for a source the game no longer takes achievement data from.</summary>
        public void Forget(Guid gameId, string sourceKey)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey) || _directory == null)
            {
                return;
            }

            lock (_sync)
            {
                var all = Read(gameId);
                if (all.Remove(sourceKey.Trim()))
                {
                    Write(gameId, all);
                }
            }
        }

        private string PathFor(Guid gameId) => _directory == null ? null : Path.Combine(_directory, gameId.ToString("D") + ".json");

        private Dictionary<string, MetadataSourceSnapshot> Read(Guid gameId)
        {
            var empty = new Dictionary<string, MetadataSourceSnapshot>(StringComparer.OrdinalIgnoreCase);
            var path = PathFor(gameId);
            if (path == null || !File.Exists(path))
            {
                return empty;
            }

            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, MetadataSourceSnapshot>>(File.ReadAllText(path, Encoding.UTF8));
                return loaded == null
                    ? empty
                    : new Dictionary<string, MetadataSourceSnapshot>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to read the source snapshot; starting empty: {path}");
                return empty;
            }
        }

        private void Write(Guid gameId, Dictionary<string, MetadataSourceSnapshot> all)
        {
            var path = PathFor(gameId);
            try
            {
                var json = JsonConvert.SerializeObject(all, Formatting.None);
                if (File.Exists(path) && string.Equals(File.ReadAllText(path, Encoding.UTF8), json, StringComparison.Ordinal))
                {
                    return;
                }

                Directory.CreateDirectory(_directory);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to write the source snapshot: {path}");
            }
        }
    }
}
