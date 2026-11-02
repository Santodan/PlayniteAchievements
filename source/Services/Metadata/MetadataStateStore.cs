using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database.Rows;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// What the metadata overlay applied to each game, kept in the achievement cache database: the
    /// borrowed icon source and its achievements, failed icon downloads, the auto capstone's art and
    /// text, and each source's match count. Rebuilt by refreshes, so it is cleared with the cache.
    /// The capstone art records (which art and text the plugin wrote) live in the custom data
    /// database instead, outside the exported payload, so they outlast a cleared cache.
    /// Reads come from memory, loaded once; each change writes the game's rows.
    /// </summary>
    /// <remarks>
    /// Created before the cache exists and attached to it once it does: until then reads are empty
    /// and writes are dropped, as for a store with nothing saved.
    /// </remarks>
    internal sealed class MetadataStateStore
    {
        private readonly object _sync = new object();
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, MetadataSourcePageStore> _pages =
            new ConcurrentDictionary<string, MetadataSourcePageStore>(StringComparer.OrdinalIgnoreCase);

        private IMetadataStateDatabase _database;
        private GameCustomDataStore _customData;
        private Dictionary<Guid, MetadataAppliedState> _applied = new Dictionary<Guid, MetadataAppliedState>();
        private Dictionary<Guid, Dictionary<string, CapstoneArtRecord>> _capstoneArt =
            new Dictionary<Guid, Dictionary<string, CapstoneArtRecord>>();

        public MetadataStateStore(ILogger logger)
        {
            _logger = logger;
            Snapshots = new MetadataSourceSnapshotStore(() => _database, logger);
        }

        /// <summary>Each source's list and matches for a game from its last overlay.</summary>
        public MetadataSourceSnapshotStore Snapshots { get; }

        /// <summary>Loads the capstone art records from the custom data database.</summary>
        public void AttachCustomData(GameCustomDataStore customData)
        {
            _customData = customData;
            var loaded = new Dictionary<Guid, Dictionary<string, CapstoneArtRecord>>();
            try
            {
                foreach (var row in customData?.LoadAllCapstoneArtRecords() ?? Array.Empty<CapstoneArtRecordRow>())
                {
                    if (!Guid.TryParse(row?.PlayniteGameId, out var gameId) || string.IsNullOrWhiteSpace(row.ApiName))
                    {
                        continue;
                    }

                    if (!loaded.TryGetValue(gameId, out var byApiName))
                    {
                        byApiName = new Dictionary<string, CapstoneArtRecord>(StringComparer.OrdinalIgnoreCase);
                        loaded[gameId] = byApiName;
                    }

                    byApiName[row.ApiName.Trim()] = ToRecord(row);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Metadata] Failed to read the capstone art records; starting empty.");
            }

            lock (_sync)
            {
                _capstoneArt = loaded;
            }
        }

        /// <summary>Loads the applied state from the cache database and follows it when the cache is cleared.</summary>
        public void AttachDatabase(IMetadataStateDatabase database)
        {
            if (database == null)
            {
                return;
            }

            _database = database;
            database.MetadataStateCleared += (_, __) => DropCachedState();
            LoadApplied();
        }

        /// <summary>The page store of a source whose one link leads to several pages (TrueGaming).</summary>
        public MetadataSourcePageStore Pages(string sourceKey) =>
            _pages.GetOrAdd(sourceKey ?? string.Empty, key => new MetadataSourcePageStore(key, () => _database, _logger));

        /// <summary>The capstone art URL the last overlay recorded for the game, or null.</summary>
        public string GetCapstoneArtUrl(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) ? state?.CapstoneArtUrl : null;
            }
        }

        /// <summary>ApiName of the game's achievement the last overlay matched to the borrowed capstone, or null.</summary>
        public string GetCapstoneMatch(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) ? state?.CapstoneMatchApiName : null;
            }
        }

        /// <summary>The borrowed capstone title and description the last overlay recorded, or nulls.</summary>
        public (string Title, string Description) GetCapstoneText(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) && state != null
                    ? (state.CapstoneTitle, state.CapstoneDescription)
                    : (null, null);
            }
        }

        /// <summary>Each collection game's borrowed platinum by category, as the last overlay recorded them; empty when none.</summary>
        public IReadOnlyList<MetadataCategoryCapstone> GetCategoryCapstones(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) && state?.CategoryCapstones != null
                    ? state.CategoryCapstones.ToList()
                    : (IReadOnlyList<MetadataCategoryCapstone>)Array.Empty<MetadataCategoryCapstone>();
            }
        }

        /// <summary>
        /// Records what an overlay applied to the game. Returns true when the game's icons changed
        /// since the last record (another icon source, or other achievements borrowing from it, as
        /// after a pairing), so the caller re-downloads icons kept under the same file names.
        /// </summary>
        public bool RecordApplied(
            Guid gameId,
            string iconSourceKey,
            string capstoneArtUrl,
            IReadOnlyDictionary<string, MetadataCoverage> coverage = null,
            IEnumerable<string> iconApiNames = null,
            string capstoneTitle = null,
            string capstoneDescription = null,
            string capstoneMatchApiName = null,
            IReadOnlyList<MetadataCategoryCapstone> categoryCapstones = null)
        {
            var perCategory = categoryCapstones?.Count > 0 ? categoryCapstones.ToList() : null;
            var perCategoryJson = WriteCategoryCapstones(perCategory);
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var icon = string.IsNullOrWhiteSpace(iconSourceKey) ? null : iconSourceKey.Trim();
            var art = string.IsNullOrWhiteSpace(capstoneArtUrl) ? null : capstoneArtUrl.Trim();
            var title = string.IsNullOrWhiteSpace(capstoneTitle) ? null : capstoneTitle.Trim();
            var description = string.IsNullOrWhiteSpace(capstoneDescription) ? null : capstoneDescription.Trim();
            var match = art == null || string.IsNullOrWhiteSpace(capstoneMatchApiName) ? null : capstoneMatchApiName.Trim();
            var counts = coverage == null || coverage.Count == 0
                ? null
                : coverage.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var borrowed = icon == null
                ? null
                : (iconApiNames ?? Enumerable.Empty<string>())
                    .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(apiName => apiName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            bool iconChanged;
            MetadataAppliedState saved;
            lock (_sync)
            {
                _applied.TryGetValue(gameId, out var previous);
                iconChanged = !string.Equals(previous?.IconSourceKey, icon, StringComparison.OrdinalIgnoreCase) ||
                              !(previous?.IconApiNames ?? new List<string>()).SequenceEqual(borrowed ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                if (!iconChanged &&
                    string.Equals(previous?.CapstoneArtUrl, art, StringComparison.Ordinal) &&
                    string.Equals(previous?.CapstoneTitle, title, StringComparison.Ordinal) &&
                    string.Equals(previous?.CapstoneDescription, description, StringComparison.Ordinal) &&
                    string.Equals(previous?.CapstoneMatchApiName, match, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(WriteCategoryCapstones(previous?.CategoryCapstones), perCategoryJson, StringComparison.Ordinal) &&
                    SameCoverage(previous?.Coverage, counts))
                {
                    return false;
                }

                if (icon == null && art == null && counts == null && perCategory == null)
                {
                    _applied.Remove(gameId);
                    saved = null;
                }
                else
                {
                    saved = new MetadataAppliedState
                    {
                        IconSourceKey = icon,
                        CapstoneArtUrl = art,
                        CapstoneTitle = art == null ? null : title,
                        CapstoneDescription = art == null ? null : description,
                        CapstoneMatchApiName = match,
                        CategoryCapstones = perCategory,
                        Coverage = counts,
                        IconApiNames = borrowed?.Count > 0 ? borrowed : null,
                        // Still the files' state while the borrowed set is unchanged; a changed set
                        // overwrites every file anyway.
                        IconFallbackApiNames = iconChanged ? null : previous?.IconFallbackApiNames
                    };
                    _applied[gameId] = saved;
                }
            }

            Persist(gameId, saved);
            return iconChanged;
        }

        /// <summary>A copy of what the last overlay applied to the game, or null.</summary>
        public MetadataAppliedState GetApplied(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) && state != null ? Copy(state) : null;
            }
        }

        /// <summary>The borrowed icons that fell back to the game's own at the last refresh.</summary>
        public IReadOnlyCollection<string> GetIconFallbacks(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) && state?.IconFallbackApiNames != null
                    ? state.IconFallbackApiNames.ToList()
                    : (IReadOnlyCollection<string>)Array.Empty<string>();
            }
        }

        /// <summary>
        /// Records which borrowed icons fell back to the game's own because their download failed,
        /// returning the ones recorded before.
        /// </summary>
        public IReadOnlyCollection<string> ReplaceIconFallbacks(Guid gameId, IEnumerable<string> apiNames)
        {
            var next = (apiNames ?? Enumerable.Empty<string>())
                .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(apiName => apiName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (gameId == Guid.Empty)
            {
                return Array.Empty<string>();
            }

            List<string> previous;
            MetadataAppliedState saved;
            lock (_sync)
            {
                if (!_applied.TryGetValue(gameId, out var state) || state == null)
                {
                    return Array.Empty<string>();
                }

                previous = state.IconFallbackApiNames ?? new List<string>();
                if (previous.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
                {
                    return previous;
                }

                state.IconFallbackApiNames = next.Count > 0 ? next : null;
                saved = Copy(state);
            }

            Persist(gameId, saved);
            return previous;
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

        /// <summary>The source key whose icon the last overlay gave this achievement, or null for its own.</summary>
        public string GetBorrowedIconSource(Guid gameId, string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return null;
            }

            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) &&
                       state?.IconApiNames?.Contains(apiName.Trim(), StringComparer.OrdinalIgnoreCase) == true
                    ? state.IconSourceKey
                    : null;
            }
        }

        /// <summary>The art the plugin last wrote for an auto capstone, or null.</summary>
        public CapstoneArtRecord GetCapstoneArtRecord(Guid gameId, string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return null;
            }

            lock (_sync)
            {
                return _capstoneArt.TryGetValue(gameId, out var byApiName) &&
                       byApiName.TryGetValue(apiName.Trim(), out var record)
                    ? record
                    : null;
            }
        }

        /// <summary>Records (or, with null, forgets) the art the plugin wrote for an auto capstone.</summary>
        public void SetCapstoneArtRecord(Guid gameId, string apiName, CapstoneArtRecord record)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(apiName))
            {
                return;
            }

            var key = apiName.Trim();
            lock (_sync)
            {
                if (!_capstoneArt.TryGetValue(gameId, out var byApiName))
                {
                    if (record == null)
                    {
                        return;
                    }

                    byApiName = new Dictionary<string, CapstoneArtRecord>(StringComparer.OrdinalIgnoreCase);
                    _capstoneArt[gameId] = byApiName;
                }

                if (record == null)
                {
                    byApiName.Remove(key);
                    if (byApiName.Count == 0)
                    {
                        _capstoneArt.Remove(gameId);
                    }
                }
                else
                {
                    byApiName[key] = record;
                }
            }

            try
            {
                _customData?.SetCapstoneArtRecord(gameId, key, record == null ? null : ToRow(gameId, key, record));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to save the capstone art record of {gameId}.");
            }
        }

        /// <summary>Forgets a removed game; its rows went with the game's cache and custom data.</summary>
        public void ForgetGame(Guid gameId)
        {
            lock (_sync)
            {
                _applied.Remove(gameId);
                _capstoneArt.Remove(gameId);
            }

            foreach (var pages in _pages.Values)
            {
                pages.ForgetCached(gameId);
            }
        }

        private void LoadApplied()
        {
            var loaded = new Dictionary<Guid, MetadataAppliedState>();
            try
            {
                foreach (var row in _database.LoadMetadataApplied() ?? new List<MetadataAppliedRow>())
                {
                    if (Guid.TryParse(row?.PlayniteGameId, out var gameId))
                    {
                        loaded[gameId] = new MetadataAppliedState
                        {
                            IconSourceKey = row.IconSourceKey,
                            CapstoneArtUrl = row.CapstoneArtUrl,
                            CapstoneTitle = row.CapstoneTitle,
                            CapstoneDescription = row.CapstoneDescription,
                            CapstoneMatchApiName = row.CapstoneMatchApiName,
                            CategoryCapstones = ReadCategoryCapstones(row.CategoryCapstonesJson),
                            IconApiNames = ReadList(row.IconApiNamesJson),
                            IconFallbackApiNames = ReadList(row.IconFallbackApiNamesJson)
                        };
                    }
                }

                foreach (var row in _database.LoadMetadataCoverage() ?? new List<MetadataCoverageRow>())
                {
                    if (Guid.TryParse(row?.PlayniteGameId, out var gameId) &&
                        loaded.TryGetValue(gameId, out var state) &&
                        !string.IsNullOrWhiteSpace(row.SourceKey))
                    {
                        state.Coverage = state.Coverage ?? new Dictionary<string, MetadataCoverage>(StringComparer.OrdinalIgnoreCase);
                        state.Coverage[row.SourceKey] = new MetadataCoverage { Matched = row.Matched, Total = row.Total };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Metadata] Failed to read the metadata applied state; starting empty.");
            }

            lock (_sync)
            {
                _applied = loaded;
            }
        }

        /// <summary>After the cache database was cleared: its applied state and pages went with it.</summary>
        private void DropCachedState()
        {
            lock (_sync)
            {
                _applied = new Dictionary<Guid, MetadataAppliedState>();
            }

            foreach (var pages in _pages.Values)
            {
                pages.DropCached();
            }
        }

        /// <summary>Writes the game's applied row and match counts; null removes them.</summary>
        private void Persist(Guid gameId, MetadataAppliedState state)
        {
            var database = _database;
            if (database == null)
            {
                return;
            }

            var id = gameId.ToString();
            try
            {
                if (state == null)
                {
                    database.DeleteMetadataApplied(id);
                    return;
                }

                database.SaveMetadataApplied(
                    new MetadataAppliedRow
                    {
                        PlayniteGameId = id,
                        IconSourceKey = state.IconSourceKey,
                        CapstoneArtUrl = state.CapstoneArtUrl,
                        CapstoneTitle = state.CapstoneTitle,
                        CapstoneDescription = state.CapstoneDescription,
                        CapstoneMatchApiName = state.CapstoneMatchApiName,
                        CategoryCapstonesJson = WriteCategoryCapstones(state.CategoryCapstones),
                        IconApiNamesJson = WriteList(state.IconApiNames),
                        IconFallbackApiNamesJson = WriteList(state.IconFallbackApiNames),
                        UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    },
                    (state.Coverage ?? new Dictionary<string, MetadataCoverage>())
                        .Select(pair => new MetadataCoverageRow
                        {
                            PlayniteGameId = id,
                            SourceKey = pair.Key,
                            Matched = pair.Value?.Matched ?? 0,
                            Total = pair.Value?.Total ?? 0
                        })
                        .ToList());
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to save the metadata applied state of {gameId}.");
            }
        }

        private static MetadataAppliedState Copy(MetadataAppliedState state) => new MetadataAppliedState
        {
            IconSourceKey = state.IconSourceKey,
            CapstoneArtUrl = state.CapstoneArtUrl,
            CapstoneTitle = state.CapstoneTitle,
            CapstoneDescription = state.CapstoneDescription,
            CapstoneMatchApiName = state.CapstoneMatchApiName,
            CategoryCapstones = ReadCategoryCapstones(WriteCategoryCapstones(state.CategoryCapstones)),
            Coverage = state.Coverage == null ? null : new Dictionary<string, MetadataCoverage>(state.Coverage, StringComparer.OrdinalIgnoreCase),
            IconApiNames = state.IconApiNames == null ? null : new List<string>(state.IconApiNames),
            IconFallbackApiNames = state.IconFallbackApiNames == null ? null : new List<string>(state.IconFallbackApiNames)
        };

        private static string WriteCategoryCapstones(IReadOnlyList<MetadataCategoryCapstone> capstones) =>
            capstones == null || capstones.Count == 0 ? null : Newtonsoft.Json.JsonConvert.SerializeObject(capstones, Newtonsoft.Json.Formatting.None);

        private static List<MetadataCategoryCapstone> ReadCategoryCapstones(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var capstones = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MetadataCategoryCapstone>>(json);
                return capstones?.Count > 0 ? capstones : null;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
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

        private static List<string> ReadList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var list = JsonConvert.DeserializeObject<List<string>>(json);
            return list?.Count > 0 ? list : null;
        }

        private static string WriteList(List<string> list) =>
            list == null || list.Count == 0 ? null : JsonConvert.SerializeObject(list);

        internal static CapstoneArtRecord ToRecord(CapstoneArtRecordRow row) => new CapstoneArtRecord
        {
            Title = row.Title,
            Description = row.Description,
            Source = row.Source,
            Path = row.Path,
            Length = row.Length ?? 0,
            WriteTimeUtc = DateTime.TryParse(row.WriteUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
                ? written
                : default
        };

        internal static CapstoneArtRecordRow ToRow(Guid gameId, string apiName, CapstoneArtRecord record) => new CapstoneArtRecordRow
        {
            PlayniteGameId = gameId.ToString("D"),
            ApiName = apiName,
            Title = record.Title,
            Description = record.Description,
            Source = record.Source,
            Path = record.Path,
            Length = record.Length,
            WriteUtc = record.WriteTimeUtc.ToString("o", CultureInfo.InvariantCulture)
        };
    }
}
