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
        private readonly CategoryArtDownload _downloadCategoryArt;

        /// <param name="downloadCategoryArt">
        /// Saves a source's category art for the categories it gave a game; null keeps none.
        /// </param>
        public MetadataOverlay(
            Func<IReadOnlyList<IMetadataProvider>> providers,
            Func<PersistedSettings> settings,
            MetadataLinkStore links,
            ILogger logger,
            CategoryArtDownload downloadCategoryArt = null)
        {
            _providers = providers;
            _settings = settings;
            _links = links;
            _logger = logger;
            _downloadCategoryArt = downloadCategoryArt;
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
                _downloadCategoryArt);
        }
    }

    /// <summary>Saves category art (label, image address) as a game's default category images.</summary>
    internal delegate Task CategoryArtDownload(
        Guid gameId,
        IReadOnlyList<(string Label, string ImageUrl)> art,
        CancellationToken ct);

    internal sealed class MetadataOverlayRun : IDisposable
    {
        /// <summary>Share of a game's achievements a name-found link must match to be kept.</summary>
        internal const double MinNameMatchShare = 0.5;

        private readonly Dictionary<string, IMetadataProvider> _providersByKey;
        private readonly PersistedSettings _settings;
        private readonly MetadataLinkStore _links;
        private readonly ILogger _logger;
        private readonly object _sessionSync = new object();
        private readonly Dictionary<string, Task<bool>> _sessions =
            new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Dictionary<string, OwnIconPaths>> _ownIcons =
            new System.Collections.Concurrent.ConcurrentDictionary<Guid, Dictionary<string, OwnIconPaths>>();

        internal MetadataOverlayRun(
            IReadOnlyList<IMetadataProvider> providers,
            PersistedSettings settings,
            MetadataLinkStore links,
            ILogger logger,
            CategoryArtDownload downloadCategoryArt = null)
        {
            _providersByKey = (providers ?? Array.Empty<IMetadataProvider>())
                .Where(provider => provider != null && !string.IsNullOrWhiteSpace(provider.Key))
                .GroupBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            _settings = settings;
            _links = links;
            _logger = logger;
            _downloadCategoryArt = downloadCategoryArt;
        }

        private readonly CategoryArtDownload _downloadCategoryArt;

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
            var plan = BuildPlan(request.ServicingProviderKey, _links?.GetChoices(gameId));
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

            var ownIcons = new Dictionary<string, OwnIconPaths>(StringComparer.OrdinalIgnoreCase);
            var iconSource = ApplyIcons(plan.IconKeys, sets, ownIcons);
            var borrowedIconApiNames = ownIcons.Keys.ToList();
            if (ownIcons.Count > 0)
            {
                _ownIcons[gameId] = ownIcons;
            }
            ApplyCategories(plan.CategoryKeys, plan.CategoryMode, sets);
            var categoryArt = CollectCategoryArt(plan.CategoryKeys, sets);
            if (categoryArt.Count > 0 && _downloadCategoryArt != null)
            {
                await _downloadCategoryArt(gameId, categoryArt, ct).ConfigureAwait(false);
            }

            // A borrowed platinum type only labels the trophy; capstones keep their own rules.
            ApplyField(
                plan.TrophyTypeKeys,
                sets,
                source => !string.IsNullOrWhiteSpace(source.TrophyType),
                (target, source) => target.TrophyType = source.TrophyType);
            ApplyField(
                plan.PointsKeys,
                sets,
                source => source.Points.HasValue,
                (target, source) => target.Points = source.Points);
            var capstone = ResolveCapstone(plan.CapstoneArtKeys, sets);
            var capstoneArt = FirstNonBlank(capstone?.UnlockedIconPath, capstone?.LockedIconPath);
            // A source that supplies achievement data is judged, and shown, by how many of the
            // game's achievements it matched; one used only for the auto capstone by its platinum.
            var coverage = new Dictionary<string, MetadataCoverage>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sets)
            {
                var perAchievement = plan.PerAchievementKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);
                if (perAchievement)
                {
                    coverage[pair.Key] = new MetadataCoverage { Matched = pair.Value.Matches.Count, Total = data.Achievements.Count };
                    _links?.Snapshots?.Record(
                        gameId,
                        pair.Key,
                        pair.Value.Set.LinkId,
                        pair.Value.Set.Achievements,
                        pair.Value.Matches.Select(match => new KeyValuePair<string, string>(match.Target.ApiName, match.Source.ApiName)));
                }
                else
                {
                    var hasPlatinum = ResolveCapstone(new[] { pair.Key }, sets) != null;
                    coverage[pair.Key] = new MetadataCoverage { Matched = hasPlatinum ? 1 : 0, Total = 1 };
                    _links?.Snapshots?.Forget(gameId, pair.Key);
                }
            }

            return _links?.RecordApplied(
                gameId,
                iconSource,
                capstoneArt,
                coverage,
                borrowedIconApiNames,
                capstone?.DisplayName,
                capstone?.Description) ?? false;
        }

        /// <summary>
        /// After the game's icon download: the borrowed icons that fell back to the game's own on an
        /// earlier refresh get their borrowed source back, because their files still hold the own
        /// icon and a download that reuses files would keep it. Returns their ApiNames, to download
        /// again with files overwritten.
        /// </summary>
        public ISet<string> ResetEarlierIconFallbacks(GameAchievementData data)
        {
            var reset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gameId = data?.PlayniteGameId ?? Guid.Empty;
            if (gameId == Guid.Empty || data.Achievements == null || !_ownIcons.TryGetValue(gameId, out var ownIcons))
            {
                return reset;
            }

            var earlier = new HashSet<string>(_links?.GetIconFallbacks(gameId) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in data.Achievements)
            {
                if (achievement?.ApiName == null ||
                    !earlier.Contains(achievement.ApiName) ||
                    !ownIcons.TryGetValue(achievement.ApiName, out var paths))
                {
                    continue;
                }

                achievement.UnlockedIconPath = paths.BorrowedUnlocked;
                achievement.LockedIconPath = paths.BorrowedLocked;
                reset.Add(achievement.ApiName);
            }

            return reset;
        }

        /// <summary>
        /// Puts the game's own icons back on each achievement whose borrowed icon did not download,
        /// so it shows what it would without the source, and records them so a later refresh tries
        /// the borrowed icon again. Call once per game, after its icon downloads. Returns the
        /// ApiNames put back, to download with files overwritten.
        /// </summary>
        public ISet<string> RestoreOwnIconsWhereDownloadFailed(GameAchievementData data, Func<string, bool> isUndownloaded)
        {
            var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gameId = data?.PlayniteGameId ?? Guid.Empty;
            if (gameId == Guid.Empty || data.Achievements == null)
            {
                return restored;
            }

            if (_ownIcons.TryRemove(gameId, out var ownIcons))
            {
                foreach (var achievement in data.Achievements)
                {
                    if (achievement?.ApiName == null ||
                        !ownIcons.TryGetValue(achievement.ApiName, out var paths) ||
                        !isUndownloaded(achievement.UnlockedIconPath))
                    {
                        continue;
                    }

                    achievement.UnlockedIconPath = paths.Unlocked;
                    achievement.LockedIconPath = paths.Locked;
                    restored.Add(achievement.ApiName);
                }
            }

            _links?.ReplaceIconFallbacks(gameId, restored);
            return restored;
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

        private OverlayPlan BuildPlan(string servicingProviderKey, MetadataChoices gameChoices)
        {
            bool Usable(string key, MetadataField field) =>
                _providersByKey.TryGetValue(key, out var provider) &&
                (provider.SupportedFields & field) == field &&
                !provider.IsNativeFor(servicingProviderKey);

            List<string> Chosen(string key, MetadataField field) =>
                !string.IsNullOrWhiteSpace(key) && Usable(key.Trim(), field)
                    ? new List<string> { key.Trim() }
                    : new List<string>();

            var choices = (_settings ?? new PersistedSettings()).ResolveMetadataChoices(servicingProviderKey, gameChoices);
            return new OverlayPlan(
                Chosen(choices.Icons, MetadataField.Icons),
                Chosen(choices.Categories, MetadataField.Categories),
                choices.CategoryMode ?? MetadataCategoryMode.Merge,
                Chosen(choices.CapstoneArt, MetadataField.CapstoneArt),
                Chosen(choices.TrophyTypes, MetadataField.TrophyTypes),
                Chosen(choices.Points, MetadataField.Points));
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

        private async Task<MatchedSet> LoadMatchedSetAsync(
            string key,
            Guid gameId,
            MetadataLinkRequest request,
            IList<AchievementDetail> targets,
            CancellationToken ct)
        {
            var provider = _providersByKey[key];
            try
            {
                if (!await BeginSessionAsync(provider, ct).ConfigureAwait(false))
                {
                    return null;
                }

                var (linkId, unconfirmed) = await ResolveLinkIdAsync(provider, gameId, request, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(linkId))
                {
                    return null;
                }

                var set = await provider.FetchAsync(request, linkId, ct).ConfigureAwait(false);
                if (set == null || set.Achievements.Count == 0)
                {
                    return null;
                }

                // A port that reused the developer's achievement IDs pairs exactly by ID, in any
                // language; titles only decide what the IDs leave unmatched.
                var overlap = set.ApiNamesMayBeShared
                    ? CrossSourceAchievementMatcher.ApiNameOverlap(targets, set.Achievements)
                    : 0;
                var matchApiNames = set.SharesApiNames || overlap >= CrossSourceAchievementMatcher.SharedApiNameThreshold;
                var matches = MatchWithPairings(targets, set.Achievements, ReadPairings(gameId, key, linkId), matchApiNames);
                _logger?.Debug(
                    $"[Metadata] {key} matched {matches.Count}/{targets.Count} achievements for '{request.Game?.Name}' " +
                    $"({linkId}, idOverlap={overlap:P0}, byId={matchApiNames}).");

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
                        return null;
                    }

                    _links?.SetAuto(gameId, provider.Key, linkId, unconfirmed.Label);
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
                return null;
            }
        }

        /// <summary>The user's pairings on the game's stored link, when it is the link being used.</summary>
        private IReadOnlyDictionary<string, string> ReadPairings(Guid gameId, string sourceKey, string linkId)
        {
            return _links != null &&
                   _links.TryGet(gameId, sourceKey, out var record) &&
                   string.Equals(record.Id, linkId, StringComparison.OrdinalIgnoreCase)
                ? record.Pairings
                : null;
        }

        /// <summary>
        /// The user's pairings first: each pairs its achievement with the named source row, and an
        /// empty one keeps the achievement out of matching. The rest are matched as usual, against
        /// the source rows no pairing took. A pairing naming a row the source no longer has is
        /// left to matching.
        /// </summary>
        internal static List<AchievementMatch> MatchWithPairings(
            IList<AchievementDetail> targets,
            IList<AchievementDetail> sources,
            IReadOnlyDictionary<string, string> pairings,
            bool matchApiNames)
        {
            if (pairings == null || pairings.Count == 0)
            {
                return CrossSourceAchievementMatcher.Match(targets, sources, matchApiNames: matchApiNames);
            }

            var sourcesByApiName = sources
                .Where(source => !string.IsNullOrWhiteSpace(source?.ApiName))
                .GroupBy(source => source.ApiName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var paired = new List<AchievementMatch>();
            var settled = new HashSet<AchievementDetail>();
            var taken = new HashSet<AchievementDetail>();
            foreach (var target in targets.Where(target => !string.IsNullOrWhiteSpace(target?.ApiName)))
            {
                if (!pairings.TryGetValue(target.ApiName, out var sourceApiName))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(sourceApiName))
                {
                    settled.Add(target);
                }
                else if (sourcesByApiName.TryGetValue(sourceApiName, out var source) && taken.Add(source))
                {
                    paired.Add(new AchievementMatch(target, source));
                    settled.Add(target);
                }
            }

            paired.AddRange(CrossSourceAchievementMatcher.Match(
                targets.Where(target => target != null && !settled.Contains(target)).ToList(),
                sources.Where(source => source != null && !taken.Contains(source)).ToList(),
                matchApiNames: matchApiNames));
            return paired;
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
        /// stored at once; one found by name comes back as <c>Unconfirmed</c> and is stored only
        /// after its achievements match.
        /// </summary>
        private async Task<(string Id, MetadataSourceLink Unconfirmed)> ResolveLinkIdAsync(
            IMetadataProvider provider,
            Guid gameId,
            MetadataLinkRequest request,
            CancellationToken ct)
        {
            if (_links != null && _links.TryGet(gameId, provider.Key, out var stored))
            {
                return (stored.IsNoMatch ? null : stored.Id, null);
            }

            var resolved = await provider.ResolveLinkAsync(request, ct).ConfigureAwait(false);
            if (resolved == null || string.IsNullOrWhiteSpace(resolved.Id))
            {
                return (null, null);
            }

            if (resolved.FoundByName)
            {
                return (resolved.Id.Trim(), resolved);
            }

            _links?.SetAuto(gameId, provider.Key, resolved.Id, resolved.Label);
            return (resolved.Id.Trim(), null);
        }

        /// <summary>
        /// Replaces each achievement's icons with the first preferred source that matched it,
        /// keeping each replaced achievement's own icons in <paramref name="ownIcons"/> by ApiName.
        /// </summary>
        private static string ApplyIcons(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets,
            IDictionary<string, OwnIconPaths> ownIcons)
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

                    var own = new OwnIconPaths
                    {
                        Unlocked = match.Target.UnlockedIconPath,
                        Locked = match.Target.LockedIconPath
                    };
                    match.Target.UnlockedIconPath = unlocked;
                    match.Target.LockedIconPath = string.IsNullOrWhiteSpace(match.Source.LockedIconPath)
                        ? null
                        : match.Source.LockedIconPath;
                    own.BorrowedUnlocked = match.Target.UnlockedIconPath;
                    own.BorrowedLocked = match.Target.LockedIconPath;
                    ownIcons[match.Target.ApiName] = own;
                    firstApplied = firstApplied ?? key;
                }
            }

            return firstApplied;
        }

        /// <summary>
        /// Copies one field to each achievement from the first preferred source that matched it
        /// and has a value for it.
        /// </summary>
        private static void ApplyField(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets,
            Func<AchievementDetail, bool> hasValue,
            Action<AchievementDetail, AchievementDetail> copy)
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
                    if (hasValue(match.Source) && done.Add(match.Target))
                    {
                        copy(match.Target, match.Source);
                    }
                }
            }
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

        /// <summary>
        /// The category art of each category source, for the categories it gave the game's
        /// achievements; the first source wins a label.
        /// </summary>
        private static List<(string Label, string ImageUrl)> CollectCategoryArt(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets)
        {
            var art = new List<(string Label, string ImageUrl)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set) || set.Set.CategoryArtUrls.Count == 0)
                {
                    continue;
                }

                var given = new HashSet<string>(
                    set.Matches
                        .Select(match => AchievementCategoryTypeHelper.NormalizeCategory(match.Target.Category))
                        .Where(label => label != null),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var pair in set.Set.CategoryArtUrls)
                {
                    var label = AchievementCategoryTypeHelper.NormalizeCategory(pair.Key);
                    if (label != null && given.Contains(label) && seen.Add(label))
                    {
                        art.Add((label, pair.Value));
                    }
                }
            }

            return art;
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
        /// <summary>
        /// The first preferred source's capstone row with art (a PlayStation platinum), whose art,
        /// title and description auto capstones take; null when none matched.
        /// </summary>
        private static AchievementDetail ResolveCapstone(IReadOnlyList<string> keys, IReadOnlyDictionary<string, MatchedSet> sets)
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
                if (FirstNonBlank(capstone?.UnlockedIconPath, capstone?.LockedIconPath) != null)
                {
                    return capstone;
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

        /// <summary>An achievement's icons as its own provider gave them, and the source's that replaced them.</summary>
        private sealed class OwnIconPaths
        {
            public string Unlocked { get; set; }

            public string Locked { get; set; }

            public string BorrowedUnlocked { get; set; }

            public string BorrowedLocked { get; set; }
        }

        private sealed class OverlayPlan
        {
            public OverlayPlan(
                List<string> iconKeys,
                List<string> categoryKeys,
                MetadataCategoryMode categoryMode,
                List<string> capstoneArtKeys,
                List<string> trophyTypeKeys,
                List<string> pointsKeys)
            {
                IconKeys = iconKeys;
                CategoryKeys = categoryKeys;
                CategoryMode = categoryMode;
                CapstoneArtKeys = capstoneArtKeys;
                TrophyTypeKeys = trophyTypeKeys;
                PointsKeys = pointsKeys;
                PerAchievementKeys = iconKeys.Concat(categoryKeys).Concat(trophyTypeKeys).Concat(pointsKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                AllKeys = PerAchievementKeys.Concat(capstoneArtKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            public List<string> TrophyTypeKeys { get; }

            public List<string> PointsKeys { get; }

            /// <summary>Sources supplying achievement fields, judged by how many achievements they match.</summary>
            public List<string> PerAchievementKeys { get; }

            public List<string> IconKeys { get; }

            public List<string> CategoryKeys { get; }

            public MetadataCategoryMode CategoryMode { get; }

            public List<string> CapstoneArtKeys { get; }

            public List<string> AllKeys { get; }

            public bool IsEmpty => AllKeys.Count == 0;
        }
    }
}
