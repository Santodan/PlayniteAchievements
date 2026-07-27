using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.ProgressReporting;
using PlayniteAchievements.Services.Friends;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using FriendRefreshProgressSession = PlayniteAchievements.Services.Refresh.RefreshRuntime.FriendRefreshProgressSession;

namespace PlayniteAchievements.Services.Refresh
{
    /// <summary>
    /// Coordinates friend refreshes: roster preparation, ownership refresh, unowned-definition
    /// discovery, achievement scraping, and avatar/image downloads. Extracted from RefreshRuntime,
    /// which delegates its friend-refresh entry points here.
    /// </summary>
    internal sealed class FriendRefreshCoordinator
    {
        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly ICacheManager _cacheService;
        private readonly AchievementIconService _achievementIconService;
        private readonly PlayniteAchievements.Providers.ProviderRegistry _providerRegistry;
        private readonly IReadOnlyList<IDataProvider> _providers;

        internal FriendRefreshCoordinator(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            ICacheManager cacheService,
            AchievementIconService achievementIconService,
            PlayniteAchievements.Providers.ProviderRegistry providerRegistry,
            IReadOnlyList<IDataProvider> providers)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _cacheService = cacheService;
            _achievementIconService = achievementIconService;
            _providerRegistry = providerRegistry;
            _providers = providers;
        }

        internal void PromoteMatchingProviderOnlyFriendGame(IDataProvider provider, GameAchievementData data)
        {
            if (_friendCache == null ||
                data?.PlayniteGameId == null ||
                data.PlayniteGameId.Value == Guid.Empty)
            {
                return;
            }

            var providerKey = string.IsNullOrWhiteSpace(data.ProviderKey)
                ? provider?.ProviderKey
                : data.ProviderKey;
            if (string.IsNullOrWhiteSpace(providerKey) ||
                !FriendRefreshWorkPolicy.HasProviderGameIdentity(data.AppId, data.ProviderGameKey))
            {
                return;
            }

            var promotion = _friendCache.PromoteProviderOnlyGameToPlayniteBacked(
                providerKey,
                data.AppId,
                data.ProviderGameKey,
                data.PlayniteGameId.Value);
            if (promotion == null)
            {
                return;
            }

            if (!promotion.Success)
            {
                _logger?.Warn(
                    $"Failed to promote provider-only friend game for {providerKey}/{GetProviderGameCacheKey(data.AppId, data.ProviderGameKey)}: {promotion.ErrorMessage}");
                return;
            }

            // The owned game already downloaded its own fresh icons and the promoted friend rows
            // now reference the owned definitions, so the provider-only friend icons are orphaned.
            if (promotion.WrittenCount > 0)
            {
                var friendCacheKey = GetProviderGameCacheKey(data.AppId, data.ProviderGameKey);
                _achievementIconService.DeleteFriendGameIconCache(providerKey, friendCacheKey);
            }
        }

        // -----------------------------
        // Friends refresh helpers
        // -----------------------------

        private const int FriendRefreshParallelism = 4;
        private const int FriendInvalidationFlushMinCompletions = 25;
        private static readonly TimeSpan FriendInvalidationFlushInterval = TimeSpan.FromSeconds(2);

