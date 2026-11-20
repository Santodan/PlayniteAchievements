using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.Rules
{
    /// <summary>
    /// Excludes games that match the user's chosen Playnite saved filters, writing each game's
    /// exclusion flag through <see cref="RuleReconciler"/> so the rule takes back only what it set.
    /// </summary>
    /// <remarks>
    /// Matching is Playnite's own filter evaluation, so whatever a saved filter can express works
    /// here without the plugin knowing about it. Work is queued and coalesced: a library-wide pass
    /// costs one filter evaluation per chosen preset, a per-game pass one match test per preset,
    /// and only games whose stored state actually changes are written.
    /// </remarks>
    public sealed class FilterExclusionRuleService : IDisposable
    {
        private const int CoalesceDelayMs = 500;

        private readonly IPlayniteAPI _api;
        private readonly GameCustomDataStore _store;
        // The wrapper, not its PersistedSettings: a settings cancel replaces that instance.
        private readonly PlayniteAchievementsSettings _settingsHost;
        private readonly ILogger _logger;
        private readonly object _queueSync = new object();
        private readonly object _runSync = new object();
        private readonly Timer _timer;
        private readonly HashSet<Guid> _pendingGameIds = new HashSet<Guid>();
        private bool _pendingAll;
        private bool _disposed;

        public FilterExclusionRuleService(
            IPlayniteAPI api,
            GameCustomDataStore store,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settingsHost = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _timer = new Timer(_ => Drain(), null, Timeout.Infinite, Timeout.Infinite);
        }

        private PersistedSettings Settings => _settingsHost.Persisted;

        /// <summary>True when either list names at least one preset.</summary>
        public bool HasRules =>
            Settings?.RefreshExclusionFilterPresetIds?.Count > 0 ||
            Settings?.SummaryExclusionFilterPresetIds?.Count > 0;

        /// <summary>Queues a pass over the whole library.</summary>
        public void QueueAll()
        {
            lock (_queueSync)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingAll = true;
                _pendingGameIds.Clear();
                _timer.Change(CoalesceDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>Queues a pass over the given games, coalesced with other queued work.</summary>
        public void QueueGames(IEnumerable<Guid> gameIds)
        {
            if (gameIds == null)
            {
                return;
            }

            lock (_queueSync)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_pendingAll)
                {
                    foreach (var id in gameIds)
                    {
                        if (id != Guid.Empty)
                        {
                            _pendingGameIds.Add(id);
                        }
                    }
                }

                _timer.Change(CoalesceDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Reconciles the given games on the calling thread. For callers that must see the result
        /// before acting, such as the refresh of newly added games.
        /// </summary>
        public void ReconcileGamesNow(IReadOnlyCollection<Guid> gameIds)
        {
            if (gameIds == null || gameIds.Count == 0)
            {
                return;
            }

            Run(all: false, gameIds);
        }

        /// <summary>Whether the game matches a refresh exclusion filter and a summary one.</summary>
        public (bool Refresh, bool Summary) GetMatches(Guid gameId)
        {
            var game = _api.Database?.Games?.Get(gameId);
            if (game == null)
            {
                return (false, false);
            }

            return (
                Matches(game, ResolvePresets(Settings?.RefreshExclusionFilterPresetIds)),
                Matches(game, ResolvePresets(Settings?.SummaryExclusionFilterPresetIds)));
        }

        /// <summary>
        /// Drops the user's own choice for the game's exclusions and lets the filters decide again.
        /// </summary>
        public void FollowFilters(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            lock (_runSync)
            {
                if (_store.TryLoad(gameId, out var stored) &&
                    (RuleReconciler.IsPinned(stored, RuleFields.ExcludedFromRefreshes) ||
                     RuleReconciler.IsPinned(stored, RuleFields.ExcludedFromSummaries)))
                {
                    _store.Update(
                        gameId,
                        data =>
                        {
                            RuleReconciler.SetPinned(data, RuleFields.ExcludedFromRefreshes, false);
                            RuleReconciler.SetPinned(data, RuleFields.ExcludedFromSummaries, false);
                        },
                        affectsSummaryData: false);
                }
            }

            Run(all: false, new[] { gameId });
        }

        private void Drain()
        {
            bool all;
            List<Guid> gameIds;
            lock (_queueSync)
            {
                all = _pendingAll;
                gameIds = _pendingGameIds.ToList();
                _pendingAll = false;
                _pendingGameIds.Clear();
            }

            if (all || gameIds.Count > 0)
            {
                Run(all, gameIds);
            }
        }

        private void Run(bool all, IReadOnlyCollection<Guid> gameIds)
        {
            // One pass at a time, so two passes cannot interleave their reads and writes.
            lock (_runSync)
            {
                try
                {
                    using (PerfScope.Start(_logger, all ? "FilterExclusionRules.All" : "FilterExclusionRules.Games", thresholdMs: 50))
                    {
                        var written = all ? ReconcileAll() : ReconcileGames(gameIds);
                        if (written > 0)
                        {
                            _logger?.Info($"Filter exclusion rules updated {written} game(s).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to apply filter exclusion rules.");
                }
            }
        }

        private int ReconcileAll()
        {
            var refreshPresets = ResolvePresets(Settings?.RefreshExclusionFilterPresetIds);
            var summaryPresets = ResolvePresets(Settings?.SummaryExclusionFilterPresetIds);
            var refreshMatches = CollectMatches(refreshPresets);
            var summaryMatches = CollectMatches(summaryPresets);

            // Only a game a rule wants, or one holding rule state, can need a write; every other
            // game is "no rule, no claim, no pin", which is a no-op.
            var candidates = new HashSet<Guid>(refreshMatches);
            candidates.UnionWith(summaryMatches);
            candidates.UnionWith(GetGamesWithRuleState());

            var written = 0;
            foreach (var gameId in candidates)
            {
                if (Apply(gameId, refreshMatches.Contains(gameId), summaryMatches.Contains(gameId)))
                {
                    written++;
                }
            }

            return written;
        }

        private int ReconcileGames(IReadOnlyCollection<Guid> gameIds)
        {
            var refreshPresets = ResolvePresets(Settings?.RefreshExclusionFilterPresetIds);
            var summaryPresets = ResolvePresets(Settings?.SummaryExclusionFilterPresetIds);
            var written = 0;
            foreach (var gameId in gameIds)
            {
                var game = _api.Database?.Games?.Get(gameId);
                if (game == null)
                {
                    continue;
                }

                if (Apply(gameId, Matches(game, refreshPresets), Matches(game, summaryPresets)))
                {
                    written++;
                }
            }

            return written;
        }

        /// <summary>Writes the game's record when either field's decision changes it.</summary>
        private bool Apply(Guid gameId, bool refreshMatch, bool summaryMatch)
        {
            var current = _store.TryLoad(gameId, out var stored) ? stored : null;
            if (Decide(current, RuleFields.ExcludedFromRefreshes, refreshMatch).IsNoOp &&
                Decide(current, RuleFields.ExcludedFromSummaries, summaryMatch).IsNoOp)
            {
                return false;
            }

            _store.Update(
                gameId,
                data =>
                {
                    // Decided again on the record the store hands over, so a write that landed
                    // since the read above is honored rather than overwritten.
                    ApplyDecision(data, RuleFields.ExcludedFromRefreshes, refreshMatch);
                    ApplyDecision(data, RuleFields.ExcludedFromSummaries, summaryMatch);
                },
                affectsSummaryData: true);
            return true;
        }

        private static RuleDecision Decide(GameCustomDataFile data, string field, bool match)
        {
            return RuleReconciler.Decide(
                field,
                RuleReconciler.ToFlagText(ReadFlag(data, field)),
                match ? RuleFields.True : null,
                RuleReconciler.FindClaim(data, field),
                RuleReconciler.IsPinned(data, field));
        }

        private static void ApplyDecision(GameCustomDataFile data, string field, bool match)
        {
            var decision = Decide(data, field, match);
            if (decision.Write)
            {
                var value = RuleReconciler.FromFlagText(decision.Value);
                if (field == RuleFields.ExcludedFromRefreshes)
                {
                    data.ExcludedFromRefreshes = value;
                }
                else
                {
                    data.ExcludedFromSummaries = value;
                }
            }

            RuleReconciler.ApplyState(data, field, decision);
        }

        private static bool? ReadFlag(GameCustomDataFile data, string field)
        {
            return field == RuleFields.ExcludedFromRefreshes
                ? data?.ExcludedFromRefreshes
                : data?.ExcludedFromSummaries;
        }

        private HashSet<Guid> GetGamesWithRuleState()
        {
            return _store.QueryAll(records => new HashSet<Guid>(records
                .Where(data => data != null && HasExclusionRuleState(data))
                .Select(data => data.PlayniteGameId)));
        }

        private static bool HasExclusionRuleState(GameCustomDataFile data)
        {
            return RuleReconciler.FindClaim(data, RuleFields.ExcludedFromRefreshes) != null ||
                   RuleReconciler.FindClaim(data, RuleFields.ExcludedFromSummaries) != null ||
                   RuleReconciler.IsPinned(data, RuleFields.ExcludedFromRefreshes) ||
                   RuleReconciler.IsPinned(data, RuleFields.ExcludedFromSummaries);
        }

        private List<FilterPresetSettings> ResolvePresets(IEnumerable<Guid> presetIds)
        {
            var resolved = new List<FilterPresetSettings>();
            if (presetIds == null)
            {
                return resolved;
            }

            foreach (var id in presetIds)
            {
                // A deleted preset is skipped: it matches nothing, so its exclusions are released.
                var settings = _api.Database?.FilterPresets?.Get(id)?.Settings;
                if (HasConditions(settings))
                {
                    resolved.Add(settings);
                }
            }

            return resolved;
        }

        private HashSet<Guid> CollectMatches(List<FilterPresetSettings> presets)
        {
            var matches = new HashSet<Guid>();
            foreach (var preset in presets)
            {
                var games = _api.Database.GetFilteredGames(preset);
                if (games == null)
                {
                    continue;
                }

                foreach (var game in games)
                {
                    if (game != null)
                    {
                        matches.Add(game.Id);
                    }
                }
            }

            return matches;
        }

        private bool Matches(Game game, List<FilterPresetSettings> presets)
        {
            foreach (var preset in presets)
            {
                if (_api.Database.GetGameMatchesFilter(game, preset))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// False for a preset that only sorts or groups. Playnite matches every game against one,
        /// which would exclude the whole library.
        /// </summary>
        public static bool HasConditions(FilterPresetSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            return settings.IsInstalled ||
                   settings.IsUnInstalled ||
                   settings.Hidden ||
                   settings.Favorite ||
                   !string.IsNullOrWhiteSpace(settings.Name) ||
                   !string.IsNullOrWhiteSpace(settings.Version) ||
                   settings.ReleaseYear?.Values?.Count > 0 ||
                   HasIds(settings.Genre) ||
                   HasIds(settings.Platform) ||
                   HasIds(settings.Publisher) ||
                   HasIds(settings.Developer) ||
                   HasIds(settings.Category) ||
                   HasIds(settings.Tag) ||
                   HasIds(settings.Series) ||
                   HasIds(settings.Region) ||
                   HasIds(settings.Source) ||
                   HasIds(settings.AgeRating) ||
                   HasIds(settings.Library) ||
                   HasIds(settings.CompletionStatuses) ||
                   HasIds(settings.Feature) ||
                   settings.UserScore?.Values?.Count > 0 ||
                   settings.CriticScore?.Values?.Count > 0 ||
                   settings.CommunityScore?.Values?.Count > 0 ||
                   settings.LastActivity?.Values?.Count > 0 ||
                   settings.RecentActivity?.Values?.Count > 0 ||
                   settings.Added?.Values?.Count > 0 ||
                   settings.Modified?.Values?.Count > 0 ||
                   settings.PlayTime?.Values?.Count > 0 ||
                   settings.InstallSize?.Values?.Count > 0;
        }

        private static bool HasIds(IdItemFilterItemProperties item)
        {
            return item != null && (item.Ids?.Count > 0 || !string.IsNullOrWhiteSpace(item.Text));
        }

        public void Dispose()
        {
            lock (_queueSync)
            {
                _disposed = true;
                _pendingAll = false;
                _pendingGameIds.Clear();
            }

            _timer.Dispose();
        }
    }
}
