using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Copies achievement metadata from secondary sources onto a game another provider services,
    /// field by field in the user's preference order. Runs on the refreshed payload before it is
    /// cached, so turning a source off reverts on the next refresh and user overrides, applied at
    /// read time, stay on top.
    /// </summary>
    internal sealed class MetadataOverlay
    {
        private readonly Func<IReadOnlyList<IMetadataProvider>> _providers;
        private readonly Func<PersistedSettings> _settings;
        private readonly MetadataLinkStore _links;
        private readonly ILogger _logger;
        private readonly Func<string, CancellationToken, Task<int>> _measureIconWidth;

        /// <param name="measureIconWidth">
        /// Reads an icon's pixel width (0 when unknown) so a source's icons replace only smaller
        /// ones. Without it, matched icons are always replaced.
        /// </param>
        public MetadataOverlay(
            Func<IReadOnlyList<IMetadataProvider>> providers,
            Func<PersistedSettings> settings,
            MetadataLinkStore links,
            ILogger logger,
            Func<string, CancellationToken, Task<int>> measureIconWidth = null)
        {
            _providers = providers;
            _settings = settings;
            _links = links;
            _logger = logger;
            _measureIconWidth = measureIconWidth;
        }

        public MetadataLinkStore Links => _links;

        /// <summary>Starts one refresh run's overlay; dispose it when the run ends to close provider sessions.</summary>
        public MetadataOverlayRun BeginRun()
        {
            return new MetadataOverlayRun(
                _providers?.Invoke() ?? Array.Empty<IMetadataProvider>(),
                _settings?.Invoke(),
                _links,
                _logger,
                _measureIconWidth);
        }
    }

    internal sealed class MetadataOverlayRun : IDisposable
    {
        /// <summary>Share of a game's achievements a name-found link must match to be kept.</summary>
        internal const double MinNameMatchShare = 0.5;

        private readonly Dictionary<string, IMetadataProvider> _providersByKey;
        private readonly PersistedSettings _settings;
        private readonly MetadataLinkStore _links;
        private readonly ILogger _logger;
        private readonly Func<string, CancellationToken, Task<int>> _measureIconWidth;
        private readonly object _sessionSync = new object();
        private readonly Dictionary<string, Task<bool>> _sessions =
            new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

        internal MetadataOverlayRun(
            IReadOnlyList<IMetadataProvider> providers,
            PersistedSettings settings,
            MetadataLinkStore links,
            ILogger logger,
            Func<string, CancellationToken, Task<int>> measureIconWidth = null)
        {
            _measureIconWidth = measureIconWidth;
            _providersByKey = (providers ?? Array.Empty<IMetadataProvider>())
                .Where(provider => provider != null && !string.IsNullOrWhiteSpace(provider.Key))
                .GroupBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            _settings = settings;
            _links = links;
            _logger = logger;
        }

        /// <summary>
        /// Applies the enabled metadata fields to the payload. Returns true when the game's icon
        /// source changed since the last overlay, so cached icon files must be overwritten.
        /// </summary>
        public async Task<bool> ApplyAsync(Game game, GameAchievementData data, CancellationToken ct)
        {
            var gameId = data?.PlayniteGameId ?? game?.Id ?? Guid.Empty;
            if (gameId == Guid.Empty || data?.Achievements == null || data.Achievements.Count == 0)
            {
                return false;
            }

            var request = CreateRequest(game, data, gameId);
            var plan = BuildPlan(request.ServicingProviderKey);
            if (plan.IsEmpty)
            {
                return _links?.RecordApplied(gameId, null, null) ?? false;
            }

            var sets = new Dictionary<string, MatchedSet>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in plan.AllKeys)
            {
                ct.ThrowIfCancellationRequested();
                var matched = await LoadMatchedSetAsync(key, gameId, request, data.Achievements, ct).ConfigureAwait(false);
                if (matched != null)
                {
                    sets[key] = matched;
                }
            }

            var borrowedIconApiNames = new List<string>();
            var iconKeys = await KeepLargerIconSourcesAsync(plan.IconKeys, sets, request.Game?.Name, ct).ConfigureAwait(false);
            var iconSource = ApplyIcons(iconKeys, sets, borrowedIconApiNames);
            ApplyCategories(plan.CategoryKeys, plan.CategoryMode, sets);
            var capstoneArt = ResolveCapstoneArt(plan.CapstoneArtKeys, sets);
            var coverage = sets.ToDictionary(
                pair => pair.Key,
                pair => new MetadataCoverage { Matched = pair.Value.Matches.Count, Total = data.Achievements.Count },
                StringComparer.OrdinalIgnoreCase);

            return _links?.RecordApplied(gameId, iconSource, capstoneArt, coverage, borrowedIconApiNames) ?? false;
        }

        public void Dispose()
        {
            List<string> begun;
            lock (_sessionSync)
            {
                begun = _sessions.Keys.ToList();
                _sessions.Clear();
            }

            foreach (var key in begun)
            {
                try
                {
                    _providersByKey[key].EndRun();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[Metadata] Ending the '{key}' session failed.");
                }
            }
        }

        private OverlayPlan BuildPlan(string servicingProviderKey)
        {
            bool Usable(string key, MetadataField field) =>
                _providersByKey.TryGetValue(key, out var provider) &&
                (provider.SupportedFields & field) == field &&
                !provider.IsNativeFor(servicingProviderKey);

            List<string> Chosen(string key, MetadataField field) =>
                !string.IsNullOrWhiteSpace(key) && Usable(key.Trim(), field)
                    ? new List<string> { key.Trim() }
                    : new List<string>();

            var choices = (_settings ?? new PersistedSettings()).ResolveMetadataChoices(servicingProviderKey);
            return new OverlayPlan(
                Chosen(choices.Icons, MetadataField.Icons),
                Chosen(choices.Categories, MetadataField.Categories),
                choices.CategoryMode ?? MetadataCategoryMode.Merge,
                Chosen(choices.CapstoneArt, MetadataField.CapstoneArt));
        }

        /// <summary>
        /// What matching one game against one source would do, for the Metadata tab's test. Runs
        /// the same lookups as a refresh but stores nothing and changes no data.
        /// </summary>
        public async Task<MetadataProbeResult> ProbeAsync(Game game, GameAchievementData data, string sourceKey, CancellationToken ct)
        {
            var result = new MetadataProbeResult { Total = data?.Achievements?.Count ?? 0 };
            var gameId = data?.PlayniteGameId ?? game?.Id ?? Guid.Empty;
            if (gameId == Guid.Empty || result.Total == 0 || string.IsNullOrWhiteSpace(sourceKey) ||
                !_providersByKey.TryGetValue(sourceKey, out var provider))
            {
                result.Outcome = MetadataProbeOutcome.Skipped;
                return result;
            }

            if (provider.IsNativeFor(data.ProviderKey))
            {
                result.Outcome = MetadataProbeOutcome.Native;
                return result;
            }

            result.Outcome = MetadataProbeOutcome.NotFound;
            await LoadMatchedSetAsync(sourceKey, gameId, CreateRequest(game, data, gameId), data.Achievements, ct, result)
                .ConfigureAwait(false);
            return result;
        }

        /// <summary>A link request carrying the labels of the game's saved links as alternate names.</summary>
        private MetadataLinkRequest CreateRequest(Game game, GameAchievementData data, Guid gameId)
        {
            return new MetadataLinkRequest(game, data)
            {
                AlternateNames = (_links?.GetAll(gameId)?.Values ?? Enumerable.Empty<MetadataLinkRecord>())
                    .Select(link => link?.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .ToList()
            };
        }

        /// <param name="probe">When set, a dry run: nothing is stored and the outcome is written here.</param>
        private async Task<MatchedSet> LoadMatchedSetAsync(
            string key,
            Guid gameId,
            MetadataLinkRequest request,
            IList<AchievementDetail> targets,
            CancellationToken ct,
            MetadataProbeResult probe = null)
        {
            var provider = _providersByKey[key];
            try
            {
                if (!await BeginSessionAsync(provider, ct).ConfigureAwait(false))
                {
                    if (probe != null)
                    {
                        probe.Outcome = MetadataProbeOutcome.SourceUnavailable;
                    }

                    return null;
                }

                var (linkId, unconfirmed, saved) = await ResolveLinkIdAsync(provider, gameId, request, ct, persist: probe == null)
                    .ConfigureAwait(false);
                if (probe != null)
                {
                    probe.LinkId = linkId;
                    probe.Label = unconfirmed?.Label;
                    probe.FromSavedLink = saved;
                    probe.FoundByName = unconfirmed != null;
                }

                if (string.IsNullOrWhiteSpace(linkId))
                {
                    return null;
                }

                var set = await provider.FetchAsync(request, linkId, ct).ConfigureAwait(false);
                if (set == null || set.Achievements.Count == 0)
                {
                    if (probe != null)
                    {
                        probe.Outcome = MetadataProbeOutcome.FetchFailed;
                    }

                    return null;
                }

                // A port that reused the developer's achievement IDs pairs exactly by ID, in any
                // language; titles only decide what the IDs leave unmatched.
                var overlap = set.ApiNamesMayBeShared
                    ? CrossSourceAchievementMatcher.ApiNameOverlap(targets, set.Achievements)
                    : 0;
                var matchApiNames = set.SharesApiNames || overlap >= CrossSourceAchievementMatcher.SharedApiNameThreshold;
                var matches = CrossSourceAchievementMatcher.Match(targets, set.Achievements, matchApiNames: matchApiNames);
                _logger?.Debug(
                    $"[Metadata] {key} matched {matches.Count}/{targets.Count} achievements for '{request.Game?.Name}' " +
                    $"({linkId}, idOverlap={overlap:P0}, byId={matchApiNames}).");

                if (probe != null)
                {
                    probe.Matched = matches.Count;
                    probe.ByAchievementId = matchApiNames;
                }

                // A game found by name is a different release when too few achievements line up
                // ("Thief: Definitive Edition" against "Thief Gold"), so it is neither kept nor used.
                // Measured against the shorter list: a console version with fewer trophies than
                // the PC version's achievements can still match all of its own.
                if (unconfirmed != null)
                {
                    if (matches.Count < Math.Min(targets.Count, set.Achievements.Count) * MinNameMatchShare)
                    {
                        _logger?.Debug(
                            $"[Metadata] {key} name match '{unconfirmed.Label ?? linkId}' rejected for '{request.Game?.Name}': " +
                            $"{matches.Count}/{targets.Count} achievements matched.");
                        if (probe != null)
                        {
                            probe.Outcome = MetadataProbeOutcome.Rejected;
                        }

                        return null;
                    }

                    if (probe == null)
                    {
                        _links?.SetAuto(gameId, provider.Key, linkId, unconfirmed.Label);
                    }
                }

                if (probe != null)
                {
                    probe.Outcome = MetadataProbeOutcome.Matched;
                }

                return new MatchedSet(set, matches);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] {key} failed for '{request.Game?.Name}'; provider data kept.");
                if (probe != null)
                {
                    probe.Outcome = MetadataProbeOutcome.FetchFailed;
                }

                return null;
            }
        }

        private Task<bool> BeginSessionAsync(IMetadataProvider provider, CancellationToken ct)
        {
            lock (_sessionSync)
            {
                if (!_sessions.TryGetValue(provider.Key, out var session))
                {
                    session = provider.BeginRunAsync(ct);
                    _sessions[provider.Key] = session;
                }

                return session;
            }
        }

        /// <summary>
        /// The stored link, or a newly resolved one. One resolved from an id the game carries is
        /// stored at once (unless <paramref name="persist"/> is false); one found by name comes back
        /// as <c>Unconfirmed</c> and is stored only after its achievements match.
        /// </summary>
        private async Task<(string Id, MetadataSourceLink Unconfirmed, bool Saved)> ResolveLinkIdAsync(
            IMetadataProvider provider,
            Guid gameId,
            MetadataLinkRequest request,
            CancellationToken ct,
            bool persist = true)
        {
            if (_links != null && _links.TryGet(gameId, provider.Key, out var stored))
            {
                return (stored.IsNoMatch ? null : stored.Id, null, true);
            }

            var resolved = await provider.ResolveLinkAsync(request, ct).ConfigureAwait(false);
            if (resolved == null || string.IsNullOrWhiteSpace(resolved.Id))
            {
                return (null, null, false);
            }

            if (resolved.FoundByName)
            {
                return (resolved.Id.Trim(), resolved, false);
            }

            if (persist)
            {
                _links?.SetAuto(gameId, provider.Key, resolved.Id, resolved.Label);
            }

            return (resolved.Id.Trim(), null, false);
        }

        /// <summary>
        /// The icon sources whose icons are larger than the game's own, judged by one matched
        /// pair per source: a source's icons share one size, and so do a provider's. A source is
        /// kept when either width cannot be read, as it was before sizes were compared.
        /// </summary>
        private async Task<IReadOnlyList<string>> KeepLargerIconSourcesAsync(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets,
            string gameName,
            CancellationToken ct)
        {
            if (_measureIconWidth == null)
            {
                return keys;
            }

            var kept = new List<string>();
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set))
                {
                    continue;
                }

                var sample = set.Matches.FirstOrDefault(match =>
                    FirstNonBlank(match.Source.UnlockedIconPath, match.Source.LockedIconPath) != null &&
                    FirstNonBlank(match.Target.UnlockedIconPath, match.Target.LockedIconPath) != null);
                if (sample.Source != null)
                {
                    var sourceWidth = await _measureIconWidth(
                        FirstNonBlank(sample.Source.UnlockedIconPath, sample.Source.LockedIconPath), ct).ConfigureAwait(false);
                    var ownWidth = await _measureIconWidth(
                        FirstNonBlank(sample.Target.UnlockedIconPath, sample.Target.LockedIconPath), ct).ConfigureAwait(false);
                    if (sourceWidth > 0 && ownWidth > 0 && sourceWidth <= ownWidth)
                    {
                        _logger?.Debug($"[Metadata] {key} icons ({sourceWidth}px) not larger than '{gameName}' icons ({ownWidth}px); kept own.");
                        continue;
                    }
                }

                kept.Add(key);
            }

            return kept;
        }

        /// <summary>
        /// Replaces each achievement's icons with the first preferred source that matched it,
        /// adding the ApiName of each one replaced to <paramref name="replacedApiNames"/>.
        /// </summary>
        private static string ApplyIcons(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets,
            ICollection<string> replacedApiNames)
        {
            string firstApplied = null;
            var done = new HashSet<AchievementDetail>();
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set))
                {
                    continue;
                }

                foreach (var match in set.Matches)
                {
                    var unlocked = FirstNonBlank(match.Source.UnlockedIconPath, match.Source.LockedIconPath);
                    if (unlocked == null || !done.Add(match.Target))
                    {
                        continue;
                    }

                    match.Target.UnlockedIconPath = unlocked;
                    match.Target.LockedIconPath = string.IsNullOrWhiteSpace(match.Source.LockedIconPath)
                        ? null
                        : match.Source.LockedIconPath;
                    replacedApiNames.Add(match.Target.ApiName);
                    firstApplied = firstApplied ?? key;
                }
            }

            return firstApplied;
        }

        /// <summary>
        /// Applies each achievement's category and types from the first source that matched it.
        /// Merge keeps a category the provider named and adds the source's types; Override replaces both.
        /// </summary>
        private static void ApplyCategories(
            IReadOnlyList<string> keys,
            MetadataCategoryMode mode,
            IReadOnlyDictionary<string, MatchedSet> sets)
        {
            var done = new HashSet<AchievementDetail>();
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set))
                {
                    continue;
                }

                foreach (var match in set.Matches)
                {
                    if (!done.Add(match.Target))
                    {
                        continue;
                    }

                    ApplyCategory(match.Target, match.Source, mode);
                }
            }
        }

        internal static void ApplyCategory(AchievementDetail target, AchievementDetail source, MetadataCategoryMode mode)
        {
            var sourceCategory = AchievementCategoryTypeHelper.NormalizeCategory(source.Category);
            var sourceType = AchievementCategoryTypeHelper.Normalize(source.CategoryType);
            if (mode == MetadataCategoryMode.Override)
            {
                target.Category = sourceCategory;
                target.CategoryType = sourceType;
                return;
            }

            if (AchievementCategoryTypeHelper.NormalizeCategory(target.Category) == null)
            {
                target.Category = sourceCategory;
            }

            target.CategoryType = AchievementCategoryTypeHelper.Combine(
                AchievementCategoryTypeHelper.ParseValues(target.CategoryType)
                    .Concat(AchievementCategoryTypeHelper.ParseValues(sourceType)));
        }

        /// <summary>The first preferred source's capstone (platinum) icon, when that source matched the game.</summary>
        private static string ResolveCapstoneArt(IReadOnlyList<string> keys, IReadOnlyDictionary<string, MatchedSet> sets)
        {
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set))
                {
                    continue;
                }

                var capstone = set.Set.Achievements.FirstOrDefault(row =>
                    row != null &&
                    (row.IsCapstone || string.Equals(row.TrophyType, "platinum", StringComparison.OrdinalIgnoreCase)));
                var art = FirstNonBlank(capstone?.UnlockedIconPath, capstone?.LockedIconPath);
                if (art != null)
                {
                    return art;
                }
            }

            return null;
        }

        private static string FirstNonBlank(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private sealed class MatchedSet
        {
            public MatchedSet(AchievementSourceSet set, List<AchievementMatch> matches)
            {
                Set = set;
                Matches = matches;
            }

            public AchievementSourceSet Set { get; }

            public List<AchievementMatch> Matches { get; }
        }

        private sealed class OverlayPlan
        {
            public OverlayPlan(
                List<string> iconKeys,
                List<string> categoryKeys,
                MetadataCategoryMode categoryMode,
                List<string> capstoneArtKeys)
            {
                IconKeys = iconKeys;
                CategoryKeys = categoryKeys;
                CategoryMode = categoryMode;
                CapstoneArtKeys = capstoneArtKeys;
                AllKeys = iconKeys.Concat(categoryKeys).Concat(capstoneArtKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            public List<string> IconKeys { get; }

            public List<string> CategoryKeys { get; }

            public MetadataCategoryMode CategoryMode { get; }

            public List<string> CapstoneArtKeys { get; }

            public List<string> AllKeys { get; }

            public bool IsEmpty => AllKeys.Count == 0;
        }
    }
}