        private IFriendCacheManager _friendCache => _cacheService as IFriendCacheManager;

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<IDataProvider> providerScope,
            FriendRefreshOptions options,
            Action<string, int, int> reportProgress,
            CancellationToken cancel = default)
        {
            var payload = new RebuildPayload();
            if (_friendCache == null)
            {
                return payload;
            }

            options = NormalizeOptions(options);
            var providers = (providerScope ?? Array.Empty<IDataProvider>())
                .Where(provider => provider?.Friends != null)
                .ToList();

            var progress = new FriendRefreshProgressSession(reportProgress);
            progress.InitializeProviderTotal(providers.Count);
            var contexts = new List<FriendProviderRefreshContext>();
            var payloadLock = new object();
            var perf = new FriendRefreshPerfSession(_logger, options, providers.Count);
            using (var friendInvalidationBatch = _friendCache.BeginFriendCacheInvalidationBatch())
            {
                try
                {
                    var prepareTimer = Stopwatch.StartNew();
                    for (var i = 0; i < providers.Count; i++)
                    {
                        var provider = providers[i];
                        cancel.ThrowIfCancellationRequested();
                        var context = await PrepareProviderRefreshAsync(
                            provider.Friends,
                            options,
                            payload,
                            progress,
                            i,
                            providers.Count,
                            cancel).ConfigureAwait(false);
                        if (context != null)
                        {
                            contexts.Add(context);
                        }
                    }
                    perf.LogPrepare(prepareTimer, contexts);
                    friendInvalidationBatch?.Flush();

                    await RefreshPreparedFriendContextsAsync(
                        contexts,
                        options,
                        payload,
                        payloadLock,
                        progress,
                        perf,
                        friendInvalidationBatch,
                        cancel).ConfigureAwait(false);

                    perf.LogTotal(payload, contexts);
                    friendInvalidationBatch?.Flush();
                }
                finally
                {
                    EndFriendRefreshContexts(contexts);
                }
            }

            return payload;
        }

        /// <summary>
        /// Work volume of the friend scrape portion of a run, for the shared LOH compaction gate
        /// in RefreshRuntime. Zero when the payload carries no friend summary (e.g. current-user
        /// only runs).
        /// </summary>
        internal static int GetFriendScrapeVolume(RebuildPayload payload)
        {
            var summary = payload?.FriendSummary;
            if (summary == null)
            {
                return 0;
            }

            return Math.Max(
                summary.CandidatesRefreshed,
                Math.Max(summary.OwnershipRowsWritten, summary.AchievementsSaved));
        }

        internal async Task RefreshPreparedFriendContextsAsync(
            IReadOnlyList<FriendProviderRefreshContext> contexts,
            FriendRefreshOptions options,
            RebuildPayload payload,
            object payloadLock,
            FriendRefreshProgressSession progress,
            FriendRefreshPerfSession perf,
            IFriendCacheInvalidationBatch friendInvalidationBatch,
            CancellationToken cancel)
        {
            var activeContexts = contexts
                .Where(context => context?.CanContinue == true)
                .ToList();

            perf.LogPhase(
                Stopwatch.StartNew(),
                "friend.rosterMetadata",
                "skipped=true reason=settings-friends-list-only");

            var ownershipBefore = payload.FriendSummary.OwnershipPagesRefreshed;
            var ownershipRowsBefore = payload.FriendSummary.OwnershipRowsWritten;
            var ownershipTimer = Stopwatch.StartNew();
            activeContexts = activeContexts
                .Where(context => context.CanContinue)
                .ToList();
            var logicalFriends = BuildLogicalFriendGroups(activeContexts, options);
            var requiresOwnershipRefresh = logicalFriends.Count > 0;
            progress.InitializeFriendLibraryTotal(logicalFriends.Count);
            if (requiresOwnershipRefresh)
            {
                await RefreshOwnershipByLogicalFriendAsync(
                    logicalFriends,
                    options,
                    payload,
                    payloadLock,
                    progress,
                    cancel).ConfigureAwait(false);
                friendInvalidationBatch?.Flush();
            }
            perf.LogPhase(
                ownershipTimer,
                "friend.ownership",
                $"logicalFriends={logicalFriends.Count} pages={payload.FriendSummary.OwnershipPagesRefreshed - ownershipBefore} rows={payload.FriendSummary.OwnershipRowsWritten - ownershipRowsBefore} required={requiresOwnershipRefresh}");

            // Pre-pass: compute every context's definition plan (a read-only cache read) and set the
            // definitions sub-band total once, before any definition/probe completion is reported. This
            // is what keeps the monotonic clamp from freezing the bar during the definitions phase.
            var planTimer = Stopwatch.StartNew();
            var definitionChecksTotal = 0;
            foreach (var context in activeContexts)
            {
                if (context.DiscoverUnowned && context.Preparation.CanRefreshAchievements)
                {
                    context.DefinitionPlan = ComputeUnownedDefinitionPlan(
                        context.ProviderKey,
                        context.OwnershipSnapshots,
                        options);
                    definitionChecksTotal += context.DefinitionPlan.TotalDefinitionChecks;
                }
            }
            perf.LogDefinitionPlan(planTimer, activeContexts, definitionChecksTotal);

            progress.InitializeDefinitionChecksTotal(definitionChecksTotal);

            var definitionsTimer = Stopwatch.StartNew();
            foreach (var context in activeContexts)
            {
                if (context.DiscoverUnowned && context.Preparation.CanRefreshAchievements)
                {
                    await RefreshUnownedDefinitionsAndOwnershipAsync(
                        context.Provider,
                        context.ProviderKey,
                        context.OwnershipSnapshots,
                        context.ProbedProviderOnlyAchievementKeys,
                        context.DefinitionPlan,
                        payload,
                        payloadLock,
                        progress,
                        friendInvalidationBatch,
                        cancel).ConfigureAwait(false);
                }
            }
            perf.LogPhase(definitionsTimer, "friend.definitions", $"checks={definitionChecksTotal}");
            friendInvalidationBatch?.Flush();

            // Mapped-game scrape work for the discovery scopes (Full/Shared/Installed) is built from the
            // fresh ownership snapshot (game-centric, live hints); provider-only games were already
            // scraped by the definition/probe phase above. Recent draws from the whole cached friend
            // library filtered by the ownership-derived recency gate, and SelectedGame targets a specific
            // library game across friends — both source from the cache-backed candidate loader. The
            // scrape total is only knowable now, so its sub-band is initialized here, still ahead of its
            // first completion.
            var loadCandidatesTimer = Stopwatch.StartNew();
            var achievementWorkItems = new List<FriendAchievementWorkItem>();
            foreach (var context in activeContexts)
            {
                if (context.OwnershipSnapshots != null && FriendRefreshWorkPolicy.UsesSnapshotCandidateBuilder(options))
                {
                    if (!context.Preparation.CanRefreshAchievements)
                    {
                        _logger?.Debug($"Skipping {context.ProviderKey} friend achievement scrapes: provider did not prepare achievement auth.");
                        MarkAuthFailure(payload, context.ProviderKey, true);
                        continue;
                    }

                    achievementWorkItems.AddRange(
                        BuildMappedAchievementWorkItemsFromSnapshots(context, options, payload));
                }
                else
                {
                    achievementWorkItems.AddRange(
                        LoadAchievementWorkItems(new[] { context }, options, payload));
                }
            }
            perf.LogCandidateLoad(loadCandidatesTimer, activeContexts, achievementWorkItems.Count);
            progress.InitializeAchievementScrapeTotal(achievementWorkItems.Count);
            var achievementsBefore = payload.FriendSummary.CandidatesRefreshed;
            var achievementsSavedBefore = payload.FriendSummary.AchievementsSaved;
            var achievementTimer = Stopwatch.StartNew();
            await RefreshAchievementWorkItemsAsync(
                achievementWorkItems,
                payload,
                payloadLock,
                progress,
                friendInvalidationBatch,
                cancel).ConfigureAwait(false);
            friendInvalidationBatch?.Flush();
            perf.LogPhase(
                achievementTimer,
                "friend.achievements",
                $"workItems={achievementWorkItems.Count} refreshed={payload.FriendSummary.CandidatesRefreshed - achievementsBefore} saved={payload.FriendSummary.AchievementsSaved - achievementsSavedBefore}");
        }

        internal void EndFriendRefreshContexts(IEnumerable<FriendProviderRefreshContext> contexts)
        {
            foreach (var context in contexts ?? Enumerable.Empty<FriendProviderRefreshContext>())
            {
                try
                {
                    context?.Provider?.EndRefresh();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed to end friend refresh for {context?.ProviderKey}.");
                }

                try
                {
                    ReleaseContextScrapeState(context);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed to release friend refresh context state for {context?.ProviderKey}.");
                }
            }
        }

        // The per-run scrape state holds several overlapping full copies of every friend-game row
        // (ownership snapshots, the grouped definition-plan copy, the current-user label set), and
        // the contexts can stay reachable past the run's hot path (combined runs hold them until
        // the whole plan finishes). Drop the big collections here so the LOH compaction at
        // refresh end can actually return their space. Friends/ScopedFriends stay intact: they
        // are small identity lists and perf logging reads them.
        private static void ReleaseContextScrapeState(FriendProviderRefreshContext context)
        {
            if (context == null)
            {
                return;
            }

            context.OwnershipSnapshots = null;
            context.DefinitionPlan = null;
            context.CurrentUserLabelIndex = null;
            context.CurrentUserLabels = Array.Empty<CurrentUserGameLabel>();
            context.ProbedProviderOnlyAchievementKeys.Clear();
            context.RecencyFreshKeys.Clear();
            context.OwnershipFetchedFriendIds.Clear();
        }

        /// <summary>
        /// Refreshes only the friend roster (the list of friends and their avatars) for each
        /// friend-capable provider, without fetching ownership, game definitions, or achievements.
        /// Used by the in-settings "refresh friends list" action. Returns the number of active friends
        /// saved across providers.
        /// </summary>
        public async Task<int> RefreshFriendRosterAsync(
            IReadOnlyList<IDataProvider> providerScope,
            CancellationToken cancel = default)
        {
            if (_friendCache == null)
            {
                return 0;
            }

            var providers = (providerScope ?? Array.Empty<IDataProvider>())
                .Where(provider => provider?.Friends != null)
                .ToList();

            var saved = 0;
            using (var friendInvalidationBatch = _friendCache.BeginFriendCacheInvalidationBatch())
            {
                foreach (var provider in providers)
                {
                    cancel.ThrowIfCancellationRequested();
                    saved += await RefreshProviderRosterAsync(provider.Friends, cancel).ConfigureAwait(false);
                    friendInvalidationBatch?.Flush();
                }
            }

            return saved;
        }

        private async Task<int> RefreshProviderRosterAsync(
            IFriendsProvider friendsProvider,
            CancellationToken cancel)
        {
            if (friendsProvider == null)
            {
                return 0;
            }

            try
            {
                var payload = new RebuildPayload();
                var context = await PrepareProviderRosterRefreshAsync(
                    friendsProvider,
                    payload,
                    cancel).ConfigureAwait(false);
                if (context?.CanContinue != true)
                {
                    return 0;
                }

                await DownloadFriendAvatarsAsync(context.ProviderKey, context.Friends, cancel).ConfigureAwait(false);
                var saved = SaveFriendList(context, payload: null);
                return Math.Max(0, saved);
            }
            finally
            {
                friendsProvider.EndRefresh();
            }
        }

        private async Task<FriendProviderRefreshContext> PrepareProviderRosterRefreshAsync(
            IFriendsProvider friendsProvider,
            RebuildPayload payload,
            CancellationToken cancel)
        {
            if (friendsProvider == null)
            {
                return null;
            }

            var providerKey = friendsProvider.ProviderKey;
            var context = new FriendProviderRefreshContext
            {
                Provider = friendsProvider,
                ProviderKey = providerKey,
                RosterSource = "provider"
            };

            var preparationResult = await friendsProvider.BeginRefreshAsync(cancel).ConfigureAwait(false);
            if (preparationResult?.Success != true)
            {
                _logger?.Debug($"Friend roster refresh skipped for {providerKey}: {preparationResult?.ErrorMessage ?? "provider unavailable"}");
                MarkAuthFailure(payload, providerKey, preparationResult?.AuthRequired == true);
                return context;
            }

            context.Preparation = preparationResult.Data ?? new FriendsRefreshPreparation();
            context.CanContinue = true;
            payload.FriendSummary.ProvidersProcessed++;

            var friendsResult = await friendsProvider.GetFriendsAsync(cancel).ConfigureAwait(false);
            if (friendsResult?.Success != true)
            {
                _logger?.Debug($"Friend roster refresh skipped for {providerKey}: {friendsResult?.ErrorMessage ?? "friend list unavailable"}");
                MarkAuthFailure(payload, providerKey, friendsResult?.AuthRequired == true);
                context.CanContinue = false;
                return context;
            }

            var discoveredFriends = NormalizeProviderFriendIdentities(providerKey, friendsResult.Data);
            context.Friends = FilterIgnoredFriends(providerKey, discoveredFriends).ToList();
            context.ScopedFriends = context.Friends;
            payload.FriendSummary.FriendsFetched += context.Friends.Count;
            return context;
        }

        private int SaveFriendList(FriendProviderRefreshContext context, RebuildPayload payload)
        {
            if (context == null)
            {
                return -1;
            }

            var writeFriends = _friendCache.SaveFriendList(context.ProviderKey, context.Friends);
            if (writeFriends?.Success != true)
            {
                _logger?.Warn($"Failed to save {context.ProviderKey} friend list: {writeFriends?.ErrorMessage}");
                return -1;
            }

            if (payload != null)
            {
                payload.FriendSummary.FriendsSaved += writeFriends.WrittenCount;
            }

            _logger?.Debug(
                $"Saved {context.ProviderKey} friend list: fetched={context.Friends.Count}, active={writeFriends.WrittenCount}, skipped={writeFriends.SkippedCount}.");
            return writeFriends.WrittenCount;
        }

        private IReadOnlyList<CurrentUserGameLabel> LoadCurrentUserGameLabelsForFriendMatching()
        {
            var labels = new List<CurrentUserGameLabel>();
            foreach (var label in _friendCache?.LoadCurrentUserGameLabels() ?? Array.Empty<CurrentUserGameLabel>())
            {
                if (label == null || label.PlayniteGameId == Guid.Empty)
                {
                    continue;
                }

                labels.Add(new CurrentUserGameLabel
                {
                    PlayniteGameId = label.PlayniteGameId,
                    GameName = label.GameName,
                    ProviderKey = label.ProviderKey,
                    ProviderPlatformKey = label.ProviderPlatformKey,
                    AppId = Math.Max(0, label.AppId),
                    ProviderGameKey = label.ProviderGameKey
                });
            }

            AddSteamLibraryCurrentUserGameLabels(labels);
            return labels;
        }

        private void AddSteamLibraryCurrentUserGameLabels(List<CurrentUserGameLabel> labels)
        {
            if (labels == null)
            {
                return;
            }

            List<Game> games;
            try
            {
                games = _api?.Database?.Games?.ToList();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to load Playnite Steam library labels for friend matching.");
                return;
            }

            if (games == null || games.Count == 0)
            {
                return;
            }

            foreach (var game in games)
            {
                if (game == null ||
                    game.Id == Guid.Empty ||
                    !SteamGameIdentity.TryGetSteamAppId(game, out var appId))
                {
                    continue;
                }

                labels.Add(new CurrentUserGameLabel
                {
                    PlayniteGameId = game.Id,
                    GameName = game.Name,
                    ProviderKey = "Steam",
                    ProviderPlatformKey = "Steam",
                    AppId = appId
                });
            }
        }

        private void PromoteProviderOnlyFriendGamesFromCurrentUserLabels(
            string providerKey,
            IReadOnlyList<CurrentUserGameLabel> labels)
        {
            if (_friendCache == null ||
                string.IsNullOrWhiteSpace(providerKey) ||
                labels == null ||
                labels.Count == 0)
            {
                return;
            }

            var promoted = 0;
            var attempted = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in labels)
            {
                if (label == null ||
                    label.PlayniteGameId == Guid.Empty ||
                    !string.Equals(label.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) ||
                    !FriendRefreshWorkPolicy.HasProviderGameIdentity(label.AppId, label.ProviderGameKey))
                {
                    continue;
                }

                var providerGameKey = GetProviderGameCacheKey(label.AppId, label.ProviderGameKey);
                var dedupeKey = providerGameKey + "|" + label.PlayniteGameId.ToString("D");
                if (string.IsNullOrWhiteSpace(providerGameKey) ||
                    !seen.Add(dedupeKey))
                {
                    continue;
                }

                attempted++;
                var result = _friendCache.PromoteProviderOnlyGameToPlayniteBacked(
                    providerKey,
                    label.AppId,
                    label.ProviderGameKey,
                    label.PlayniteGameId);
                if (result?.Success == true)
                {
                    promoted += Math.Max(0, result.WrittenCount);
                }
                else if (result != null)
                {
                    _logger?.Debug(
                        $"Failed to promote cached friend game {providerKey}/{providerGameKey} to Playnite game {label.PlayniteGameId:D}: {result.ErrorMessage}");
                }
            }

            if (promoted > 0)
            {
                _logger?.Debug(
                    $"Promoted {promoted} cached provider-only friend game row(s) for provider={providerKey} from {attempted} current-user label candidate(s).");
            }
        }

        private static IReadOnlyDictionary<string, Guid> BuildCurrentUserLabelIndex(
            string providerKey,
            IReadOnlyList<CurrentUserGameLabel> labels)
        {
            var index = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in labels ?? Enumerable.Empty<CurrentUserGameLabel>())
            {
                if (label == null ||
                    label.PlayniteGameId == Guid.Empty ||
                    !string.Equals(label.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var cacheKey = GetProviderGameCacheKey(label.AppId, label.ProviderGameKey);
                if (string.IsNullOrWhiteSpace(cacheKey) || index.ContainsKey(cacheKey))
                {
                    continue;
                }

                index.Add(cacheKey, label.PlayniteGameId);
            }

            return index;
        }

        internal async Task<FriendProviderRefreshContext> PrepareProviderRefreshAsync(
            IFriendsProvider friendsProvider,
            FriendRefreshOptions options,
            RebuildPayload payload,
            FriendRefreshProgressSession progress,
            int providerIndex,
            int providerTotal,
            CancellationToken cancel)
        {
            if (friendsProvider == null)
            {
                return null;
            }

            var providerKey = friendsProvider.ProviderKey;
            var context = new FriendProviderRefreshContext
            {
                Provider = friendsProvider,
                ProviderKey = providerKey,
                DiscoverUnowned = FriendRefreshWorkPolicy.ShouldDiscoverUnowned(providerKey, options),
                MaxDegreeOfParallelism = ResolveFriendRefreshParallelism()
            };
            // The game-centric candidate builder reads the fresh, hint-bearing ownership snapshot for
            // every scope that fetches ownership (Full/Shared/Installed/Recent and ownership-mapping
            // Custom). SelectedGame (and other non-ownership scopes) leave this null and fall back to the
            // cache-sourced candidate path. Retaining the snapshot adds no network cost: the ownership
            // scrape already runs for these scopes (see ShouldRefreshOwnership).
            context.OwnershipSnapshots = FriendRefreshWorkPolicy.ShouldRefreshOwnership(providerKey, options)
                ? new List<FriendOwnershipSnapshot>()
                : null;

            progress?.ReportLoadingFriends(providerKey, providerIndex, providerTotal);
            var preparationResult = await friendsProvider.BeginRefreshAsync(cancel).ConfigureAwait(false);
            if (preparationResult?.Success != true)
            {
                _logger?.Debug($"Friends refresh skipped for {providerKey}: {preparationResult?.ErrorMessage ?? "provider unavailable"}");
                MarkAuthFailure(payload, providerKey, preparationResult?.AuthRequired == true);
                progress?.ReportProviderRosterLoaded(providerKey);
                return context;
            }

            context.Preparation = preparationResult.Data ?? new FriendsRefreshPreparation();
            context.CanContinue = true;
            payload.FriendSummary.ProvidersProcessed++;

            var currentUserLabels = LoadCurrentUserGameLabelsForFriendMatching();
            context.CurrentUserLabels = currentUserLabels;
            context.CurrentUserLabelIndex = FriendRefreshWorkPolicy.ShouldMapOwnershipFromCurrentUserLabels(providerKey)
                ? BuildCurrentUserLabelIndex(providerKey, currentUserLabels)
                : null;
            PromoteProviderOnlyFriendGamesFromCurrentUserLabels(providerKey, currentUserLabels);

            if (friendsProvider is ICurrentUserGameLabelReceiver labelReceiver)
            {
                labelReceiver.SetCurrentUserGameLabels(currentUserLabels);
                _logger?.Debug(
                    $"Supplied {currentUserLabels.Count} current-user game label(s) to {providerKey} friend merge.");
            }

            if (!TryPrepareFriendRosterFromSettingsOrCache(providerKey, options, context, payload))
            {
                context.CanContinue = false;
            }

            progress?.ReportProviderRosterLoaded(providerKey);
            return context;
        }

        private async Task RefreshOwnershipByLogicalFriendAsync(
            IReadOnlyList<LogicalFriendRefreshGroup> logicalFriends,
            FriendRefreshOptions options,
            RebuildPayload payload,
            object payloadLock,
            FriendRefreshProgressSession progress,
            CancellationToken cancel)
        {
            if (logicalFriends == null || logicalFriends.Count == 0)
            {
                return;
            }

            var maxDegreeOfParallelism = Math.Max(1, logicalFriends.Max(group =>
                group?.Accounts?.Max(account => account?.Context?.MaxDegreeOfParallelism ?? 1) ?? 1));
            if (maxDegreeOfParallelism <= 1 || logicalFriends.Count == 1)
            {
                for (var i = 0; i < logicalFriends.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var shouldContinue = await RefreshLogicalFriendOwnershipAsync(
                        logicalFriends[i],
                        options,
                        payload,
                        payloadLock,
                        cancel).ConfigureAwait(false);
                    progress?.ReportFriendLibraryCompleted(logicalFriends[i]?.DisplayName);
                    if (!shouldContinue)
                    {
                        return;
                    }
                }

                return;
            }

            using (var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                try
                {
                    await RunBoundedAsync(
                        logicalFriends,
                        maxDegreeOfParallelism,
                        async (group, _, token) =>
                        {
                            var shouldContinue = await RefreshLogicalFriendOwnershipAsync(
                                group,
                                options,
                                payload,
                                payloadLock,
                                token).ConfigureAwait(false);
                            progress?.ReportFriendLibraryCompleted(group?.DisplayName);
                            if (!shouldContinue)
                            {
                                authCts.Cancel();
                            }
                        },
                        authCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancel.ThrowIfCancellationRequested();
                }
            }
        }

        private async Task<bool> RefreshLogicalFriendOwnershipAsync(
            LogicalFriendRefreshGroup logicalFriend,
            FriendRefreshOptions options,
            RebuildPayload payload,
            object payloadLock,
            CancellationToken cancel)
        {
            foreach (var account in logicalFriend?.Accounts ?? Enumerable.Empty<FriendAccountRefreshItem>())
            {
                var context = account?.Context;
                var friend = account?.Friend;
                if (!ShouldRefreshOwnershipForFriend(context, friend, options))
                {
                    continue;
                }

                var shouldContinue = await RefreshOwnershipItemAsync(
                    context.Provider,
                    context.ProviderKey,
                    friend,
                    ResolveMergedExophaseFriendForSteamOwnership(context.ProviderKey, friend.ExternalUserId),
                    options,
                    payload,
                    payloadLock,
                    context.OwnershipSnapshots,
                    context.RecencyFreshKeys,
                    context.OwnershipFetchedFriendIds,
                    context.CurrentUserLabelIndex,
                    context.CurrentUserLabels,
                    cancel).ConfigureAwait(false);
                if (!shouldContinue)
                {
                    return false;
                }
            }

            return true;
        }

        // Game-centric mapped-unlock candidate builder. Sources scrape work directly from the fresh,
        // hint-bearing ownership snapshot instead of round-tripping hint-less rows through the cache, so
        // the mode-aware selection (installed / recency / no-achievement) is decided from live data.
        // Only games shared with the current user's library are produced here; provider-only games are
        // scraped by the Full-scope definition/probe phase (which prunes zero-unlock games pre-network).
        private List<FriendAchievementWorkItem> BuildMappedAchievementWorkItemsFromSnapshots(
            FriendProviderRefreshContext context,
            FriendRefreshOptions options,
            RebuildPayload payload)
        {
            var workItems = new List<FriendAchievementWorkItem>();
            var snapshots = context?.OwnershipSnapshots;
            if (snapshots == null)
            {
                return workItems;
            }

            var mappedIds = BuildFriendGameMappingLookup(context.ProviderKey);
            var installedIds = ResolveInstalledFriendGameIds(options);
            var ignoredIds = GetIgnoredFriendIds(context.ProviderKey);
            var scope = options?.Scope ?? FriendRefreshScope.Recent;
            var isRecent = scope == FriendRefreshScope.Recent;
            var hasExplicitTargets = FriendRefreshWorkPolicy.HasExplicitProviderGameTargets(options);
            var raw = 0;

            foreach (var snapshot in snapshots)
            {
                var friend = snapshot?.Friend;
                if (friend == null ||
                    string.IsNullOrWhiteSpace(friend.ExternalUserId) ||
                    ignoredIds.Contains(friend.ExternalUserId))
                {
                    continue;
                }

                foreach (var item in snapshot.Ownership ?? Enumerable.Empty<FriendGameOwnership>())
                {
                    if (!FriendRefreshWorkPolicy.HasProviderGameIdentity(item))
                    {
                        continue;
                    }

                    raw++;

                    // Explicit provider-game targets narrow the set to the requested games.
                    if (hasExplicitTargets &&
                        !FriendRefreshWorkPolicy.IsExplicitProviderGameTarget(options, item.AppId, item.ProviderGameKey))
                    {
                        continue;
                    }

                    // Games the provider reports as having no achievements never qualify.
                    if (item.AchievementTotalHint.HasValue && item.AchievementTotalHint.Value <= 0)
                    {
                        continue;
                    }

                    var key = GetProviderGameCacheKey(item);
                    var mapped = ResolveMappedFriendGame(context.ProviderKey, item, key, mappedIds, out var playniteId);

                    // Provider-only games are handled by the definition/probe phase; skip them here.
                    if (!mapped)
                    {
                        continue;
                    }

                    // Installed scope: keep only games whose mapped Playnite game is installed.
                    if (scope == FriendRefreshScope.Installed &&
                        !(playniteId.HasValue && installedIds != null && installedIds.Contains(playniteId.Value)))
                    {
                        continue;
                    }

                    // Recent scope: skip games the ownership step positively confirmed unchanged since
                    // the last successful scrape.
                    if (isRecent &&
                        context.RecencyFreshKeys.Contains(BuildRecencyGameKey(friend.ExternalUserId, key)))
                    {
                        continue;
                    }

                    workItems.Add(new FriendAchievementWorkItem
                    {
                        Context = context,
                        Candidate = new FriendRefreshCandidate
                        {
                            Friend = friend,
                            AppId = item.AppId,
                            ProviderGameKey = item.ProviderGameKey,
                            PlayniteGameId = playniteId,
                            GameName = item.GameName,
                            PlaytimeForeverMinutes = item.PlaytimeForeverMinutes,
                            LastPlayedUtc = item.LastPlayedUtc
                        }
                    });
                }
            }

            context.RawCandidatesLoaded += raw;
            context.CandidatesQueued += workItems.Count;
            if (payload != null)
            {
                payload.FriendSummary.CandidatesLoaded += workItems.Count;
            }

            _logger?.Debug(
                $"Built {context.ProviderKey} mapped friend scrape candidates from snapshot: raw={raw}, queued={workItems.Count}, scope={scope}.");
            return workItems;
        }

        private Dictionary<string, Guid> BuildFriendGameMappingLookup(string providerKey)
        {
            var dict = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var mappings = _friendCache?.LoadFriendGameMappings(providerKey);
            if (mappings != null)
            {
                foreach (var mapping in mappings)
                {
                    if (mapping == null || mapping.PlayniteGameId == Guid.Empty)
                    {
                        continue;
                    }

                    var key = GetProviderGameCacheKey(mapping.AppId, mapping.ProviderGameKey);
                    if (!string.IsNullOrEmpty(key))
                    {
                        dict[key] = mapping.PlayniteGameId;
                    }
                }
            }

            return dict;
        }

        private static HashSet<Guid> ResolveInstalledFriendGameIds(FriendRefreshOptions options)
        {
            if (options?.Scope != FriendRefreshScope.Installed)
            {
                return null;
            }

            var set = new HashSet<Guid>();
            foreach (var id in options.PlayniteGameIds ?? Enumerable.Empty<Guid>())
            {
                if (id != Guid.Empty)
                {
                    set.Add(id);
                }
            }

            return set;
        }

        // Resolves whether a freshly-scraped friend game maps to the current user's library, preferring
        // the materialized per-provider mapping (also yields the Playnite game id for the Installed
        // intersection). Falls back to the per-game cache check only when no mapping is materialized
        // (e.g. a test double that does not populate LoadFriendGameMappings).
        private bool ResolveMappedFriendGame(
            string providerKey,
            FriendGameOwnership item,
            string key,
            IReadOnlyDictionary<string, Guid> mappedIds,
            out Guid? playniteGameId)
        {
            // An inline mapping resolved by the provider during the scrape (e.g. Exophase maps by
            // name/slug against the current-user labels) is authoritative, even before the mapping is
            // persisted to the Games table. Steam/RA leave it null and are resolved from the cache below.
            if (item?.PlayniteGameId.HasValue == true && item.PlayniteGameId.Value != Guid.Empty)
            {
                playniteGameId = item.PlayniteGameId;
                return true;
            }

            if (mappedIds != null && mappedIds.Count > 0)
            {
                if (!string.IsNullOrEmpty(key) && mappedIds.TryGetValue(key, out var id))
                {
                    playniteGameId = id;
                    return true;
                }

                playniteGameId = null;
                return false;
            }

            playniteGameId = null;
            return IsPlayniteLibraryFriendGame(providerKey, item);
        }

        private List<FriendAchievementWorkItem> LoadAchievementWorkItems(
            IReadOnlyList<FriendProviderRefreshContext> contexts,
            FriendRefreshOptions options,
            RebuildPayload payload)
        {
            var workItems = new List<FriendAchievementWorkItem>();
            foreach (var context in contexts ?? Array.Empty<FriendProviderRefreshContext>())
            {
                var rawCandidates = _friendCache.LoadFriendRefreshCandidates(context.ProviderKey, options) ??
                                    new List<FriendRefreshCandidate>();
                context.RawCandidatesLoaded += rawCandidates.Count;
                var candidates = FilterProviderOnlyDetailCandidates(
                    context.ProviderKey,
                    FilterIgnoredCandidates(
                        context.ProviderKey,
                        rawCandidates),
                    options);

                if (context.ProbedProviderOnlyAchievementKeys.Count > 0)
                {
                    var beforeProbedFilter = candidates.Count;
                    candidates = candidates
                        .Where(candidate => !context.ProbedProviderOnlyAchievementKeys.Contains(
                            BuildFriendProviderGameKey(candidate.Friend?.ExternalUserId, GetProviderGameCacheKey(candidate))))
                        .ToList();
                    context.CandidatesSkippedAlreadyProbed += Math.Max(0, beforeProbedFilter - candidates.Count);
                }

                // Recent scope: drop only games the ownership step positively confirmed unchanged since the
                // last successful scrape (provider-driven recency). Anything not confirmed fresh is still
                // scraped. Other scopes scrape every candidate.
                if (options.Scope == FriendRefreshScope.Recent)
                {
                    var beforeRecencyFilter = candidates.Count;
                    candidates = candidates
                        .Where(candidate => !context.RecencyFreshKeys.Contains(
                            BuildRecencyGameKey(candidate.Friend?.ExternalUserId, GetProviderGameCacheKey(candidate))))
                        .ToList();
                    context.CandidatesSkippedRecencyFresh += Math.Max(0, beforeRecencyFilter - candidates.Count);

                    // Fail closed: without a fresh ownership snapshot for a friend, none of their games
                    // can be recency-confirmed, so a fetch hiccup would otherwise dump the friend's whole
                    // cached backlog into the scrape queue. Explicit game targets are never dropped.
                    if (!FriendRefreshWorkPolicy.HasExplicitProviderGameTargets(options))
                    {
                        var beforeOwnershipFilter = candidates.Count;
                        candidates = candidates
                            .Where(candidate =>
                                !string.IsNullOrWhiteSpace(candidate.Friend?.ExternalUserId) &&
                                context.OwnershipFetchedFriendIds.Contains(candidate.Friend.ExternalUserId.Trim()))
                            .ToList();
                        context.CandidatesSkippedOwnershipUnavailable +=
                            Math.Max(0, beforeOwnershipFilter - candidates.Count);
                    }
                }

                context.CandidatesQueued += candidates.Count;
                payload.FriendSummary.CandidatesLoaded += candidates.Count;
                _logger?.Debug(
                    $"Loaded {context.ProviderKey} friend achievement scrape candidates: raw={rawCandidates.Count}, queued={candidates.Count}, skippedAlreadyProbed={context.CandidatesSkippedAlreadyProbed}, skippedRecencyFresh={context.CandidatesSkippedRecencyFresh}, skippedOwnershipUnavailable={context.CandidatesSkippedOwnershipUnavailable}, scope={options.Scope}.");

                if (!context.Preparation.CanRefreshAchievements)
                {
                    _logger?.Debug($"Skipping {context.ProviderKey} friend achievement scrapes: provider did not prepare achievement auth.");
                    MarkAuthFailure(payload, context.ProviderKey, true);
                    continue;
                }

                workItems.AddRange(candidates.Select(candidate => new FriendAchievementWorkItem
                {
                    Context = context,
                    Candidate = candidate
                }));
            }

            return workItems;
        }

        private async Task RefreshAchievementWorkItemsAsync(
            IReadOnlyList<FriendAchievementWorkItem> workItems,
            RebuildPayload payload,
            object payloadLock,
            FriendRefreshProgressSession progress,
            IFriendCacheInvalidationBatch friendInvalidationBatch,
            CancellationToken cancel)
        {
            if (workItems == null || workItems.Count == 0)
            {
                return;
            }

            var maxDegreeOfParallelism = Math.Max(1, workItems.Max(item => item?.Context?.MaxDegreeOfParallelism ?? 1));
            var invalidationFlushState = new FriendInvalidationFlushState();
            if (maxDegreeOfParallelism <= 1 || workItems.Count == 1)
            {
                var limiter = CreateScanRateLimiter();
                foreach (var item in workItems)
                {
                    cancel.ThrowIfCancellationRequested();
                    var shouldContinue = await RefreshAchievementCandidateAsync(
                        item.Context.Provider,
                        item.Context.ProviderKey,
                        item.Candidate,
                        payload,
                        payloadLock,
                        delayBeforeRequest: true,
                        limiter,
                        cancel).ConfigureAwait(false);
                    progress?.ReportAchievementScrapeCompleted(FormatFriendGameDetail(item.Candidate));
                    MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState);
                    if (!shouldContinue)
                    {
                        MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState, force: true);
                        return;
                    }
                }

                MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState, force: true);
                return;
            }

            using (var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                try
                {
                    await RunBoundedAsync(
                        workItems,
                        maxDegreeOfParallelism,
                        async (item, _, token) =>
                        {
                            var limiter = CreateScanRateLimiter();
                            var shouldContinue = await RefreshAchievementCandidateAsync(
                                item.Context.Provider,
                                item.Context.ProviderKey,
                                item.Candidate,
                                payload,
                                payloadLock,
                                delayBeforeRequest: false,
                                limiter,
                                token).ConfigureAwait(false);
                            progress?.ReportAchievementScrapeCompleted(FormatFriendGameDetail(item.Candidate));
                            MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState);
                            if (!shouldContinue)
                            {
                                authCts.Cancel();
                            }
                        },
                        authCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancel.ThrowIfCancellationRequested();
                }
            }

            MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState, force: true);
        }

        private async Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetOwnedGamesForFriendRefreshAsync(
            IFriendsProvider friendsProvider,
            string providerKey,
            FriendIdentity friend,
            string exophaseSteamOwnershipUserId,
            IReadOnlyList<CurrentUserGameLabel> currentUserLabels,
            RateLimiter limiter,
            CancellationToken cancel)
        {
            var ownershipResult = await limiter.ExecuteWithRetryAsync(
                () => friendsProvider.GetOwnedGamesAsync(friend, cancel),
                FriendRefreshWorkPolicy.IsTransientError,
                cancel).ConfigureAwait(false);
            if (ownershipResult?.Success != true)
            {
                return ownershipResult;
            }

            var augmentedResult = await TryGetExophaseSteamFriendOwnershipAsync(
                providerKey,
                friend,
                exophaseSteamOwnershipUserId,
                ownershipResult.Data,
                currentUserLabels,
                limiter,
                cancel).ConfigureAwait(false);
            return augmentedResult ?? ownershipResult;
        }

        private async Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> TryGetExophaseSteamFriendOwnershipAsync(
            string providerKey,
            FriendIdentity steamFriend,
            string exophaseUserId,
            IReadOnlyList<FriendGameOwnership> knownSteamOwnership,
            IReadOnlyList<CurrentUserGameLabel> currentUserLabels,
            RateLimiter limiter,
            CancellationToken cancel)
        {
            if (!string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(steamFriend?.ExternalUserId) ||
                string.IsNullOrWhiteSpace(exophaseUserId))
            {
                return null;
            }

            var source = ResolveExophaseSteamOwnershipSource();
            if (source == null)
            {
                return null;
            }

            try
            {
                // Reuse the labels resolved once during provider preparation instead of
                // re-materializing the whole current-user library (and the Playnite games list)
                // for every friend; fall back to a fresh load only if they were not supplied.
                var resolvedLabels = currentUserLabels ?? LoadCurrentUserGameLabelsForFriendMatching();
                var exophaseResult = await limiter.ExecuteWithRetryAsync(
                    () => source.GetSteamOwnedGamesAsync(exophaseUserId, resolvedLabels, knownSteamOwnership, cancel),
                    FriendRefreshWorkPolicy.IsTransientError,
                    cancel).ConfigureAwait(false);
                if (exophaseResult?.Success != true)
                {
                    _logger?.Debug(
                        $"Exophase Steam ownership augmentation unavailable for Steam friend {steamFriend.ExternalUserId} " +
                        $"via Exophase/{exophaseUserId}: {exophaseResult?.ErrorMessage ?? "provider unavailable"}. Keeping Steam ownership.");
                    return null;
                }

                var steamMappings = _friendCache?.LoadFriendGameMappings("Steam") ??
                                    new List<FriendGameMapping>();
                var mapped = SteamExophaseFriendOwnershipMapper.MapToSteamOwnership(
                    steamFriend.ExternalUserId,
                    exophaseResult.Data,
                    steamMappings,
                    currentUserLabels);
                if (mapped.Ownership.Count == 0)
                {
                    _logger?.Debug(
                        $"Exophase Steam ownership augmentation produced no extra Steam-mapped rows for Steam friend {steamFriend.ExternalUserId} " +
                        $"via Exophase/{exophaseUserId} (incoming={mapped.IncomingCount}, skipped={mapped.SkippedCount}). Keeping Steam ownership.");
                    return null;
                }

                var combined = MergeSteamOwnershipWithSupplement(knownSteamOwnership, mapped.Ownership);
                var baseCount = knownSteamOwnership?.Count ?? 0;
                var addedCount = combined.Count - baseCount;
                if (addedCount <= 0)
                {
                    _logger?.Debug(
                        $"Exophase Steam ownership augmentation found no new Steam AppIDs for Steam friend {steamFriend.ExternalUserId} " +
                        $"via Exophase/{exophaseUserId} (incoming={mapped.IncomingCount}, mapped={mapped.Ownership.Count}, skipped={mapped.SkippedCount}).");
                    return null;
                }

                _logger?.Info(
                    $"Augmented Steam friend ownership with Exophase for Steam friend {steamFriend.ExternalUserId} " +
                    $"via Exophase/{exophaseUserId}: steamBase={baseCount}, incoming={mapped.IncomingCount}, " +
                    $"mapped={mapped.Ownership.Count}, added={addedCount}, skipped={mapped.SkippedCount}.");
                return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.FromData(combined);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(
                    ex,
                    $"Exophase Steam ownership augmentation failed for Steam friend {steamFriend.ExternalUserId} " +
                    $"via Exophase/{exophaseUserId}. Keeping Steam ownership.");
                return null;
            }
        }

        private static IReadOnlyList<FriendGameOwnership> MergeSteamOwnershipWithSupplement(
            IReadOnlyList<FriendGameOwnership> steamOwnership,
            IReadOnlyList<FriendGameOwnership> supplementalOwnership)
        {
            var merged = new List<FriendGameOwnership>();
            var seenAppIds = new HashSet<int>();

            foreach (var item in steamOwnership ?? Array.Empty<FriendGameOwnership>())
            {
                if (item == null)
                {
                    continue;
                }

                if (item.AppId > 0)
                {
                    seenAppIds.Add(item.AppId);
                }

                merged.Add(item);
            }

            foreach (var item in supplementalOwnership ?? Array.Empty<FriendGameOwnership>())
            {
                if (item == null)
                {
                    continue;
                }

                if (item.AppId > 0 && !seenAppIds.Add(item.AppId))
                {
                    continue;
                }

                merged.Add(item);
            }

            return merged;
        }

        private string ResolveMergedExophaseFriendForSteamOwnership(string providerKey, string externalUserId)
        {
            if (!string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(externalUserId) ||
                !ShouldUseExophaseForSteamFriendOwnership())
            {
                return null;
            }

            var persisted = _settings?.Persisted;
            var group = persisted?.GetFriendMergeGroupForAccount("Steam", externalUserId);
            var exophaseMember = group?.Members?
                .FirstOrDefault(member => string.Equals(member?.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(exophaseMember?.ExternalUserId))
            {
                return null;
            }

            var exophaseFriend = persisted.GetFriendSetting("Exophase", exophaseMember.ExternalUserId);
            return exophaseFriend == null ||
                   exophaseFriend.IsIgnored ||
                   string.IsNullOrWhiteSpace(exophaseFriend.ExternalUserId)
                ? null
                : exophaseFriend.ExternalUserId.Trim();
        }

        private bool ShouldUseExophaseForSteamFriendOwnership()
        {
            return _settings?.Persisted?.UseExophaseForSteamFriendOwnership == true &&
                   IsProviderEnabledForFriendOwnership("Exophase") &&
                   ResolveExophaseSteamOwnershipSource() != null;
        }

        private bool IsProviderEnabledForFriendOwnership(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return false;
            }

            if (_providerRegistry != null)
            {
                return _providerRegistry.IsProviderEnabled(providerKey);
            }

            try
            {
                var provider = _providers?
                    .FirstOrDefault(item => string.Equals(item?.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
                var settings = provider?.GetSettings();
                return settings == null || settings.IsEnabled;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to resolve provider enabled state for {providerKey}.");
                return false;
            }
        }

        private ISteamFriendOwnershipSupplementSource ResolveExophaseSteamOwnershipSource()
        {
            return _providers?
                .FirstOrDefault(provider => string.Equals(provider?.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase))
                ?.Friends as ISteamFriendOwnershipSupplementSource;
        }

        private async Task<bool> RefreshOwnershipItemAsync(
            IFriendsProvider friendsProvider,
            string providerKey,
            FriendIdentity friend,
            string exophaseSteamOwnershipUserId,
            FriendRefreshOptions options,
            RebuildPayload payload,
            object payloadLock,
            List<FriendOwnershipSnapshot> ownershipSnapshots,
            HashSet<string> recencyFreshKeys,
            HashSet<string> ownershipFetchedFriendIds,
            IReadOnlyDictionary<string, Guid> currentUserLabelIndex,
            IReadOnlyList<CurrentUserGameLabel> currentUserLabels,
            CancellationToken cancel)
        {
            if (friend == null || string.IsNullOrWhiteSpace(friend.ExternalUserId))
            {
                return true;
            }

            var limiter = CreateScanRateLimiter();
            var ownershipResult = await GetOwnedGamesForFriendRefreshAsync(
                friendsProvider,
                providerKey,
                friend,
                exophaseSteamOwnershipUserId,
                currentUserLabels,
                limiter,
                cancel).ConfigureAwait(false);
            if (ownershipResult?.Success != true)
            {
                _logger?.Debug($"Friend ownership unavailable for {providerKey}/{friend.ExternalUserId}: {ownershipResult?.ErrorMessage}");
                if (ownershipResult?.AuthRequired == true)
                {
                    lock (payloadLock)
                    {
                        MarkAuthFailure(payload, providerKey, true);
                    }

                    return false;
                }

                return true;
            }

            // Record the successful fetch so Recent's fail-closed candidate filter knows this
            // friend's recency signals are current (see LoadAchievementWorkItems).
            if (ownershipFetchedFriendIds != null)
            {
                lock (ownershipFetchedFriendIds)
                {
                    ownershipFetchedFriendIds.Add(friend.ExternalUserId.Trim());
                }
            }

            lock (payloadLock)
            {
                payload.FriendSummary.OwnershipPagesRefreshed++;
            }

            var scopedOwnedGames = ScopeOwnedGamesForRefresh(ownershipResult.Data, options);
            var ownedGames = FilterOwnedGamesForProviderRefresh(providerKey, scopedOwnedGames);
            StampPlayniteGameIdsFromCurrentUserLabels(ownedGames, currentUserLabelIndex);
            _logger?.Debug(
                $"[RefreshPerf] phase=friend.ownership.provider provider={providerKey} friend={friend.ExternalUserId} returned={ownershipResult.Data?.Count ?? 0} scoped={scopedOwnedGames?.Count ?? 0} filtered={ownedGames.Count} scope={options?.Scope}.");
            // Retain the fresh, hint-bearing ownership snapshot for the game-centric candidate builder.
            // The list is non-null for every scope that fetches ownership (see PrepareProviderRefreshAsync).
            if (ownershipSnapshots != null)
            {
                lock (ownershipSnapshots)
                {
                    ownershipSnapshots.Add(new FriendOwnershipSnapshot
                    {
                        Friend = friend,
                        Ownership = ownedGames
                            .Where(item => FriendRefreshWorkPolicy.HasProviderGameIdentity(item))
                            .ToList()
                    });
                }
            }

            // Recent scope: decide recency here, while the freshly-fetched playtime / last-played is still
            // in hand and the cached row has not yet been overwritten by the save below. Steam compares
            // playtime; RA/Exophase compare the last-played/last-unlock timestamp against the last scrape.
            // We record only the games positively confirmed unchanged; everything else is (re)scraped.
            if (options?.Scope == FriendRefreshScope.Recent && recencyFreshKeys != null)
            {
                var previous = _friendCache.LoadFriendOwnershipRecency(providerKey, friend.ExternalUserId) ??
                               new Dictionary<string, FriendOwnershipRecency>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in ownedGames)
                {
                    if (!FriendRefreshWorkPolicy.HasProviderGameIdentity(item))
                    {
                        continue;
                    }

                    var cacheKey = GetProviderGameCacheKey(item);
                    if (string.IsNullOrEmpty(cacheKey))
                    {
                        continue;
                    }

                    previous.TryGetValue(cacheKey, out var prev);
                    if (!FriendRefreshWorkPolicy.IsRecencyStale(providerKey, item, prev))
                    {
                        lock (recencyFreshKeys)
                        {
                            recencyFreshKeys.Add(BuildRecencyGameKey(friend.ExternalUserId, cacheKey));
                        }
                    }
                }
            }

            // The per-friend ownership save only syncs mapped/shared games (and prunes stale rows).
            // Provider-only ownership is persisted solely by ProbeAndPersistProviderOnlyFriendGameAsync
            // once a friend's unlocks are confirmed, so it is never written blindly here.
            var writeOwnership = _friendCache.SaveFriendOwnership(
                providerKey,
                friend.ExternalUserId,
                ownedGames,
                new FriendOwnershipSaveOptions
                {
                    IncludeProviderOnlyGames = false,
                    PruneStaleShared = FriendRefreshWorkPolicy.ShouldPruneStaleSharedOwnership(options, ownedGames)
                });
            if (writeOwnership?.Success != true)
            {
                _logger?.Warn($"Failed to save friend ownership for {providerKey}/{friend.ExternalUserId}: {writeOwnership?.ErrorMessage}");
                return true;
            }

            lock (payloadLock)
            {
                payload.FriendSummary.OwnershipRowsWritten += writeOwnership.WrittenCount;
            }

            _logger?.Debug(
                $"Saved friend ownership for {providerKey}/{friend.ExternalUserId}: " +
                $"fetched={ownedGames.Count}, shared={writeOwnership.WrittenCount}, skippedUnshared={writeOwnership.SkippedCount}.");
            return true;
        }

        // Computes the unowned-definition plan without performing any fetch (the only cache access is the
        // read-only LoadFriendGameDefinitionStates). Run up front so the definitions progress sub-band
        // knows its full total before it emits a completion.
        private UnownedDefinitionPlan ComputeUnownedDefinitionPlan(
            string providerKey,
            IReadOnlyList<FriendOwnershipSnapshot> ownershipSnapshots,
            FriendRefreshOptions options)
        {
            var plan = new UnownedDefinitionPlan();
            var snapshots = ownershipSnapshots?
                .Where(snapshot => snapshot?.Friend != null && snapshot.Ownership?.Count > 0)
                .ToList();
            if (snapshots == null || snapshots.Count == 0)
            {
                return plan;
            }

            var requestedProviderGameKeys = options?.ProviderGameKeys?
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requestedAppIds = options?.ProviderAppIds?
                .Where(id => id > 0)
                .Select(id => id.ToString())
                .Distinct()
                .ToList();
            var requestedKeys = new List<string>();
            if (requestedProviderGameKeys?.Count > 0)
            {
                requestedKeys.AddRange(requestedProviderGameKeys);
            }

            if (requestedAppIds?.Count > 0)
            {
                requestedKeys.AddRange(requestedAppIds);
            }

            var requestedKeySet = requestedKeys.Count > 0
                ? new HashSet<string>(requestedKeys, StringComparer.OrdinalIgnoreCase)
                : null;

            var ownershipByKey = snapshots
                .SelectMany(snapshot => snapshot.Ownership)
                .Where(item => FriendRefreshWorkPolicy.HasProviderGameIdentity(item) &&
                               ShouldRefreshFriendGameDefinition(providerKey, item, options) &&
                               (requestedKeySet == null || requestedKeySet.Contains(GetProviderGameCacheKey(item))))
                .GroupBy(GetProviderGameCacheKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList());
            if (ownershipByKey.Count == 0)
            {
                return plan;
            }

            var providerGameKeys = ownershipByKey.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
            var states = _friendCache.LoadFriendGameDefinitionStates(providerKey, providerGameKeys) ??
                         new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);
            var dueProviderGameKeys = FriendRefreshWorkPolicy.ShouldSeedDefinitionsFromFriendAchievementScrape(providerKey)
                ? new List<string>()
                : options?.ForceDefinitionRefresh == true
                    ? providerGameKeys.ToList()
                    : providerGameKeys
                        .Where(key => FriendRefreshWorkPolicy.IsDefinitionCheckDue(states.TryGetValue(key, out var state) ? state : null))
                        .ToList();

            // Games whose cached definitions still carry legacy display-derived Exophase keys are
            // definition-due regardless of check freshness: the definition fetch performs the
            // in-place rename to stable ids, which must happen before locale-independent unlock
            // rows can match.
            var legacyKeyedGameKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!FriendRefreshWorkPolicy.ShouldSeedDefinitionsFromFriendAchievementScrape(providerKey))
            {
                var legacyKeyedGameKeys = _friendCache.LoadLegacyKeyedDefinitionGameKeys(providerKey, providerGameKeys);
                if (legacyKeyedGameKeys?.Count > 0)
                {
                    var dueKeySet = new HashSet<string>(dueProviderGameKeys, StringComparer.OrdinalIgnoreCase);
                    foreach (var legacyKey in legacyKeyedGameKeys)
                    {
                        legacyKeyedGameKeySet.Add(legacyKey);
                        if (dueKeySet.Add(legacyKey))
                        {
                            dueProviderGameKeys.Add(legacyKey);
                        }
                    }

                    _logger?.Info($"[FriendRefresh] {legacyKeyedGameKeys.Count} {providerKey} games have legacy-keyed definitions; queued for definition refresh to migrate to stable ids.");
                }
            }

            plan.OwnershipByKey = ownershipByKey;
            plan.ProviderGameKeys = providerGameKeys;
            plan.DueProviderGameKeys = dueProviderGameKeys;

            // Defer the definition fetch for provider-only games whose owners all carry an unknown
            // unlock hint: the probe (which runs anyway) decides first whether the friend has unlocks,
            // and the definition is only fetched/saved once one owner is confirmed. This keeps
            // zero-unlock unowned games from persisting definition rows, a provider-only Games row, or
            // images. Mapped, explicitly-targeted, positive-hint and legacy-key-migration games stay
            // eager (the in-place key rename must run unconditionally).
            if (FriendRefreshWorkPolicy.ShouldGuardProviderOnlyZeroUnlocks(providerKey))
            {
                var eagerKeys = new List<string>();
                var deferredKeys = new List<string>();
                foreach (var key in dueProviderGameKeys)
                {
                    var owners = ownershipByKey.TryGetValue(key, out var rows) ? rows : null;
                    var mustFetchEagerly =
                        owners == null ||
                        legacyKeyedGameKeySet.Contains(key) ||
                        owners.Any(item => item != null &&
                            (IsPlayniteLibraryFriendGame(providerKey, item) ||
                             FriendRefreshWorkPolicy.IsExplicitProviderGameTarget(options, item.AppId, item.ProviderGameKey) ||
                             FriendRefreshWorkPolicy.HasPositiveUnlockHint(item)));
                    (mustFetchEagerly ? eagerKeys : deferredKeys).Add(key);
                }

                plan.DueProviderGameKeys = eagerKeys;
                plan.DeferredProviderGameKeys = deferredKeys;
            }

            // Provider-only probe scrapes only happen for providers that guard zero-unlock games; count
            // exactly the items the probe loop below will visit so the definitions total stays exact.
            if (FriendRefreshWorkPolicy.ShouldGuardProviderOnlyZeroUnlocks(providerKey))
            {
                var discovered = new HashSet<string>(providerGameKeys, StringComparer.OrdinalIgnoreCase);
                plan.ProbeItemCount = snapshots.Sum(snapshot => snapshot.Ownership.Count(item =>
                    FriendRefreshWorkPolicy.HasProviderGameIdentity(item) &&
                    discovered.Contains(GetProviderGameCacheKey(item)) &&
                    !IsPlayniteLibraryFriendGame(providerKey, item)));
            }

            return plan;
        }

        private async Task RefreshUnownedDefinitionsAndOwnershipAsync(
            IFriendsProvider friendsProvider,
            string providerKey,
            IReadOnlyList<FriendOwnershipSnapshot> ownershipSnapshots,
            HashSet<string> probedProviderOnlyAchievementKeys,
            UnownedDefinitionPlan plan,
            RebuildPayload payload,
            object payloadLock,
            FriendRefreshProgressSession progress,
            IFriendCacheInvalidationBatch friendInvalidationBatch,
            CancellationToken cancel)
        {
            var snapshots = ownershipSnapshots?
                .Where(snapshot => snapshot?.Friend != null && snapshot.Ownership?.Count > 0)
                .ToList();
            if (snapshots == null || snapshots.Count == 0 || plan == null || plan.OwnershipByKey.Count == 0)
            {
                return;
            }

            // The definitions total was set once from this plan before the phase began (see the pre-pass
            // in RefreshPreparedFriendContextsAsync); do not grow it here.
            var ownershipByKey = plan.OwnershipByKey;
            var providerGameKeys = plan.ProviderGameKeys;
            var dueProviderGameKeys = plan.DueProviderGameKeys;
            var noAchievementDefinitionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var noAchievementProbeSkips = 0;
            var invalidationFlushState = new FriendInvalidationFlushState();

            if (dueProviderGameKeys.Count > 0)
            {
                var limiter = CreateScanRateLimiter();
                for (var i = 0; i < dueProviderGameKeys.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var providerGameKey = dueProviderGameKeys[i];
                    var definition = await FetchAndPersistFriendGameDefinitionAsync(
                        friendsProvider,
                        providerKey,
                        providerGameKey,
                        ownershipByKey[providerGameKey],
                        $"{i + 1}/{dueProviderGameKeys.Count}",
                        limiter,
                        payload,
                        payloadLock,
                        progress,
                        friendInvalidationBatch,
                        invalidationFlushState,
                        cancel).ConfigureAwait(false);
                    if (definition == null)
                    {
                        return;
                    }

                    if (definition.Status == FriendGameDefinitionStatus.NoAchievements)
                    {
                        noAchievementDefinitionKeys.Add(providerGameKey);
                    }
                }
            }

            var discoveredProviderGameKeys = new HashSet<string>(providerGameKeys, StringComparer.OrdinalIgnoreCase);
            var providerOnlyProbeLimiter = CreateScanRateLimiter();

            // Probe game-major so a deferred game's definition decision is made once per game across
            // all its owners: the first friend whose probe confirms unlocks triggers the (memoized)
            // definition fetch; a game every owner probes empty is reported as a skipped definition
            // check and leaves no trace.
            var probeOwnersByKey = new Dictionary<string, List<KeyValuePair<FriendIdentity, FriendGameOwnership>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in snapshots)
            {
                foreach (var item in snapshot.Ownership
                    .Where(item => FriendRefreshWorkPolicy.HasProviderGameIdentity(item) && discoveredProviderGameKeys.Contains(GetProviderGameCacheKey(item))))
                {
                    // Mapped (Playnite-library) games are already persisted by the per-friend ownership
                    // save; only provider-only games need the probe to confirm unlocks before persisting.
                    if (IsPlayniteLibraryFriendGame(providerKey, item))
                    {
                        continue;
                    }

                    var providerGameKey = GetProviderGameCacheKey(item);
                    if (!probeOwnersByKey.TryGetValue(providerGameKey, out var owners))
                    {
                        owners = new List<KeyValuePair<FriendIdentity, FriendGameOwnership>>();
                        probeOwnersByKey.Add(providerGameKey, owners);
                    }

                    owners.Add(new KeyValuePair<FriendIdentity, FriendGameOwnership>(snapshot.Friend, item));
                }
            }

            var deferredKeySet = new HashSet<string>(plan.DeferredProviderGameKeys, StringComparer.OrdinalIgnoreCase);
            var confirmedDeferredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var probeGameKey in providerGameKeys)
            {
                if (!probeOwnersByKey.TryGetValue(probeGameKey, out var probeOwners))
                {
                    continue;
                }

                // Memoized per game: the first confirmed-unlock probe fetches and persists the
                // definition (schema + icons + banner) exactly once; later owners of the same game
                // reuse the completed task.
                Task<FriendGameDefinition> deferredDefinitionTask = null;
                Func<Task<bool>> ensureDefinitionAsync = null;
                if (deferredKeySet.Contains(probeGameKey))
                {
                    var ownershipRows = ownershipByKey[probeGameKey];
                    ensureDefinitionAsync = async () =>
                    {
                        if (deferredDefinitionTask == null)
                        {
                            confirmedDeferredKeys.Add(probeGameKey);
                            deferredDefinitionTask = FetchAndPersistFriendGameDefinitionAsync(
                                friendsProvider,
                                providerKey,
                                probeGameKey,
                                ownershipRows,
                                "deferred",
                                providerOnlyProbeLimiter,
                                payload,
                                payloadLock,
                                progress,
                                friendInvalidationBatch,
                                invalidationFlushState,
                                cancel);
                        }

                        return await deferredDefinitionTask.ConfigureAwait(false) != null;
                    };
                }

                foreach (var probeOwner in probeOwners)
                {
                    var item = probeOwner.Value;
                    if (noAchievementDefinitionKeys.Contains(probeGameKey))
                    {
                        noAchievementProbeSkips++;
                        if (FriendRefreshWorkPolicy.ShouldGuardProviderOnlyZeroUnlocks(providerKey))
                        {
                            progress?.ReportDefinitionCheckCompleted(
                                ResolveOwnershipGameName(new[] { item }, providerKey, probeGameKey));
                        }

                        continue;
                    }

                    var shouldContinue = await ProbeAndPersistProviderOnlyFriendGameAsync(
                        friendsProvider,
                        providerKey,
                        probeOwner.Key,
                        item,
                        probedProviderOnlyAchievementKeys,
                        providerOnlyProbeLimiter,
                        payload,
                        payloadLock,
                        progress,
                        cancel,
                        ensureDefinitionAsync).ConfigureAwait(false);
                    // Provider-only probes are network scrapes counted in the definitions total (see
                    // ComputeUnownedDefinitionPlan); report one completion each so the bar advances
                    // through them. Gated on the same guard used to count them so total and completions
                    // stay in lockstep.
                    if (FriendRefreshWorkPolicy.ShouldGuardProviderOnlyZeroUnlocks(providerKey))
                    {
                        progress?.ReportDefinitionCheckCompleted(
                            ResolveOwnershipGameName(new[] { item }, providerKey, GetProviderGameCacheKey(item)));
                    }
                    MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState);

                    if (!shouldContinue)
                    {
                        MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState, force: true);
                        return;
                    }
                }

                // Every deferred key was counted as one definition check in the plan total; a game no
                // owner confirmed resolves that count as a skip (nothing was fetched or written).
                if (deferredKeySet.Contains(probeGameKey) && !confirmedDeferredKeys.Contains(probeGameKey))
                {
                    progress?.ReportDefinitionCheckCompleted(
                        ResolveOwnershipGameName(ownershipByKey[probeGameKey], providerKey, probeGameKey));
                }
            }

            // Banner-preferring providers (Exophase) acquire provider-only images from the game header
            // banner during the definition fetch above. Skip the profile-thumbnail download for them: it
            // runs after that loop and, because SaveProviderGameImagePaths lets a non-null value win via
            // COALESCE, a small thumbnail would overwrite the higher-quality banner.
            if (!FriendRefreshWorkPolicy.PrefersDefinitionHeaderBannerImages(providerKey))
            {
                // Deferred games nobody confirmed have no Games row; downloading their thumbnails would
                // leave orphan image files on disk for games that must leave no trace.
                var unownedImageKeys = new HashSet<string>(discoveredProviderGameKeys, StringComparer.OrdinalIgnoreCase);
                unownedImageKeys.RemoveWhere(key => deferredKeySet.Contains(key) && !confirmedDeferredKeys.Contains(key));
                await DownloadUnownedGameImagesAsync(providerKey, unownedImageKeys, ownershipByKey, cancel, progress).ConfigureAwait(false);
            }

            MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState, force: true);
            _logger?.Debug(
                $"[RefreshPerf] phase=friend.definitions.provider provider={providerKey} providerKeys={providerGameKeys.Count} dueDefinitions={dueProviderGameKeys.Count} deferredDefinitions={plan.DeferredProviderGameKeys.Count} confirmedDeferred={confirmedDeferredKeys.Count} probeItems={plan.ProbeItemCount} noAchievementDefinitionKeys={noAchievementDefinitionKeys.Count} noAchievementProbeSkips={noAchievementProbeSkips}");
        }

        // Fetches one provider game's definition, persists it, and downloads its achievement icons and
        // header banner. Returns the definition, or null when the provider demanded authentication (the
        // caller aborts the phase). Used eagerly for due keys and lazily (post-probe) for deferred keys.
        private async Task<FriendGameDefinition> FetchAndPersistFriendGameDefinitionAsync(
            IFriendsProvider friendsProvider,
            string providerKey,
            string providerGameKey,
            IReadOnlyList<FriendGameOwnership> ownershipRows,
            string fetchLogLabel,
            RateLimiter limiter,
            RebuildPayload payload,
            object payloadLock,
            FriendRefreshProgressSession progress,
            IFriendCacheInvalidationBatch friendInvalidationBatch,
            FriendInvalidationFlushState invalidationFlushState,
            CancellationToken cancel)
        {
            var sample = ownershipRows?.FirstOrDefault(item => item != null);
            var appId = Math.Max(0, sample?.AppId ?? 0);
            var gameName = ResolveOwnershipGameName(ownershipRows, providerKey, providerGameKey);
            progress?.ReportDefinitionCheckActive(gameName);

            await limiter.DelayBeforeNextAsync(cancel).ConfigureAwait(false);
            _logger?.Info($"[FriendRefresh] Fetching game definition {fetchLogLabel} for {providerKey}/{providerGameKey} ('{gameName}').");
            var definitionResult = await limiter.ExecuteWithRetryAsync(
                () => friendsProvider.GetFriendGameDefinitionAsync(providerGameKey, appId, gameName, cancel),
                FriendRefreshWorkPolicy.IsTransientError,
                cancel).ConfigureAwait(false);

            if (definitionResult?.AuthRequired == true)
            {
                lock (payloadLock)
                {
                    MarkAuthFailure(payload, providerKey, true);
                }

                return null;
            }

            var definition = definitionResult?.Data ?? new FriendGameDefinition
            {
                ProviderKey = providerKey,
                AppId = appId,
                ProviderGameKey = providerGameKey,
                GameName = gameName,
                Status = definitionResult?.TransientFailure == true
                    ? FriendGameDefinitionStatus.Transient
                    : FriendGameDefinitionStatus.Unavailable,
                LastCheckedUtc = DateTime.UtcNow
            };

            definition.ProviderKey = providerKey;
            definition.AppId = appId;
            definition.ProviderGameKey = providerGameKey;
            if (string.IsNullOrWhiteSpace(definition.GameName))
            {
                definition.GameName = gameName;
            }

            await DownloadDefinitionAchievementIconsAsync(definition, cancel, progress).ConfigureAwait(false);

            var writeDefinition = _friendCache.SaveFriendGameDefinition(providerKey, definition);
            if (writeDefinition?.Success != true)
            {
                _logger?.Warn($"Failed to save friend game definition for {providerKey}/{providerGameKey}: {writeDefinition?.ErrorMessage}");
            }

            // Download the achievements-page header banner and store it as the game's local
            // icon+cover paths, mirroring the Steam owned-game image flow. The URL is never
            // persisted.
            await DownloadDefinitionGameImageAsync(providerKey, providerGameKey, appId, definition.IconUrl, definition.GameName, cancel, progress)
                .ConfigureAwait(false);
            progress?.ReportDefinitionCheckCompleted(gameName);
            MaybeFlushFriendInvalidations(friendInvalidationBatch, invalidationFlushState);
            return definition;
        }

        private async Task<bool> ProbeAndPersistProviderOnlyFriendGameAsync(
            IFriendsProvider friendsProvider,
            string providerKey,
            FriendIdentity friend,
            FriendGameOwnership ownership,
            HashSet<string> probedProviderOnlyAchievementKeys,
            RateLimiter limiter,
            RebuildPayload payload,
            object payloadLock,
            FriendRefreshProgressSession progress,
            CancellationToken cancel,
            Func<Task<bool>> ensureDefinitionAsync = null)
        {
            if (friendsProvider == null ||
                friend == null ||
                ownership == null ||
                !FriendRefreshWorkPolicy.ShouldGuardProviderOnlyZeroUnlocks(providerKey))
            {
                return true;
            }

            await limiter.DelayBeforeNextAsync(cancel).ConfigureAwait(false);
            var scrapeResult = await limiter.ExecuteWithRetryAsync(
                () => friendsProvider.GetFriendGameAchievementsAsync(
                    friend,
                    ownership.ProviderGameKey,
                    ownership.AppId,
                    ownership.GameName,
                    cancel),
                FriendRefreshWorkPolicy.IsTransientError,
                cancel).ConfigureAwait(false);

            if (scrapeResult?.AuthRequired == true)
            {
                lock (payloadLock)
                {
                    MarkAuthFailure(payload, providerKey, true);
                }

                return false;
            }

            if (scrapeResult?.Success != true)
            {
                return true;
            }

            var probedKey = BuildFriendProviderGameKey(friend.ExternalUserId, GetProviderGameCacheKey(ownership));
            if (!string.IsNullOrEmpty(probedKey) && probedProviderOnlyAchievementKeys != null)
            {
                lock (probedProviderOnlyAchievementKeys)
                {
                    probedProviderOnlyAchievementKeys.Add(probedKey);
                }
            }

            var achievements = scrapeResult.Data ?? new FriendGameAchievements();
            achievements.Friend = achievements.Friend ?? friend;
            achievements.AppId = achievements.AppId > 0 ? achievements.AppId : ownership.AppId;
            achievements.ProviderGameKey = string.IsNullOrWhiteSpace(achievements.ProviderGameKey)
                ? ownership.ProviderGameKey
                : achievements.ProviderGameKey;

            if (!FriendRefreshWorkPolicy.HasAnyUnlockedFriendAchievements(achievements))
            {
                return true;
            }

            // Deferred definition fetch (unknown-hint provider-only games): the game's schema is only
            // fetched and persisted once a probe has confirmed unlocks. Must run before the ownership
            // and achievements saves so stable-keyed rows (Exophase) can match AchievementDefinitions.
            // A false return means the definition fetch hit an auth wall; abort like the eager path.
            if (ensureDefinitionAsync != null &&
                !await ensureDefinitionAsync().ConfigureAwait(false))
            {
                return false;
            }

            var writeOwnership = _friendCache.SaveFriendOwnership(
                providerKey,
                friend.ExternalUserId,
                new[] { ownership },
                new FriendOwnershipSaveOptions { IncludeProviderOnlyGames = true });
            if (writeOwnership?.Success != true)
            {
                _logger?.Warn($"Failed to save provider-only friend ownership for {providerKey}/{friend.ExternalUserId}: {writeOwnership?.ErrorMessage}");
                return true;
            }

            lock (payloadLock)
            {
                payload.FriendSummary.OwnershipRowsWritten += writeOwnership.WrittenCount;
            }

            var writeAchievements = _friendCache.SaveFriendGameAchievements(
                providerKey,
                friend.ExternalUserId,
                ownership.ProviderGameKey,
                ownership.AppId,
                achievements);
            if (writeAchievements?.Success != true)
            {
                _logger?.Warn($"Failed to save probed provider-only friend achievements for {providerKey}/{friend.ExternalUserId}/{GetProviderGameCacheKey(ownership)}: {writeAchievements?.ErrorMessage}");
                return true;
            }

            lock (payloadLock)
            {
                payload.FriendSummary.CandidatesRefreshed++;
                payload.FriendSummary.AchievementsSaved++;
            }

            // Persist the game header banner parsed from the same achievement page as this provider-only
            // game's icon and cover. For seed-from-scrape providers (Exophase) the definition-fetch block that
            // normally downloads the banner is skipped, so this probe is the only place it gets captured. The
            // provider-only Games row was created by the SaveFriendOwnership call above, so the UPDATE-only
            // image save matches it.
            if (!string.IsNullOrWhiteSpace(achievements.IconUrl))
            {
                await DownloadDefinitionGameImageAsync(
                    providerKey,
                    ownership.ProviderGameKey,
                    ownership.AppId,
                    achievements.IconUrl,
                    ownership.GameName,
                    cancel,
                    progress).ConfigureAwait(false);
            }

            return true;
        }

        private async Task DownloadFriendAvatarsAsync(
            string providerKey,
            IReadOnlyList<FriendIdentity> friends,
            CancellationToken cancel)
        {
            if (friends == null || friends.Count == 0)
            {
                return;
            }

            var friendsWithAvatars = friends
                .Where(friend => friend != null && DiskImageService.IsCacheableImageSource(friend.AvatarUrl))
                .ToList();
            if (friendsWithAvatars.Count == 0)
            {
                return;
            }

            // Because the avatar filename no longer changes when the source URL changes, compare the
            // incoming URL against the persisted one (single load per provider) so a friend's new
            // avatar is re-downloaded while unchanged avatars reuse the cached file.
            var persistedAvatarUrls = LoadPersistedAvatarUrls(providerKey);

            await Task.WhenAll(friendsWithAvatars
                    .Select(async friend =>
                    {
                        try
                        {
                            persistedAvatarUrls.TryGetValue(friend.ExternalUserId ?? string.Empty, out var previousUrl);
                            await _achievementIconService
                                .PopulateFriendAvatarIconCacheAsync(providerKey, friend, previousUrl, cancel)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, $"Failed to cache friend avatar for {providerKey}/{friend.ExternalUserId}.");
                        }
                    }))
                .ConfigureAwait(false);
        }

        private Dictionary<string, string> LoadPersistedAvatarUrls(string providerKey)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var persisted = _friendCache?.LoadFriendIdentities(providerKey);
                if (persisted != null)
                {
                    foreach (var identity in persisted)
                    {
                        if (identity != null && !string.IsNullOrWhiteSpace(identity.ExternalUserId))
                        {
                            map[identity.ExternalUserId] = identity.AvatarUrl;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to load persisted friend avatar URLs for {providerKey}.");
            }

            return map;
        }

        // Progress denominators for the friend image download loops. These mirror what the loops
        // actually attempt (one unit per distinct cacheable game image), using the same
        // DiskImageService.IsCacheableImageSource predicate the downloads use, so the total can't drift
        // from the reported completions.
        private static int CountFriendGameImageSources(
            IEnumerable<string> providerGameKeys,
            Dictionary<string, List<FriendGameOwnership>> ownershipByKey)
        {
            if (providerGameKeys == null || ownershipByKey == null)
            {
                return 0;
            }

            var total = 0;
            foreach (var providerGameKey in providerGameKeys)
            {
                if (string.IsNullOrWhiteSpace(providerGameKey) ||
                    !ownershipByKey.TryGetValue(providerGameKey, out var owners))
                {
                    continue;
                }

                var source = owners?.FirstOrDefault(item => item != null);
                if (source == null)
                {
                    continue;
                }

                total += CountDistinctCacheableSources(source.IconUrl, source.CoverUrl);
            }

            return total;
        }

        private static int CountDistinctCacheableSources(params string[] sources)
        {
            if (sources == null || sources.Length == 0)
            {
                return 0;
            }

            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                if (DiskImageService.IsCacheableImageSource(source))
                {
                    distinct.Add(source);
                }
            }

            return distinct.Count;
        }

        private async Task DownloadDefinitionAchievementIconsAsync(
            FriendGameDefinition definition,
            CancellationToken cancel,
            FriendRefreshProgressSession progress)
        {
            if (_achievementIconService == null)
            {
                return;
            }

            // The icon pipeline no-ops on empty input and reports its own (completed, total), so no
            // pre-count guard is needed here.
            await _achievementIconService
                .PopulateFriendAchievementIconCacheAsync(
                    definition,
                    cancel,
                    (completed, total) => progress?.ReportAchievementImages(completed, total, definition.GameName))
                .ConfigureAwait(false);
        }

        private async Task DownloadUnownedGameImagesAsync(
            string providerKey,
            HashSet<string> providerGameKeys,
            Dictionary<string, List<FriendGameOwnership>> ownershipByKey,
            CancellationToken cancel,
            FriendRefreshProgressSession progress)
        {
            if (providerGameKeys == null || providerGameKeys.Count == 0)
            {
                return;
            }

            var imageAttempts = CountFriendGameImageSources(providerGameKeys, ownershipByKey);
            if (imageAttempts <= 0)
            {
                return;
            }

            var completed = 0;
            progress?.ReportFriendGameImages(0, imageAttempts);
            void ReportImageCompleted(string gameName)
            {
                var count = Interlocked.Increment(ref completed);
                progress?.ReportFriendGameImages(count, imageAttempts, gameName);
            }

            await Task.WhenAll(providerGameKeys.Select(providerGameKey =>
                    DownloadUnownedGameImageAsync(providerKey, providerGameKey, ownershipByKey, ReportImageCompleted, cancel)))
                .ConfigureAwait(false);
        }

        private async Task DownloadDefinitionGameImageAsync(
            string providerKey,
            string providerGameKey,
            int appId,
            string bannerUrl,
            string gameName,
            CancellationToken cancel,
            FriendRefreshProgressSession progress)
        {
            var imageAttempts = DiskImageService.IsCacheableImageSource(bannerUrl) ? 1 : 0;
            if (imageAttempts <= 0)
            {
                return;
            }

            var cacheKey = GetProviderGameCacheKey(appId, providerGameKey);
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return;
            }

            var detail = !string.IsNullOrWhiteSpace(gameName) ? gameName : cacheKey;
            progress?.ReportFriendGameImages(0, imageAttempts, detail);
            var completed = 0;
            try
            {
                var localPath = await _achievementIconService
                    .PopulateFriendGameIconCacheAsync(
                        providerKey,
                        cacheKey,
                        bannerUrl,
                        cancel,
                        () => progress?.ReportFriendGameImages(Interlocked.Increment(ref completed), imageAttempts, detail))
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    return;
                }

                var writeImages = _friendCache.SaveProviderGameImagePaths(providerKey, cacheKey, appId, localPath, localPath);
                if (writeImages?.Success != true)
                {
                    _logger?.Warn($"Failed to save friend game header image paths for {providerKey}/{cacheKey}: {writeImages?.ErrorMessage}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to cache friend game header image for {providerKey}/{cacheKey}.");
            }
        }

        private async Task DownloadUnownedGameImageAsync(
            string providerKey,
            string providerGameKey,
            Dictionary<string, List<FriendGameOwnership>> ownershipByKey,
            Action<string> reportImageCompleted,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(providerGameKey) ||
                ownershipByKey == null ||
                !ownershipByKey.TryGetValue(providerGameKey, out var owners))
            {
                return;
            }

            var source = owners?.FirstOrDefault(item => item != null);
            if (source == null)
            {
                return;
            }

            var gameKey = GetProviderGameCacheKey(source.AppId, source.ProviderGameKey) ??
                          (string.IsNullOrWhiteSpace(providerGameKey) ? null : providerGameKey.Trim());
            if (string.IsNullOrWhiteSpace(gameKey))
            {
                return;
            }

            var gameName = !string.IsNullOrWhiteSpace(source.GameName) ? source.GameName : gameKey;
            try
            {
                var result = await PopulateFriendGameImageCacheWithSteamFallbackAsync(
                        providerKey,
                        gameKey,
                        source,
                        cancel,
                        () => reportImageCompleted?.Invoke(gameName))
                    .ConfigureAwait(false);
                if (result?.HasAnyPath != true)
                {
                    return;
                }

                var writeImages = _friendCache.SaveProviderGameImagePaths(providerKey, gameKey, source.AppId, result.IconPath, result.CoverPath);
                if (writeImages?.Success != true)
                {
                    _logger?.Warn($"Failed to save unowned game image paths for {providerKey}/{gameKey}: {writeImages?.ErrorMessage}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to cache unowned game images for {providerKey}/{providerGameKey}.");
            }
        }

        private async Task<FriendGameImageCacheResult> PopulateFriendGameImageCacheWithSteamFallbackAsync(
            string providerKey,
            string gameKey,
            FriendGameOwnership source,
            CancellationToken cancel,
            Action onImageResolved)
        {
            var result = await _achievementIconService
                .PopulateFriendGameImageCacheAsync(
                    providerKey,
                    gameKey,
                    source?.IconUrl,
                    source?.CoverUrl,
                    cancel,
                    onImageResolved)
                .ConfigureAwait(false);

            if (!FriendRefreshWorkPolicy.ShouldTrySteamGameImageFallback(providerKey, source))
            {
                return result;
            }

            var missingIcon = string.IsNullOrWhiteSpace(result?.IconPath);
            var missingCover = string.IsNullOrWhiteSpace(result?.CoverPath);
            if (!missingIcon && !missingCover)
            {
                return result;
            }

            if (!missingIcon && missingCover)
            {
                var heroResult = await TryPopulateSteamFriendGameImageFallbackAsync(
                        providerKey,
                        gameKey,
                        iconUrl: null,
                        coverUrl: SteamImageUrls.LibraryHero(source.AppId),
                        sourceIconUrl: source.IconUrl,
                        sourceCoverUrl: source.CoverUrl,
                        cancel: cancel)
                    .ConfigureAwait(false);
                result = MergeFriendGameImageResults(result, heroResult);
                missingCover = string.IsNullOrWhiteSpace(result?.CoverPath);
            }

            missingIcon = string.IsNullOrWhiteSpace(result?.IconPath);
            if (!missingIcon && !missingCover)
            {
                return result;
            }

            var storeFallback = await SteamImageUrls
                .GetStoreFallbackAsync(source.AppId, cancel, _logger)
                .ConfigureAwait(false);
            if (storeFallback == null)
            {
                return result;
            }

            var storeResult = await TryPopulateSteamFriendGameImageFallbackAsync(
                    providerKey,
                    gameKey,
                    iconUrl: missingIcon ? storeFallback.IconUrl : null,
                    coverUrl: missingCover ? storeFallback.CoverUrl : null,
                    sourceIconUrl: source.IconUrl,
                    sourceCoverUrl: source.CoverUrl,
                    cancel: cancel)
                .ConfigureAwait(false);
            return MergeFriendGameImageResults(result, storeResult);
        }

        private async Task<FriendGameImageCacheResult> TryPopulateSteamFriendGameImageFallbackAsync(
            string providerKey,
            string gameKey,
            string iconUrl,
            string coverUrl,
            string sourceIconUrl,
            string sourceCoverUrl,
            CancellationToken cancel)
        {
            var fallbackIconUrl = FriendRefreshWorkPolicy.IsDistinctCacheableImageSource(iconUrl, sourceIconUrl) ? iconUrl : null;
            var fallbackCoverUrl = FriendRefreshWorkPolicy.IsDistinctCacheableImageSource(coverUrl, sourceCoverUrl) ? coverUrl : null;
            if (string.IsNullOrWhiteSpace(fallbackIconUrl) && string.IsNullOrWhiteSpace(fallbackCoverUrl))
            {
                return null;
            }

            return await _achievementIconService
                .PopulateFriendGameImageCacheAsync(
                    providerKey,
                    gameKey,
                    fallbackIconUrl,
                    fallbackCoverUrl,
                    cancel,
                    onImageResolved: null)
                .ConfigureAwait(false);
        }

        private static FriendGameImageCacheResult MergeFriendGameImageResults(
            FriendGameImageCacheResult primary,
            FriendGameImageCacheResult fallback)
        {
            if (fallback == null)
            {
                return primary;
            }

            if (primary == null)
            {
                return fallback;
            }

            return new FriendGameImageCacheResult
            {
                IconPath = FirstNonBlank(primary.IconPath, fallback.IconPath),
                CoverPath = FirstNonBlank(primary.CoverPath, fallback.CoverPath)
            };
        }

        private static string FirstNonBlank(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private async Task<bool> RefreshAchievementCandidateAsync(
            IFriendsProvider friendsProvider,
            string providerKey,
            FriendRefreshCandidate candidate,
            RebuildPayload payload,
            object payloadLock,
            bool delayBeforeRequest,
            RateLimiter limiter,
            CancellationToken cancel)
        {
            if (candidate?.Friend == null || !FriendRefreshWorkPolicy.HasProviderGameIdentity(candidate.AppId, candidate.ProviderGameKey))
            {
                return true;
            }

            if (delayBeforeRequest)
            {
                await limiter.DelayBeforeNextAsync(cancel).ConfigureAwait(false);
            }

            var scrapeResult = await limiter.ExecuteWithRetryAsync(
                () => friendsProvider.GetFriendGameAchievementsAsync(
                    candidate.Friend,
                    candidate.ProviderGameKey,
                    candidate.AppId,
                    candidate.GameName,
                    cancel),
                FriendRefreshWorkPolicy.IsTransientError,
                cancel).ConfigureAwait(false);

            if (scrapeResult?.AuthRequired == true)
            {
                lock (payloadLock)
                {
                    MarkAuthFailure(payload, providerKey, true);
                }

                return false;
            }

            var achievements = scrapeResult?.Data ?? CreateFailureResult(candidate, scrapeResult);
            if (ShouldSkipProviderOnlyZeroUnlocks(providerKey, candidate, scrapeResult, achievements))
            {
                lock (payloadLock)
                {
                    payload.FriendSummary.CandidatesRefreshed++;
                }

                return true;
            }

            var writeAchievements = _friendCache.SaveFriendGameAchievements(
                providerKey,
                candidate.Friend.ExternalUserId,
                candidate.ProviderGameKey,
                candidate.AppId,
                achievements);
            if (writeAchievements?.Success != true)
            {
                _logger?.Warn($"Failed to save friend achievements for {providerKey}/{candidate.Friend.ExternalUserId}/{GetProviderGameCacheKey(candidate)}: {writeAchievements?.ErrorMessage}");
                return true;
            }

            lock (payloadLock)
            {
                payload.FriendSummary.CandidatesRefreshed++;
                payload.FriendSummary.AchievementsSaved++;
            }

            return true;
        }

        private int ResolveFriendRefreshParallelism()
        {
            return (_settings?.Persisted?.EnableParallelProviderRefresh ?? true)
                ? FriendRefreshParallelism
                : 1;
        }

        private static void MaybeFlushFriendInvalidations(
            IFriendCacheInvalidationBatch friendInvalidationBatch,
            FriendInvalidationFlushState state,
            bool force = false)
        {
            if (friendInvalidationBatch == null || state == null)
            {
                return;
            }

            var shouldFlush = false;
            lock (state.Sync)
            {
                state.CompletedSinceFlush++;
                var elapsed = DateTime.UtcNow - state.LastFlushUtc;
                if (force ||
                    state.CompletedSinceFlush >= FriendInvalidationFlushMinCompletions ||
                    elapsed >= FriendInvalidationFlushInterval)
                {
                    state.CompletedSinceFlush = 0;
                    state.LastFlushUtc = DateTime.UtcNow;
                    shouldFlush = true;
                }
            }

            if (shouldFlush)
            {
                friendInvalidationBatch.Flush();
            }
        }

        private bool TryPrepareFriendRosterFromSettingsOrCache(
            string providerKey,
            FriendRefreshOptions options,
            FriendProviderRefreshContext context,
            RebuildPayload payload)
        {
            if (context == null)
            {
                return false;
            }

            var selection = ResolveProviderFriendSelection(options, providerKey);
            var selectedIds = selection.ExternalUserIds;
            var ignoredIds = GetIgnoredFriendIds(providerKey);
            var focused = FriendRefreshWorkPolicy.IsFocusedFriendGameRefresh(options);
            var friends = LoadConfiguredFriendIdentities(providerKey, ignoredIds, out var source);
            var lookup = friends
                .Where(friend => friend != null && !string.IsNullOrWhiteSpace(friend.ExternalUserId))
                .GroupBy(friend => friend.ExternalUserId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var scoped = new List<FriendIdentity>();
            var synthesized = 0;
            if (selection.HasExplicitSelection)
            {
                foreach (var id in selectedIds)
                {
                    if (ignoredIds.Contains(id))
                    {
                        continue;
                    }

                    if (lookup.TryGetValue(id, out var friend))
                    {
                        scoped.Add(friend);
                        continue;
                    }

                    if (!focused)
                    {
                        continue;
                    }

                    synthesized++;
                    scoped.Add(new FriendIdentity
                    {
                        ProviderKey = providerKey,
                        ExternalUserId = id,
                        DisplayName = id
                    });
                }
            }
            else
            {
                scoped.AddRange(lookup.Values);
            }

            if (scoped.Count == 0)
            {
                _logger?.Debug(
                    $"Friends refresh skipped for {providerKey}: no configured or cached active friends available for refresh.");
                context.RosterSource = source;
                return false;
            }

            var allFriends = lookup.Values.ToList();
            foreach (var friend in scoped)
            {
                if (!string.IsNullOrWhiteSpace(friend.ExternalUserId) &&
                    !lookup.ContainsKey(friend.ExternalUserId.Trim()))
                {
                    allFriends.Add(friend);
                }
            }

            context.Friends = allFriends;
            context.ScopedFriends = scoped;
            context.RosterSource = synthesized > 0
                ? (lookup.Count > 0 ? source + "+request" : "request")
                : source;
            payload.FriendSummary.FriendsFetched += context.Friends.Count;

            _logger?.Debug(
                    $"[RefreshPerf] phase=friend.roster provider={providerKey} source={context.RosterSource} friends={context.Friends.Count} scopedFriends={context.ScopedFriends.Count} selectedIds={selectedIds.Count} synthesized={synthesized}");
            return true;
        }

        private List<FriendIdentity> LoadConfiguredFriendIdentities(
            string providerKey,
            HashSet<string> ignoredIds,
            out string source)
        {
            var settingsFriends = NormalizeProviderFriendIdentities(
                    providerKey,
                    _settings?.Persisted?.GetActiveFriendIdentities(providerKey))
                .Where(friend => friend != null &&
                                 !string.IsNullOrWhiteSpace(friend.ExternalUserId) &&
                                 !(ignoredIds?.Contains(friend.ExternalUserId.Trim()) == true))
                .ToList();
            if (settingsFriends.Count > 0)
            {
                source = "settings";
                return settingsFriends;
            }

            try
            {
                var cachedFriends = NormalizeProviderFriendIdentities(
                        providerKey,
                        _friendCache?.LoadFriendIdentities(providerKey))
                    .Where(friend => friend != null &&
                                     !string.IsNullOrWhiteSpace(friend.ExternalUserId) &&
                                     !(ignoredIds?.Contains(friend.ExternalUserId.Trim()) == true))
                    .ToList();
                source = cachedFriends.Count > 0 ? "cache" : "none";
                return cachedFriends;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Friend roster cache lookup failed for {providerKey}.");
                source = "cache-error";
                return new List<FriendIdentity>();
            }
        }

        private IReadOnlyList<FriendIdentity> FilterIgnoredFriends(
            string providerKey,
            IReadOnlyList<FriendIdentity> friends)
        {
            var ignoredIds = GetIgnoredFriendIds(providerKey);
            if (ignoredIds.Count == 0)
            {
                return friends ?? Array.Empty<FriendIdentity>();
            }

            return (friends ?? Array.Empty<FriendIdentity>())
                .Where(friend => !ignoredIds.Contains(friend?.ExternalUserId ?? string.Empty))
                .ToList();
        }

        private List<LogicalFriendRefreshGroup> BuildLogicalFriendGroups(
            IReadOnlyList<FriendProviderRefreshContext> contexts,
            FriendRefreshOptions options)
        {
            var groups = new Dictionary<string, LogicalFriendRefreshGroup>(StringComparer.OrdinalIgnoreCase);
            var accountLookup = new Dictionary<string, FriendIdentity>(StringComparer.OrdinalIgnoreCase);
            foreach (var context in contexts ?? Array.Empty<FriendProviderRefreshContext>())
            {
                foreach (var friend in (IEnumerable<FriendIdentity>)context?.ScopedFriends ?? Array.Empty<FriendIdentity>())
                {
                    if (!ShouldRefreshOwnershipForFriend(context, friend, options))
                    {
                        continue;
                    }

                    var accountKey = FriendAccountRef.BuildKey(context.ProviderKey, friend?.ExternalUserId);
                    if (!string.IsNullOrWhiteSpace(accountKey) && !accountLookup.ContainsKey(accountKey))
                    {
                        accountLookup[accountKey] = friend;
                    }
                }
            }

            foreach (var context in contexts ?? Array.Empty<FriendProviderRefreshContext>())
            {
                foreach (var friend in (IEnumerable<FriendIdentity>)context?.ScopedFriends ?? Array.Empty<FriendIdentity>())
                {
                    if (friend == null ||
                        string.IsNullOrWhiteSpace(friend.ExternalUserId) ||
                        !ShouldRefreshOwnershipForFriend(context, friend, options))
                    {
                        continue;
                    }

                    var mergeGroup = _settings?.Persisted?.GetFriendMergeGroupForAccount(context.ProviderKey, friend.ExternalUserId);
                    var groupKey = !string.IsNullOrWhiteSpace(mergeGroup?.Id)
                        ? "merged|" + mergeGroup.Id
                        : "account|" + FriendAccountRef.BuildKey(context.ProviderKey, friend.ExternalUserId);
                    if (string.IsNullOrWhiteSpace(groupKey))
                    {
                        continue;
                    }

                    if (!groups.TryGetValue(groupKey, out var group))
                    {
                        group = new LogicalFriendRefreshGroup
                        {
                            Key = groupKey,
                            DisplayName = ResolveLogicalFriendDisplayName(mergeGroup, friend, accountLookup)
                        };
                        groups[groupKey] = group;
                    }

                    group.Accounts.Add(new FriendAccountRefreshItem
                    {
                        Context = context,
                        Friend = friend
                    });
                }
            }

            return groups.Values
                .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool ShouldRefreshOwnershipForFriend(
            FriendProviderRefreshContext context,
            FriendIdentity friend,
            FriendRefreshOptions options)
        {
            if (context == null ||
                friend == null ||
                !FriendRefreshWorkPolicy.ShouldRefreshOwnership(context.ProviderKey, options))
            {
                return false;
            }

            return true;
        }

        private static string ResolveLogicalFriendDisplayName(
            FriendMergeGroup mergeGroup,
            FriendIdentity fallbackFriend,
            IReadOnlyDictionary<string, FriendIdentity> accountLookup)
        {
            if (!string.IsNullOrWhiteSpace(mergeGroup?.Nickname))
            {
                return mergeGroup.Nickname.Trim();
            }

            foreach (var member in mergeGroup?.Members ?? Enumerable.Empty<FriendAccountRef>())
            {
                if (!string.IsNullOrWhiteSpace(member?.Key) &&
                    accountLookup != null &&
                    accountLookup.TryGetValue(member.Key, out var friend) &&
                    !string.IsNullOrWhiteSpace(friend?.DisplayName))
                {
                    return friend.DisplayName.Trim();
                }
            }

            return GetFriendDisplayName(fallbackFriend);
        }

        private static IReadOnlyList<FriendIdentity> NormalizeProviderFriendIdentities(
            string providerKey,
            IReadOnlyList<FriendIdentity> friends)
        {
            return (friends ?? Array.Empty<FriendIdentity>())
                .Where(friend => !string.IsNullOrWhiteSpace(friend?.ExternalUserId))
                .Select(friend => new FriendIdentity
                {
                    ProviderKey = string.IsNullOrWhiteSpace(friend.ProviderKey)
                        ? providerKey
                        : friend.ProviderKey.Trim(),
                    ExternalUserId = friend.ExternalUserId.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(friend.DisplayName)
                        ? friend.ExternalUserId.Trim()
                        : friend.DisplayName.Trim(),
                    AvatarUrl = string.IsNullOrWhiteSpace(friend.AvatarUrl) ? null : friend.AvatarUrl.Trim(),
                    AvatarPath = string.IsNullOrWhiteSpace(friend.AvatarPath) ? null : friend.AvatarPath.Trim(),
                    LastRefreshedUtc = friend.LastRefreshedUtc
                })
                .GroupBy(friend => friend.ExternalUserId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        // Restricts per-friend work to the requested subset when the options carry a friend
        // selection (e.g. a custom refresh of specific friends); otherwise returns all friends.
        private static IReadOnlyList<FriendIdentity> ScopeFriendsToSelection(
            IReadOnlyList<FriendIdentity> friends,
            FriendRefreshOptions options)
        {
            var all = friends ?? Array.Empty<FriendIdentity>();
            var selection = ResolveProviderFriendSelection(options, null);
            var selectedIds = selection.ExternalUserIds;
            if (!selection.HasExplicitSelection)
            {
                return all;
            }

            return all
                .Where(friend => friend != null && selectedIds.Contains(friend.ExternalUserId?.Trim() ?? string.Empty))
                .ToList();
        }

        private static HashSet<string> NormalizeFriendSelectionIds(IReadOnlyCollection<string> friendExternalUserIds)
        {
            return new HashSet<string>(
                friendExternalUserIds?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim()) ??
                Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static ProviderFriendSelection ResolveProviderFriendSelection(
            FriendRefreshOptions options,
            string providerKey)
        {
            var accounts = NormalizeFriendAccounts(options?.FriendAccounts);
            if (accounts.Count > 0)
            {
                return new ProviderFriendSelection
                {
                    HasExplicitSelection = true,
                    ExternalUserIds = new HashSet<string>(
                        accounts
                            .Where(account =>
                                string.IsNullOrWhiteSpace(providerKey) ||
                                string.Equals(account.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
                            .Select(account => account.ExternalUserId)
                            .Where(id => !string.IsNullOrWhiteSpace(id)),
                        StringComparer.OrdinalIgnoreCase)
                };
            }

            var legacyIds = NormalizeFriendSelectionIds(options?.FriendExternalUserIds);
            return new ProviderFriendSelection
            {
                HasExplicitSelection = legacyIds.Count > 0,
                ExternalUserIds = legacyIds
            };
        }

        private static List<FriendAccountRef> NormalizeFriendAccounts(IReadOnlyCollection<FriendAccountRef> accounts)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<FriendAccountRef>();
            foreach (var account in accounts ?? Enumerable.Empty<FriendAccountRef>())
            {
                var next = account?.Clone()?.Normalize();
                if (string.IsNullOrWhiteSpace(next?.Key) || !seen.Add(next.Key))
                {
                    continue;
                }

                normalized.Add(next);
            }

            return normalized;
        }

        private List<FriendRefreshCandidate> FilterIgnoredCandidates(
            string providerKey,
            List<FriendRefreshCandidate> candidates)
        {
            var ignoredIds = GetIgnoredFriendIds(providerKey);
            if (ignoredIds.Count == 0)
            {
                return candidates ?? new List<FriendRefreshCandidate>();
            }

            return (candidates ?? new List<FriendRefreshCandidate>())
                .Where(candidate => !ignoredIds.Contains(candidate?.Friend?.ExternalUserId ?? string.Empty))
                .ToList();
        }

        private List<FriendRefreshCandidate> FilterProviderOnlyDetailCandidates(
            string providerKey,
            List<FriendRefreshCandidate> candidates,
            FriendRefreshOptions options)
        {
            // This cache-sourced loader now serves only the scopes that do not build candidates from the
            // fresh ownership snapshot (Recent, SelectedGame, Custom). Recent and SelectedGame resolve to
            // library-mapped games; Custom provider-only targets are explicit and, when discovered this
            // run, are de-duplicated against the probe via ProbedProviderOnlyAchievementKeys upstream.
            return (candidates ?? new List<FriendRefreshCandidate>())
                .Where(candidate => ShouldRefreshFriendGameAchievements(providerKey, candidate, options))
                .ToList();
        }

        private HashSet<string> GetIgnoredFriendIds(string providerKey)
        {
            return _settings?.Persisted?.GetIgnoredFriendIds(providerKey) ??
                   new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private bool IsPlayniteLibraryFriendGame(string providerKey, FriendGameOwnership ownership)
        {
            if (ownership?.PlayniteGameId.HasValue == true &&
                ownership.PlayniteGameId.Value != Guid.Empty)
            {
                return true;
            }

            return _friendCache?.IsProviderGameMappedToPlayniteLibrary(
                providerKey,
                ownership?.AppId ?? 0,
                ownership?.ProviderGameKey) == true;
        }

        private bool IsPlayniteLibraryFriendGame(string providerKey, FriendRefreshCandidate candidate)
        {
            if (candidate?.PlayniteGameId.HasValue == true &&
                candidate.PlayniteGameId.Value != Guid.Empty)
            {
                return true;
            }

            return _friendCache?.IsProviderGameMappedToPlayniteLibrary(
                providerKey,
                candidate?.AppId ?? 0,
                candidate?.ProviderGameKey) == true;
        }

        private bool ShouldRefreshFriendGameDefinition(
            string providerKey,
            FriendGameOwnership ownership,
            FriendRefreshOptions options)
        {
            if (ownership == null)
            {
                return false;
            }

            // Games the provider reports as having no achievements are never candidates (symmetric with
            // the mapped-unlock builder). Excludes them before the schema fetch rather than discovering
            // it only after fetching a NoAchievements definition.
            if (ownership.AchievementTotalHint.HasValue && ownership.AchievementTotalHint.Value <= 0)
            {
                return false;
            }

            if (IsPlayniteLibraryFriendGame(providerKey, ownership))
            {
                return true;
            }

            if (FriendRefreshWorkPolicy.IsExplicitProviderGameTarget(options, ownership.AppId, ownership.ProviderGameKey))
            {
                return true;
            }

            if (FriendRefreshWorkPolicy.HasZeroUnlockHint(ownership))
            {
                return false;
            }

            if (FriendRefreshWorkPolicy.HasPositiveUnlockHint(ownership))
            {
                return true;
            }

            // Hint unknown (provider did not supply an earned count): in a scope that discovers
            // provider-only games (Full) scrape it anyway rather than silently dropping the game.
            // The post-scrape zero-unlock guard prunes it if it turns out empty.
            return options?.DiscoversProviderOnlyGames() == true;
        }

        private bool ShouldRefreshFriendGameAchievements(
            string providerKey,
            FriendRefreshCandidate candidate,
            FriendRefreshOptions options)
        {
            if (candidate?.Friend == null || !FriendRefreshWorkPolicy.HasProviderGameIdentity(candidate.AppId, candidate.ProviderGameKey))
            {
                return false;
            }

            if (IsPlayniteLibraryFriendGame(providerKey, candidate))
            {
                return true;
            }

            if (FriendRefreshWorkPolicy.IsExplicitProviderGameTarget(options, candidate.AppId, candidate.ProviderGameKey))
            {
                return true;
            }

            // Cache-sourced candidates carry no unlock hint (the hint is a live-scrape-only signal), so
            // candidacy for the remaining games is decided by scope: a discovery scope keeps them, others
            // (the mapped checks above already returned) drop them.
            return options?.DiscoversProviderOnlyGames() == true;
        }

        private static IReadOnlyList<FriendGameOwnership> ScopeOwnedGamesForRefresh(
            IReadOnlyList<FriendGameOwnership> ownedGames,
            FriendRefreshOptions options)
        {
            var source = ownedGames ?? Array.Empty<FriendGameOwnership>();
            if (!FriendRefreshWorkPolicy.HasExplicitProviderGameTargets(options))
            {
                return source;
            }

            return source
                .Where(item => FriendRefreshWorkPolicy.IsExplicitProviderGameTarget(options, item?.AppId ?? 0, item?.ProviderGameKey))
                .ToList();
        }

        private static IReadOnlyList<FriendGameOwnership> FilterOwnedGamesForProviderRefresh(
            string providerKey,
            IReadOnlyList<FriendGameOwnership> ownedGames)
        {
            return ownedGames ?? Array.Empty<FriendGameOwnership>();
        }

        private bool ShouldSkipProviderOnlyZeroUnlocks(
            string providerKey,
            FriendRefreshCandidate candidate,
            FriendsProviderResult<FriendGameAchievements> scrapeResult,
            FriendGameAchievements achievements)
        {
            return FriendRefreshWorkPolicy.ShouldGuardProviderOnlyZeroUnlocks(providerKey) &&
                   scrapeResult?.Success == true &&
                   candidate?.Friend != null &&
                   FriendRefreshWorkPolicy.HasProviderGameIdentity(candidate.AppId, candidate.ProviderGameKey) &&
                   !IsPlayniteLibraryFriendGame(providerKey, candidate) &&
                   !FriendRefreshWorkPolicy.HasAnyUnlockedFriendAchievements(achievements);
        }

        private RateLimiter CreateScanRateLimiter()
        {
            return new RateLimiter(
                Math.Max(0, _settings?.Persisted?.ScanDelayMs ?? 200),
                Math.Max(0, _settings?.Persisted?.MaxRetryAttempts ?? 3));
        }

        private static async Task RunBoundedAsync<T>(
            IReadOnlyList<T> items,
            int maxDegreeOfParallelism,
            Func<T, int, CancellationToken, Task> body,
            CancellationToken cancel)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (items == null || items.Count == 0)
            {
                return;
            }

            var degree = Math.Max(1, Math.Min(maxDegreeOfParallelism, items.Count));
            if (degree == 1)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    await body(items[i], i, cancel).ConfigureAwait(false);
                }

                return;
            }

            var nextIndex = -1;
            var workers = Enumerable.Range(0, degree)
                .Select(_ => WorkerAsync())
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);

            async Task WorkerAsync()
            {
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= items.Count)
                    {
                        return;
                    }

                    await body(items[index], index, cancel).ConfigureAwait(false);
                }
            }
        }

        private static string ResolveOwnershipGameName(
            IReadOnlyList<FriendGameOwnership> ownership,
            string providerKey,
            string providerGameKey)
        {
            var name = ownership?
                .Select(item => item?.GameName)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : $"{providerKey} Game {providerGameKey}";
        }

        // Stamps the current-user library mapping onto freshly-fetched ownership items so the shared
        // ownership save can create/upgrade a library-mapped Games row (via the same inline-id channel
        // Exophase uses) instead of silently skipping games that have no pre-existing mapped row. This
        // is what makes a shared game appear in the friends overview even when the friend has no
        // unlocks, and what routes it through the mapped scrape instead of the provider-only probe.
        private static void StampPlayniteGameIdsFromCurrentUserLabels(
            IReadOnlyList<FriendGameOwnership> ownedGames,
            IReadOnlyDictionary<string, Guid> currentUserLabelIndex)
        {
            if (ownedGames == null || currentUserLabelIndex == null || currentUserLabelIndex.Count == 0)
            {
                return;
            }

            foreach (var item in ownedGames)
            {
                if (item == null ||
                    (item.PlayniteGameId.HasValue && item.PlayniteGameId.Value != Guid.Empty))
                {
                    continue;
                }

                var cacheKey = GetProviderGameCacheKey(item);
                if (!string.IsNullOrWhiteSpace(cacheKey) &&
                    currentUserLabelIndex.TryGetValue(cacheKey, out var playniteGameId))
                {
                    item.PlayniteGameId = playniteGameId;
                }
            }
        }

        private static string GetProviderGameCacheKey(FriendGameOwnership ownership)
        {
            return ownership == null ? null : GetProviderGameCacheKey(ownership.AppId, ownership.ProviderGameKey);
        }

        private static string GetProviderGameCacheKey(FriendRefreshCandidate candidate)
        {
            return candidate == null ? null : GetProviderGameCacheKey(candidate.AppId, candidate.ProviderGameKey);
        }

        private static string GetProviderGameCacheKey(int appId, string providerGameKey)
        {
            return !string.IsNullOrWhiteSpace(providerGameKey)
                ? providerGameKey.Trim()
                : (appId > 0 ? appId.ToString() : null);
        }

        private static string BuildRecencyGameKey(string externalUserId, string providerGameCacheKey)
        {
            return BuildFriendProviderGameKey(externalUserId, providerGameCacheKey);
        }

        private static string BuildFriendProviderGameKey(string externalUserId, string providerGameCacheKey)
        {
            return (externalUserId?.Trim() ?? string.Empty) + (char)31 + (providerGameCacheKey ?? string.Empty);
        }


        private FriendRefreshOptions NormalizeOptions(FriendRefreshOptions options)
        {
            return options?.Clone() ?? new FriendRefreshOptions();
        }

        private static bool RequiresAnyOwnershipRefresh(
            IEnumerable<FriendProviderRefreshContext> contexts,
            FriendRefreshOptions options)
        {
            return (contexts ?? Enumerable.Empty<FriendProviderRefreshContext>())
                .Any(context => context != null && FriendRefreshWorkPolicy.ShouldRefreshOwnership(context.ProviderKey, options));
        }

        private static string GetFriendDisplayName(FriendIdentity friend)
        {
            if (!string.IsNullOrWhiteSpace(friend?.DisplayName))
            {
                return friend.DisplayName.Trim();
            }

            return friend?.ExternalUserId?.Trim();
        }

        private static string FormatFriendGameDetail(FriendRefreshCandidate candidate)
        {
            if (candidate == null)
            {
                return null;
            }

            var friendName = GetFriendDisplayName(candidate.Friend);
            var gameName = !string.IsNullOrWhiteSpace(candidate.GameName)
                ? candidate.GameName.Trim()
                : GetProviderGameCacheKey(candidate);

            if (string.IsNullOrWhiteSpace(friendName))
            {
                return gameName;
            }

            if (string.IsNullOrWhiteSpace(gameName))
            {
                return friendName;
            }

            return $"{friendName} - {gameName}";
        }

        internal static void MarkAuthFailure(RebuildPayload payload, string providerKey, bool authRequired)
        {
            if (!authRequired || payload == null)
            {
                return;
            }

            payload.AuthRequired = true;
            if (!string.IsNullOrWhiteSpace(providerKey) &&
                !payload.FailedProviderKeys.Contains(providerKey, StringComparer.OrdinalIgnoreCase))
            {
                payload.FailedProviderKeys.Add(providerKey);
            }
        }

        internal static void Merge(RebuildPayload target, RebuildPayload source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.AuthRequired |= source.AuthRequired;
            foreach (var key in source.FailedProviderKeys ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(key) &&
                    !target.FailedProviderKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    target.FailedProviderKeys.Add(key);
                }
            }

            target.Summary.GamesRefreshed += source.Summary?.GamesRefreshed ?? 0;
            target.Summary.GamesWithAchievements += source.Summary?.GamesWithAchievements ?? 0;
            target.Summary.GamesWithoutAchievements += source.Summary?.GamesWithoutAchievements ?? 0;
            foreach (var gameId in source.Summary?.RefreshedGameIds ?? Enumerable.Empty<Guid>())
            {
                if (gameId != Guid.Empty && !target.Summary.RefreshedGameIds.Contains(gameId))
                {
                    target.Summary.RefreshedGameIds.Add(gameId);
                }
            }

            target.FriendSummary.ProvidersProcessed += source.FriendSummary?.ProvidersProcessed ?? 0;
            target.FriendSummary.FriendsFetched += source.FriendSummary?.FriendsFetched ?? 0;
            target.FriendSummary.FriendsSaved += source.FriendSummary?.FriendsSaved ?? 0;
            target.FriendSummary.OwnershipPagesRefreshed += source.FriendSummary?.OwnershipPagesRefreshed ?? 0;
            target.FriendSummary.OwnershipRowsWritten += source.FriendSummary?.OwnershipRowsWritten ?? 0;
            target.FriendSummary.CandidatesLoaded += source.FriendSummary?.CandidatesLoaded ?? 0;
            target.FriendSummary.CandidatesRefreshed += source.FriendSummary?.CandidatesRefreshed ?? 0;
            target.FriendSummary.AchievementsSaved += source.FriendSummary?.AchievementsSaved ?? 0;
        }

        private static FriendGameAchievements CreateFailureResult(
            FriendRefreshCandidate candidate,
            FriendsProviderResult<FriendGameAchievements> scrapeResult)
        {
            return new FriendGameAchievements
            {
                Friend = candidate.Friend,
                AppId = candidate.AppId,
                ProviderGameKey = candidate.ProviderGameKey,
                LastUpdatedUtc = DateTime.UtcNow,
                StatsUnavailable = true,
                TransientFailure = scrapeResult?.TransientFailure == true,
                DetailCode = SteamScrapeDetail.None
            };
        }

        internal sealed class FriendOwnershipSnapshot
        {
            public FriendIdentity Friend { get; set; }
            public List<FriendGameOwnership> Ownership { get; set; } = new List<FriendGameOwnership>();
        }

        internal sealed class FriendProviderRefreshContext
        {
            public IFriendsProvider Provider { get; set; }
            public string ProviderKey { get; set; }
            public FriendsRefreshPreparation Preparation { get; set; } = new FriendsRefreshPreparation();
            public List<FriendIdentity> Friends { get; set; } = new List<FriendIdentity>();
            public List<FriendIdentity> ScopedFriends { get; set; } = new List<FriendIdentity>();
            public IReadOnlyList<CurrentUserGameLabel> CurrentUserLabels { get; set; } =
                new List<CurrentUserGameLabel>();
            // (AppId/ProviderGameKey) cache key -> PlayniteGameId, for providers where friend
            // ownership items are stamped with the current-user library mapping before the shared
            // ownership save (see FriendRefreshWorkPolicy.ShouldMapOwnershipFromCurrentUserLabels).
            // Built once in the sequential prepare phase; read-only afterwards.
            public IReadOnlyDictionary<string, Guid> CurrentUserLabelIndex { get; set; }
            public string RosterSource { get; set; } = "unknown";
            public bool DiscoverUnowned { get; set; }
            public bool CanContinue { get; set; }
            public int MaxDegreeOfParallelism { get; set; } = 1;
            public List<FriendOwnershipSnapshot> OwnershipSnapshots { get; set; }
            public HashSet<string> ProbedProviderOnlyAchievementKeys { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int RawCandidatesLoaded { get; set; }
            public int CandidatesSkippedAlreadyProbed { get; set; }
            public int CandidatesSkippedRecencyFresh { get; set; }
            public int CandidatesSkippedOwnershipUnavailable { get; set; }
            public int CandidatesQueued { get; set; }

            // Full-scope only: the unowned-definition plan (which provider games are due for a
            // definition fetch, plus the provider-only probe count), computed up front so the friend
            // definitions progress sub-band knows its full total before it emits any completion.
            public UnownedDefinitionPlan DefinitionPlan { get; set; }

            // Recent-scope only: keys for friend games the freshly-fetched ownership positively confirms
            // unchanged since the last successful scrape (Steam: playtime equal; RA/Exophase: no newer
            // last-played/unlock). Populated at the ownership step and used by LoadAchievementWorkItems to SKIP.
            public HashSet<string> RecencyFreshKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Recent-scope only: friends whose owned-games fetch succeeded this run. Recent scrapes
            // fail closed: candidates for friends without a fresh ownership snapshot are dropped
            // rather than scraped blind (a fetch hiccup must not dump the whole cached backlog).
            public HashSet<string> OwnershipFetchedFriendIds { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class LogicalFriendRefreshGroup
        {
            public string Key { get; set; }
            public string DisplayName { get; set; }
            public List<FriendAccountRefreshItem> Accounts { get; } = new List<FriendAccountRefreshItem>();
        }

        private sealed class FriendAccountRefreshItem
        {
            public FriendProviderRefreshContext Context { get; set; }
            public FriendIdentity Friend { get; set; }
        }

        private sealed class ProviderFriendSelection
        {
            public bool HasExplicitSelection { get; set; }
            public HashSet<string> ExternalUserIds { get; set; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class FriendAchievementWorkItem
        {
            public FriendProviderRefreshContext Context { get; set; }
            public FriendRefreshCandidate Candidate { get; set; }
        }

        private sealed class FriendInvalidationFlushState
        {
            public object Sync { get; } = new object();
            public DateTime LastFlushUtc { get; set; } = DateTime.UtcNow;
            public int CompletedSinceFlush { get; set; }
        }

        // Precomputed plan for the unowned-definition discovery phase. Computed once (a read-only cache
        // read) so the definitions progress sub-band has its full total before it reports any completion,
        // avoiding the monotonic-clamp freeze that piecemeal totals caused.
        internal sealed class UnownedDefinitionPlan
        {
            public Dictionary<string, List<FriendGameOwnership>> OwnershipByKey { get; set; } =
                new Dictionary<string, List<FriendGameOwnership>>(StringComparer.OrdinalIgnoreCase);
            public List<string> ProviderGameKeys { get; set; } = new List<string>();
            public List<string> DueProviderGameKeys { get; set; } = new List<string>();
            // Definition-due provider-only games whose owners all have an unknown unlock hint. Their
            // definition is fetched lazily — only after a probe confirms the friend has unlocks — so a
            // zero-unlock unowned game leaves no trace (no definition rows, no Games row, no image).
            public List<string> DeferredProviderGameKeys { get; set; } = new List<string>();
            public int ProbeItemCount { get; set; }

            // Total number of definitions-phase progress completions: one per eager definition fetch,
            // one per deferred key (resolved exactly once — fetched on first confirmed unlock, or
            // reported as skipped after every owner probes empty) plus one per provider-only probe.
            public int TotalDefinitionChecks => DueProviderGameKeys.Count + DeferredProviderGameKeys.Count + ProbeItemCount;
        }

        internal sealed class FriendRefreshPerfSession
        {
            private readonly ILogger _logger;
            private readonly FriendRefreshOptions _options;
            private readonly int _providerCount;
            private readonly string _kind;
            private readonly Stopwatch _total = Stopwatch.StartNew();
            private readonly MemorySnapshot _memBaseline =
                MemoryDiagnostics.Enabled ? MemoryDiagnostics.Capture() : default(MemorySnapshot);

            public FriendRefreshPerfSession(
                ILogger logger,
                FriendRefreshOptions options,
                int providerCount,
                string kind = "friends")
            {
                _logger = logger;
                _options = options;
                _providerCount = Math.Max(0, providerCount);
                _kind = string.IsNullOrWhiteSpace(kind) ? "friends" : kind.Trim();
                Log(
                    "friend.start",
                    $"providers={_providerCount} scope={_options?.Scope} focused={FriendRefreshWorkPolicy.IsFocusedFriendGameRefresh(_options)} playniteGames={Count(_options?.PlayniteGameIds)} providerApps={Count(_options?.ProviderAppIds)} providerKeys={Count(_options?.ProviderGameKeys)} friends={Count(_options?.FriendExternalUserIds)} forceDefinitions={_options?.ForceDefinitionRefresh == true}");
            }

            public void LogPrepare(
                Stopwatch timer,
                IReadOnlyList<FriendProviderRefreshContext> contexts,
                string extra = null)
            {
                var active = contexts?.Count(context => context?.CanContinue == true) ?? 0;
                var friends = contexts?.Sum(context => context?.Friends?.Count ?? 0) ?? 0;
                var scoped = contexts?.Sum(context => context?.ScopedFriends?.Count ?? 0) ?? 0;
                var sources = string.Join(
                    ",",
                    (contexts ?? Array.Empty<FriendProviderRefreshContext>())
                    .Where(context => context != null)
                    .GroupBy(context => context.RosterSource ?? "unknown", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Key + ":" + group.Count()));
                Log(
                    "friend.prepare",
                    $"ms={Elapsed(timer)} contexts={contexts?.Count ?? 0} active={active} friends={friends} scopedFriends={scoped} rosterSources={sources}{FormatExtra(extra)}");
            }

            public void LogDefinitionPlan(
                Stopwatch timer,
                IReadOnlyList<FriendProviderRefreshContext> contexts,
                int totalChecks)
            {
                var providerKeys = contexts?
                    .Where(context => context?.DefinitionPlan != null)
                    .Sum(context => context.DefinitionPlan.ProviderGameKeys.Count) ?? 0;
                var dueDefinitions = contexts?
                    .Where(context => context?.DefinitionPlan != null)
                    .Sum(context => context.DefinitionPlan.DueProviderGameKeys.Count) ?? 0;
                var probes = contexts?
                    .Where(context => context?.DefinitionPlan != null)
                    .Sum(context => context.DefinitionPlan.ProbeItemCount) ?? 0;
                Log(
                    "friend.definitionPlan",
                    $"ms={Elapsed(timer)} providerKeys={providerKeys} dueDefinitions={dueDefinitions} probes={probes} totalChecks={totalChecks}");
            }

            public void LogCandidateLoad(
                Stopwatch timer,
                IReadOnlyList<FriendProviderRefreshContext> contexts,
                int workItems)
            {
                var raw = contexts?.Sum(context => context?.RawCandidatesLoaded ?? 0) ?? 0;
                var queued = contexts?.Sum(context => context?.CandidatesQueued ?? 0) ?? 0;
                var probed = contexts?.Sum(context => context?.CandidatesSkippedAlreadyProbed ?? 0) ?? 0;
                var recency = contexts?.Sum(context => context?.CandidatesSkippedRecencyFresh ?? 0) ?? 0;
                var ownershipUnavailable = contexts?.Sum(context => context?.CandidatesSkippedOwnershipUnavailable ?? 0) ?? 0;
                Log(
                    "friend.loadCandidates",
                    $"ms={Elapsed(timer)} raw={raw} queued={queued} workItems={workItems} skippedAlreadyProbed={probed} skippedRecencyFresh={recency} skippedOwnershipUnavailable={ownershipUnavailable}");
            }

            public void LogPhase(Stopwatch timer, string phase, string detail)
            {
                Log(phase, $"ms={Elapsed(timer)} {detail}".TrimEnd());
            }

            public void LogTotal(
                RebuildPayload payload,
                IReadOnlyList<FriendProviderRefreshContext> contexts)
            {
                _total.Stop();
                var summary = payload?.FriendSummary ?? new FriendRefreshSummary();
                var scoped = contexts?.Sum(context => context?.ScopedFriends?.Count ?? 0) ?? 0;
                Log(
                    "friend.total",
                    $"ms={_total.ElapsedMilliseconds} providersProcessed={summary.ProvidersProcessed} scopedFriends={scoped} friendsFetched={summary.FriendsFetched} friendsSaved={summary.FriendsSaved} ownershipPages={summary.OwnershipPagesRefreshed} ownershipRows={summary.OwnershipRowsWritten} candidatesLoaded={summary.CandidatesLoaded} candidatesRefreshed={summary.CandidatesRefreshed} achievementsSaved={summary.AchievementsSaved}");
            }

            private void Log(string phase, string detail)
            {
                // Memory fields are appended at the end so the established
                // "[RefreshPerf] kind=... phase=... {detail}" prefix stays grep-stable.
                _logger?.Debug($"[RefreshPerf] kind={_kind} phase={phase} {detail}{MemoryDiagnostics.FormatInlineSuffix(_memBaseline)}");
            }

            private static long Elapsed(Stopwatch timer)
            {
                timer?.Stop();
                return timer?.ElapsedMilliseconds ?? 0;
            }

            private static int Count<T>(IReadOnlyCollection<T> values)
            {
                return values?.Count ?? 0;
            }

            private static string FormatExtra(string extra)
            {
                return string.IsNullOrWhiteSpace(extra) ? string.Empty : " " + extra.Trim();
            }
        }
    }
}
