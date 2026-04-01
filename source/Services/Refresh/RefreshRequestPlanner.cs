using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services
{
    internal sealed class RefreshRequestPlanner
    {
        internal sealed class ResolvedRequest
        {
            public RefreshModeType Mode { get; set; }
            public Guid? SingleGameId { get; set; }
            public CacheRefreshOptions Options { get; set; }
            public bool ForceIconRefresh { get; set; }
            public IReadOnlyList<IDataProvider> ProviderScope { get; set; }
            public bool? RunProvidersInParallelOverride { get; set; }
            public string ErrorLogMessage { get; set; }
            public string EmptySelectionLogMessage { get; set; }
            public string UserMessage { get; set; }
            public bool ShouldExecute { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly TargetSelectionResolver _targetSelectionResolver;

        public RefreshRequestPlanner(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            TargetSelectionResolver targetSelectionResolver)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _targetSelectionResolver = targetSelectionResolver ?? throw new ArgumentNullException(nameof(targetSelectionResolver));
        }

        public ResolvedRequest Resolve(RefreshRequest request, IReadOnlyList<IDataProvider> authenticatedProviders)
        {
            request ??= new RefreshRequest();

            if (request.GameIds != null && request.GameIds.Count > 0)
            {
                var ids = request.GameIds
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                return ResolveGameIdList(
                    RefreshModeType.LibrarySelected,
                    ids,
                    "Library selected games refresh failed.",
                    "No games selected in Playnite library for refresh.",
                    bypassExclusions: true,
                    forceIconRefresh: request.ForceIconRefresh);
            }

            var mode = ResolveMode(request);
            switch (mode)
            {
                case RefreshModeType.Recent:
                    return new ResolvedRequest
                    {
                        Mode = mode,
                        ShouldExecute = true,
                        ForceIconRefresh = request.ForceIconRefresh,
                        ErrorLogMessage = "Recent refresh failed.",
                        Options = BuildRecentOptions()
                    };

                case RefreshModeType.Full:
                    return new ResolvedRequest
                    {
                        Mode = mode,
                        ShouldExecute = true,
                        ForceIconRefresh = request.ForceIconRefresh,
                        ErrorLogMessage = "Full achievement refresh failed.",
                        Options = BuildFullOptions()
                    };

                case RefreshModeType.Installed:
                    return ResolveGameIdList(
                        mode,
                        GetInstalledGameIds(),
                        "Installed games refresh failed.",
                        "No installed games found for refresh.",
                        forceIconRefresh: request.ForceIconRefresh);

                case RefreshModeType.Favorites:
                    return ResolveGameIdList(
                        mode,
                        GetFavoriteGameIds(),
                        "Favorites refresh failed.",
                        "No favorite games found for refresh.",
                        forceIconRefresh: request.ForceIconRefresh);

                case RefreshModeType.Single:
                    if (!request.SingleGameId.HasValue)
                    {
                        _logger?.Info("Single refresh mode requested but no game ID provided.");
                        return new ResolvedRequest
                        {
                            Mode = mode,
                            ForceIconRefresh = request.ForceIconRefresh,
                            ShouldExecute = false
                        };
                    }

                    return new ResolvedRequest
                    {
                        Mode = mode,
                        SingleGameId = request.SingleGameId.Value,
                        ShouldExecute = true,
                        ForceIconRefresh = request.ForceIconRefresh,
                        ErrorLogMessage = "Single game refresh failed.",
                        Options = BuildSingleGameOptions(request.SingleGameId.Value)
                    };

                case RefreshModeType.LibrarySelected:
                    return ResolveGameIdList(
                        mode,
                        GetLibrarySelectedGameIds(),
                        "Library selected games refresh failed.",
                        "No games selected in Playnite library for refresh.",
                        bypassExclusions: true,
                        forceIconRefresh: request.ForceIconRefresh);

                case RefreshModeType.Missing:
                    return ResolveGameIdList(
                        mode,
                        GetMissingGameIds(authenticatedProviders),
                        "Missing games refresh failed.",
                        forceIconRefresh: request.ForceIconRefresh);

                case RefreshModeType.Custom:
                    return ResolveCustom(request.CustomOptions, authenticatedProviders, request.ForceIconRefresh);

                default:
                    _logger?.Warn(string.Format(
                        "Unknown refresh mode: {0}. Falling back to Recent.",
                        mode));
                    return new ResolvedRequest
                    {
                        Mode = RefreshModeType.Recent,
                        ShouldExecute = true,
                        ForceIconRefresh = request.ForceIconRefresh,
                        ErrorLogMessage = "Recent refresh failed.",
                        Options = BuildRecentOptions()
                    };
            }
        }

        private RefreshModeType ResolveMode(RefreshRequest request)
        {
            if (request.Mode.HasValue)
            {
                return request.Mode.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.ModeKey))
            {
                if (Enum.TryParse(request.ModeKey, out RefreshModeType parsed))
                {
                    return parsed;
                }

                _logger?.Warn(string.Format(
                    "Unknown refresh mode key: {0}. Falling back to Recent.",
                    request.ModeKey));
            }

            return RefreshModeType.Recent;
        }

        private ResolvedRequest ResolveCustom(
            CustomRefreshOptions options,
            IReadOnlyList<IDataProvider> authenticatedProviders,
            bool forceIconRefresh)
        {
            var resolvedOptions = options?.Clone() ?? new CustomRefreshOptions();
            var providers = ResolveCustomProviders(resolvedOptions, authenticatedProviders);
            var runProvidersInParallel = resolvedOptions.RunProvidersInParallelOverride ?? (_settings?.Persisted?.EnableParallelProviderRefresh ?? true);

            if (providers.Count == 0)
            {
                return new ResolvedRequest
                {
                    Mode = RefreshModeType.Custom,
                    ForceIconRefresh = forceIconRefresh,
                    ShouldExecute = false,
                    UserMessage = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_NoMatchingProviders")
                };
            }

            var scopedGames = ResolveCustomScopeGames(resolvedOptions, providers);
            var includeIds = resolvedOptions.IncludeGameIds?
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList() ?? new List<Guid>();
            var excludeIds = resolvedOptions.ExcludeGameIds?
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList() ?? new List<Guid>();

            var explicitIncludeSet = new HashSet<Guid>(includeIds);
            var explicitExcludeSet = new HashSet<Guid>(excludeIds);

            var mergedIds = new List<Guid>();
            var seenIds = new HashSet<Guid>();
            foreach (var game in scopedGames)
            {
                if (game == null || game.Id == Guid.Empty || !seenIds.Add(game.Id))
                {
                    continue;
                }

                mergedIds.Add(game.Id);
            }

            foreach (var includeId in includeIds)
            {
                if (seenIds.Add(includeId))
                {
                    mergedIds.Add(includeId);
                }
            }

            if (explicitExcludeSet.Count > 0)
            {
                mergedIds = mergedIds
                    .Where(gameId => !explicitExcludeSet.Contains(gameId))
                    .ToList();
            }

            if (resolvedOptions.RespectUserExclusions)
            {
                var excludedByUser = GameCustomDataLookup.GetExcludedRefreshGameIds(_settings?.Persisted);
                if (excludedByUser != null && excludedByUser.Count > 0)
                {
                    mergedIds = mergedIds
                        .Where(gameId =>
                        {
                            if (!excludedByUser.Contains(gameId))
                            {
                                return true;
                            }

                            return resolvedOptions.ForceBypassExclusionsForExplicitIncludes &&
                                   explicitIncludeSet.Contains(gameId) &&
                                   !explicitExcludeSet.Contains(gameId);
                        })
                        .ToList();
                }
            }

            var resolvedGames = mergedIds
                .Select(gameId => _api.Database.Games.Get(gameId))
                .Where(game => game != null)
                .ToList();

            var targetGameIds = resolvedGames
                .Where(game => _targetSelectionResolver.ResolveProviderForGame(game, providers) != null)
                .Select(game => game.Id)
                .ToList();

            if (targetGameIds.Count == 0)
            {
                return new ResolvedRequest
                {
                    Mode = RefreshModeType.Custom,
                    ForceIconRefresh = forceIconRefresh,
                    ShouldExecute = false,
                    UserMessage = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_NoMatchingGames")
                };
            }

            return new ResolvedRequest
            {
                Mode = RefreshModeType.Custom,
                ShouldExecute = true,
                ForceIconRefresh = forceIconRefresh,
                ErrorLogMessage = "Custom refresh failed.",
                ProviderScope = providers,
                RunProvidersInParallelOverride = runProvidersInParallel,
                Options = new CacheRefreshOptions
                {
                    PlayniteGameIds = targetGameIds,
                    IncludeUnplayedGames = true,
                    BypassExclusions = true
                }
            };
        }

        private IReadOnlyList<IDataProvider> ResolveCustomProviders(
            CustomRefreshOptions options,
            IReadOnlyList<IDataProvider> authenticatedProviders)
        {
            var providers = authenticatedProviders?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();
            if (providers.Count == 0)
            {
                return Array.Empty<IDataProvider>();
            }

            var requestedKeys = options?.ProviderKeys?
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requestedKeys == null || requestedKeys.Count == 0)
            {
                return providers;
            }

            var requestedSet = new HashSet<string>(requestedKeys, StringComparer.OrdinalIgnoreCase);
            return providers
                .Where(provider => requestedSet.Contains(provider.ProviderKey))
                .ToList();
        }

        private List<Game> ResolveCustomScopeGames(
            CustomRefreshOptions options,
            IReadOnlyList<IDataProvider> providers)
        {
            options ??= new CustomRefreshOptions();
            var includeUnplayed = options.IncludeUnplayedOverride ?? (_settings?.Persisted?.IncludeUnplayedGames ?? true);
            var allGames = _api.Database.Games.Where(game => game != null).ToList();

            IEnumerable<Game> scopedGames;
            switch (options.Scope)
            {
                case CustomGameScope.All:
                    scopedGames = allGames;
                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }
                    break;

                case CustomGameScope.Installed:
                    scopedGames = allGames.Where(game => game.IsInstalled);
                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }
                    break;

                case CustomGameScope.Favorites:
                    scopedGames = allGames.Where(game => game.Favorite);
                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }
                    break;

                case CustomGameScope.Recent:
                    var recentLimit = Math.Max(1, options.RecentLimitOverride ?? (_settings?.Persisted?.RecentRefreshGamesCount ?? 10));
                    scopedGames = allGames
                        .Where(game => game.LastActivity.HasValue)
                        .OrderByDescending(game => game.LastActivity.Value);

                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }

                    scopedGames = scopedGames.Take(recentLimit);
                    break;

                case CustomGameScope.LibrarySelected:
                    scopedGames = _api.MainView.SelectedGames?.Where(game => game != null) ?? Enumerable.Empty<Game>();
                    break;

                case CustomGameScope.Missing:
                    var missingIds = GetMissingGameIds(providers);
                    scopedGames = missingIds
                        .Select(gameId => _api.Database.Games.Get(gameId))
                        .Where(game => game != null);
                    break;

                case CustomGameScope.Explicit:
                    scopedGames = Enumerable.Empty<Game>();
                    break;

                default:
                    scopedGames = allGames;
                    break;
            }

            return scopedGames.ToList();
        }

        private ResolvedRequest ResolveGameIdList(
            RefreshModeType mode,
            List<Guid> gameIds,
            string errorLogMessage,
            string emptySelectionLogMessage = null,
            bool bypassExclusions = false,
            bool forceIconRefresh = false)
        {
            if (gameIds == null || gameIds.Count == 0)
            {
                return new ResolvedRequest
                {
                    Mode = mode,
                    ForceIconRefresh = forceIconRefresh,
                    ShouldExecute = false,
                    EmptySelectionLogMessage = emptySelectionLogMessage
                };
            }

            return new ResolvedRequest
            {
                Mode = mode,
                ShouldExecute = true,
                ForceIconRefresh = forceIconRefresh,
                ErrorLogMessage = errorLogMessage,
                Options = new CacheRefreshOptions
                {
                    PlayniteGameIds = gameIds,
                    IncludeUnplayedGames = true,
                    BypassExclusions = bypassExclusions
                }
            };
        }

        private CacheRefreshOptions BuildFullOptions()
        {
            return new CacheRefreshOptions
            {
                RecentRefreshMode = false,
                IncludeUnplayedGames = _settings.Persisted.IncludeUnplayedGames
            };
        }

        private CacheRefreshOptions BuildRecentOptions()
        {
            return new CacheRefreshOptions
            {
                RecentRefreshMode = true,
                RecentRefreshGamesCount = _settings?.Persisted?.RecentRefreshGamesCount ?? 10,
                IncludeUnplayedGames = _settings.Persisted.IncludeUnplayedGames
            };
        }

        private static CacheRefreshOptions BuildSingleGameOptions(Guid playniteGameId)
        {
            return new CacheRefreshOptions
            {
                PlayniteGameIds = new[] { playniteGameId },
                IncludeUnplayedGames = true,
                BypassExclusions = true
            };
        }

        private List<Guid> GetInstalledGameIds()
        {
            var games = _api.Database.Games
                .Where(g => g != null && g.IsInstalled);

            if (!ShouldIncludeUnplayedGames())
            {
                games = games.Where(g => g.Playtime > 0);
            }

            return games
                .Select(g => g.Id)
                .ToList();
        }

        private List<Guid> GetMissingGameIds(IReadOnlyList<IDataProvider> authenticatedProviders)
        {
            var missingIds = _targetSelectionResolver.GetMissingGameIds(authenticatedProviders);
            if (ShouldIncludeUnplayedGames())
            {
                return missingIds;
            }

            return missingIds
                .Where(gameId => _api.Database.Games.Get(gameId)?.Playtime > 0)
                .ToList();
        }

        private bool ShouldIncludeUnplayedGames()
        {
            return _settings?.Persisted?.IncludeUnplayedGames ?? true;
        }

        private List<Guid> GetFavoriteGameIds()
        {
            return _api.Database.Games
                .Where(g => g != null && g.Favorite)
                .Select(g => g.Id)
                .ToList();
        }

        private List<Guid> GetLibrarySelectedGameIds()
        {
            return _api.MainView.SelectedGames?
                .Where(g => g != null)
                .Select(g => g.Id)
                .ToList();
        }
    }
}
