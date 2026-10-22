using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database.Rows;
using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>Whether the row is the source's capstone (a PlayStation platinum).</summary>
        [JsonProperty("platinum", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsPlatinum { get; set; }
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
    /// Each metadata source's list and matches for a game at its last overlay, kept in the cache
    /// database, so Manage Achievements can show and fix the pairings without fetching anything.
    /// Rebuilt by the next refresh and never exported.
    /// </summary>
    public sealed class MetadataSourceSnapshotStore
    {
        private readonly object _sync = new object();
        private readonly Func<IMetadataStateDatabase> _database;
        private readonly ILogger _logger;

        internal MetadataSourceSnapshotStore(Func<IMetadataStateDatabase> database, ILogger logger)
        {
            _database = database;
            _logger = logger;
        }

        /// <summary>The source's snapshot for the game when it was taken from <paramref name="linkId"/>, else null.</summary>
        public MetadataSourceSnapshot Get(Guid gameId, string sourceKey, string linkId)
        {
            var database = _database?.Invoke();
            if (database == null || string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(linkId))
            {
                return null;
            }

            try
            {
                var row = database.GetMetadataSourceSnapshot(gameId.ToString(), sourceKey.Trim());
                if (row == null || !string.Equals(row.LinkId, linkId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new MetadataSourceSnapshot
                {
                    LinkId = row.LinkId,
                    Rows = string.IsNullOrWhiteSpace(row.RowsJson)
                        ? new List<MetadataSourceRow>()
                        : JsonConvert.DeserializeObject<List<MetadataSourceRow>>(row.RowsJson) ?? new List<MetadataSourceRow>(),
                    Matches = new Dictionary<string, string>(
                        string.IsNullOrWhiteSpace(row.MatchesJson)
                            ? new Dictionary<string, string>()
                            : JsonConvert.DeserializeObject<Dictionary<string, string>>(row.MatchesJson) ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase)
                };
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to read the {sourceKey} snapshot of {gameId}.");
                return null;
            }
        }

        /// <summary>Records a source's rows and matches for the game; the row is written only when they changed.</summary>
        public void Record(
            Guid gameId,
            string sourceKey,
            string linkId,
            IEnumerable<AchievementDetail> rows,
            IEnumerable<KeyValuePair<string, string>> matches)
        {
            var database = _database?.Invoke();
            if (database == null || gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(linkId))
            {
                return;
            }

            var sourceRows = (rows ?? Enumerable.Empty<AchievementDetail>())
                .Where(row => !string.IsNullOrWhiteSpace(row?.ApiName))
                .GroupBy(row => row.ApiName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(row => new MetadataSourceRow
                {
                    ApiName = row.ApiName,
                    Name = row.DisplayName,
                    Description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description,
                    Icon = string.IsNullOrWhiteSpace(row.UnlockedIconPath) ? row.LockedIconPath : row.UnlockedIconPath,
                    IsPlatinum = row.IsCapstone ||
                                 string.Equals(row.TrophyType?.Trim(), "platinum", StringComparison.OrdinalIgnoreCase)
                })
                .ToList();
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in matches ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    pairs[pair.Key] = pair.Value;
                }
            }

            var next = new MetadataSourceSnapshotRow
            {
                PlayniteGameId = gameId.ToString(),
                SourceKey = sourceKey.Trim(),
                LinkId = linkId.Trim(),
                RowsJson = JsonConvert.SerializeObject(sourceRows, Formatting.None),
                MatchesJson = JsonConvert.SerializeObject(pairs, Formatting.None)
            };

            try
            {
                lock (_sync)
                {
                    var current = database.GetMetadataSourceSnapshot(next.PlayniteGameId, next.SourceKey);
                    if (current != null &&
                        string.Equals(current.LinkId, next.LinkId, StringComparison.Ordinal) &&
                        string.Equals(current.RowsJson, next.RowsJson, StringComparison.Ordinal) &&
                        string.Equals(current.MatchesJson, next.MatchesJson, StringComparison.Ordinal))
                    {
                        return;
                    }

                    database.SaveMetadataSourceSnapshot(next);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to save the {sourceKey} snapshot of {gameId}.");
            }
        }

        /// <summary>Drops a source's snapshot, for a source the game no longer takes achievement data from.</summary>
        public void Forget(Guid gameId, string sourceKey)
        {
            var database = _database?.Invoke();
            if (database == null || gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey))
            {
                return;
            }

            try
            {
                database.DeleteMetadataSourceSnapshot(gameId.ToString(), sourceKey.Trim());
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to drop the {sourceKey} snapshot of {gameId}.");
            }
        }
    }
}