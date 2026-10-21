using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
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
        public string Id { get; set; }

        public string Label { get; set; }

        public MetadataLinkOrigin Origin { get; set; }

        public bool IsNoMatch => string.IsNullOrWhiteSpace(Id);

        /// <summary>The user's achievement pairings, by the game's ApiName; an empty value is "none".</summary>
        public IReadOnlyDictionary<string, string> Pairings { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>The borrowed capstone's title (a PlayStation platinum's), or null.</summary>
        [JsonProperty("capstoneTitle")]
        public string CapstoneTitle { get; set; }

        /// <summary>The borrowed capstone's description, or null.</summary>
        [JsonProperty("capstoneDescription")]
        public string CapstoneDescription { get; set; }

        /// <summary>How many of the game's achievements each source matched, by source key.</summary>
        [JsonProperty("coverage")]
        public Dictionary<string, MetadataCoverage> Coverage { get; set; }

        /// <summary>ApiNames of the achievements whose icons came from <see cref="IconSourceKey"/>.</summary>
        [JsonProperty("iconApiNames")]
        public List<string> IconApiNames { get; set; }

        /// <summary>
        /// ApiNames among <see cref="IconApiNames"/> whose borrowed icon failed to download, so the
        /// cached file holds the game's own icon until a later download succeeds.
        /// </summary>
        [JsonProperty("iconFallbackApiNames")]
        public List<string> IconFallbackApiNames { get; set; }
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
    /// The art the plugin last wrote into an auto capstone's icon file: where it came from and the
    /// file it left, so a later refresh can tell its own file from one the user replaced. Also the
    /// borrowed text it last wrote, so that text is recognized as the plugin's and not the user's.
    /// </summary>
    public sealed class CapstoneArtRecord
    {
        /// <summary>The borrowed title the plugin wrote, or null while the capstone uses the templates.</summary>
        [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)]
        public string Title { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("length")]
        public long Length { get; set; }

        [JsonProperty("writeUtc")]
        public DateTime WriteTimeUtc { get; set; }
    }

    /// <summary>
    /// Per-game links to metadata sources (a Steam appId, a PlayStation NPWR id), keyed by game and
    /// source key, plus a local record of what the last overlay applied. Links are stored with the
    /// game's custom data so they travel with exports and the Workshop; the applied record and the
    /// capstone art record describe this machine's files and stay in a local file.
    /// </summary>
    public sealed class MetadataLinkStore
    {
        private const string FileName = "links.json";
        private const int FileVersion = 2;

        private readonly object _sync = new object();
        private readonly object _fileSync = new object();
        private readonly GameCustomDataStore _customData;
        private readonly ILogger _logger;
        private readonly string _path;
        private Dictionary<Guid, MetadataAppliedState> _applied = new Dictionary<Guid, MetadataAppliedState>();
        private Dictionary<Guid, Dictionary<string, CapstoneArtRecord>> _capstoneArt =
            new Dictionary<Guid, Dictionary<string, CapstoneArtRecord>>();

        private sealed class AppliedFile
        {
            [JsonProperty("version")]
            public int Version { get; set; } = FileVersion;

            [JsonProperty("applied")]
            public Dictionary<Guid, MetadataAppliedState> Applied { get; set; }

            [JsonProperty("capstoneArt")]
            public Dictionary<Guid, Dictionary<string, CapstoneArtRecord>> CapstoneArt { get; set; }
        }

        /// <summary>Raised after a link is added, changed or removed, with the game id.</summary>
        public event Action<Guid> LinkChanged;

        /// <summary>Each source's list and matches from the last overlay; null without a data folder.</summary>
        public MetadataSourceSnapshotStore Snapshots { get; }

        public MetadataLinkStore(string pluginUserDataPath, GameCustomDataStore customData, ILogger logger)
        {
            _customData = customData;
            _logger = logger;
            if (!string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                var directory = Path.Combine(pluginUserDataPath, "metadata");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, FileName);
                Snapshots = new MetadataSourceSnapshotStore(directory, logger);
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

            var links = ReadLinks(gameId);
            if (links == null || !links.TryGetValue(sourceKey.Trim(), out var stored) || stored == null)
            {
                return false;
            }

            record = ToRecord(stored);
            return true;
        }

        /// <summary>All of a game's links, by source key.</summary>
        public IReadOnlyDictionary<string, MetadataLinkRecord> GetAll(Guid gameId)
        {
            return (ReadLinks(gameId) ?? new Dictionary<string, MetadataLinkData>())
                .Where(pair => pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => ToRecord(pair.Value), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The game's own metadata choices, or null when it follows its platform throughout.</summary>
        public MetadataChoices GetChoices(Guid gameId)
        {
            return gameId != Guid.Empty && _customData != null && _customData.TryLoad(gameId, out var data)
                ? data?.MetadataChoices?.Clone()
                : null;
        }

        /// <summary>Stores the game's own metadata choices; empty ones are removed, so the game follows its platform.</summary>
        public bool SetChoices(Guid gameId, MetadataChoices choices)
        {
            if (gameId == Guid.Empty || _customData == null)
            {
                return false;
            }

            var next = choices?.IsEmpty() == false ? choices.Clone() : null;
            var current = GetChoices(gameId);
            if (next == null ? current == null : next.SameAs(current))
            {
                return false;
            }

            _customData.Update(
                gameId,
                data => data.MetadataChoices = next,
                affectsSummaryData: false,
                affectsOverrideMirror: false);
            LinkChanged?.Invoke(gameId);
            return true;
        }

        /// <summary>Stores an automatic match unless the user already set this link.</summary>
        public bool SetAuto(Guid gameId, string sourceKey, string id, string label = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            return Write(gameId, sourceKey, existing => existing?.IsUserSet == true
                ? null
                : new MetadataLinkData { Id = id.Trim(), Label = NormalizeLabel(label) });
        }

        /// <summary>
        /// Records that the source's own matching found no page for the game, unless the user set
        /// this link, so later refreshes do not look again; <see cref="Clear"/> lets matching retry.
        /// </summary>
        public bool SetAutoNoMatch(Guid gameId, string sourceKey)
        {
            return Write(gameId, sourceKey, existing => existing?.IsUserSet == true
                ? null
                : new MetadataLinkData { Id = null });
        }

        /// <summary>
        /// Stores the user's link. A blank id stores an explicit "no match", which stops automatic
        /// matching for this source; <see cref="Clear"/> removes the record so matching runs again.
        /// </summary>
        public bool SetUser(Guid gameId, string sourceKey, string id, string label = null)
        {
            return Write(gameId, sourceKey, existing => new MetadataLinkData
            {
                Id = string.IsNullOrWhiteSpace(id) ? null : id.Trim(),
                Label = NormalizeLabel(label),
                IsUserSet = true
            });
        }

        /// <summary>
        /// Sets the user's pairing for one of the game's achievements on a linked source: a source
        /// row's ApiName, an empty string for "never borrow", or null to leave it to matching.
        /// </summary>
        public bool SetPairing(Guid gameId, string sourceKey, string targetApiName, string sourceApiName)
        {
            return string.IsNullOrWhiteSpace(targetApiName)
                ? false
                : SetPairings(gameId, sourceKey, new Dictionary<string, string> { { targetApiName, sourceApiName } });
        }

        /// <summary>Applies several pairing changes in one write; a null value leaves that achievement to matching.</summary>
        public bool SetPairings(Guid gameId, string sourceKey, IReadOnlyDictionary<string, string> changes)
        {
            if (changes == null || changes.Count == 0)
            {
                return false;
            }

            return Write(gameId, sourceKey, existing =>
            {
                if (existing == null || string.IsNullOrWhiteSpace(existing.Id))
                {
                    return null;
                }

                var next = existing.Clone();
                var pairings = next.Pairings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var change in changes)
                {
                    if (string.IsNullOrWhiteSpace(change.Key))
                    {
                        continue;
                    }

                    var target = change.Key.Trim();
                    if (change.Value == null)
                    {
                        pairings.Remove(target);
                    }
                    else
                    {
                        pairings[target] = change.Value.Trim();
                    }
                }

                next.Pairings = pairings.Count > 0 ? pairings : null;
                return next;
            });
        }

        public bool Clear(Guid gameId, string sourceKey)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey) || _customData == null)
            {
                return false;
            }

            var key = sourceKey.Trim();
            if (ReadLinks(gameId)?.ContainsKey(key) != true)
            {
                return false;
            }

            _customData.Update(
                gameId,
                data =>
                {
                    data.MetadataLinks?.Remove(key);
                    if (data.MetadataLinks?.Count == 0)
                    {
                        data.MetadataLinks = null;
                    }
                },
                affectsSummaryData: false,
                affectsOverrideMirror: false);
            LinkChanged?.Invoke(gameId);
            return true;
        }

        /// <summary>The capstone art URL the last overlay recorded for the game, or null.</summary>
        public string GetCapstoneArtUrl(Guid gameId)
        {
            lock (_sync)
            {
                return _applied.TryGetValue(gameId, out var state) ? state?.CapstoneArtUrl : null;
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
            string capstoneDescription = null)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var icon = string.IsNullOrWhiteSpace(iconSourceKey) ? null : iconSourceKey.Trim();
            var art = string.IsNullOrWhiteSpace(capstoneArtUrl) ? null : capstoneArtUrl.Trim();
            var title = string.IsNullOrWhiteSpace(capstoneTitle) ? null : capstoneTitle.Trim();
            var description = string.IsNullOrWhiteSpace(capstoneDescription) ? null : capstoneDescription.Trim();
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
            lock (_sync)
            {
                _applied.TryGetValue(gameId, out var previous);
                iconChanged = !string.Equals(previous?.IconSourceKey, icon, StringComparison.OrdinalIgnoreCase) ||
                              !(previous?.IconApiNames ?? new List<string>()).SequenceEqual(borrowed ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                if (!iconChanged &&
                    string.Equals(previous?.CapstoneArtUrl, art, StringComparison.Ordinal) &&
                    string.Equals(previous?.CapstoneTitle, title, StringComparison.Ordinal) &&
                    string.Equals(previous?.CapstoneDescription, description, StringComparison.Ordinal) &&
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
                    _applied[gameId] = new MetadataAppliedState
                    {
                        IconSourceKey = icon,
                        CapstoneArtUrl = art,
                        CapstoneTitle = art == null ? null : title,
                        CapstoneDescription = art == null ? null : description,
                        Coverage = counts,
                        IconApiNames = borrowed?.Count > 0 ? borrowed : null,
                        // Still the files' state while the borrowed set is unchanged; a changed set
                        // overwrites every file anyway.
                        IconFallbackApiNames = iconChanged ? null : previous?.IconFallbackApiNames
                    };
                }
            }

            Save();
            return iconChanged;
        }

        /// <summary>The borrowed icons that fell back to the game's own at the last refresh.</summary>
        /// <summary>A copy of what the last overlay applied to the game, or null.</summary>
        public MetadataAppliedState GetApplied(Guid gameId)
        {
            lock (_sync)
            {
                if (!_applied.TryGetValue(gameId, out var state) || state == null)
                {
                    return null;
                }

                return new MetadataAppliedState
                {
                    IconSourceKey = state.IconSourceKey,
                    CapstoneArtUrl = state.CapstoneArtUrl,
                    CapstoneTitle = state.CapstoneTitle,
                    CapstoneDescription = state.CapstoneDescription,
                    Coverage = state.Coverage == null ? null : new Dictionary<string, MetadataCoverage>(state.Coverage, StringComparer.OrdinalIgnoreCase),
                    IconApiNames = state.IconApiNames == null ? null : new List<string>(state.IconApiNames),
                    IconFallbackApiNames = state.IconFallbackApiNames == null ? null : new List<string>(state.IconFallbackApiNames)
                };
            }
        }

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
            }

            Save();
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
                    byApiName.Remove(apiName.Trim());
                    if (byApiName.Count == 0)
                    {
                        _capstoneArt.Remove(gameId);
                    }
                }
                else
                {
                    byApiName[apiName.Trim()] = record;
                }
            }

            Save();
        }

        private Dictionary<string, MetadataLinkData> ReadLinks(Guid gameId)
        {
            if (gameId == Guid.Empty || _customData == null)
            {
                return null;
            }

            var links = _customData.TryLoad(gameId, out var data) ? data?.MetadataLinks : null;
            return links == null
                ? null
                : new Dictionary<string, MetadataLinkData>(links, StringComparer.OrdinalIgnoreCase);
        }

        private bool Write(Guid gameId, string sourceKey, Func<MetadataLinkData, MetadataLinkData> build)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey) || _customData == null)
            {
                return false;
            }

            var key = sourceKey.Trim();
            MetadataLinkData existing = null;
            ReadLinks(gameId)?.TryGetValue(key, out existing);
            var next = build(existing);
            if (next == null)
            {
                return false;
            }

            // Pairings belong to the link they were made against: kept while it stays the same.
            if (next.Pairings == null && existing?.Pairings != null &&
                string.Equals(existing.Id, next.Id, StringComparison.Ordinal))
            {
                next.Pairings = new Dictionary<string, string>(existing.Pairings, StringComparer.OrdinalIgnoreCase);
            }

            if (existing != null &&
                existing.IsUserSet == next.IsUserSet &&
                string.Equals(existing.Id, next.Id, StringComparison.Ordinal) &&
                string.Equals(existing.Label, next.Label, StringComparison.Ordinal) &&
                SamePairings(existing.Pairings, next.Pairings))
            {
                return false;
            }

            _customData.Update(
                gameId,
                data =>
                {
                    data.MetadataLinks = data.MetadataLinks ??
                                         new Dictionary<string, MetadataLinkData>(StringComparer.OrdinalIgnoreCase);
                    data.MetadataLinks[key] = next;
                },
                affectsSummaryData: false,
                affectsOverrideMirror: false);
            LinkChanged?.Invoke(gameId);
            return true;
        }

        private static MetadataLinkRecord ToRecord(MetadataLinkData link)
        {
            return new MetadataLinkRecord
            {
                Id = link.Id,
                Label = link.Label,
                Origin = link.IsUserSet ? MetadataLinkOrigin.User : MetadataLinkOrigin.Auto,
                Pairings = new Dictionary<string, string>(
                    link.Pairings ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static bool SamePairings(Dictionary<string, string> previous, Dictionary<string, string> next)
        {
            if ((previous?.Count ?? 0) != (next?.Count ?? 0))
            {
                return false;
            }

            return next == null || next.All(pair =>
                previous.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
        }

        private static string NormalizeLabel(string label)
        {
            return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
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

        private void Load()
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return;
            }

            try
            {
                var parsed = JsonConvert.DeserializeObject<AppliedFile>(File.ReadAllText(_path, Encoding.UTF8));
                lock (_sync)
                {
                    _applied = (parsed?.Applied ?? new Dictionary<Guid, MetadataAppliedState>())
                        .Where(pair => pair.Key != Guid.Empty && pair.Value != null)
                        .ToDictionary(pair => pair.Key, pair => NormalizeLoaded(pair.Value));
                    _capstoneArt = (parsed?.CapstoneArt ?? new Dictionary<Guid, Dictionary<string, CapstoneArtRecord>>())
                        .Where(pair => pair.Key != Guid.Empty && pair.Value != null)
                        .ToDictionary(
                            pair => pair.Key,
                            pair => new Dictionary<string, CapstoneArtRecord>(pair.Value, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] Failed to read the metadata applied state; starting empty: {_path}");
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

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            string json;
            lock (_sync)
            {
                json = JsonConvert.SerializeObject(
                    new AppliedFile { Applied = _applied, CapstoneArt = _capstoneArt },
                    Formatting.None);
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
                _logger?.Warn(ex, $"[Metadata] Failed to save the metadata applied state: {_path}");
            }
        }
    }
}
