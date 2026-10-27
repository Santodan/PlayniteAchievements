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
        private readonly Func<Guid, GameAchievementData> _loadStored;
        private readonly SourceIconDownload _downloadSourceIcons;
        private readonly Action<Guid, string> _deleteSourceIcons;
        private readonly EnglishTitleFetch _englishTitles;
        private readonly Action<IEnumerable<string>> _deleteUnusedSourceIcons;
        private readonly object _runSync = new object();
        private int _openRuns;
        private bool _sweepDue;

        /// <param name="downloadCategoryArt">
        /// Saves a source's category art for the categories it gave a game; null keeps none.
        /// </param>
        /// <param name="loadStored">
        /// The game's last saved data, whose values a source that fails keeps; null keeps none.
        /// </param>
        /// <param name="downloadSourceIcons">
        /// Saves a source's row icons for a game as local files; null keeps none.
        /// </param>
        /// <param name="deleteSourceIcons">Deletes a source's row icons for a game; null deletes none.</param>
        /// <param name="englishTitles">
        /// The game's achievements in English from its servicing provider, for pairing when every
        /// other way fails; null pairs only as given.
        /// </param>
        /// <param name="deleteUnusedSourceIcons">
        /// Deletes the saved source lists no game uses, given the icons the games' snapshots point
        /// at; null deletes none.
        /// </param>
        public MetadataOverlay(
            Func<IReadOnlyList<IMetadataProvider>> providers,
            Func<PersistedSettings> settings,
            MetadataLinkStore links,
            ILogger logger,
            CategoryArtDownload downloadCategoryArt = null,
            Func<Guid, GameAchievementData> loadStored = null,
            SourceIconDownload downloadSourceIcons = null,
            Action<Guid, string> deleteSourceIcons = null,
            EnglishTitleFetch englishTitles = null,
            Action<IEnumerable<string>> deleteUnusedSourceIcons = null)
        {
            _englishTitles = englishTitles;
            _deleteUnusedSourceIcons = deleteUnusedSourceIcons;
            _providers = providers;
            _settings = settings;
            _links = links;
            _logger = logger;
            _downloadCategoryArt = downloadCategoryArt;
            _loadStored = loadStored;
            _downloadSourceIcons = downloadSourceIcons;
            _deleteSourceIcons = deleteSourceIcons;
        }

        public MetadataLinkStore Links => _links;

        /// <summary>Starts one refresh run's overlay; dispose it when the run ends to close provider sessions.</summary>
        /// <param name="keepStored">
        /// True for a refresh made while a game is played: no source is asked, and every game keeps
        /// the metadata its last full refresh saved.
        /// </param>
        public MetadataOverlayRun BeginRun(bool keepStored = false)
        {
            lock (_runSync)
            {
                _openRuns++;
                _sweepDue |= !keepStored;
            }

            return new MetadataOverlayRun(
                _providers?.Invoke() ?? Array.Empty<IMetadataProvider>(),
                _settings?.Invoke(),
                _links,
                _logger,
                _downloadCategoryArt,
                _loadStored,
                _downloadSourceIcons,
                _deleteSourceIcons,
                _englishTitles,
                keepStored,
                EndRun);
        }

        /// <summary>
        /// When the last open run ends, deletes the saved source lists no game's snapshot points
        /// into. It runs under the lock a new run takes, so no run saves a list it would miss.
        /// </summary>
        private void EndRun()
        {
            lock (_runSync)
            {
                _openRuns = Math.Max(0, _openRuns - 1);
                if (_openRuns > 0 || !_sweepDue || _deleteUnusedSourceIcons == null)
                {
                    return;
                }

                _sweepDue = false;
                var used = _links?.Snapshots?.LoadIconPaths();
                if (used == null)
                {
                    return;
                }

                try
                {
                    _deleteUnusedSourceIcons(used);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Metadata] Deleting unused source lists failed.");
                }
            }
        }
    }

    /// <summary>
    /// The game's achievements in English from its servicing provider, keyed by the same ApiNames;
    /// null when the provider has no English copy.
    /// </summary>
    internal delegate Task<IList<AchievementDetail>> EnglishTitleFetch(
        Game game,
        GameAchievementData data,
        CancellationToken ct);

    /// <summary>Saves category art (label, image address) as a game's default category images.</summary>
    internal delegate Task CategoryArtDownload(
        Guid gameId,
        IReadOnlyList<(string Label, string ImageUrl)> art,
        CancellationToken ct);

    /// <summary>
    /// Saves a source's row icons for a game as local files, overwriting existing ones when
    /// <paramref name="force"/> is set. Returns each row's icon paths: local where the file was
    /// saved, the row's own address where it was not.
    /// </summary>
    internal delegate Task<IReadOnlyDictionary<AchievementDetail, (string Unlocked, string Locked)>> SourceIconDownload(
        Guid gameId,
        string sourceKey,
        IReadOnlyList<MetadataSourceList> lists,
        bool force,
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
            CategoryArtDownload downloadCategoryArt = null,
            Func<Guid, GameAchievementData> loadStored = null,
            SourceIconDownload downloadSourceIcons = null,
            Action<Guid, string> deleteSourceIcons = null,
            EnglishTitleFetch englishTitles = null,
            bool keepStored = false,
            Action onEnded = null)
        {
            KeepsStored = keepStored;
            _onEnded = onEnded;
            _englishTitles = englishTitles;
            _loadStored = loadStored;
            _downloadSourceIcons = downloadSourceIcons;
            _deleteSourceIcons = deleteSourceIcons;
            _providersByKey = (providers ?? Array.Empty<IMetadataProvider>())
                .Where(provider => provider != null && !string.IsNullOrWhiteSpace(provider.Key))
                .GroupBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            _settings = settings;
            _links = links;
            _logger = logger;
            _resolver = new MetadataLinkResolver(links, logger, settings?.GlobalLanguage);
            _downloadCategoryArt = downloadCategoryArt;
        }

        /// <summary>True when this run asks no source and every game keeps its stored metadata.</summary>
        public bool KeepsStored { get; }

        private readonly EnglishTitleFetch _englishTitles;
        private readonly MetadataLinkResolver _resolver;
        private readonly CategoryArtDownload _downloadCategoryArt;
        private readonly Func<Guid, GameAchievementData> _loadStored;
        private readonly SourceIconDownload _downloadSourceIcons;
        private readonly Action<Guid, string> _deleteSourceIcons;
        private Action _onEnded;

        /// <summary>
        /// Whether the game's last overlay borrowed a capstone from a source: its art or text, or a
        /// collection's per-category platinums.
        /// </summary>
        public bool HasBorrowedCapstone(Guid gameId)
        {
            var applied = _links?.GetApplied(gameId);
            return applied != null &&
                   (!string.IsNullOrWhiteSpace(applied.CapstoneArtUrl) ||
                    !string.IsNullOrWhiteSpace(applied.CapstoneTitle) ||
                    applied.CategoryCapstones?.Count > 0);
        }

        /// <summary>
        /// Applies the enabled metadata fields to the payload. Returns true when the game's icon
        /// source changed since the last overlay, so cached icon files must be overwritten.
        /// <paramref name="forceIcons"/> overwrites the sources' saved row icons as well.
        /// <paramref name="keepStored"/>, or a run that <see cref="KeepsStored"/>, asks no source:
        /// every planned source is treated as failed, so the game keeps what was last saved.
        /// </summary>
        public async Task<bool> ApplyAsync(Game game, GameAchievementData data, CancellationToken ct, bool forceIcons = false, bool keepStored = false)
        {
            var gameId = data?.PlayniteGameId ?? game?.Id ?? Guid.Empty;
            if (gameId == Guid.Empty || data?.Achievements == null || data.Achievements.Count == 0)
            {
                return false;
            }

            var englishTitles = EnglishTitlesFor(game, data);
            var request = CreateRequest(game, data, gameId, englishTitles);
            var plan = BuildPlan(request.ServicingProviderKey, _links?.GetChoices(gameId));
            if (plan.IsEmpty)
            {
                return _links?.RecordApplied(gameId, null, null) ?? false;
            }

            var sets = new Dictionary<string, MatchedSet>(StringComparer.OrdinalIgnoreCase);
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ct.ThrowIfCancellationRequested();
            if (KeepsStored || keepStored)
            {
                failed.UnionWith(plan.AllKeys);
            }
            else
            {
                await LoadAllAsync(game, data, gameId, request, englishTitles, plan, sets, failed, ct).ConfigureAwait(false);
            }

            BorrowTitles(gameId, sets, data.Achievements);
            foreach (var pair in sets)
            {
                await MatchInEnglishAsync(pair.Key, gameId, pair.Value.Set, pair.Value.Matches, pair.Value.LinkId, request, data.Achievements, ct)
                    .ConfigureAwait(false);
            }

            await DownloadSourceIconsAsync(gameId, plan, sets, forceIcons, ct).ConfigureAwait(false);

            return await ApplySetsAsync(gameId, data, plan, sets, failed, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads every planned source's rows for the game, matched to its achievements; a source
        /// that failed lands in <paramref name="failed"/>.
        /// </summary>
        private async Task LoadAllAsync(
            Game game,
            GameAchievementData data,
            Guid gameId,
            MetadataLinkRequest request,
            Func<CancellationToken, Task<IList<AchievementDetail>>> englishTitles,
            OverlayPlan plan,
            Dictionary<string, MatchedSet> sets,
            HashSet<string> failed,
            CancellationToken ct)
        {
            // The sources load at the same time, each with its own request; they share no state but
            // the link store, which serializes its writes. Their answers are then applied in order.
            var unlinked = plan.AllKeys.Where(key => _links == null || !_links.TryGet(gameId, key, out _)).ToList();
            var loads = (await LoadSetsAsync(plan.AllKeys, plan, gameId, request, data, ct).ConfigureAwait(false)).ToList();

            // What one source found the game under is searched by the others: a source that looked
            // for it this refresh and found nothing looks once more, under the names the others
            // found (their links' labels).
            var retry = unlinked
                .Where(key => _links != null && _links.TryGet(gameId, key, out var record) && record.IsNoMatch && record.Origin == MetadataLinkOrigin.Auto)
                .ToList();
            if (retry.Count > 0)
            {
                var shared = CreateRequest(game, data, gameId, englishTitles);
                if (KnowsMoreNames(shared, request))
                {
                    foreach (var key in retry)
                    {
                        _links.Clear(gameId, key);
                    }

                    var again = await LoadSetsAsync(retry, plan, gameId, shared, data, ct).ConfigureAwait(false);
                    loads = loads.Where(load => !retry.Contains(load.Key, StringComparer.OrdinalIgnoreCase)).Concat(again).ToList();
                }
            }

            foreach (var (key, matched, fetchFailed) in loads)
            {
                if (matched != null)
                {
                    sets[key] = matched;
                }
                else if (fetchFailed)
                {
                    failed.Add(key);
                }
            }
        }

        /// <summary>
        /// Applies the loaded sources' fields to the payload and records what was applied. A failed
        /// source's fields keep the values last saved. Returns whether the icon source changed.
        /// </summary>
        private async Task<bool> ApplySetsAsync(
            Guid gameId,
            GameAchievementData data,
            OverlayPlan plan,
            Dictionary<string, MatchedSet> sets,
            HashSet<string> failed,
            CancellationToken ct)
        {
            // A source that could not answer, wholly or for a field, leaves the game as its last
            // refresh saved it there, never blank.
            bool Failed(IEnumerable<string> keys, MetadataField field) => keys.Any(key =>
                failed.Contains(key) || (sets.TryGetValue(key, out var answered) && (answered.Set.UnavailableFields & field) != 0));
            var anyFailed = failed.Count > 0 || sets.Values.Any(answered => answered.Set.UnavailableFields != MetadataField.None);
            var previous = anyFailed ? _links?.GetApplied(gameId) : null;
            var stored = anyFailed ? StoredByApiName(gameId) : null;

            var ownIcons = new Dictionary<string, OwnIconPaths>(StringComparer.OrdinalIgnoreCase);
            var iconSource = ApplyIcons(plan.IconKeys, sets, ownIcons);
            var borrowedIconApiNames = ownIcons.Keys.ToList();
            if (iconSource == null && plan.IconKeys.Any(failed.Contains) &&
                previous?.IconSourceKey != null && failed.Contains(previous.IconSourceKey))
            {
                iconSource = previous.IconSourceKey;
                borrowedIconApiNames = previous.IconApiNames ?? new List<string>();
                var borrowed = new HashSet<string>(borrowedIconApiNames, StringComparer.OrdinalIgnoreCase);
                KeepStored(data.Achievements, stored, row => borrowed.Contains(row.ApiName ?? string.Empty), (target, old) =>
                {
                    target.UnlockedIconPath = old.UnlockedIconPath;
                    target.LockedIconPath = old.LockedIconPath;
                });
            }
            if (ownIcons.Count > 0)
            {
                _ownIcons[gameId] = ownIcons;
            }
            Replace(plan.CategoryKeys, sets, ApplyCategory, MetadataField.Categories);
            Replace(plan.TagKeys, sets, ApplyTags, MetadataField.Tags);
            if (Failed(plan.CategoryKeys, MetadataField.Categories))
            {
                KeepStored(data.Achievements, stored, _ => true, ApplyCategory);
            }

            if (Failed(plan.TagKeys, MetadataField.Tags))
            {
                KeepStored(data.Achievements, stored, _ => true, ApplyTags);
            }
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
            // A source's rarity is its players' share of unlocks; it replaces the platform's tier
            // wherever the source has a share for the achievement.
            Replace(plan.RarityKeys, sets, ApplyRarity, MetadataField.Rarity);
            if (Failed(plan.RarityKeys, MetadataField.Rarity))
            {
                KeepStored(data.Achievements, stored, _ => true, (target, old) =>
                {
                    if (old.GlobalPercentUnlocked.HasValue)
                    {
                        target.GlobalPercentUnlocked = old.GlobalPercentUnlocked;
                        target.Rarity = old.Rarity;
                    }
                });
            }

            if (Failed(plan.TrophyTypeKeys, MetadataField.TrophyTypes))
            {
                KeepStored(data.Achievements, stored, _ => true, (target, old) => target.TrophyType = old.TrophyType ?? target.TrophyType);
            }

            if (Failed(plan.PointsKeys, MetadataField.Points))
            {
                KeepStored(data.Achievements, stored, _ => true, (target, old) => target.Points = old.Points ?? target.Points);
            }

            var capstone = ResolveCapstone(plan.CapstoneArtKeys, sets);
            var capstoneArt = SavedArt(sets.Values, capstone);
            var capstoneTitle = capstone?.DisplayName;
            var capstoneDescription = capstone?.Description;
            var capstoneMatch = MatchCapstone(plan.CapstoneArtKeys, sets, capstone, data.Achievements);
            // A collection linked to several lists gets each list's platinum for its own game, and
            // no platinum for the whole game.
            var categoryCapstones = ResolveCategoryCapstones(plan.CapstoneArtKeys, sets, data.Achievements);
            if (categoryCapstones.Count > 0)
            {
                capstoneArt = capstoneTitle = capstoneDescription = capstoneMatch = null;
            }

            if (capstone == null && plan.CapstoneArtKeys.Any(failed.Contains) && previous != null)
            {
                capstoneArt = previous.CapstoneArtUrl;
                capstoneTitle = previous.CapstoneTitle;
                capstoneDescription = previous.CapstoneDescription;
                capstoneMatch = previous.CapstoneMatchApiName;
                categoryCapstones = previous.CategoryCapstones ?? new List<MetadataCategoryCapstone>();
            }

            // Text goes last, after everything above has matched and read the game's own titles.
            // A source whose page is in another language leaves the game's own text.
            var textSets = sets
                .Where(pair => pair.Value.Set.TextInPluginLanguage)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            Replace(plan.TextKeys, textSets, ApplyText, MetadataField.Text);
            if (Failed(plan.TextKeys, MetadataField.Text))
            {
                KeepStored(data.Achievements, stored, _ => true, ApplyText);
            }
            // A source that supplies achievement data is judged, and shown, by how many of the
            // game's achievements it matched; one used only for the auto capstone by its platinum.
            var coverage = new Dictionary<string, MetadataCoverage>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sets)
            {
                var perAchievement = plan.PerAchievementKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);
                if (perAchievement)
                {
                    coverage[pair.Key] = new MetadataCoverage { Matched = pair.Value.Matches.Count, Total = data.Achievements.Count };
                    if (pair.Value.Set.KeepsNoMatchList)
                    {
                        _links?.Snapshots?.Forget(gameId, pair.Key);
                        continue;
                    }

                    _links?.Snapshots?.Record(
                        gameId,
                        pair.Key,
                        pair.Value.LinkId,
                        pair.Value.Set.Achievements,
                        pair.Value.Matches.Select(match => new KeyValuePair<string, string>(match.Target.ApiName, match.Source.ApiName)),
                        pair.Value.Set.Lists,
                        row =>
                        {
                            var icons = pair.Value.IconsOf(row);
                            return string.IsNullOrWhiteSpace(icons.Unlocked) ? icons.Locked : icons.Unlocked;
                        });
                }
                else
                {
                    var hasPlatinum = ResolveCapstone(new[] { pair.Key }, sets) != null;
                    coverage[pair.Key] = new MetadataCoverage { Matched = hasPlatinum ? 1 : 0, Total = 1 };
                    _links?.Snapshots?.Forget(gameId, pair.Key);
                    _deleteSourceIcons?.Invoke(gameId, pair.Key);
                }
            }

            foreach (var key in failed)
            {
                if (previous?.Coverage != null && previous.Coverage.TryGetValue(key, out var kept))
                {
                    coverage[key] = kept;
                }
            }

            return _links?.RecordApplied(
                gameId,
                iconSource,
                capstoneArt,
                coverage,
                borrowedIconApiNames,
                capstoneTitle,
                capstoneDescription,
                capstoneMatch,
                categoryCapstones) ?? false;
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

            Interlocked.Exchange(ref _onEnded, null)?.Invoke();
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

            var choices = MetadataSourceRouting.Route(
                (_settings ?? new PersistedSettings()).ResolveMetadataChoices(servicingProviderKey, gameChoices),
                _providersByKey.Values,
                servicingProviderKey);
            return new OverlayPlan(
                Chosen(choices.Icons, MetadataField.Icons),
                Chosen(choices.Categories, MetadataField.Categories),
                Chosen(choices.Tags, MetadataField.Tags),
                Chosen(choices.Rarity, MetadataField.Rarity),
                Chosen(choices.CapstoneArt, MetadataField.CapstoneArt),
                Chosen(choices.TrophyTypes, MetadataField.TrophyTypes),
                Chosen(choices.Points, MetadataField.Points),
                Chosen(choices.Text, MetadataField.Text));
        }

        /// <summary>
        /// What one source matched an achievement to is another title it goes by ("Welcome to
        /// Rapture" for "Completed Welcome to Rapture"), so each source's unmatched achievements are
        /// matched again under the titles the other sources matched them to, against the rows still
        /// free. An achievement the user paired, or kept out, is left as it is.
        /// </summary>
        private void BorrowTitles(Guid gameId, IDictionary<string, MatchedSet> sets, IList<AchievementDetail> targets)
        {
            if (sets.Count < 2 || targets == null)
            {
                return;
            }

            var matchedElsewhere = sets
                .SelectMany(pair => pair.Value.Matches.Select(match => (pair.Key, match.Target, match.Source)))
                .Where(entry => entry.Target != null && entry.Source != null && !string.IsNullOrWhiteSpace(entry.Source.DisplayName))
                .ToList();
            foreach (var pair in sets)
            {
                var set = pair.Value;
                var matched = new HashSet<AchievementDetail>(set.Matches.Select(match => match.Target));
                var pairings = ReadPairings(gameId, pair.Key, set.LinkId) ?? new Dictionary<string, string>();
                var used = new HashSet<AchievementDetail>(set.Matches.Select(match => match.Source));
                var open = set.Set.Achievements.Where(row => row != null && !used.Contains(row)).ToList();
                if (open.Count == 0)
                {
                    continue;
                }

                // One stand-in per title another source matched the achievement to.
                var standIns = new List<AchievementDetail>();
                var targetOf = new Dictionary<AchievementDetail, AchievementDetail>();
                foreach (var entry in matchedElsewhere.Where(entry =>
                             !string.Equals(entry.Key, pair.Key, StringComparison.OrdinalIgnoreCase) &&
                             !matched.Contains(entry.Target) &&
                             !pairings.ContainsKey(entry.Target.ApiName ?? string.Empty)))
                {
                    var standIn = new AchievementDetail
                    {
                        ApiName = entry.Target.ApiName,
                        DisplayName = entry.Source.DisplayName,
                        Description = entry.Source.Description,
                        TrophyType = entry.Source.TrophyType,
                        Points = entry.Source.Points
                    };
                    standIns.Add(standIn);
                    targetOf[standIn] = entry.Target;
                }

                if (standIns.Count == 0)
                {
                    continue;
                }

                var added = 0;
                foreach (var match in CrossSourceAchievementMatcher.Match(standIns, open))
                {
                    if (matched.Add(targetOf[match.Target]))
                    {
                        set.Matches.Add(new AchievementMatch(targetOf[match.Target], match.Source));
                        added++;
                    }
                }

                if (added > 0)
                {
                    _logger?.Debug($"[Metadata] {pair.Key} matched {added} more achievement(s) by titles other sources matched them to.");
                }
            }
        }

        /// <summary>
        /// The last way to pair a source: the achievements and rows every other way left
        /// unmatched, compared in English (see <see cref="EnglishTitlePairing"/>). Fetches English
        /// text only when both sides have something left, and never touches an achievement the
        /// user kept out. Returns how many it paired; a failure pairs none and never fails the game.
        /// </summary>
        private async Task<int> MatchInEnglishAsync(
            string key,
            Guid gameId,
            AchievementSourceSet set,
            List<AchievementMatch> matches,
            string linkId,
            MetadataLinkRequest request,
            IList<AchievementDetail> targets,
            CancellationToken ct)
        {
            if (request?.EnglishTitles == null || set == null || targets == null)
            {
                return 0;
            }

            var pairings = ReadPairings(gameId, key, linkId);
            var matched = new HashSet<AchievementDetail>(matches.Select(match => match.Target));
            var used = new HashSet<AchievementDetail>(matches.Select(match => match.Source));
            var openTargets = targets
                .Where(target => target != null && !matched.Contains(target) && !IsKeptOut(pairings, target))
                .ToList();
            var openRows = set.Achievements.Where(row => row != null && !used.Contains(row)).ToList();
            if (openTargets.Count == 0 || openRows.Count == 0)
            {
                return 0;
            }

            try
            {
                var english = await request.EnglishTitles(ct).ConfigureAwait(false);
                if (english == null || english.Count == 0)
                {
                    return 0;
                }

                var englishRows = set.EnglishRows == null ? null : await set.EnglishRows(ct).ConfigureAwait(false);
                if (set.EnglishRows != null && (englishRows == null || englishRows.Count == 0))
                {
                    return 0;
                }

                var found = EnglishTitlePairing.Match(openTargets, english, openRows, englishRows);
                matches.AddRange(found);
                _logger?.Debug(
                    $"[Metadata] {key} matched {found.Count} more of {openTargets.Count} unmatched achievement(s) " +
                    $"for '{request.Game?.Name}' in English.");
                return found.Count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Metadata] {key} could not pair '{request.Game?.Name}' in English.");
                return 0;
            }
        }

        /// <summary>True when the user's pairings keep <paramref name="target"/> out of matching.</summary>
        private static bool IsKeptOut(IReadOnlyDictionary<string, string> pairings, AchievementDetail target) =>
            pairings != null &&
            !string.IsNullOrWhiteSpace(target?.ApiName) &&
            pairings.TryGetValue(target.ApiName, out var paired) &&
            string.IsNullOrEmpty(paired);

        /// <summary>
        /// The game's English achievements from its servicing provider, loaded at most once for the
        /// game and only when first asked for; null when the plugin's language is English or there
        /// is no provider to ask.
        /// </summary>
        private Func<CancellationToken, Task<IList<AchievementDetail>>> EnglishTitlesFor(Game game, GameAchievementData data)
        {
            if (_englishTitles == null || EnglishTitlePairing.IsEnglish(_settings?.GlobalLanguage))
            {
                return null;
            }

            var gate = new object();
            Task<IList<AchievementDetail>> loading = null;
            return ct =>
            {
                lock (gate)
                {
                    return loading ?? (loading = LoadEnglishTitlesAsync(game, data, ct));
                }
            };
        }

        private async Task<IList<AchievementDetail>> LoadEnglishTitlesAsync(Game game, GameAchievementData data, CancellationToken ct)
        {
            try
            {
                var english = await _englishTitles(game, data, ct).ConfigureAwait(false);
                _logger?.Debug(
                    $"[Metadata] English titles for '{game?.Name}' from {data?.ProviderKey}: " +
                    $"{(english == null ? "none" : english.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");
                return english;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Metadata] Loading English titles for '{game?.Name}' from {data?.ProviderKey} failed.");
                return null;
            }
        }

        /// <summary>The sources' rows for the game, loaded at the same time.</summary>
        private async Task<IReadOnlyList<(string Key, MatchedSet Matched, bool FetchFailed)>> LoadSetsAsync(
            IEnumerable<string> keys,
            OverlayPlan plan,
            Guid gameId,
            MetadataLinkRequest request,
            GameAchievementData data,
            CancellationToken ct)
        {
            return await Task.WhenAll(keys.Select(async key =>
            {
                var (matchedSet, didFail) = await LoadMatchedSetAsync(key, gameId, request.ForSource(plan.FieldsFor(key)), data.Achievements, ct)
                    .ConfigureAwait(false);
                return (Key: key, Matched: matchedSet, FetchFailed: didFail);
            })).ConfigureAwait(false);
        }

        /// <summary>True when <paramref name="now"/> has a name to search that <paramref name="before"/> did not.</summary>
        private static bool KnowsMoreNames(MetadataLinkRequest now, MetadataLinkRequest before)
        {
            var known = new HashSet<string>(before.AlternateNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if ((now.AlternateNames ?? Array.Empty<string>()).Any(name => !known.Contains(name)))
            {
                return true;
            }

            foreach (var pair in now.CategoryNames ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                IReadOnlyList<string> earlier = null;
                before.CategoryNames?.TryGetValue(pair.Key, out earlier);
                if (pair.Value.Any(name => earlier == null || !earlier.Contains(name, StringComparer.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A link request carrying what the game's saved links found it under (their labels) as
        /// names to search: titles found for the whole game as alternate names, titles found for a
        /// category as that category's names.
        /// </summary>
        private MetadataLinkRequest CreateRequest(
            Game game,
            GameAchievementData data,
            Guid gameId,
            Func<CancellationToken, Task<IList<AchievementDetail>>> englishTitles)
        {
            var links = _links?.GetAll(gameId) ?? new Dictionary<string, MetadataLinkRecord>();
            var found = links.Values
                .Where(link => link != null && !link.IsNoMatch)
                .SelectMany(link => MetadataFoundNames.Parse(link.Label))
                .ToList();
            return new MetadataLinkRequest(game, data)
            {
                EnglishTitles = englishTitles,
                OtherLinks = links
                    .Where(pair => pair.Value != null && !pair.Value.IsNoMatch)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.OrdinalIgnoreCase),
                AlternateNames = found
                    .Where(name => name.Category == null)
                    .Select(name => name.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                CategoryNames = found
                    .Where(name => name.Category != null)
                    .GroupBy(name => name.Category, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<string>)group.Select(name => name.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// The source's rows for the game, matched to its achievements. Failed is true when the
        /// source could not answer (no session, an error, or nothing fetched for a link it had
        /// already confirmed), as opposed to having no link or no match.
        /// </summary>
        private async Task<(MatchedSet Set, bool Failed)> LoadMatchedSetAsync(
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
                    return (null, true);
                }

                var (linkId, unconfirmed, unavailable) = await _resolver.ResolveAsync(provider, gameId, request, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(linkId))
                {
                    return (null, unavailable);
                }

                // The label names the link's lists only while it is the label of this link.
                var label = _links != null && _links.TryGet(gameId, key, out var linkRecord) &&
                            string.Equals(linkRecord?.Id?.Trim(), linkId.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? linkRecord.Label
                    : null;
                var set = await MetadataListFetch.FetchAsync(provider, request, linkId, _logger, ct, label).ConfigureAwait(false);
                // The id the game's link holds after this fetch, which the match list is kept under.
                var storedId = linkId;
                if (set == null || set.Achievements.Count == 0)
                {
                    if (unconfirmed == null)
                    {
                        _logger?.Debug($"[Metadata] {key} returned nothing for '{request.Game?.Name}' ({linkId}); previous values kept.");
                    }

                    return (null, unconfirmed == null);
                }

                // A port that reused the developer's achievement IDs pairs exactly by ID, in any
                // language; titles only decide what the IDs leave unmatched.
                var overlap = set.ApiNamesMayBeShared
                    ? CrossSourceAchievementMatcher.ApiNameOverlap(targets, set.Achievements)
                    : 0;
                var matchApiNames = set.SharesApiNames || overlap >= CrossSourceAchievementMatcher.SharedApiNameThreshold;
                var matches = MatchWithPairings(targets, set.Achievements, ReadPairings(gameId, key, linkId), matchApiNames, set.Lists, set.ApiNameAliases);
                if (matches.Count < targets.Count)
                {
                    _logger?.Debug(
                        $"[Metadata] {key} matched {matches.Count}/{targets.Count} achievements for '{request.Game?.Name}' " +
                        $"({linkId}, idOverlap={overlap:P0}, byId={matchApiNames}).");
                }

                // A game found by name is a different release when too few achievements line up
                // ("Thief: Definitive Edition" against "Thief Gold"), so it is neither kept nor used.
                // Measured against the shorter list: a console version with fewer trophies than
                // the PC version's achievements can still match all of its own.
                if (unconfirmed != null)
                {
                    if (matches.Count < Math.Min(targets.Count, set.Achievements.Count) * MinNameMatchShare)
                    {
                        // Its last chance before rejection: the titles may only differ in language.
                        await MatchInEnglishAsync(key, gameId, set, matches, linkId, request, targets, ct).ConfigureAwait(false);
                    }

                    if (matches.Count < Math.Min(targets.Count, set.Achievements.Count) * MinNameMatchShare)
                    {
                        _logger?.Debug(
                            $"[Metadata] {key} name match '{unconfirmed.Label ?? linkId}' rejected for '{request.Game?.Name}': " +
                            $"{matches.Count}/{targets.Count} achievements matched.");
                        _links?.SetAutoNoMatch(gameId, provider.Key);
                        return (null, false);
                    }

                    _links?.SetAuto(gameId, provider.Key, ExtendedOr(set, linkId), unconfirmed.Label);
                    storedId = ExtendedOr(set, linkId);
                }
                else if (!request.LinkIsUserSet && !string.IsNullOrWhiteSpace(set.ExtendedLinkId) &&
                         !string.Equals(set.ExtendedLinkId.Trim(), linkId, StringComparison.OrdinalIgnoreCase))
                {
                    _links?.ExtendAuto(gameId, provider.Key, set.ExtendedLinkId);
                    storedId = set.ExtendedLinkId.Trim();
                }

                return (new MatchedSet(set, matches, storedId), false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Metadata] {key} failed for '{request.Game?.Name}'; previous values kept.");
                return (null, true);
            }
        }

        private static string ExtendedOr(AchievementSourceSet set, string linkId) =>
            string.IsNullOrWhiteSpace(set.ExtendedLinkId) ? linkId : set.ExtendedLinkId.Trim();

        /// <summary>The game's last saved achievements by ApiName; empty when there are none.</summary>
        private Dictionary<string, AchievementDetail> StoredByApiName(Guid gameId)
        {
            var byApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in _loadStored?.Invoke(gameId)?.Achievements ?? Enumerable.Empty<AchievementDetail>())
                {
                    var apiName = row?.ApiName?.Trim();
                    if (!string.IsNullOrEmpty(apiName) && !byApiName.ContainsKey(apiName))
                    {
                        byApiName[apiName] = row;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Metadata] Reading the stored achievements of {gameId} failed.");
            }

            return byApiName;
        }

        /// <summary>Copies a field from each achievement's saved row, for the achievements <paramref name="applies"/> selects.</summary>
        private static void KeepStored(
            IList<AchievementDetail> targets,
            IReadOnlyDictionary<string, AchievementDetail> stored,
            Func<AchievementDetail, bool> applies,
            Action<AchievementDetail, AchievementDetail> copy)
        {
            if (stored == null || stored.Count == 0)
            {
                return;
            }

            foreach (var target in targets)
            {
                if (target?.ApiName != null && applies(target) &&
                    stored.TryGetValue(target.ApiName.Trim(), out var old) && old != null)
                {
                    copy(target, old);
                }
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
        /// left to matching. A pairing naming another id a row went by (<paramref name="aliases"/>)
        /// pairs with that row. With several <paramref name="lists"/> (a collection's games), each
        /// category is matched against its own list first; see <see cref="MatchByList"/>.
        /// </summary>
        internal static List<AchievementMatch> MatchWithPairings(
            IList<AchievementDetail> targets,
            IList<AchievementDetail> sources,
            IReadOnlyDictionary<string, string> pairings,
            bool matchApiNames,
            IReadOnlyList<MetadataSourceList> lists = null,
            IReadOnlyDictionary<string, AchievementDetail> aliases = null)
        {
            if (pairings == null || pairings.Count == 0)
            {
                return MatchByList(targets, sources, lists, matchApiNames);
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
                else if ((sourcesByApiName.TryGetValue(sourceApiName, out var source) ||
                          (aliases != null && aliases.TryGetValue(sourceApiName, out source) && sources.Contains(source))) &&
                         taken.Add(source))
                {
                    paired.Add(new AchievementMatch(target, source));
                    settled.Add(target);
                }
            }

            paired.AddRange(MatchByList(
                targets.Where(target => target != null && !settled.Contains(target)).ToList(),
                sources.Where(source => source != null && !taken.Contains(source)).ToList(),
                lists,
                matchApiNames));
            return paired;
        }

        /// <summary>
        /// Matches a collection's achievements list by list: each category against the one list
        /// that matches most of it, so a title two of its games share ("Undefeated") pairs within
        /// its own game rather than tying. What that leaves is matched against every list. With one
        /// list or none this is plain matching.
        /// </summary>
        internal static List<AchievementMatch> MatchByList(
            IList<AchievementDetail> targets,
            IList<AchievementDetail> sources,
            IReadOnlyList<MetadataSourceList> lists,
            bool matchApiNames)
        {
            if (lists == null || lists.Count < 2)
            {
                return CrossSourceAchievementMatcher.Match(targets, sources, matchApiNames: matchApiNames);
            }

            var open = new HashSet<AchievementDetail>(sources.Where(source => source != null));
            var matches = new List<AchievementMatch>();
            var matched = new HashSet<AchievementDetail>();
            foreach (var category in targets.Where(target => target != null).GroupBy(target => target.Category?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var rows = category.ToList();
                var best = lists
                    .Select(list => list.Rows.Where(open.Contains).ToList())
                    .Select(listRows => (Rows: listRows, Matches: CrossSourceAchievementMatcher.Match(rows, listRows, matchApiNames: matchApiNames)))
                    .OrderByDescending(entry => entry.Matches.Count)
                    .FirstOrDefault();
                foreach (var match in best.Matches ?? new List<AchievementMatch>())
                {
                    matches.Add(match);
                    matched.Add(match.Target);
                    open.Remove(match.Source);
                }
            }

            matches.AddRange(CrossSourceAchievementMatcher.Match(
                targets.Where(target => target != null && !matched.Contains(target)).ToList(),
                sources.Where(open.Contains).ToList(),
                matchApiNames: matchApiNames));
            return matches;
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
        /// Saves each per-achievement source's row icons in the game's icon cache, all sources at
        /// once, so borrowed icons copy those files and the matches window shows them. A relinked
        /// source overwrites its files, which are named by row. A failure keeps the rows' addresses
        /// and never fails the refresh.
        /// </summary>
        private async Task DownloadSourceIconsAsync(
            Guid gameId,
            OverlayPlan plan,
            IReadOnlyDictionary<string, MatchedSet> sets,
            bool forceIcons,
            CancellationToken ct)
        {
            if (_downloadSourceIcons == null)
            {
                return;
            }

            // A set of the game's own rows has no icons of its own and no match list to show.
            var downloads = sets
                .Where(pair => plan.PerAchievementKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) &&
                               !pair.Value.Set.KeepsNoMatchList)
                .Select(async pair =>
                {
                    var savedLinkId = _links?.Snapshots?.GetLinkId(gameId, pair.Key);
                    var relinked = savedLinkId != null &&
                        !string.Equals(savedLinkId, pair.Value.LinkId?.Trim(), StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        pair.Value.Icons = await _downloadSourceIcons(
                                gameId,
                                pair.Key,
                                IconLists(pair.Value),
                                forceIcons || relinked,
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _logger?.Debug(ex, $"[Metadata] Saving the {pair.Key} icons of {gameId} failed.");
                    }
                })
                .ToList();
            await Task.WhenAll(downloads).ConfigureAwait(false);
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
                    var icons = set.IconsOf(match.Source);
                    var unlocked = FirstNonBlank(icons.Unlocked, icons.Locked);
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
                    match.Target.LockedIconPath = string.IsNullOrWhiteSpace(icons.Locked)
                        ? null
                        : icons.Locked;
                    own.BorrowedUnlocked = match.Target.UnlockedIconPath;
                    own.BorrowedLocked = match.Target.LockedIconPath;
                    ownIcons[match.Target.ApiName] = own;
                    firstApplied = firstApplied ?? key;
                }
            }

            return firstApplied;
        }

        /// <summary>
        /// Fills one field of each achievement that has no value of its own, from the first
        /// preferred source that matched it and has one: a platform with its own points or trophy
        /// types (RetroAchievements, Epic, the MMOs) keeps them.
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
                    if (!hasValue(match.Target) && hasValue(match.Source) && done.Add(match.Target))
                    {
                        copy(match.Target, match.Source);
                    }
                }
            }
        }

        /// <summary>
        /// The category art of each category source, for the categories it gave the game's
        /// achievements; the first source wins a label. A collection's lists also give their game
        /// image to the category most of their matched achievements are in.
        /// </summary>
        private static List<(string Label, string ImageUrl)> CollectCategoryArt(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets)
        {
            var art = new List<(string Label, string ImageUrl)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set))
                {
                    continue;
                }

                var given = new HashSet<string>(
                    set.Matches
                        .Select(match => AchievementCategoryHelper.NormalizeCategory(match.Target.Category))
                        .Where(label => label != null),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var pair in set.Set.CategoryArtUrls)
                {
                    var label = AchievementCategoryHelper.NormalizeCategory(pair.Key);
                    if (label != null && given.Contains(label) && seen.Add(label))
                    {
                        art.Add((label, pair.Value));
                    }
                }

                foreach (var (label, imageUrl) in ListArt(set))
                {
                    if (seen.Add(label))
                    {
                        art.Add((label, imageUrl));
                    }
                }
            }

            return art;
        }

        /// <summary>
        /// For a link to two or more lists, each list's game image with the category most of the
        /// list's matched achievements are in.
        /// </summary>
        private static IEnumerable<(string Label, string ImageUrl)> ListArt(MatchedSet set)
        {
            var lists = set.Set.Lists;
            if (lists == null || lists.Count < 2)
            {
                yield break;
            }

            foreach (var list in lists.Where(list => !string.IsNullOrWhiteSpace(list.ArtUrl)))
            {
                var rows = new HashSet<AchievementDetail>(list.Rows);
                var label = set.Matches
                    .Where(match => rows.Contains(match.Source))
                    .Select(match => AchievementCategoryHelper.NormalizeCategory(match.Target.Category))
                    .Where(category => category != null)
                    .GroupBy(category => category, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .FirstOrDefault();
                if (label != null)
                {
                    yield return (label, list.ArtUrl);
                }
            }
        }

        /// <summary>
        /// Replaces each matched achievement's value with that of the first source that matched it,
        /// whatever the achievement had.
        /// </summary>
        private static void Replace(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets,
            Action<AchievementDetail, AchievementDetail> copy,
            MetadataField field)
        {
            var done = new HashSet<AchievementDetail>();
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set) || (set.Set.UnavailableFields & field) != 0)
                {
                    continue;
                }

                foreach (var match in set.Matches)
                {
                    if (done.Add(match.Target))
                    {
                        copy(match.Target, match.Source);
                    }
                }
            }
        }

        /// <summary>The source's unlock share and its rarity tier, where the source has a share.</summary>
        internal static void ApplyRarity(AchievementDetail target, AchievementDetail source)
        {
            if (!source.GlobalPercentUnlocked.HasValue)
            {
                return;
            }

            var percent = Math.Max(0, Math.Min(100, source.GlobalPercentUnlocked.Value));
            target.GlobalPercentUnlocked = percent;
            target.Rarity = PercentRarityHelper.GetRarityTier(percent);
        }

        /// <summary>
        /// The source's name and description, each where the source has one: a source that hides a
        /// description (as Steam does for hidden achievements) leaves the game's own.
        /// </summary>
        internal static void ApplyText(AchievementDetail target, AchievementDetail source)
        {
            if (!string.IsNullOrWhiteSpace(source.DisplayName))
            {
                target.DisplayName = source.DisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(source.Description))
            {
                target.Description = source.Description.Trim();
            }
        }

        /// <summary>Replaces an achievement's category and kind with the source's.</summary>
        internal static void ApplyCategory(AchievementDetail target, AchievementDetail source)
        {
            target.Category = AchievementCategoryHelper.NormalizeCategory(source.Category);
            target.CategoryKind = CategoryKindHelper.Normalize(source.CategoryKind);
        }

        /// <summary>
        /// Replaces an achievement's tags with the source's, keeping what only the game's own
        /// provider can know: the unlock mode (how this user earned it) and Unobtainable (the
        /// platform retired it, as Steam does with archived achievements).
        /// </summary>
        internal static void ApplyTags(AchievementDetail target, AchievementDetail source)
        {
            target.Tags = AchievementTagHelper.Combine(new[]
            {
                AchievementTagHelper.StripDerived(source.Tags),
                AchievementTagHelper.DerivedOnly(target.Tags),
                AchievementTagHelper.IsUnobtainable(target.Tags) ? AchievementTagHelper.Unobtainable : null
            });
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

        /// <summary>
        /// ApiName of the game's achievement that is <paramref name="capstone"/>'s counterpart: the
        /// one its source paired with it (a user pairing or a title match), else the one achievement
        /// no other row paired whose description is the platinum's. DLC and subset achievements
        /// never count; null when there is none.
        /// </summary>
        private static string MatchCapstone(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets,
            AchievementDetail capstone,
            IList<AchievementDetail> targets)
        {
            if (capstone == null || targets == null)
            {
                return null;
            }

            var set = keys
                .Select(key => sets.TryGetValue(key, out var found) ? found : null)
                .FirstOrDefault(found => found != null && found.Set.Achievements.Contains(capstone));
            if (set == null)
            {
                return null;
            }

            return MatchCapstoneIn(set, capstone, targets, _ => true);
        }

        /// <summary>
        /// The game's achievement paired with <paramref name="capstone"/>, else the one unpaired
        /// achievement among those <paramref name="within"/> keeps whose description is the
        /// platinum's; DLC, subset and authored achievements never count.
        /// </summary>
        private static string MatchCapstoneIn(
            MatchedSet set,
            AchievementDetail capstone,
            IList<AchievementDetail> targets,
            Func<AchievementDetail, bool> within)
        {
            foreach (var match in set.Matches)
            {
                if (ReferenceEquals(match.Source, capstone))
                {
                    return IsCapstoneEligible(match.Target) && within(match.Target) ? match.Target.ApiName.Trim() : null;
                }
            }

            var description = CrossSourceAchievementMatcher.NormalizeText(capstone.Description);
            if (string.IsNullOrEmpty(description))
            {
                return null;
            }

            var paired = new HashSet<AchievementDetail>(set.Matches.Select(match => match.Target));
            var byDescription = targets
                .Where(target => IsCapstoneEligible(target) && within(target) && !paired.Contains(target))
                .Where(target => string.Equals(
                    CrossSourceAchievementMatcher.NormalizeText(target.Description),
                    description,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            return byDescription.Count == 1 ? byDescription[0].ApiName.Trim() : null;
        }

        private static bool IsCapstoneEligible(AchievementDetail target) =>
            target != null &&
            !target.IsCustom &&
            !string.IsNullOrWhiteSpace(target.ApiName) &&
            !CategoryKindHelper.IsDlc(target.CategoryKind) &&
            !CategoryKindHelper.IsSubset(target.CategoryKind);

        private static bool IsPlatinumWithArt(AchievementDetail row) =>
            row != null &&
            (row.IsCapstone || string.Equals(row.TrophyType, "platinum", StringComparison.OrdinalIgnoreCase)) &&
            FirstNonBlank(row.UnlockedIconPath, row.LockedIconPath) != null;

        /// <summary>
        /// For the first capstone source whose link names several lists, each list's platinum filed
        /// under the category most of that list's matches fall in, with the achievement in that
        /// category it matched. Empty unless at least two lists give one, and for a link to one
        /// list; a category takes the first list that claims it.
        /// </summary>
        private static List<MetadataCategoryCapstone> ResolveCategoryCapstones(
            IReadOnlyList<string> keys,
            IReadOnlyDictionary<string, MatchedSet> sets,
            IList<AchievementDetail> targets)
        {
            var result = new List<MetadataCategoryCapstone>();
            var set = keys
                .Select(key => sets.TryGetValue(key, out var found) ? found : null)
                .FirstOrDefault(found => found != null && found.Set.Achievements.Any(IsPlatinumWithArt));
            if (set == null || set.Set.Lists.Count < 2)
            {
                return result;
            }

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in set.Set.Lists)
            {
                var platinum = list.Rows.FirstOrDefault(IsPlatinumWithArt);
                if (platinum == null)
                {
                    continue;
                }

                var inList = new HashSet<AchievementDetail>(list.Rows);
                var category = set.Matches
                    .Where(match => inList.Contains(match.Source) && IsCapstoneEligible(match.Target) && !string.IsNullOrWhiteSpace(match.Target.Category))
                    .GroupBy(match => match.Target.Category.Trim(), StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .FirstOrDefault();
                if (category == null || !claimed.Add(category))
                {
                    continue;
                }

                result.Add(new MetadataCategoryCapstone
                {
                    Category = category,
                    ArtUrl = SavedArt(new[] { set }, platinum),
                    Title = string.IsNullOrWhiteSpace(platinum.DisplayName) ? null : platinum.DisplayName.Trim(),
                    Description = string.IsNullOrWhiteSpace(platinum.Description) ? null : platinum.Description.Trim(),
                    MatchApiName = MatchCapstoneIn(set, platinum, targets ?? new List<AchievementDetail>(), target =>
                        string.Equals(target.Category?.Trim(), category, StringComparison.OrdinalIgnoreCase))
                });
            }

            return result.Count >= 2 ? result : new List<MetadataCategoryCapstone>();
        }

        /// <summary>
        /// The set's rows by the source list they come from, so each list's icons are saved once
        /// for every game linked to it. A set of one list goes by its link.
        /// </summary>
        private static IReadOnlyList<MetadataSourceList> IconLists(MatchedSet matched)
        {
            var set = matched.Set;
            if (set.Lists?.Count > 1)
            {
                return set.Lists;
            }

            var id = FirstNonBlank(set.LinkId, matched.LinkId);
            return new[] { new MetadataSourceList(id, id, set.Achievements.ToList()) };
        }

        /// <summary>
        /// A platinum's art as saved in the game's icon cache, so the capstone shows that file
        /// rather than keeping a copy of its own; its address when the source's icons were not saved.
        /// </summary>
        private static string SavedArt(IEnumerable<MatchedSet> sets, AchievementDetail row)
        {
            if (row == null)
            {
                return null;
            }

            var saved = sets
                .Where(set => set?.Icons != null && set.Icons.ContainsKey(row))
                .Select(set => set.IconsOf(row))
                .Select(icons => FirstNonBlank(icons.Unlocked, icons.Locked))
                .FirstOrDefault(art => art != null);
            return saved ?? FirstNonBlank(row.UnlockedIconPath, row.LockedIconPath);
        }

        private static string FirstNonBlank(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private sealed class MatchedSet
        {
            public MatchedSet(AchievementSourceSet set, List<AchievementMatch> matches, string linkId)
            {
                Set = set;
                Matches = matches;
                LinkId = linkId;
            }

            public AchievementSourceSet Set { get; }

            /// <summary>The id the game's link holds, which can differ from the fetched one when the source extended it.</summary>
            public string LinkId { get; }

            public List<AchievementMatch> Matches { get; }

            /// <summary>The rows' icons as saved in the game's icon cache; null before they are.</summary>
            public IReadOnlyDictionary<AchievementDetail, (string Unlocked, string Locked)> Icons { get; set; }

            /// <summary>A row's saved icons, else its own.</summary>
            public (string Unlocked, string Locked) IconsOf(AchievementDetail row) =>
                Icons != null && row != null && Icons.TryGetValue(row, out var icons)
                    ? icons
                    : (row?.UnlockedIconPath, row?.LockedIconPath);
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
                List<string> tagKeys,
                List<string> rarityKeys,
                List<string> capstoneArtKeys,
                List<string> trophyTypeKeys,
                List<string> pointsKeys,
                List<string> textKeys)
            {
                IconKeys = iconKeys;
                CategoryKeys = categoryKeys;
                TagKeys = tagKeys;
                RarityKeys = rarityKeys;
                CapstoneArtKeys = capstoneArtKeys;
                TrophyTypeKeys = trophyTypeKeys;
                PointsKeys = pointsKeys;
                TextKeys = textKeys;
                PerAchievementKeys = iconKeys.Concat(categoryKeys).Concat(tagKeys).Concat(rarityKeys).Concat(trophyTypeKeys).Concat(pointsKeys).Concat(textKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                AllKeys = PerAchievementKeys.Concat(capstoneArtKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            public List<string> TrophyTypeKeys { get; }

            public List<string> PointsKeys { get; }

            public List<string> TextKeys { get; }

            /// <summary>Sources supplying achievement fields, judged by how many achievements they match.</summary>
            public List<string> PerAchievementKeys { get; }

            public List<string> IconKeys { get; }

            public List<string> CategoryKeys { get; }

            public List<string> TagKeys { get; }

            public List<string> RarityKeys { get; }

            public List<string> CapstoneArtKeys { get; }

            public List<string> AllKeys { get; }

            public bool IsEmpty => AllKeys.Count == 0;

            /// <summary>The fields this plan takes from <paramref name="key"/>; category art comes with categories.</summary>
            public MetadataField FieldsFor(string key)
            {
                bool Has(List<string> keys) => keys.Contains(key, StringComparer.OrdinalIgnoreCase);
                return (Has(IconKeys) ? MetadataField.Icons : MetadataField.None) |
                       (Has(CategoryKeys) ? MetadataField.Categories | MetadataField.CategoryArt : MetadataField.None) |
                       (Has(TagKeys) ? MetadataField.Tags : MetadataField.None) |
                       (Has(RarityKeys) ? MetadataField.Rarity : MetadataField.None) |
                       (Has(CapstoneArtKeys) ? MetadataField.CapstoneArt : MetadataField.None) |
                       (Has(TrophyTypeKeys) ? MetadataField.TrophyTypes : MetadataField.None) |
                       (Has(PointsKeys) ? MetadataField.Points : MetadataField.None) |
                       (Has(TextKeys) ? MetadataField.Text : MetadataField.None);
            }
        }
    }
}
