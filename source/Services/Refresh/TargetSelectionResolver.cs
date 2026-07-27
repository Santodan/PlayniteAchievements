using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PlayniteAchievements.Services.Refresh
{
    internal sealed class TargetSelectionCache
    {
        private readonly Dictionary<string, bool> _capabilityByGameProvider =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public int CapabilityCheckCount { get; private set; }

        public bool TryGetCapability(Guid gameId, string providerKey, out bool capable)
        {
            capable = false;
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(providerKey))
            {
                return false;
            }

            return _capabilityByGameProvider.TryGetValue(BuildKey(gameId, providerKey), out capable);
        }

        public void SetCapability(Guid gameId, string providerKey, bool capable)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            _capabilityByGameProvider[BuildKey(gameId, providerKey)] = capable;
            CapabilityCheckCount++;
        }

        private static string BuildKey(Guid gameId, string providerKey)
        {
            return gameId.ToString("D") + "|" + providerKey.Trim();
        }
    }

    internal sealed class TargetSelectionResolver
    {
        private readonly Dictionary<string, int> _refreshOrderIndex;

        public sealed class ResolvedRefreshTarget
        {
            public Game Game { get; set; }

            public IDataProvider Provider { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ICacheManager _cacheService;
        private readonly ILogger _logger;

        public TargetSelectionResolver(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ICacheManager cacheService,
            ILogger logger,
            IEnumerable<string> refreshOrder)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            _logger = logger;
            _refreshOrderIndex = BuildOrderIndex(refreshOrder);
        }

        public IDataProvider ResolveProviderForGame(
            Game game,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache = null)
        {
            if (game == null || providers == null || providers.Count == 0)
            {
                return null;
            }

            var forcedProvider = ResolveForcedProviderForGame(game, providers);
            if (forcedProvider != null || HasForcedProviderOverride(game.Id))
            {
                return forcedProvider;
            }

            foreach (var provider in OrderProvidersForRefresh(providers))
            {
                try
                {
                    if (IsProviderCapable(game, provider, targetSelectionCache))
                    {
                        return provider;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, string.Format(
                        "Platform capability check failed for game '{0}'.",
                        game?.Name));
                }
            }

            return null;
        }

        private bool IsProviderCapable(
            Game game,
            IDataProvider provider,
            TargetSelectionCache targetSelectionCache)
        {
            if (game == null || provider == null)
            {
                return false;
            }

            if (targetSelectionCache != null &&
                targetSelectionCache.TryGetCapability(game.Id, provider.ProviderKey, out var cached))
            {
                return cached;
            }

            var capable = provider.IsCapable(game);
            targetSelectionCache?.SetCapability(game.Id, provider.ProviderKey, capable);
            return capable;
        }

        private IDataProvider ResolveForcedProviderForGame(Game game, IReadOnlyList<IDataProvider> providers)
        {
            if (game == null || providers == null || providers.Count == 0)
            {
                return null;
            }

            if (GameCustomDataLookup.TryGetProviderOverride(game.Id, out var providerOverride))
            {
                return providers.FirstOrDefault(provider =>
                    provider != null &&
                    provider.IsAuthenticated &&
                    string.Equals(provider.ProviderKey, providerOverride.ProviderKey, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static bool HasForcedProviderOverride(Guid gameId)
        {
             return GameCustomDataLookup.TryGetProviderOverride(gameId, out _);
        }

        /// <summary>
        /// Returns the subset of providers that could service at least one candidate game,
        /// ignoring authentication state. Games with a forced provider override mark the
        /// override provider directly, since override resolution never falls through to
        /// capability checks. A provider whose capability check throws is kept, so filtering
        /// can only over-approximate the set of providers worth probing.
        /// </summary>
        public IReadOnlyList<IDataProvider> GetProvidersWithCapableGames(
            IEnumerable<Game> candidateGames,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache = null)
        {
            var candidates = providers?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();
            if (candidates.Count == 0 || candidateGames == null)
            {
                return new List<IDataProvider>();
            }

            var markedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in candidateGames)
            {
                if (markedKeys.Count == candidates.Count)
                {
                    break;
                }

                if (game == null)
                {
                    continue;
                }

                if (GameCustomDataLookup.TryGetProviderOverride(game.Id, out var providerOverride))
                {
                    var overrideProvider = candidates.FirstOrDefault(provider =>
                        string.Equals(provider.ProviderKey, providerOverride.ProviderKey, StringComparison.OrdinalIgnoreCase));
                    if (overrideProvider != null)
                    {
                        markedKeys.Add(overrideProvider.ProviderKey);
                    }

                    continue;
                }

                foreach (var provider in candidates)
                {
                    if (markedKeys.Contains(provider.ProviderKey))
                    {
                        continue;
                    }

                    try
                    {
                        if (IsProviderCapable(game, provider, targetSelectionCache))
                        {
                            markedKeys.Add(provider.ProviderKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, string.Format(
                            "Platform capability check failed for game '{0}'.",
                            game?.Name));
                        markedKeys.Add(provider.ProviderKey);
                    }
                }
            }

            return candidates
                .Where(provider => markedKeys.Contains(provider.ProviderKey))
                .ToList();
        }

        public IReadOnlyList<IDataProvider> OrderProvidersForRefresh(IEnumerable<IDataProvider> providers)
        {
            return (providers ?? Enumerable.Empty<IDataProvider>())
                .Where(provider => provider != null)
                .OrderBy(provider => GetRefreshOrderIndex(provider.ProviderKey))
                .ThenBy(provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<ResolvedRefreshTarget> GetRefreshTargets(
            CacheRefreshOptions options,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache = null)
        {
            var timer = Stopwatch.StartNew();
            options ??= new CacheRefreshOptions();
            var candidatesSeen = 0;

            HashSet<Guid> excludedGameIds = null;
            var skipCachedNoAchievements = false;
            if (options.SkipNoAchievementsGames && !options.BypassExclusions)
            {
                excludedGameIds = GameCustomDataLookup.GetExcludedRefreshGameIds(_settings?.Persisted);
                skipCachedNoAchievements = true;
            }

            IEnumerable<Game> candidates;
            if (options.PlayniteGameIds?.Count > 0)
            {
                candidates = options.PlayniteGameIds
                    .Select(id => _api.Database.Games.Get(id))
                    .Where(g => g != null);
            }
            else
            {
                var allGames = _api.Database.Games.ToList();
                if (options.RecentRefreshMode)
                {
                    candidates = allGames
                        .Where(g => g != null && g.LastActivity != null)
                        .OrderByDescending(g => g.LastActivity);
                }
                else if (!options.IncludeUnplayedGames)
                {
                    candidates = allGames.Where(g => g != null && g.Playtime > 0);
                }
                else
                {
                    candidates = allGames.Where(g => g != null);
                }
            }

            var targets = new List<ResolvedRefreshTarget>();
            var seenGameIds = new HashSet<Guid>();
            var recentLimit = Math.Max(1, options.RecentRefreshGamesCount);
            var skippedNoProvider = 0;
            var skippedNoAchievements = 0;
            var skippedHiddenGames = 0;
            var applyBulkHiddenFilter = options.PlayniteGameIds == null || options.PlayniteGameIds.Count == 0;

            foreach (var game in candidates)
            {
                candidatesSeen++;
                if (game == null || !seenGameIds.Add(game.Id))
                {
                    continue;
                }

                if (applyBulkHiddenFilter && !BulkRefreshGameFilter.ShouldIncludeGame(game, _settings?.Persisted))
                {
                    skippedHiddenGames++;
                    continue;
                }

                if ((excludedGameIds != null && excludedGameIds.Contains(game.Id)) ||
                    (skipCachedNoAchievements && IsCachedNoAchievements(game)))
                {
                    skippedNoAchievements++;
                    continue;
                }

                var provider = ResolveProviderForGame(game, providers, targetSelectionCache);
                if (provider == null)
                {
                    skippedNoProvider++;
                    continue;
                }

                targets.Add(new ResolvedRefreshTarget { Game = game, Provider = provider });

                if (options.RecentRefreshMode && targets.Count >= recentLimit)
                {
                    break;
                }
            }

            if (skippedNoProvider > 0)
            {
                _logger?.Debug($"Skipped {skippedNoProvider} games without a capable provider.");
            }

            if (skippedNoAchievements > 0)
            {
                _logger?.Debug($"Skipped {skippedNoAchievements} games with HasAchievements=false or ExcludedByUser=true.");
            }

            if (skippedHiddenGames > 0)
            {
                _logger?.Debug($"Skipped {skippedHiddenGames} hidden games during bulk refresh targeting.");
            }

            timer.Stop();
            _logger?.Debug(
                $"[RefreshPerf] phase=target.selection ms={timer.ElapsedMilliseconds} candidates={candidatesSeen} selected={targets.Count} providers={providers?.Count ?? 0} capabilityChecks={targetSelectionCache?.CapabilityCheckCount ?? 0} skippedNoProvider={skippedNoProvider} skippedNoAchievements={skippedNoAchievements} skippedHidden={skippedHiddenGames}");

            return targets;
        }

        private bool IsCachedNoAchievements(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            try
            {
                var cached = _cacheService.LoadGameData(game.Id.ToString("D"));
                return cached?.HasAchievements == false;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to read cached achievement state for game '{game.Name}'.");
                return false;
            }
        }

        public List<Guid> GetMissingGameIds(
            IReadOnlyList<IDataProvider> authenticatedProviders,
            TargetSelectionCache targetSelectionCache = null)
        {
            var timer = Stopwatch.StartNew();
            var providers = authenticatedProviders?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();
            if (providers.Count == 0)
            {
                _logger?.Info("No authenticated platforms available for missing refresh.");
                timer.Stop();
                _logger?.Debug(
                    $"[RefreshPerf] phase=target.missing ms={timer.ElapsedMilliseconds} selected=0 providers=0 capabilityChecks={targetSelectionCache?.CapabilityCheckCount ?? 0}");
                return new List<Guid>();
            }

            var cachedGameIds = new HashSet<string>(
                PlayniteAchievementsPlugin.Instance?.AchievementDataService?.GetCachedGameIds()
                    ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var allGames = _api.Database.Games.ToList();

            var missingGameIds = new List<Guid>();
            foreach (var game in allGames)
            {
                if (game == null)
                {
                    continue;
                }

                if (!BulkRefreshGameFilter.ShouldIncludeGame(game, _settings?.Persisted))
                {
                    continue;
                }

                var provider = ResolveProviderForGame(game, providers, targetSelectionCache);
                if (provider == null)
                {
                    continue;
                }

                if (!cachedGameIds.Contains(game.Id.ToString()))
                {
                    missingGameIds.Add(game.Id);
                }
            }

            if (missingGameIds.Count == 0)
            {
                _logger?.Info("No games missing achievement data found.");
                timer.Stop();
                _logger?.Debug(
                    $"[RefreshPerf] phase=target.missing ms={timer.ElapsedMilliseconds} selected=0 providers={providers.Count} capabilityChecks={targetSelectionCache?.CapabilityCheckCount ?? 0}");
                return missingGameIds;
            }

            _logger?.Info(string.Format(
                "Found {0} games missing achievement data.",
                missingGameIds.Count));
            timer.Stop();
            _logger?.Debug(
                $"[RefreshPerf] phase=target.missing ms={timer.ElapsedMilliseconds} selected={missingGameIds.Count} providers={providers.Count} capabilityChecks={targetSelectionCache?.CapabilityCheckCount ?? 0}");
            return missingGameIds;
        }

        private int GetRefreshOrderIndex(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return int.MaxValue;
            }

            return _refreshOrderIndex.TryGetValue(providerKey, out var index) ? index : int.MaxValue;
        }

        private static Dictionary<string, int> BuildOrderIndex(IEnumerable<string> providerOrder)
        {
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (providerOrder == null)
            {
                return orderIndex;
            }

            var index = 0;
            foreach (var providerKey in providerOrder.Where(key => !string.IsNullOrWhiteSpace(key)))
            {
                if (!orderIndex.ContainsKey(providerKey))
                {
                    orderIndex[providerKey] = index++;
                }
            }

            return orderIndex;
        }
    }
}
