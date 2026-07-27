using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Refresh
{
    internal sealed class RefreshRequestPlanner
    {
        internal sealed class ResolvedRequest
        {
            public RefreshModeType Mode { get; set; }
            public Guid? SingleGameId { get; set; }
            public RefreshOptions Options { get; set; }
            public CacheRefreshOptions CurrentUserOptions { get; set; }
            public FriendRefreshOptions FriendOptions { get; set; }
            public IReadOnlyList<IDataProvider> ProviderScope { get; set; }
            public IReadOnlyList<IDataProvider> CurrentProviderScope { get; set; }
            public IReadOnlyList<IDataProvider> FriendProviderScope { get; set; }
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

        public ResolvedRequest Resolve(
            RefreshRequest request,
            IReadOnlyList<IDataProvider> authenticatedProviders,
            TargetSelectionCache targetSelectionCache = null)
        {
            request ??= new RefreshRequest();
            var mode = ResolveMode(request);
            var options = ResolveOptionsForRequest(request, mode);
            var resolved = ResolveUnified(
                mode,
                request.SingleGameId,
                options,
                authenticatedProviders,
                targetSelectionCache);
            resolved.ErrorLogMessage = resolved.ErrorLogMessage ?? ResolveErrorLogMessage(mode, options);
            return resolved;
        }

        /// <summary>
        /// Returns the subset of enabled providers worth probing for authentication before this
        /// request runs: providers with at least one capable candidate game (or a forced
        /// per-game override), plus friend-capable providers when the request includes friends.
        /// Candidate derivation ignores authentication state, which is unknown before the probe,
        /// so the result can only over-approximate the providers the resolved plan will use.
        /// </summary>
        public IReadOnlyList<IDataProvider> ResolveAuthProbeCandidates(
            RefreshRequest request,
            IReadOnlyList<IDataProvider> enabledProviders,
            TargetSelectionCache targetSelectionCache = null)
        {
            var providers = enabledProviders?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();
            if (request == null || providers.Count == 0)
            {
                return providers;
            }

            var mode = ResolveMode(request);
            var options = ResolveOptionsForRequest(request, mode);
            var filtered = ResolveProviders(options, providers);
            if (filtered.Count == 0)
            {
                return Array.Empty<IDataProvider>();
            }

            var wantsCurrent = (options.Subjects & RefreshSubjects.CurrentUser) == RefreshSubjects.CurrentUser;
            var wantsFriends = (options.Subjects & RefreshSubjects.Friends) == RefreshSubjects.Friends;
            var candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (wantsFriends)
            {
                foreach (var provider in filtered)
                {
                    if (provider.Friends != null)
                    {
                        candidateKeys.Add(provider.ProviderKey);
                    }
                }
            }

            if (wantsCurrent && candidateKeys.Count < filtered.Count)
            {
                var candidateGames = ResolveAuthProbeCandidateGames(mode, options, filtered, targetSelectionCache);
                foreach (var provider in _targetSelectionResolver.GetProvidersWithCapableGames(
                    candidateGames,
                    filtered,
                    targetSelectionCache))
                {
                    candidateKeys.Add(provider.ProviderKey);
                }
            }

            return filtered
                .Where(provider => candidateKeys.Contains(provider.ProviderKey))
                .ToList();
        }

        private IEnumerable<Game> ResolveAuthProbeCandidateGames(
            RefreshModeType mode,
            RefreshOptions options,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache)
        {
            if (mode == RefreshModeType.Single &&
                (options.PlayniteGameIds == null || options.PlayniteGameIds.Count == 0))
            {
                return Enumerable.Empty<Game>();
            }

            if (IsNativeBulkCurrentMode(mode, options))
            {
                // Mirrors the candidate enumeration in TargetSelectionResolver.GetRefreshTargets,
                // but without the recent-mode target limit: an authentication failure on an
                // earlier game's provider frees a slot, so providers capable only of games
                // beyond the limit must still be probed.
                var allGames = _api.Database.Games.Where(game => game != null);
                var games = mode == RefreshModeType.Recent
                    ? allGames.Where(game => game.LastActivity != null)
                    : ShouldIncludeUnplayedGames()
                        ? allGames
                        : allGames.Where(game => game.Playtime > 0);
                return BulkRefreshGameFilter.ApplyHiddenFilter(games, _settings?.Persisted);
            }

            var custom = options.ToCustomOptions();
            if (options.Scope == RefreshGameScope.SelectedGame)
            {
                custom.Scope = CustomGameScope.Explicit;
            }

            if (custom.Scope == CustomGameScope.Missing)
            {
                // Missing-scope resolution consults per-provider authentication state, which is
                // unknown before the probe; the full hidden-filtered library over-approximates it.
                return BulkRefreshGameFilter.ApplyHiddenFilter(
                    _api.Database.Games.Where(game => game != null),
                    _settings?.Persisted);
            }

            return ResolveCandidateGameIds(custom, providers, targetSelectionCache)
                .Select(gameId => _api.Database.Games.Get(gameId))
                .Where(game => game != null);
        }

        private RefreshOptions ResolveOptionsForRequest(RefreshRequest request, RefreshModeType mode)
        {
            if (request.GameIds?.Count > 0)
            {
                return new RefreshOptions
                {
                    Subjects = RefreshSubjects.CurrentUser,
                    Scope = RefreshGameScope.Explicit,
                    PlayniteGameIds = request.GameIds,
                    RespectUserExclusions = false,
                    ForceBypassExclusionsForExplicitIncludes = true
                }.Clone();
            }

            if (request.Options != null)
            {
                return request.Options.Clone();
            }

            switch (mode)
            {
                case RefreshModeType.Full:
                    return BuildCurrentModeOptions(RefreshGameScope.All);
                case RefreshModeType.Installed:
                    return BuildCurrentModeOptions(RefreshGameScope.Installed);
                case RefreshModeType.Favorites:
                    return BuildCurrentModeOptions(RefreshGameScope.Favorites);
                case RefreshModeType.Single:
                    return new RefreshOptions
                    {
                        Subjects = RefreshSubjects.CurrentUser,
                        Scope = RefreshGameScope.SelectedGame,
                        PlayniteGameIds = request.SingleGameId.HasValue && request.SingleGameId.Value != Guid.Empty
                            ? new[] { request.SingleGameId.Value }
                            : null,
                        RespectUserExclusions = false,
                        ForceBypassExclusionsForExplicitIncludes = true
                    };
                case RefreshModeType.LibrarySelected:
                    return BuildCurrentModeOptions(RefreshGameScope.LibrarySelected);
                case RefreshModeType.Missing:
                    return BuildCurrentModeOptions(RefreshGameScope.Missing);
                case RefreshModeType.Custom:
                    return new RefreshOptions
                    {
                        Subjects = RefreshSubjects.CurrentUser,
                        Scope = RefreshGameScope.All
                    };
                case RefreshModeType.FriendsRecent:
                    return BuildFriendModeOptions(RefreshGameScope.Recent);
                case RefreshModeType.FriendsFull:
                    return BuildFriendModeOptions(RefreshGameScope.All);
                case RefreshModeType.FriendsShared:
                    return BuildFriendModeOptions(RefreshGameScope.Shared);
                case RefreshModeType.FriendsInstalled:
                    return BuildFriendModeOptions(RefreshGameScope.Installed);
                case RefreshModeType.FriendsSelectedGame:
                    return new RefreshOptions
                    {
                        Subjects = RefreshSubjects.Friends,
                        Scope = RefreshGameScope.SelectedGame,
                        PlayniteGameIds = request.SingleGameId.HasValue && request.SingleGameId.Value != Guid.Empty
                            ? new[] { request.SingleGameId.Value }
                            : null,
                        ForceDefinitionRefresh = true
                    };
                case RefreshModeType.FriendsCustom:
                    return new RefreshOptions
                    {
                        Subjects = RefreshSubjects.Friends,
                        Scope = RefreshGameScope.Recent
                    };
                case RefreshModeType.Recent:
                default:
                    return BuildCurrentModeOptions(RefreshGameScope.Recent);
            }
        }

        private static RefreshOptions BuildCurrentModeOptions(RefreshGameScope scope)
        {
            return new RefreshOptions
            {
                Subjects = RefreshSubjects.CurrentUser,
                Scope = scope
            };
        }

        private static RefreshOptions BuildFriendModeOptions(RefreshGameScope scope)
        {
            return new RefreshOptions
            {
                Subjects = RefreshSubjects.Friends,
                Scope = scope
            };
        }

        private ResolvedRequest ResolveUnified(
            RefreshModeType mode,
            Guid? singleGameId,
            RefreshOptions options,
            IReadOnlyList<IDataProvider> authenticatedProviders,
            TargetSelectionCache targetSelectionCache)
        {
            options = (options ?? new RefreshOptions()).Clone();
            var providers = ResolveProviders(options, authenticatedProviders);
            var wantsCurrent = (options.Subjects & RefreshSubjects.CurrentUser) == RefreshSubjects.CurrentUser;
            var wantsFriends = (options.Subjects & RefreshSubjects.Friends) == RefreshSubjects.Friends;

            CacheRefreshOptions currentOptions = null;
            FriendRefreshOptions friendOptions = null;
            IReadOnlyList<IDataProvider> currentProviders = Array.Empty<IDataProvider>();
            IReadOnlyList<IDataProvider> friendProviders = Array.Empty<IDataProvider>();
            string emptySelection = null;
            string userMessage = null;

            if (wantsCurrent)
            {
                currentProviders = providers;
                var currentResult = ResolveCurrentOptions(
                    mode,
                    options,
                    currentProviders,
                    targetSelectionCache);
                if (currentResult.ShouldExecute)
                {
                    currentOptions = currentResult.Options;
                }
                else
                {
                    emptySelection = currentResult.EmptySelectionLogMessage;
                    userMessage = currentResult.UserMessage;
                }
            }

            if (wantsFriends)
            {
                friendProviders = providers.Where(provider => provider?.Friends != null).ToList();
                var friendResult = ResolveFriendOptions(mode, options, friendProviders);
                if (friendResult.ShouldExecute)
                {
                    friendOptions = friendResult.Options;
                    friendProviders = friendResult.ProviderScope;
                }
                else
                {
                    emptySelection = FirstNonEmpty(emptySelection, friendResult.EmptySelectionLogMessage);
                    userMessage = FirstNonEmpty(userMessage, friendResult.UserMessage);
                }
            }

            var providerScope = MergeProviderScopes(
                currentOptions != null ? currentProviders : Array.Empty<IDataProvider>(),
                friendOptions != null ? friendProviders : Array.Empty<IDataProvider>());

            return new ResolvedRequest
            {
                Mode = mode,
                SingleGameId = singleGameId,
                Options = options,
                CurrentUserOptions = currentOptions,
                FriendOptions = friendOptions,
                ProviderScope = providerScope,
                CurrentProviderScope = currentOptions != null ? currentProviders : Array.Empty<IDataProvider>(),
                FriendProviderScope = friendOptions != null ? friendProviders : Array.Empty<IDataProvider>(),
                RunProvidersInParallelOverride = options.RunProvidersInParallelOverride,
                ShouldExecute = currentOptions != null || friendOptions != null,
                EmptySelectionLogMessage = emptySelection,
                UserMessage = userMessage
            };
        }

        private IReadOnlyList<IDataProvider> ResolveProviders(
            RefreshOptions options,
            IReadOnlyList<IDataProvider> authenticatedProviders)
        {
            var providers = authenticatedProviders?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();
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

        private sealed class CurrentOptionResult
        {
            public bool ShouldExecute { get; set; }
            public CacheRefreshOptions Options { get; set; }
            public string EmptySelectionLogMessage { get; set; }
            public string UserMessage { get; set; }
        }

        private CurrentOptionResult ResolveCurrentOptions(
            RefreshModeType mode,
            RefreshOptions options,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache)
        {
            if (providers == null || providers.Count == 0)
            {
                return new CurrentOptionResult
                {
                    UserMessage = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_NoMatchingProviders")
                };
            }

            if (mode == RefreshModeType.Single &&
                (options.PlayniteGameIds == null || options.PlayniteGameIds.Count == 0))
            {
                _logger?.Info("Single refresh mode requested but no game ID provided.");
                return new CurrentOptionResult();
            }

            if (IsNativeBulkCurrentMode(mode, options))
            {
                return new CurrentOptionResult
                {
                    ShouldExecute = true,
                    Options = BuildNativeCurrentOptions(mode)
                };
            }

            var custom = options.ToCustomOptions();
            if (options.Scope == RefreshGameScope.SelectedGame)
            {
                custom.Scope = CustomGameScope.Explicit;
            }

            var mergedIds = ResolveCandidateGameIds(custom, providers, targetSelectionCache);

            var targetGameIds = new List<Guid>();
            var unserviceableGameNames = new List<string>();
            foreach (var gameId in mergedIds)
            {
                var game = _api.Database.Games.Get(gameId);
                if (game == null)
                {
                    continue;
                }

                if (_targetSelectionResolver.ResolveProviderForGame(game, providers, targetSelectionCache) != null)
                {
                    targetGameIds.Add(game.Id);
                }
                else
                {
                    unserviceableGameNames.Add(game.Name);
                }
            }

            if (targetGameIds.Count == 0)
            {
                return new CurrentOptionResult
                {
                    UserMessage = ResolveEmptyTargetsUserMessage(unserviceableGameNames),
                    EmptySelectionLogMessage = ResolveEmptySelectionMessage(mode, options.Scope)
                };
            }

            return new CurrentOptionResult
            {
                ShouldExecute = true,
                Options = new CacheRefreshOptions
                {
                    PlayniteGameIds = targetGameIds,
                    IncludeUnplayedGames = true,
                    BypassExclusions = true
                }
            };
        }

        /// <summary>
        /// Derives the candidate game IDs for a non-native current-user refresh: scope
        /// resolution plus include/exclude merging and user-exclusion filtering, before any
        /// provider capability or authentication checks.
        /// </summary>
        private List<Guid> ResolveCandidateGameIds(
            CustomRefreshOptions custom,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache)
        {
            var scopedGames = ResolveCustomScopeGames(custom, providers, targetSelectionCache);
            var includeIds = custom.IncludeGameIds?
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList() ?? new List<Guid>();
            var excludeIds = custom.ExcludeGameIds?
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList() ?? new List<Guid>();
            var explicitIncludeSet = new HashSet<Guid>(includeIds);
            var explicitExcludeSet = new HashSet<Guid>(excludeIds);

            var mergedIds = new List<Guid>();
            var seenIds = new HashSet<Guid>();
            foreach (var game in scopedGames)
            {
                if (game != null && game.Id != Guid.Empty && seenIds.Add(game.Id))
                {
                    mergedIds.Add(game.Id);
                }
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
                mergedIds = mergedIds.Where(gameId => !explicitExcludeSet.Contains(gameId)).ToList();
            }

            if (custom.RespectUserExclusions)
            {
                var excludedByUser = GameCustomDataLookup.GetExcludedRefreshGameIds(_settings?.Persisted);
                if (excludedByUser?.Count > 0)
                {
                    mergedIds = mergedIds
                        .Where(gameId =>
                            !excludedByUser.Contains(gameId) ||
                            (custom.ForceBypassExclusionsForExplicitIncludes &&
                             explicitIncludeSet.Contains(gameId) &&
                             !explicitExcludeSet.Contains(gameId)))
                        .ToList();
                }
            }

            return mergedIds;
        }

        /// <summary>
        /// Picks the empty-target dialog text: when candidates matched the request but every one
        /// was dropped because no enabled and authenticated provider services it, names those
        /// games; otherwise falls back to the generic no-matching-games message.
        /// </summary>
        private static string ResolveEmptyTargetsUserMessage(IReadOnlyList<string> unserviceableGameNames)
        {
            var names = unserviceableGameNames?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList() ?? new List<string>();
            if (names.Count == 0)
            {
                return ResourceProvider.GetString("LOCPlayAch_CustomRefresh_NoMatchingGames");
            }

            const int maxDisplayedNames = 5;
            var displayNames = string.Join(", ", names.Take(maxDisplayedNames));
            if (names.Count > maxDisplayedNames)
            {
                displayNames += ", ...";
            }

            var format = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_NoCapableProviderForGames");
            return string.Format(format, displayNames);
        }

        private bool IsNativeBulkCurrentMode(RefreshModeType mode, RefreshOptions options)
        {
            if (options == null ||
                options.ProviderKeys?.Count > 0 ||
                options.PlayniteGameIds?.Count > 0 ||
                options.ExcludeGameIds?.Count > 0 ||
                options.IncludeUnplayedOverride.HasValue ||
                options.RecentLimitOverride.HasValue ||
                !options.RespectUserExclusions)
            {
                return false;
            }

            return mode == RefreshModeType.Recent || mode == RefreshModeType.Full;
        }

        private CacheRefreshOptions BuildNativeCurrentOptions(RefreshModeType mode)
        {
            if (mode == RefreshModeType.Full)
            {
                return new CacheRefreshOptions
                {
                    RecentRefreshMode = false,
                    IncludeUnplayedGames = _settings.Persisted.IncludeUnplayedGames
                };
            }

            return new CacheRefreshOptions
            {
                RecentRefreshMode = true,
                RecentRefreshGamesCount = _settings?.Persisted?.RecentRefreshGamesCount ?? 10,
                IncludeUnplayedGames = _settings.Persisted.IncludeUnplayedGames
            };
        }

        private sealed class FriendOptionResult
        {
            public bool ShouldExecute { get; set; }
            public FriendRefreshOptions Options { get; set; }
            public IReadOnlyList<IDataProvider> ProviderScope { get; set; } = Array.Empty<IDataProvider>();
            public string EmptySelectionLogMessage { get; set; }
            public string UserMessage { get; set; }
        }

        private FriendOptionResult ResolveFriendOptions(
            RefreshModeType mode,
            RefreshOptions options,
            IReadOnlyList<IDataProvider> friendProviders)
        {
            var providers = friendProviders?.Where(provider => provider?.Friends != null).ToList() ??
                            new List<IDataProvider>();
            if (providers.Count == 0)
            {
                return new FriendOptionResult
                {
                    UserMessage = ResourceProvider.GetString("LOCPlayAch_FriendsRefresh_NoAuthenticatedProviders")
                };
            }

            var friendScope = ResolveFriendScope(options.Scope);

            // Global unowned-games gate: Full is the only scope that scans games outside the
            // current user's library, so when unowned friend games are excluded every Full
            // request (UI mode, per-friend refresh, scheduled run) is clamped to Shared here,
            // the single seam all friend refreshes pass through.
            if (friendScope == FriendRefreshScope.Full &&
                _settings?.Persisted?.IncludeUnownedFriendGames != true)
            {
                friendScope = FriendRefreshScope.Shared;
            }

            IReadOnlyCollection<Guid> playniteGameIds = options.PlayniteGameIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            IReadOnlyCollection<int> providerAppIds = options.ProviderAppIds?
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            IReadOnlyCollection<string> providerGameKeys = options.ProviderGameKeys?
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (friendScope == FriendRefreshScope.Installed)
            {
                playniteGameIds = GetInstalledGameIds();
            }

            if (friendScope == FriendRefreshScope.SelectedGame &&
                (playniteGameIds == null || playniteGameIds.Count == 0) &&
                (providerAppIds == null || providerAppIds.Count == 0) &&
                (providerGameKeys == null || providerGameKeys.Count == 0))
            {
                return new FriendOptionResult
                {
                    EmptySelectionLogMessage = "No selected game provided for friends selected-game refresh."
                };
            }

            if (friendScope == FriendRefreshScope.Installed &&
                (playniteGameIds == null || playniteGameIds.Count == 0))
            {
                return new FriendOptionResult
                {
                    EmptySelectionLogMessage = mode == RefreshModeType.FriendsInstalled
                        ? "No installed games found for friends refresh."
                        : "No installed games found for custom friends refresh."
                };
            }

            return new FriendOptionResult
            {
                ShouldExecute = true,
                ProviderScope = providers,
                Options = new FriendRefreshOptions
                {
                    Scope = friendScope,
                    PlayniteGameIds = playniteGameIds,
                    ProviderAppIds = providerAppIds,
                    ProviderGameKeys = providerGameKeys,
                    FriendAccounts = options.FriendAccounts,
                    FriendExternalUserIds = options.FriendExternalUserIds,
                    // SelectedGame/Full are user-initiated "refresh this" actions, so they re-download
                    // schemas by default; PreferCachedDefinitions lets latency-sensitive programmatic
                    // callers (the in-game poller) reuse cached Ok schemas instead.
                    ForceDefinitionRefresh = !options.PreferCachedDefinitions &&
                                             (options.ForceDefinitionRefresh ||
                                              friendScope == FriendRefreshScope.SelectedGame ||
                                              friendScope == FriendRefreshScope.Full)
                }
            };
        }

        private static FriendRefreshScope ResolveFriendScope(RefreshGameScope scope)
        {
            switch (scope)
            {
                case RefreshGameScope.All: return FriendRefreshScope.Full;
                case RefreshGameScope.Shared: return FriendRefreshScope.Shared;
                case RefreshGameScope.Installed: return FriendRefreshScope.Installed;
                case RefreshGameScope.SelectedGame: return FriendRefreshScope.SelectedGame;
                case RefreshGameScope.Explicit: return FriendRefreshScope.Custom;
                case RefreshGameScope.Recent:
                default:
                    return FriendRefreshScope.Recent;
            }
        }

        private List<Game> ResolveCustomScopeGames(
            CustomRefreshOptions options,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache)
        {
            options ??= new CustomRefreshOptions();
            var includeUnplayed = options.IncludeUnplayedOverride ??
                                  (_settings?.Persisted?.IncludeUnplayedGames ?? true);
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
                    scopedGames = allGames.Where(IsInstalledOrHasOverride);
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
                    var recentLimit = Math.Max(1, options.RecentLimitOverride ??
                                                  (_settings?.Persisted?.RecentRefreshGamesCount ?? 10));
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
                    scopedGames = _api.MainView.SelectedGames?.Where(game => game != null) ??
                                  Enumerable.Empty<Game>();
                    break;
                case CustomGameScope.Missing:
                    scopedGames = GetMissingGameIds(providers, targetSelectionCache)
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

            if (ShouldApplyHiddenFilter(options.Scope))
            {
                scopedGames = BulkRefreshGameFilter.ApplyHiddenFilter(scopedGames, _settings?.Persisted);
            }

            return scopedGames.ToList();
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

                _logger?.Warn($"Unknown refresh mode key: {request.ModeKey}. Falling back to Recent.");
            }

            return RefreshModeType.Recent;
        }

        private List<Guid> GetInstalledGameIds()
        {
            IEnumerable<Game> games = _api.Database.Games.Where(game => game != null && IsInstalledOrHasOverride(game));
            if (!ShouldIncludeUnplayedGames())
            {
                games = games.Where(game => game.Playtime > 0);
            }

            games = BulkRefreshGameFilter.ApplyHiddenFilter(games, _settings?.Persisted);
            return games.Select(game => game.Id).ToList();
        }

        private static bool IsInstalledOrHasOverride(Game game)
        {
            return game != null &&
                   (game.IsInstalled || GameCustomDataLookup.TryGetProviderOverride(game.Id, out _));
        }

        private List<Guid> GetMissingGameIds(
            IReadOnlyList<IDataProvider> authenticatedProviders,
            TargetSelectionCache targetSelectionCache)
        {
            var missingIds = _targetSelectionResolver.GetMissingGameIds(
                authenticatedProviders,
                targetSelectionCache);
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

        private static bool ShouldApplyHiddenFilter(CustomGameScope scope)
        {
            switch (scope)
            {
                case CustomGameScope.All:
                case CustomGameScope.Installed:
                case CustomGameScope.Favorites:
                case CustomGameScope.Recent:
                case CustomGameScope.Missing:
                    return true;
                default:
                    return false;
            }
        }

        private static IReadOnlyList<IDataProvider> MergeProviderScopes(
            IReadOnlyList<IDataProvider> currentProviders,
            IReadOnlyList<IDataProvider> friendProviders)
        {
            var merged = new List<IDataProvider>();
            foreach (var provider in (currentProviders ?? Array.Empty<IDataProvider>())
                .Concat(friendProviders ?? Array.Empty<IDataProvider>()))
            {
                if (provider != null && !merged.Contains(provider))
                {
                    merged.Add(provider);
                }
            }

            return merged;
        }

        private static string ResolveErrorLogMessage(RefreshModeType mode, RefreshOptions options)
        {
            if ((options?.Subjects & RefreshSubjects.All) == RefreshSubjects.All)
            {
                return "Unified refresh failed.";
            }

            switch (mode)
            {
                case RefreshModeType.Full: return "Full achievement refresh failed.";
                case RefreshModeType.Installed: return "Installed games refresh failed.";
                case RefreshModeType.Favorites: return "Favorites refresh failed.";
                case RefreshModeType.Single: return "Single game refresh failed.";
                case RefreshModeType.LibrarySelected: return "Library selected games refresh failed.";
                case RefreshModeType.Missing: return "Missing games refresh failed.";
                case RefreshModeType.Custom: return "Custom refresh failed.";
                case RefreshModeType.FriendsRecent: return "Recent friends refresh failed.";
                case RefreshModeType.FriendsFull: return "Full friends refresh failed.";
                case RefreshModeType.FriendsShared: return "Shared friends refresh failed.";
                case RefreshModeType.FriendsInstalled: return "Installed friends refresh failed.";
                case RefreshModeType.FriendsSelectedGame: return "Selected-game friends refresh failed.";
                case RefreshModeType.FriendsCustom: return "Custom friends refresh failed.";
                case RefreshModeType.Recent:
                default:
                    return "Recent refresh failed.";
            }
        }

        private static string ResolveEmptySelectionMessage(RefreshModeType mode, RefreshGameScope scope)
        {
            if (mode == RefreshModeType.LibrarySelected || scope == RefreshGameScope.LibrarySelected)
            {
                return "No games selected in Playnite library for refresh.";
            }

            if (mode == RefreshModeType.Installed || scope == RefreshGameScope.Installed)
            {
                return "No installed games found for refresh.";
            }

            if (mode == RefreshModeType.Favorites || scope == RefreshGameScope.Favorites)
            {
                return "No favorite games found for refresh.";
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
    }
}
