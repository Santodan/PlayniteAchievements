using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services
{
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

        public IDataProvider ResolveProviderForGame(Game game, IReadOnlyList<IDataProvider> providers)
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
                    if (provider.IsCapable(game))
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

        private IDataProvider ResolveForcedProviderForGame(Game game, IReadOnlyList<IDataProvider> providers)
        {
            if (game == null || providers == null || providers.Count == 0)
            {
                return null;
            }

            if (GameCustomDataLookup.TryGetXeniaTitleIdOverride(game.Id, out _))
            {
                return providers.FirstOrDefault(provider =>
                    provider != null &&
                    string.Equals(provider.ProviderKey, "Xenia", StringComparison.OrdinalIgnoreCase));
            }

            if (GameCustomDataLookup.TryGetShadPS4MatchIdOverride(game.Id, out _))
            {
                return providers.FirstOrDefault(provider =>
                    provider != null &&
                    string.Equals(provider.ProviderKey, "ShadPS4", StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static bool HasForcedProviderOverride(Guid gameId)
        {
            return GameCustomDataLookup.TryGetXeniaTitleIdOverride(gameId, out _) ||
                   GameCustomDataLookup.TryGetShadPS4MatchIdOverride(gameId, out _);
        }

        public IReadOnlyList<IDataProvider> OrderProvidersForRefresh(IEnumerable<IDataProvider> providers)
        {
            return (providers ?? Enumerable.Empty<IDataProvider>())
                .Where(provider => provider != null)
                .OrderBy(provider => GetRefreshOrderIndex(provider.ProviderKey))
                .ThenBy(provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<ResolvedRefreshTarget> GetRefreshTargets(CacheRefreshOptions options, IReadOnlyList<IDataProvider> providers)
        {
            options ??= new CacheRefreshOptions();

            HashSet<Guid> excludedGameIds = null;
            if (options.SkipNoAchievementsGames && !options.BypassExclusions)
            {
                excludedGameIds = GameCustomDataLookup.GetExcludedRefreshGameIds(_settings?.Persisted);
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

            foreach (var game in candidates)
            {
                if (game == null || !seenGameIds.Add(game.Id))
                {
                    continue;
                }

                if (excludedGameIds != null && excludedGameIds.Contains(game.Id))
                {
                    skippedNoAchievements++;
                    continue;
                }

                var provider = ResolveProviderForGame(game, providers);
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

            return targets;
        }

        public List<Guid> GetMissingGameIds(IReadOnlyList<IDataProvider> authenticatedProviders)
        {
            var providers = authenticatedProviders?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();
            if (providers.Count == 0)
            {
                _logger?.Info("No authenticated platforms available for missing refresh.");
                return new List<Guid>();
            }

            var cachedGameIds = new HashSet<string>(_cacheService.GetCachedGameIds(), StringComparer.OrdinalIgnoreCase);
            var allGames = _api.Database.Games.ToList();

            var missingGameIds = new List<Guid>();
            foreach (var game in allGames)
            {
                if (game == null)
                {
                    continue;
                }

                var provider = ResolveProviderForGame(game, providers);
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
                return missingGameIds;
            }

            _logger?.Info(string.Format(
                "Found {0} games missing achievement data.",
                missingGameIds.Count));
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
