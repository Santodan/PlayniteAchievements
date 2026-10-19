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

        public MetadataOverlay(
            Func<IReadOnlyList<IMetadataProvider>> providers,
            Func<PersistedSettings> settings,
            MetadataLinkStore links,
            ILogger logger)
        {
            _providers = providers;
            _settings = settings;
            _links = links;
            _logger = logger;
        }

        public MetadataLinkStore Links => _links;

        /// <summary>Starts one refresh run's overlay; dispose it when the run ends to close provider sessions.</summary>
        public MetadataOverlayRun BeginRun()
        {
            return new MetadataOverlayRun(
                _providers?.Invoke() ?? Array.Empty<IMetadataProvider>(),
                _settings?.Invoke(),
                _links,
                _logger);
        }
    }

    internal sealed class MetadataOverlayRun : IDisposable
    {
        private readonly Dictionary<string, IMetadataProvider> _providersByKey;
        private readonly PersistedSettings _settings;
        private readonly MetadataLinkStore _links;
        private readonly ILogger _logger;
        private readonly object _sessionSync = new object();
        private readonly Dictionary<string, Task<bool>> _sessions =
            new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

        internal MetadataOverlayRun(
            IReadOnlyList<IMetadataProvider> providers,
            PersistedSettings settings,
            MetadataLinkStore links,
            ILogger logger)
        {
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

            var request = new MetadataLinkRequest(game, data);
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

            var iconSource = ApplyIcons(plan.IconKeys, sets);
            ApplyCategories(plan.CategoryKeys, sets);
            var capstoneArt = ResolveCapstoneArt(plan.CapstoneArtKeys, sets);

            return _links?.RecordApplied(gameId, iconSource, capstoneArt) ?? false;
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

            List<string> Ordered(IEnumerable<string> keys, MetadataField field) =>
                (keys ?? Enumerable.Empty<string>())
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(key => Usable(key, field))
                    .ToList();

            var categoryKeys = _providersByKey.Values
                .Where(provider => provider.CategoryMode != MetadataCategoryMode.Off)
                .Select(provider => provider.Key)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

            return new OverlayPlan(
                Ordered(_settings?.MetadataIconSources, MetadataField.Icons),
                Ordered(categoryKeys, MetadataField.Categories),
                Ordered(_settings?.MetadataCapstoneArtSources, MetadataField.CapstoneArt));
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

                var linkId = await ResolveLinkIdAsync(provider, gameId, request, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(linkId))
                {
                    return null;
                }

                var set = await provider.FetchAsync(request, linkId, ct).ConfigureAwait(false);
                if (set == null || set.Achievements.Count == 0)
                {
                    return null;
                }

                var matches = CrossSourceAchievementMatcher.Match(targets, set.Achievements, matchApiNames: set.SharesApiNames);
                _logger?.Debug($"[Metadata] {key} matched {matches.Count}/{targets.Count} achievements for '{request.Game?.Name}' ({linkId}).");
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

        private async Task<string> ResolveLinkIdAsync(
            IMetadataProvider provider,
            Guid gameId,
            MetadataLinkRequest request,
            CancellationToken ct)
        {
            if (_links != null && _links.TryGet(gameId, provider.Key, out var stored))
            {
                return stored.IsNoMatch ? null : stored.Id;
            }

            var resolved = await provider.ResolveLinkAsync(request, ct).ConfigureAwait(false);
            if (resolved == null || string.IsNullOrWhiteSpace(resolved.Id))
            {
                return null;
            }

            _links?.SetAuto(gameId, provider.Key, resolved.Id, resolved.Label);
            return resolved.Id.Trim();
        }

        /// <summary>Replaces each achievement's icons with the first preferred source that matched it.</summary>
        private static string ApplyIcons(IReadOnlyList<string> keys, IReadOnlyDictionary<string, MatchedSet> sets)
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
                    firstApplied = firstApplied ?? key;
                }
            }

            return firstApplied;
        }

        /// <summary>
        /// Applies each achievement's category and types from the first source that matched it.
        /// Merge keeps a category the provider named and adds the source's types; Override replaces both.
        /// </summary>
        private void ApplyCategories(IReadOnlyList<string> keys, IReadOnlyDictionary<string, MatchedSet> sets)
        {
            var done = new HashSet<AchievementDetail>();
            foreach (var key in keys)
            {
                if (!sets.TryGetValue(key, out var set))
                {
                    continue;
                }

                var mode = _providersByKey[key].CategoryMode;
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
            public OverlayPlan(List<string> iconKeys, List<string> categoryKeys, List<string> capstoneArtKeys)
            {
                IconKeys = iconKeys;
                CategoryKeys = categoryKeys;
                CapstoneArtKeys = capstoneArtKeys;
                AllKeys = iconKeys.Concat(categoryKeys).Concat(capstoneArtKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            public List<string> IconKeys { get; }

            public List<string> CategoryKeys { get; }

            public List<string> CapstoneArtKeys { get; }

            public List<string> AllKeys { get; }

            public bool IsEmpty => AllKeys.Count == 0;
        }
    }
}
