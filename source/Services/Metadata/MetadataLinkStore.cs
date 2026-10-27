using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>ApiName of the game's own achievement the borrowed capstone matched, or null.</summary>
        [JsonProperty("capstoneMatch", NullValueHandling = NullValueHandling.Ignore)]
        public string CapstoneMatchApiName { get; set; }

        /// <summary>
        /// For a link to several lists (a collection's games), each list's platinum filed under the
        /// category its list matched; null otherwise. The game then has no whole-game platinum.
        /// </summary>
        [JsonProperty("categoryCapstones", NullValueHandling = NullValueHandling.Ignore)]
        public List<MetadataCategoryCapstone> CategoryCapstones { get; set; }

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

    /// <summary>One game of a collection's platinum, borrowed for the category its list matched.</summary>
    public sealed class MetadataCategoryCapstone
    {
        [JsonProperty("category")]
        public string Category { get; set; }

        /// <summary>The platinum's image URL.</summary>
        [JsonProperty("art")]
        public string ArtUrl { get; set; }

        [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)]
        public string Title { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        /// <summary>ApiName of the game's own achievement in the category that the platinum matched, or null.</summary>
        [JsonProperty("match", NullValueHandling = NullValueHandling.Ignore)]
        public string MatchApiName { get; set; }
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
    /// source key. Links are stored with the game's custom data so they travel with exports and the
    /// Workshop. What the last overlay applied, and the capstone art records, describe this
    /// machine's files; they are kept by <see cref="MetadataStateStore"/>, which this store reads through.
    /// </summary>
    public sealed class MetadataLinkStore
    {

        // A game's custom data is read, changed and saved whole, so link writes for one game (the
        // overlay's sources answer at the same time) go one at a time or one would be lost.
        private readonly object _writeSync = new object();
        private readonly GameCustomDataStore _customData;
        private readonly ILogger _logger;
        private readonly MetadataStateStore _state;

        /// <summary>Raised after a link is added, changed or removed, with the game id.</summary>
        public event Action<Guid> LinkChanged;

        /// <summary>Each source's list and matches from the last overlay; null without a state store.</summary>
        public MetadataSourceSnapshotStore Snapshots => _state?.Snapshots;

        /// <param name="state">
        /// Where the applied state, capstone art records and source lists are kept; the links
        /// themselves stay in <paramref name="customData"/>.
        /// </param>
        internal MetadataLinkStore(GameCustomDataStore customData, MetadataStateStore state, ILogger logger)
        {
            _customData = customData;
            _state = state;
            _logger = logger;
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
            lock (_writeSync)
            {
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
            }

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
        /// Replaces the id of an automatic link that the source extended, keeping its label and
        /// pairings; a stored no-match or a missing link is left alone, and so is a user's link
        /// unless <paramref name="includeUser"/>, when it stays the user's.
        /// </summary>
        public bool ExtendAuto(Guid gameId, string sourceKey, string id, bool includeUser = false)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            return Write(gameId, sourceKey, existing => existing == null || (existing.IsUserSet && !includeUser) || string.IsNullOrWhiteSpace(existing.Id)
                ? null
                : new MetadataLinkData
                {
                    Id = id.Trim(),
                    Label = existing.Label,
                    IsUserSet = existing.IsUserSet,
                    Pairings = existing.Pairings == null
                        ? null
                        : new Dictionary<string, string>(existing.Pairings, StringComparer.OrdinalIgnoreCase)
                });
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
            lock (_writeSync)
            {
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
            }

            LinkChanged?.Invoke(gameId);
            return true;
        }

        /// <summary>The capstone art URL the last overlay recorded for the game, or null.</summary>
        public string GetCapstoneArtUrl(Guid gameId) => _state?.GetCapstoneArtUrl(gameId);

        /// <summary>ApiName of the game's achievement the last overlay matched to the borrowed capstone, or null.</summary>
        public string GetCapstoneMatch(Guid gameId) => _state?.GetCapstoneMatch(gameId);

        /// <summary>The borrowed capstone title and description the last overlay recorded, or nulls.</summary>
        public (string Title, string Description) GetCapstoneText(Guid gameId) =>
            _state?.GetCapstoneText(gameId) ?? (null, null);

        /// <summary>Each collection game's borrowed platinum by category, as the last overlay recorded them; empty when none.</summary>
        public IReadOnlyList<MetadataCategoryCapstone> GetCategoryCapstones(Guid gameId) =>
            _state?.GetCategoryCapstones(gameId) ?? Array.Empty<MetadataCategoryCapstone>();

        /// <summary>
        /// Records what an overlay applied to the game. Returns true when the game's icons changed
        /// since the last record, so the caller re-downloads icons kept under the same file names.
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
            IReadOnlyList<MetadataCategoryCapstone> categoryCapstones = null) =>
            _state?.RecordApplied(gameId, iconSourceKey, capstoneArtUrl, coverage, iconApiNames, capstoneTitle, capstoneDescription, capstoneMatchApiName, categoryCapstones) ?? false;

        /// <summary>A copy of what the last overlay applied to the game, or null.</summary>
        public MetadataAppliedState GetApplied(Guid gameId) => _state?.GetApplied(gameId);

        /// <summary>The borrowed icons that fell back to the game's own at the last refresh.</summary>
        public IReadOnlyCollection<string> GetIconFallbacks(Guid gameId) =>
            _state?.GetIconFallbacks(gameId) ?? Array.Empty<string>();

        /// <summary>Records which borrowed icons fell back to the game's own, returning the ones recorded before.</summary>
        public IReadOnlyCollection<string> ReplaceIconFallbacks(Guid gameId, IEnumerable<string> apiNames) =>
            _state?.ReplaceIconFallbacks(gameId, apiNames) ?? Array.Empty<string>();

        // The search inputs each automatic link was found from, kept beside the sources' page records
        // in the cache database: one record per game, one "site" per source key.
        private const string LinkBasisStoreKey = "MetadataLinkBasis";
        private const string LinkBasisName = "basis";

        /// <summary>
        /// What the source's last automatic lookup for the game searched from (see
        /// <see cref="MetadataOverlay"/>), or null when none is recorded.
        /// </summary>
        public string GetLinkBasis(Guid gameId, string sourceKey) =>
            _state != null && !string.IsNullOrWhiteSpace(sourceKey) &&
            _state.Pages(LinkBasisStoreKey).TryGet(gameId, LinkBasisName, sourceKey.Trim(), out var basis)
                ? basis
                : null;

        /// <summary>Records what the source's automatic lookup for the game searched from.</summary>
        public void SetLinkBasis(Guid gameId, string sourceKey, string basis)
        {
            if (_state != null && !string.IsNullOrWhiteSpace(sourceKey) && !string.IsNullOrWhiteSpace(basis))
            {
                _state.Pages(LinkBasisStoreKey).Set(gameId, LinkBasisName, sourceKey.Trim(), basis);
            }
        }

        /// <summary>A source's match count for the game at the last overlay, or null when it did not run.</summary>
        public MetadataCoverage GetCoverage(Guid gameId, string sourceKey) => _state?.GetCoverage(gameId, sourceKey);

        /// <summary>The source key whose icon the last overlay gave this achievement, or null for its own.</summary>
        public string GetBorrowedIconSource(Guid gameId, string apiName) => _state?.GetBorrowedIconSource(gameId, apiName);

        /// <summary>
        /// The stored file of the icon the last overlay gave this achievement, read from the
        /// source's snapshot; null for an achievement showing its own icon or a file that is gone.
        /// </summary>
        public string GetBorrowedIconPath(Guid gameId, string apiName)
        {
            var sourceKey = GetBorrowedIconSource(gameId, apiName);
            var snapshots = Snapshots;
            if (string.IsNullOrWhiteSpace(sourceKey) || snapshots == null)
            {
                return null;
            }

            var snapshot = snapshots.Get(gameId, sourceKey, snapshots.GetLinkId(gameId, sourceKey));
            if (snapshot?.Matches == null || !snapshot.Matches.TryGetValue(apiName.Trim(), out var sourceApiName))
            {
                return null;
            }

            var path = MetadataSourceSnapshotStore.LocalIconPath(snapshot.Rows?
                .FirstOrDefault(row => string.Equals(row?.ApiName, sourceApiName, StringComparison.OrdinalIgnoreCase))?
                .Icon);
            return path != null && System.IO.File.Exists(path) ? path : null;
        }

        /// <summary>The art the plugin last wrote for an auto capstone, or null.</summary>
        public CapstoneArtRecord GetCapstoneArtRecord(Guid gameId, string apiName) => _state?.GetCapstoneArtRecord(gameId, apiName);

        /// <summary>Records (or, with null, forgets) the art the plugin wrote for an auto capstone.</summary>
        public void SetCapstoneArtRecord(Guid gameId, string apiName, CapstoneArtRecord record) =>
            _state?.SetCapstoneArtRecord(gameId, apiName, record);

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
            lock (_writeSync)
            {
                MetadataLinkData existing = null;
                ReadLinks(gameId)?.TryGetValue(key, out existing);
                var next = build(existing);
                if (next == null)
                {
                    return false;
                }

                // Pairings belong to the lists they were made against: kept while the link still
                // names every one of them, so adding a collection's list or reordering keeps them.
                if (next.Pairings == null && existing?.Pairings != null &&
                    (string.Equals(existing.Id, next.Id, StringComparison.Ordinal) ||
                     MetadataLinkIds.ContainsAll(next.Id, existing.Id)))
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
            }

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
    }
}
