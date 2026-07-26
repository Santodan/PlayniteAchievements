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
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.ProgressReporting;
using PlayniteAchievements.Services.Friends;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;

namespace PlayniteAchievements.Services.Refresh
{
    /// <summary>
    /// Manages user achievement refreshing and caching operations.
    /// </summary>
    public partial class RefreshRuntime : IDisposable
    {
        public event EventHandler<ProgressReport> RebuildProgress;

        /// <summary>
        /// Gets the list of game IDs that were refreshed in the most recent refresh operation.
        /// </summary>
        public List<Guid> LastRefreshedGameIds { get; private set; } = new List<Guid>();

        /// <summary>
        /// Gets the provider keys that failed authentication in the most recent refresh.
        /// </summary>
        public List<string> GetLastFailedAuthProviderKeys() => new List<string>(_lastFailedAuthProviderKeys);

        /// <summary>
        /// Raised after each individual game is refreshed and cached.
        /// Argument is the game ID that was refreshed.
        /// </summary>
        public event Action<Guid> GameRefreshed;

        public ProgressReport GetLastRebuildProgress() => _progressReportingService.GetLastProgress();
        public string GetLastRebuildStatus() => _progressReportingService.GetLastStatus();

        public bool IsRebuilding
        {
            get { return _refreshStateManager.IsRebuilding; }
        }

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly ICacheManager _cacheService;
        private readonly DiskImageService _diskImageService;
        private readonly AchievementIconService _achievementIconService;
        private readonly ProgressReportingService _progressReportingService;
        private readonly RefreshStateManager _refreshStateManager;
        private readonly TargetSelectionResolver _targetSelectionResolver;
        private readonly RefreshRequestPlanner _refreshRequestPlanner;
        private readonly RefreshProgressReporter _refreshProgressReporter;
        private readonly PlayniteAchievements.Providers.ProviderRegistry _providerRegistry;
        private readonly Action<RebuildPayload> _onRefreshCompleted;
        private int _savedGamesInCurrentRun;

        // A refresh that saves many games or scrapes many friend rows fetches many provider web
        // pages (multi-MB HTML on the LOH), inflating the working set the CLR then holds. After a
        // large run, request a one-time LOH compaction. The gate takes the max of current-user
        // saved games and friend scrape volume so combined runs whose weight is on the friend
        // side still compact, while Single/Recent runs (a handful of games) never trigger a
        // collection.
        private const int LohCompactionSavedGamesThreshold = 25;
        private volatile List<string> _lastFailedAuthProviderKeys = new List<string>();

        // Dependencies that need disposal
        private readonly IReadOnlyList<IDataProvider> _providers;

        public ICacheManager Cache => _cacheService;

        /// <summary>
        /// Gets the provider registry for checking/modifying provider enabled state.
        /// </summary>
        public PlayniteAchievements.Providers.ProviderRegistry ProviderRegistry => _providerRegistry;

        internal virtual async Task<RefreshAuthContext> GetRefreshAuthContextOrShowDialogAsync(
            RefreshRequest request,
            CancellationToken ct = default)
        {
            var authContext = await GetRefreshAuthContextAsync(request, ct).ConfigureAwait(false);
            if (authContext.HasAuthenticatedProviders)
            {
                return authContext;
            }

            if (HasSteamTransientAuthFailure(authContext))
            {
                _logger.Warn("Refresh skipped because Steam web authentication could not be verified; suppressing generic authentication modal.");
                return authContext;
            }

            _logger.Info("Refresh attempted but no platforms are authenticated.");
            PostToUi(() => _api.Dialogs.ShowMessage(
                ResourceProvider.GetString("LOCPlayAch_Error_NoAuthenticatedProviders"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning));
            return authContext;
        }

        internal virtual async Task<IReadOnlyList<IDataProvider>> GetAuthenticatedProvidersOrShowDialogAsync(CancellationToken ct = default)
        {
            var authContext = await GetRefreshAuthContextOrShowDialogAsync(null, ct).ConfigureAwait(false);
            return authContext?.AuthenticatedProviders ?? Array.Empty<IDataProvider>();
        }

        /// <summary>
        /// Gets the list of available data providers.
        /// </summary>
        public IReadOnlyList<IDataProvider> Providers => _providers;

        /// <summary>
        /// Gets the list of available refresh modes with localized display names.
        /// </summary>
        public IReadOnlyList<RefreshMode> GetRefreshModes()
        {
            return ((RefreshModeType[])Enum.GetValues(typeof(RefreshModeType)))
                .Where(modeType => !modeType.IsFriendRefreshMode())
                .Select(modeType =>
            {
                var mode = new RefreshMode(modeType, modeType.GetResourceKey(), modeType.GetShortResourceKey())
                {
                    DisplayName = ResourceProvider.GetString(modeType.GetResourceKey()) ?? modeType.GetKey(),
                    ShortDisplayName = ResourceProvider.GetString(modeType.GetShortResourceKey()) ?? modeType.GetKey()
                };

                return mode;
            })
            .ToList();
        }

        public event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated
        {
            add => _cacheService.GameCacheUpdated += value;
            remove => _cacheService.GameCacheUpdated -= value;
        }

        public event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated
        {
            add => _cacheService.CacheDeltaUpdated += value;
            remove => _cacheService.CacheDeltaUpdated -= value;
        }

        public event EventHandler<CacheInvalidatedEventArgs> CacheInvalidated
        {
            add => _cacheService.CacheInvalidated += value;
            remove => _cacheService.CacheInvalidated -= value;
        }

        public event EventHandler<FriendCacheInvalidatedEventArgs> FriendCacheInvalidated
        {
            add
            {
                if (_cacheService is IFriendCacheManager friendCache)
                {
                    friendCache.FriendCacheInvalidated += value;
                }
            }
            remove
            {
                if (_cacheService is IFriendCacheManager friendCache)
                {
                    friendCache.FriendCacheInvalidated -= value;
                }
            }
        }

        public RefreshRuntime(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            PlayniteAchievementsPlugin plugin,
            IEnumerable<IDataProvider> providers,
            DiskImageService diskImageService,
            ManagedCustomIconService managedCustomIconService,
            PlayniteAchievements.Providers.ProviderRegistry providerRegistry,
            IEnumerable<string> refreshOrder,
            Action<RebuildPayload> onRefreshCompleted = null)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (providers == null) throw new ArgumentNullException(nameof(providers));
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
            _cacheService = new CacheManager(api, logger, plugin, _diskImageService);
            _achievementIconService = new AchievementIconService(
                _diskImageService,
                managedCustomIconService ?? throw new ArgumentNullException(nameof(managedCustomIconService)),
                settings?.Persisted,
                _logger);
            _progressReportingService = new ProgressReportingService(_logger, PostToUi);
            _refreshStateManager = new RefreshStateManager();
            _targetSelectionResolver = new TargetSelectionResolver(_api, _settings, _cacheService, _logger, refreshOrder);
            _refreshRequestPlanner = new RefreshRequestPlanner(
                _api,
                _settings,
                _logger,
                _targetSelectionResolver);
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
            _refreshProgressReporter = new RefreshProgressReporter((report, prioritizePending) => Report(report, prioritizePending));
            _onRefreshCompleted = onRefreshCompleted;

            _providers = providers.ToList();
        }

        public void Dispose()
        {
            _progressReportingService?.Dispose();

            try
            {
                if (_cacheService is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
            }

            // SettingsPersister removed - user settings are now only saved via ISettings.EndEdit()
        }

        // -----------------------------
        // UI helpers
        // -----------------------------

        private void PostToUi(Action action)
        {
            var dispatcher = _api?.MainView?.UIDispatcher;
            dispatcher.InvokeIfNeeded(action, DispatcherPriority.Background);
        }

        private (Guid? OperationId, RefreshModeType? Mode, Guid? SingleGameId) GetActiveRunContext()
        {
            var context = _refreshStateManager.GetActiveRunContext();
            return (context.OperationId, context.Mode, context.SingleGameId);
        }

        private void Report(
            string message,
            int current = 0,
            int total = 0,
            bool canceled = false,
            Guid? operationId = null,
            RefreshModeType? mode = null,
            Guid? currentGameId = null)
        {
            var report = new ProgressReport
            {
                Message = message,
                CurrentStep = current,
                TotalSteps = total,
                IsCanceled = canceled,
                OperationId = operationId,
                Mode = mode,
                CurrentGameId = currentGameId
            };

            Report(report);
        }

        private void Report(ProgressReport report)
        {
            Report(report, prioritizePending: false);
        }

        private void Report(ProgressReport report, bool prioritizePending)
        {
            if (report == null)
            {
                return;
            }

            var context = GetActiveRunContext();
            if (!report.OperationId.HasValue)
            {
                report.OperationId = context.OperationId;
            }

            if (!report.Mode.HasValue)
            {
                report.Mode = context.Mode;
            }

            if (!report.CurrentGameId.HasValue && report.Mode == RefreshModeType.Single)
            {
                report.CurrentGameId = context.SingleGameId;
            }

            _progressReportingService.Report(this, RebuildProgress, report, prioritizePending);
        }

        /// <summary>
        /// Calculates percentage for a refresh progress report.
        /// </summary>
        public double CalculateProgressPercent(ProgressReport report)
        {
            return _progressReportingService.CalculateProgressPercent(report);
        }

        /// <summary>
        /// Determines if the provided report represents a final refresh state.
        /// </summary>
        public bool IsFinalProgressReport(ProgressReport report)
        {
            return _progressReportingService.IsFinalProgressReport(report);
        }

        /// <summary>
        /// Resolves the user-facing refresh status message from report + manager state.
        /// </summary>
        public string ResolveProgressMessage(ProgressReport report = null)
        {
            return _progressReportingService.ResolveProgressMessage(report);
        }

        /// <summary>
        /// Gets a centralized refresh status snapshot for UI consumers.
        /// </summary>
        public RefreshStatusSnapshot GetRefreshStatusSnapshot(ProgressReport report = null)
        {
            return _progressReportingService.GetRefreshStatusSnapshot(IsRebuilding, report);
        }

        /// <summary>
        /// Gets a transient "starting refresh" snapshot for immediate UI updates.
        /// </summary>
        public RefreshStatusSnapshot GetStartingRefreshStatusSnapshot()
        {
            return _progressReportingService.GetStartingRefreshStatusSnapshot(true);
        }

        // -----------------------------
        // Managed refresh runner
        // -----------------------------

        private bool TryBeginRun(
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId,
            CancellationToken externalToken,
            out CancellationTokenSource cts)
        {
            if (!_refreshStateManager.TryBeginRun(
                operationId,
                mode,
                singleGameId,
                externalToken,
                out cts,
                out var activeContext))
            {
                _logger.Info("Refresh already in progress.");
                Report(
                    _progressReportingService.GetLastStatus() ?? ResourceProvider.GetString("LOCPlayAch_Status_UpdatingCache"),
                    0,
                    1,
                    operationId: activeContext.OperationId,
                    mode: activeContext.Mode,
                    currentGameId: activeContext.SingleGameId);
                return false;
            }

            _logger.Info("Starting new refresh.");
            return true;
        }

        private void EndRun()
        {
            _logger.Info("Refresh ended.");
            _refreshStateManager.EndRun();
        }

        private static List<IDataProvider> MaterializeProviderScope(IEnumerable<IDataProvider> providers)
        {
            return providers?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();
        }

        private static List<IRefreshAuthContextReceiver> BeginScopedRefreshAuthContext(
            RefreshAuthContext authContext,
            IEnumerable<IDataProvider> providers)
        {
            var receivers = new List<IRefreshAuthContextReceiver>();
            if (authContext == null)
            {
                return receivers;
            }

            foreach (var receiver in (providers ?? Enumerable.Empty<IDataProvider>())
                .OfType<IRefreshAuthContextReceiver>())
            {
                receiver.BeginRefreshAuthContext(authContext);
                receivers.Add(receiver);
            }

            return receivers;
        }

        private static void EndScopedRefreshAuthContext(
            RefreshAuthContext authContext,
            IReadOnlyList<IRefreshAuthContextReceiver> receivers)
        {
            if (authContext == null || receivers == null)
            {
                return;
            }

            for (var i = receivers.Count - 1; i >= 0; i--)
            {
                try
                {
                    receivers[i]?.EndRefreshAuthContext(authContext);
                }
                catch
                {
                }
            }
        }

        private async Task RunManagedAsync(
            RefreshModeType mode,
            Guid? singleGameId,
            CancellationToken externalToken,
            Func<Guid, CancellationToken, Task<RebuildPayload>> runner,
            Func<RebuildPayload, string> finalMessage,
            string errorLogMessage,
            IReadOnlyList<IDataProvider> providerScope = null,
            RefreshAuthContext authContext = null)
        {
            var operationId = Guid.NewGuid();

            var effectiveAuthContext = authContext;
            if (effectiveAuthContext == null && providerScope == null)
            {
                effectiveAuthContext = await GetRefreshAuthContextAsync(externalToken).ConfigureAwait(false);
            }

            var effectiveProviderScope = MaterializeProviderScope(
                providerScope ?? effectiveAuthContext?.AuthenticatedProviders);

            if (effectiveProviderScope.Count == 0)
            {
                _logger.Info("Refresh requested but no platforms are authenticated.");
                Report(
                    ResourceProvider.GetString("LOCPlayAch_Error_NoAuthenticatedProviders"),
                    0,
                    1,
                    operationId: operationId,
                    mode: mode,
                    currentGameId: singleGameId);
                return;
            }

            if (!TryBeginRun(operationId, mode, singleGameId, externalToken, out var cts))
                return;

            _refreshProgressReporter.Reset();
            Interlocked.Exchange(ref _savedGamesInCurrentRun, 0);
            List<IRefreshAuthContextReceiver> authContextReceivers = null;

            var memBaseline = MemoryDiagnostics.Log(_logger, "refresh.start", $"mode={mode} operation={operationId}");
            var memSampler = MemoryDiagnostics.StartSampler(_logger, $"mode={mode}", TimeSpan.FromSeconds(60));

            // Report immediately so UI updates buttons before any async work
            var startMsg = ResourceProvider.GetString("LOCPlayAch_Status_Starting");
            Report(
                startMsg,
                0,
                1,
                operationId: operationId,
                mode: mode,
                currentGameId: singleGameId);

            RebuildPayload payload = null;
            try
            {
                authContextReceivers = BeginScopedRefreshAuthContext(effectiveAuthContext, effectiveProviderScope);

                // Run refresh setup/execution on background thread so UI commands are never blocked
                // by synchronous pre-refresh work (game filtering, capability checks, etc.).
                payload = await Task.Run(
                    async () => await runner(operationId, cts.Token).ConfigureAwait(false),
                    cts.Token).ConfigureAwait(false);

                // Store refreshed game IDs for subscribers (e.g., tag syncing)
                if (payload?.Summary?.RefreshedGameIds != null)
                {
                    LastRefreshedGameIds = new List<Guid>(payload.Summary.RefreshedGameIds);
                }
                else
                {
                    LastRefreshedGameIds = new List<Guid>();
                }

                // Store failed provider keys for notification consumers.
                _lastFailedAuthProviderKeys = payload?.FailedProviderKeys?.Count > 0
                    ? new List<string>(payload.FailedProviderKeys)
                    : new List<string>();
            }
            catch (OperationCanceledException ex) when (!cts.IsCancellationRequested)
            {
                // A cancellation-shaped exception (e.g. an HttpClient timeout's
                // TaskCanceledException) without the run token being cancelled is a failure,
                // not a user cancel; log it with its stack so the timeout site is identifiable.
                _logger.Error(ex, $"{errorLogMessage} (operation canceled without the run token being cancelled)");
                Report(
                    ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"),
                    0,
                    1,
                    operationId: operationId,
                    mode: mode,
                    currentGameId: singleGameId);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("User achievement refresh was canceled.");
                Report(
                    ResourceProvider.GetString("LOCPlayAch_Status_Canceled"),
                    0,
                    1,
                    canceled: true,
                    operationId: operationId,
                    mode: mode,
                    currentGameId: singleGameId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, errorLogMessage);
                Report(
                    ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"),
                    0,
                    1,
                    operationId: operationId,
                    mode: mode,
                    currentGameId: singleGameId);
            }
            finally
            {
                memSampler?.Dispose();
                EndScopedRefreshAuthContext(effectiveAuthContext, authContextReceivers);
                var savedGamesCount = Interlocked.Exchange(ref _savedGamesInCurrentRun, 0);
                var hasSavedGames = savedGamesCount > 0;
                var wasCanceled = cts.IsCancellationRequested;
                var finalTotalSteps = _refreshProgressReporter.CompletionTotalSteps;
                EndRun();

                // Send final completion report AFTER EndRun so IsRebuilding is false when UI processes it
                if (!wasCanceled && payload != null)
                {
                    var msg = ResolveFinalSuccessMessage(payload, finalMessage);
                    Report(
                        msg,
                        finalTotalSteps,
                        finalTotalSteps,
                        operationId: operationId,
                        mode: mode,
                        currentGameId: singleGameId);
                }

                if (hasSavedGames)
                {
                    // Scoped when the run knows which games it refreshed (a poller tick names
                    // exactly one); null or an over-large list degrades to a full invalidation.
                    _cacheService.NotifyCacheInvalidated(payload?.Summary?.RefreshedGameIds);
                }

                MemoryDiagnostics.Log(
                    _logger,
                    "refresh.end",
                    memBaseline,
                    $"mode={mode} savedGames={savedGamesCount} canceled={wasCanceled}");

                // Runs on a threadpool continuation (the run body is awaited with
                // ConfigureAwait(false)), so the blocking collection stays off the UI thread.
                // payload is null on cancel/exception paths, so friend volume is 0 there and
                // only current-user saves can gate the compaction (matching prior behavior).
                var compactionWorkVolume = Math.Max(
                    savedGamesCount, FriendRefreshCoordinator.GetFriendScrapeVolume(payload));
                MemoryMaintenance.CompactLargeObjectHeapAfterLargeScan(
                    compactionWorkVolume,
                    LohCompactionSavedGamesThreshold,
                    _logger,
                    context: $"refresh.end mode={mode}");
                ScheduleFollowUpCompaction(compactionWorkVolume, mode);

                // Notify refresh completion subscribers (e.g., auth failure notifications).
                if (!wasCanceled && payload != null)
                {
                    try { _onRefreshCompleted?.Invoke(payload); } catch (Exception ex) { _logger?.Debug(ex, "Refresh completion callback failed."); }
                }

                _refreshProgressReporter.Reset();
            }
        }

        // The completion compaction returns the scrape peak, but the post-refresh rebuild wave
        // (final friends snapshot build, overview and theme refreshes) keeps allocating after
        // refresh end, and that garbage otherwise lingers in the working set until an unrelated
        // gen-2 collection. Measured on a large scan, the wave settles within ~20s, so the first
        // follow-up sweeps then; a later one catches stragglers (demand-driven theme rebuilds).
        // Same work-volume gate; a sweep is skipped (and the chain stopped) when a newer refresh
        // is already running, since its own completion compaction covers it.
        private static readonly TimeSpan[] FollowUpCompactionDelays =
        {
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(90)
        };

        private void ScheduleFollowUpCompaction(int workVolume, RefreshModeType mode)
        {
            if (workVolume < LohCompactionSavedGamesThreshold)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var elapsed = TimeSpan.Zero;
                    foreach (var delay in FollowUpCompactionDelays)
                    {
                        await Task.Delay(delay - elapsed).ConfigureAwait(false);
                        elapsed = delay;
                        if (IsRebuilding)
                        {
                            return;
                        }

                        MemoryMaintenance.CompactLargeObjectHeapAfterLargeScan(
                            workVolume,
                            LohCompactionSavedGamesThreshold,
                            _logger,
                            context: $"refresh.followUp mode={mode} atSeconds={(int)delay.TotalSeconds}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Follow-up LOH compaction failed.");
                }
            });
        }

        private sealed class RefreshGameTarget
        {
            public Game Game { get; set; }
            public IDataProvider Provider { get; set; }
        }

        private sealed class CurrentRefreshPlanBuildResult
        {
            public IReadOnlyList<IDataProvider> Providers { get; set; } = Array.Empty<IDataProvider>();
            public List<RefreshGameTarget> Targets { get; set; } = new List<RefreshGameTarget>();
            public List<ProviderRefreshExecutor.ProviderExecutionPlan> ProviderPlans { get; set; } =
                new List<ProviderRefreshExecutor.ProviderExecutionPlan>();
        }

        internal Task<RefreshAuthContext> GetRefreshAuthContextAsync(CancellationToken ct = default)
        {
            return GetRefreshAuthContextAsync(null, ct);
        }

        internal virtual async Task<RefreshAuthContext> GetRefreshAuthContextAsync(
            RefreshRequest request,
            CancellationToken ct = default)
        {
            var context = new RefreshAuthContext(Guid.NewGuid());
            var enabledProviders = GetEnabledProviders();
            if (enabledProviders.Count == 0)
            {
                return context;
            }

            var targetSelectionCache = new TargetSelectionCache();
            context.TargetSelectionCache = targetSelectionCache;

            IReadOnlyList<IDataProvider> probeCandidates = enabledProviders;
            if (request != null && _refreshRequestPlanner != null)
            {
                var filterTimer = Stopwatch.StartNew();
                probeCandidates = _refreshRequestPlanner.ResolveAuthProbeCandidates(
                    request,
                    enabledProviders,
                    targetSelectionCache);
                filterTimer.Stop();
                _logger?.Debug(
                    $"[RefreshPerf] phase=auth.preflight.filter enabled={enabledProviders.Count} candidates={probeCandidates.Count} ms={filterTimer.ElapsedMilliseconds}");
            }

            await ProbeProvidersForAuthContextAsync(probeCandidates, context, ct).ConfigureAwait(false);

            if (probeCandidates.Count < enabledProviders.Count &&
                !enabledProviders.Any(provider => context.IsProviderAuthenticated(provider.ProviderKey)))
            {
                // Second chance: when no capability-filtered provider authenticates, probe the
                // remaining enabled providers so failure dialogs and dead-end messages match an
                // unfiltered preflight.
                var probedKeys = new HashSet<string>(
                    probeCandidates.Select(provider => provider.ProviderKey),
                    StringComparer.OrdinalIgnoreCase);
                var remaining = enabledProviders
                    .Where(provider => !probedKeys.Contains(provider.ProviderKey))
                    .ToList();
                _logger?.Debug(
                    $"[RefreshPerf] phase=auth.preflight.secondchance remaining={remaining.Count}");
                await ProbeProvidersForAuthContextAsync(remaining, context, ct).ConfigureAwait(false);
            }

            context.SetAuthenticatedProviders(
                enabledProviders.Where(provider => context.IsProviderAuthenticated(provider.ProviderKey)));
            return context;
        }

        private List<IDataProvider> GetEnabledProviders()
        {
            return _providers
                .Where(provider => provider != null &&
                                   (_providerRegistry == null ||
                                    _providerRegistry.IsProviderEnabled(provider.ProviderKey)))
                .ToList();
        }

        private async Task ProbeProvidersForAuthContextAsync(
            IReadOnlyList<IDataProvider> providers,
            RefreshAuthContext context,
            CancellationToken ct)
        {
            if (providers == null || providers.Count == 0)
            {
                return;
            }

            var maxParallelism = Math.Max(1, Math.Min(8, providers.Count));
            using (var gate = new SemaphoreSlim(maxParallelism, maxParallelism))
            {
                var tasks = providers
                    .Select(provider => ProbeProviderForAuthContextAsync(provider, context, gate, ct))
                    .ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        public async Task<IReadOnlyList<IDataProvider>> GetAuthenticatedProvidersAsync(CancellationToken ct = default)
        {
            var context = await GetRefreshAuthContextAsync(ct).ConfigureAwait(false);
            return context.AuthenticatedProviders;
        }

        private async Task ProbeProviderForAuthContextAsync(
            IDataProvider provider,
            RefreshAuthContext context,
            SemaphoreSlim gate,
            CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            AuthProbeResult result = null;
            object artifact = null;

            try
            {
                result = await ProbeProviderAuthStateAsync(provider, ct).ConfigureAwait(false);
                if (result?.IsSuccess == true &&
                    provider?.AuthSession is IRefreshAuthArtifactSource artifactSource)
                {
                    artifact = artifactSource.GetRefreshAuthArtifact(result);
                }
            }
            finally
            {
                timer.Stop();
                context.SetProbeResult(provider?.ProviderKey, result, timer.ElapsedMilliseconds, artifact);
                _logger?.Debug(
                    $"[RefreshPerf] phase=auth.preflight provider={provider?.ProviderKey ?? "unknown"} ms={timer.ElapsedMilliseconds} outcome={result?.Outcome.ToString() ?? "null"} success={result?.IsSuccess == true}");
                gate.Release();
            }
        }

        private List<RefreshGameTarget> GetRefreshTargets(
            CacheRefreshOptions options,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache = null)
        {
            return _targetSelectionResolver.GetRefreshTargets(options, providers, targetSelectionCache)
                .Select(target => new RefreshGameTarget { Game = target.Game, Provider = target.Provider })
                .ToList();
        }

        private CurrentRefreshPlanBuildResult BuildCurrentRefreshPlans(
            CacheRefreshOptions options,
            IReadOnlyList<IDataProvider> providers,
            TargetSelectionCache targetSelectionCache = null)
        {
            options ??= new CacheRefreshOptions();
            var scopedProviders = MaterializeProviderScope(providers);
            var result = new CurrentRefreshPlanBuildResult
            {
                Providers = scopedProviders
            };

            if (scopedProviders.Count == 0)
            {
                return result;
            }

            var refreshTargets = GetRefreshTargets(options, scopedProviders, targetSelectionCache);
            var orderedProviders = _targetSelectionResolver.OrderProvidersForRefresh(scopedProviders);
            var providerOrder = orderedProviders
                .Select((provider, index) => new { provider, index })
                .ToDictionary(x => x.provider, x => x.index);

            result.Targets = refreshTargets;
            result.ProviderPlans = refreshTargets
                .GroupBy(x => x.Provider)
                .OrderBy(group => providerOrder.TryGetValue(group.Key, out var index) ? index : int.MaxValue)
                .Select(group => new ProviderRefreshExecutor.ProviderExecutionPlan
                {
                    Provider = group.Key,
                    Games = group.Select(x => x.Game).ToList()
                })
                .ToList();

            _logger.Debug(string.Format(
                "Games to refresh: {0}, Platforms: {1}, Grouped platforms: {2}",
                refreshTargets.Count,
                _providers.Count,
                result.ProviderPlans.Count));

            return result;
        }

        private async Task<RebuildPayload> RefreshAsync(
            CacheRefreshOptions options,
            CancellationToken cancel,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId = null,
            bool forceIconRefresh = false,
            IReadOnlyList<IDataProvider> providerScope = null,
            bool? runProvidersInParallelOverride = null,
            TargetSelectionCache targetSelectionCache = null)
        {
            var providers = providerScope == null
                ? MaterializeProviderScope(await GetAuthenticatedProvidersAsync(cancel).ConfigureAwait(false))
                : MaterializeProviderScope(providerScope);
            if (providers.Count == 0)
            {
                _logger?.Warn("No authenticated platforms available for refresh.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var planBuild = BuildCurrentRefreshPlans(options, providers, targetSelectionCache);
            if (planBuild.ProviderPlans.Count == 0)
            {
                _logger?.Warn("No matching platforms available for refresh options.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ExecuteCurrentProviderPlansAsync(
                planBuild.ProviderPlans,
                planBuild.Targets.Count,
                cancel,
                operationId,
                mode,
                singleGameId,
                forceIconRefresh,
                runProvidersInParallelOverride,
                initializeProgress: true).ConfigureAwait(false);
        }

        private async Task<RebuildPayload> ExecuteCurrentProviderPlansAsync(
            IReadOnlyList<ProviderRefreshExecutor.ProviderExecutionPlan> providerPlans,
            int totalGames,
            CancellationToken cancel,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId = null,
            bool forceIconRefresh = false,
            bool? runProvidersInParallelOverride = null,
            bool initializeProgress = true)
        {
            var plans = providerPlans?
                .Where(plan => plan?.Provider != null && plan.Games?.Count > 0)
                .ToList() ?? new List<ProviderRefreshExecutor.ProviderExecutionPlan>();
            if (plans.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            if (initializeProgress)
            {
                // Run the owned-game refresh in weighted units [0, TotalUnits] so per-game icon downloads
                // can advance the bar fractionally within a game. The plain game-count scale is too coarse
                // (a single-game refresh would sit at 0 until completion, then jump to 100%).
                _refreshProgressReporter.ConfigureWeightedProgress(
                    FriendRefreshProgressSession.TotalUnits, 0, FriendRefreshProgressSession.TotalUnits);
                // One id per (game, provider) pass; the reporter counts distinct games so multi-provider
                // games do not inflate the "n/total" denominator.
                _refreshProgressReporter.Initialize(
                    plans.SelectMany(plan => plan.Games).Select(game => game?.Id ?? Guid.Empty));
            }

            var progressScope = new RefreshProgressScope(operationId, mode, singleGameId);

            var runProvidersInParallel = runProvidersInParallelOverride ?? (_settings?.Persisted?.EnableParallelProviderRefresh ?? true);
            var timer = Stopwatch.StartNew();
            _logger?.Debug(
                $"[RefreshPerf] phase=current.start mode={mode} providers={plans.Count} games={totalGames} parallel={runProvidersInParallel}");
            var providerResults = await ProviderRefreshExecutor.ExecuteProvidersAsync(
                plans,
                runProvidersInParallel,
                plan => ExecuteCurrentProviderPlanAsync(plan, progressScope, forceIconRefresh, cancel),
                cancel).ConfigureAwait(false);

            var payload = CreateCurrentRefreshPayload(providerResults);
            timer.Stop();
            _logger?.Debug(
                $"[RefreshPerf] phase=current.total mode={mode} ms={timer.ElapsedMilliseconds} providers={plans.Count} games={totalGames} refreshed={payload.Summary.GamesRefreshed} withAchievements={payload.Summary.GamesWithAchievements} withoutAchievements={payload.Summary.GamesWithoutAchievements}");
            return payload;
        }

        private async Task<RebuildPayload> ExecuteCurrentProviderPlanAsync(
            ProviderRefreshExecutor.ProviderExecutionPlan plan,
            RefreshProgressScope progressScope,
            bool forceIconRefresh,
            CancellationToken cancel)
        {
            if (plan?.Provider == null || plan.Games == null || plan.Games.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var timer = Stopwatch.StartNew();
            try
            {
                return await plan.Provider.RefreshAsync(
                    plan.Games,
                    game => _refreshProgressReporter.ReportGameStarting(game, progressScope),
                    (game, data) => _refreshProgressReporter.OnProviderGameCompletedAsync(
                        plan.Provider,
                        game,
                        data,
                        progressScope,
                        cancel,
                        (provider, refreshedGame, refreshedData, scope, token) =>
                            OnGameRefreshed(provider, refreshedGame, refreshedData, scope, forceIconRefresh, token)),
                    cancel).ConfigureAwait(false);
            }
            finally
            {
                timer.Stop();
                _logger?.Debug(
                    $"[RefreshPerf] phase=current.provider provider={plan.Provider.ProviderKey} ms={timer.ElapsedMilliseconds} games={plan.Games.Count}");
            }
        }

        private RebuildPayload CreateCurrentRefreshPayload(
            IReadOnlyList<ProviderRefreshExecutor.ProviderExecutionResult> providerResults)
        {
            var mergedSummary = new RebuildSummary();
            var authRequired = false;
            var failedProviderKeys = new List<string>();
            var faultedProviderKeys = new List<string>();

            foreach (var result in providerResults)
            {
                if (result?.Payload == null)
                {
                    continue;
                }

                RecordProviderFault(result, faultedProviderKeys);

                if (result.Payload.AuthRequired)
                {
                    authRequired = true;
                    var key = result.Provider?.ProviderKey;
                    if (!string.IsNullOrWhiteSpace(key) &&
                        !failedProviderKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        failedProviderKeys.Add(key);
                    }
                }

                if (result.Payload.Summary == null)
                {
                    continue;
                }

                mergedSummary.GamesWithAchievements += result.Payload.Summary.GamesWithAchievements;
                mergedSummary.GamesWithoutAchievements += result.Payload.Summary.GamesWithoutAchievements;

                if (result.Payload.Summary.RefreshedGameIds != null)
                {
                    mergedSummary.RefreshedGameIds.AddRange(result.Payload.Summary.RefreshedGameIds);
                }
            }

            // Providers count their own passes, so summing per-provider GamesRefreshed counts a game
            // once per servicing provider. The user-facing count is distinct games refreshed.
            mergedSummary.GamesRefreshed = mergedSummary.RefreshedGameIds.Distinct().Count();

            return new RebuildPayload
            {
                Summary = mergedSummary,
                AuthRequired = authRequired,
                FailedProviderKeys = failedProviderKeys,
                FaultedProviderKeys = faultedProviderKeys
            };
        }

        /// <summary>
        /// Logs a provider execution fault and records its provider key so the completion
        /// message can name the provider whose games did not refresh.
        /// </summary>
        private void RecordProviderFault(
            ProviderRefreshExecutor.ProviderExecutionResult result,
            List<string> faultedProviderKeys)
        {
            if (result?.Fault == null)
            {
                return;
            }

            var key = result.Provider?.ProviderKey;
            _logger?.Error(
                result.Fault,
                $"Provider '{key ?? "unknown"}' refresh faulted; remaining providers continued.");

            if (!string.IsNullOrWhiteSpace(key) &&
                !faultedProviderKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                faultedProviderKeys.Add(key);
            }
        }

        /// <summary>
        /// Refreshes only the friend roster (friend list + avatars) for authenticated friend-capable
        /// providers, without ownership/definition/achievement work. Backs the in-settings
        /// "refresh friends list" action. Returns the number of active friends saved.
        /// </summary>
        public async Task<int> RefreshFriendRosterAsync(CancellationToken cancel = default)
        {
            var autoProviderKeys = _settings?.Persisted?.AutoDiscoverFriendProviderKeys;
            return await RefreshFriendRosterAsync(autoProviderKeys, cancel).ConfigureAwait(false);
        }

        public async Task<int> RefreshFriendRosterAsync(
            IReadOnlyCollection<string> providerKeys,
            CancellationToken cancel = default)
        {
            if ((_cacheService as IFriendCacheManager) == null)
            {
                return 0;
            }

            var providerKeySet = providerKeys == null
                ? null
                : new HashSet<string>(
                    providerKeys
                        .Where(key => !string.IsNullOrWhiteSpace(key))
                        .Select(key => key.Trim()),
                    StringComparer.OrdinalIgnoreCase);

            var friendProviders = GetEnabledProviders()
                .Where(provider => provider.Friends != null &&
                                   (providerKeySet == null || providerKeySet.Count == 0 ||
                                    providerKeySet.Contains(provider.ProviderKey)))
                .ToList();
            var authContext = new RefreshAuthContext(Guid.NewGuid());
            await ProbeProvidersForAuthContextAsync(friendProviders, authContext, cancel).ConfigureAwait(false);
            authContext.SetAuthenticatedProviders(
                friendProviders.Where(provider => authContext.IsProviderAuthenticated(provider.ProviderKey)));
            var providers = MaterializeProviderScope(authContext.AuthenticatedProviders);

            if (providers.Count == 0)
            {
                _logger?.Warn("No authenticated friend-capable platforms available for friend roster refresh.");
                return 0;
            }

            var receivers = BeginScopedRefreshAuthContext(authContext, providers);
            try
            {
                return await RefreshFriendRosterAsync(providers, cancel)
                    .ConfigureAwait(false);
            }
            finally
            {
                EndScopedRefreshAuthContext(authContext, receivers);
            }
        }

        private async Task<RebuildPayload> RefreshFriendsAsync(
            FriendRefreshOptions options,
            CancellationToken cancel,
            Guid operationId,
            RefreshModeType mode,
            IReadOnlyList<IDataProvider> providerScope = null)
        {
            var providers = providerScope == null
                ? MaterializeProviderScope(await GetAuthenticatedProvidersAsync(cancel).ConfigureAwait(false))
                : MaterializeProviderScope(providerScope);

            providers = providers
                .Where(provider => provider?.Friends != null)
                .ToList();

            if (providers.Count == 0 || (_cacheService as IFriendCacheManager) == null)
            {
                _logger?.Warn("No authenticated friend-capable platforms available for friends refresh.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            void ReportFriendProgress(string message, int current, int total)
            {
                _refreshProgressReporter.ReportFriendProgress(
                    message,
                    Math.Max(0, Math.Min(current, Math.Max(1, total) - 1)),
                    Math.Max(1, total),
                    new RefreshProgressScope(operationId, mode, null));
            }

            return await RefreshAsync(providers, options, ReportFriendProgress, cancel)
                .ConfigureAwait(false);
        }

        private async Task<RebuildPayload> ExecuteRefreshPlanAsync(
            RefreshRequestPlanner.ResolvedRequest resolved,
            CancellationToken cancel,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId,
            TargetSelectionCache targetSelectionCache = null)
        {
            var payload = new RebuildPayload();
            if (resolved == null)
            {
                return payload;
            }

            var currentOptions = resolved.CurrentUserOptions;
            var friendOptions = resolved.FriendOptions;
            var forceIconRefresh = resolved.Options?.ForceIconRefresh == true;

            if (currentOptions == null)
            {
                return await RefreshFriendsAsync(
                    friendOptions,
                    cancel,
                    operationId,
                    mode,
                    resolved.FriendProviderScope).ConfigureAwait(false);
            }

            if (friendOptions == null)
            {
                return await RefreshAsync(
                    currentOptions,
                    cancel,
                    operationId,
                    mode,
                    singleGameId,
                    forceIconRefresh,
                    resolved.CurrentProviderScope,
                    resolved.RunProvidersInParallelOverride,
                    targetSelectionCache).ConfigureAwait(false);
            }

            var currentProviders = MaterializeProviderScope(resolved.CurrentProviderScope);
            var friendProviders = MaterializeProviderScope(resolved.FriendProviderScope)
                .Where(provider => provider?.Friends != null)
                .ToList();

            var currentPlanBuild = BuildCurrentRefreshPlans(currentOptions, currentProviders, targetSelectionCache);
            var currentPlansByProvider = currentPlanBuild.ProviderPlans
                .Where(plan => plan?.Provider != null && plan.Games?.Count > 0)
                .ToDictionary(plan => plan.Provider);

            var hasCurrentWork = currentPlansByProvider.Count > 0;
            var hasFriendWork = friendProviders.Count > 0 && (_cacheService as IFriendCacheManager) != null;
            if (!hasCurrentWork && !hasFriendWork)
            {
                _logger?.Warn("No matching platforms available for refresh options.");
                return payload;
            }

            if (friendOptions != null && friendProviders.Count == 0)
            {
                _logger?.Warn("No authenticated friend-capable platforms available for friends refresh.");
            }

            if (!hasFriendWork)
            {
                return await RefreshAsync(
                    currentOptions,
                    cancel,
                    operationId,
                    mode,
                    singleGameId,
                    forceIconRefresh,
                    resolved.CurrentProviderScope,
                    resolved.RunProvidersInParallelOverride,
                    targetSelectionCache).ConfigureAwait(false);
            }

            if (!hasCurrentWork)
            {
                return await RefreshFriendsAsync(
                    friendOptions,
                    cancel,
                    operationId,
                    mode,
                    resolved.FriendProviderScope).ConfigureAwait(false);
            }

            var friendProviderSet = new HashSet<IDataProvider>(friendProviders);
            var providerOrder = ResolveCombinedProviderOrder(currentPlansByProvider.Keys.Concat(friendProviders));
            if (providerOrder.Count == 0)
            {
                return payload;
            }

            const int totalUnits = FriendRefreshProgressSession.TotalUnits;
            const int maxReportUnits = totalUnits - 1;
            var preparationUnits = hasFriendWork
                ? Math.Min(3500, Math.Max(1500, maxReportUnits / 3))
                : maxReportUnits;

            void ReportFriendProgress(string message, int current, int total)
            {
                _refreshProgressReporter.ReportFriendProgress(
                    message,
                    Math.Max(0, Math.Min(current, Math.Max(1, total) - 1)),
                    Math.Max(1, total),
                    new RefreshProgressScope(operationId, mode, singleGameId));
            }

            var progressScope = new RefreshProgressScope(operationId, mode, singleGameId);
            var friendProgress = new FriendRefreshProgressSession(ReportFriendProgress);
            var orderedFriendProviders = providerOrder
                .Where(provider => friendProviderSet.Contains(provider))
                .ToList();
            var friendProviderIndexByProvider = orderedFriendProviders
                .Select((provider, index) => new { provider, index })
                .ToDictionary(item => item.provider, item => item.index);
            friendProgress.InitializeProviderTotal(orderedFriendProviders.Count);

            var providerWorkPlans = providerOrder
                .Where(provider => currentPlansByProvider.ContainsKey(provider) || friendProviderSet.Contains(provider))
                .Select(provider => new ProviderRefreshExecutor.ProviderExecutionPlan
                {
                    Provider = provider,
                    Games = currentPlansByProvider.TryGetValue(provider, out var currentPlan)
                        ? currentPlan.Games
                        : Array.Empty<Game>()
                })
                .ToList();

            var runProvidersInParallel = resolved.RunProvidersInParallelOverride ??
                                         (_settings?.Persisted?.EnableParallelProviderRefresh ?? true);
            var contexts = new List<FriendRefreshCoordinator.FriendProviderRefreshContext>();
            var contextsLock = new object();
            var payloadLock = new object();
            var friendPerf = new FriendRefreshCoordinator.FriendRefreshPerfSession(_logger, friendOptions, friendProviders.Count, "combined");
            var friendPrepareTimer = Stopwatch.StartNew();

            using (var friendInvalidationBatch = hasFriendWork ? _friendCache?.BeginFriendCacheInvalidationBatch() : null)
            {
                try
                {
                    // Combined preparation runs current-game refreshes and friend-roster loads concurrently.
                    // Drive one shared aggregate over [0, preparationUnits] so the two workstreams cannot
                    // collide or freeze each other. Initialize still tracks the current-game targets (one
                    // id per provider pass, counted as distinct games) so the per-game status text
                    // ("Refreshing X (n/total)") stays correct.
                    _refreshProgressReporter.Initialize(
                        currentPlanBuild.Targets.Select(target => target.Game?.Id ?? Guid.Empty));
                    _refreshProgressReporter.InitializePreparation(
                        currentPlanBuild.Targets.Count + orderedFriendProviders.Count,
                        preparationUnits,
                        totalUnits);
                    var providerResults = await ProviderRefreshExecutor.ExecuteProvidersAsync(
                        providerWorkPlans,
                        runProvidersInParallel,
                        async plan =>
                        {
                            cancel.ThrowIfCancellationRequested();
                            var providerPayload = new RebuildPayload();
                            var provider = plan.Provider;

                            if (currentPlansByProvider.TryGetValue(provider, out var currentPlan))
                            {
                                var currentPayload = await ExecuteCurrentProviderPlanAsync(
                                    currentPlan,
                                    progressScope,
                                    forceIconRefresh,
                                    cancel).ConfigureAwait(false);
                                FriendRefreshCoordinator.Merge(providerPayload, currentPayload);
                                FriendRefreshCoordinator.MarkAuthFailure(providerPayload, provider.ProviderKey, currentPayload?.AuthRequired == true);
                            }

                            if (friendProviderIndexByProvider.TryGetValue(provider, out var friendProviderIndex))
                            {
                                // Suppress the friend session's roster emission during phase 1 (pass null
                                // progress); roster loads are reflected through the shared preparation
                                // aggregate instead, so they no longer collide with current-game progress.
                                var context = await FriendRefresh.PrepareProviderRefreshAsync(
                                    provider.Friends,
                                    friendOptions,
                                    providerPayload,
                                    progress: null,
                                    friendProviderIndex,
                                    orderedFriendProviders.Count,
                                    cancel).ConfigureAwait(false);

                                var rosterFormat = ResourceProvider.GetString("LOCPlayAch_FriendsRefresh_Progress_LoadingFriends");
                                if (string.IsNullOrWhiteSpace(rosterFormat))
                                {
                                    rosterFormat = "Loading friends {1}/{2}: {0}";
                                }

                                _refreshProgressReporter.ReportPreparationUnitCompleted(
                                    string.Format(
                                        rosterFormat,
                                        string.IsNullOrWhiteSpace(provider.ProviderKey) ? "provider" : provider.ProviderKey.Trim(),
                                        friendProviderIndex + 1,
                                        orderedFriendProviders.Count),
                                    progressScope);

                                if (context != null)
                                {
                                    lock (contextsLock)
                                    {
                                        contexts.Add(context);
                                    }
                                }
                            }

                            return providerPayload;
                        },
                        cancel).ConfigureAwait(false);

                    foreach (var result in providerResults)
                    {
                        FriendRefreshCoordinator.Merge(payload, result?.Payload);
                        RecordProviderFault(result, payload.FaultedProviderKeys);
                    }

                    // Merge dedupes RefreshedGameIds but sums per-provider pass counts; report the
                    // user-facing count as distinct games refreshed.
                    if (payload.Summary != null)
                    {
                        payload.Summary.GamesRefreshed = payload.Summary.RefreshedGameIds?.Distinct().Count()
                            ?? payload.Summary.GamesRefreshed;
                    }

                    if (hasFriendWork)
                    {
                        friendPerf.LogPrepare(friendPrepareTimer, contexts, "combinedProviderWork=true");
                        friendInvalidationBatch?.Flush();
                        _refreshProgressReporter.ConfigureWeightedProgress(totalUnits, preparationUnits, maxReportUnits);
                        await FriendRefresh.RefreshPreparedFriendContextsAsync(
                            contexts,
                            friendOptions,
                            payload,
                            payloadLock,
                            friendProgress,
                            friendPerf,
                            friendInvalidationBatch,
                            cancel).ConfigureAwait(false);
                        friendPerf.LogTotal(payload, contexts);
                        friendInvalidationBatch?.Flush();
                    }
                }
                finally
                {
                    FriendRefresh.EndFriendRefreshContexts(contexts);
                }
            }

            return payload;
        }

        private IReadOnlyList<IDataProvider> ResolveCombinedProviderOrder(IEnumerable<IDataProvider> providers)
        {
            var unique = new List<IDataProvider>();
            foreach (var provider in providers ?? Enumerable.Empty<IDataProvider>())
            {
                if (provider != null && !unique.Contains(provider))
                {
                    unique.Add(provider);
                }
            }

            return _targetSelectionResolver.OrderProvidersForRefresh(unique);
        }

        private async Task OnGameRefreshed(
            IDataProvider provider,
            Game game,
            GameAchievementData data,
            RefreshProgressScope progressScope,
            bool forceIconRefresh,
            CancellationToken cancel = default)
        {
            if (data?.PlayniteGameId == null) return;

            // Ensure provider metadata is persisted for diagnostics and future multi-provider caching.
            try
            {
                if (string.IsNullOrWhiteSpace(data.ProviderKey))
                {
                    data.ProviderKey = provider?.ProviderKey;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to backfill provider key on refreshed game data.");
            }

            var unlockedIconOverrides = GameCustomDataLookup.GetAchievementUnlockedIconOverrides(data.PlayniteGameId.Value);
            var lockedIconOverrides = GameCustomDataLookup.GetAchievementLockedIconOverrides(data.PlayniteGameId.Value);

            await _achievementIconService.PopulateAchievementIconCacheAsync(
                data,
                unlockedIconOverrides,
                lockedIconOverrides,
                cancel,
                (downloaded, total) => _refreshProgressReporter.ReportIconProgress(game, data, downloaded, total, progressScope),
                forceRefreshExistingTargets: forceIconRefresh)
                .ConfigureAwait(false);

            var key = data.PlayniteGameId.Value.ToString();

            if (!string.IsNullOrWhiteSpace(key))
            {
                // Persist provider payload as-is. Runtime overlays (capstone/category/order/game reference)
                // are applied on read and are not written back to cache.

                var writeResult = _cacheService.SaveGameData(key, data);
                if (writeResult == null || !writeResult.Success)
                {
                    var errorCode = writeResult?.ErrorCode ?? "unknown";
                    var errorMessage = writeResult?.ErrorMessage ?? "Unknown cache persistence failure.";

                    throw new CachePersistenceException(
                        key,
                        provider?.ProviderKey ?? data.ProviderKey,
                        errorCode,
                        $"Persisting refreshed game data failed. key={key}, provider={provider?.ProviderKey ?? data.ProviderKey}, code={errorCode}, message={errorMessage}",
                        writeResult?.Exception);
                }

                Interlocked.Increment(ref _savedGamesInCurrentRun);

                // The persisted icon paths now point at original-resolution files, so the game's
                // retired compressed 128px folder (if any) is no longer referenced and can go.
                _achievementIconService.DeleteLegacyCompressedIconFolder(key);

                FriendRefresh.PromoteMatchingProviderOnlyFriendGame(provider, data);

                // Fire per-game refresh event for amortized tag syncing
                try { GameRefreshed?.Invoke(game.Id); } catch (Exception ex) { _logger?.Debug(ex, "GameRefreshed event handler failed."); }
            }
        }

        // Applies icon overrides for the given achievements against the cached game data without
        // running providers: materialize changed overrides (or restore cleared ones to the cached
        // provider default) and persist the rewritten paths. The legacy compressed folder is kept
        // because a clear-restore may resolve into it, and GameRefreshed/tag sync is not raised;
        // the store's CustomDataChanged side effects already cover the override edit itself.
        public async Task ApplyAchievementIconOverridesAsync(
            Guid gameId,
            IReadOnlyCollection<string> changedApiNames,
            CancellationToken cancel = default)
        {
            if (gameId == Guid.Empty ||
                changedApiNames == null ||
                changedApiNames.Count == 0 ||
                _achievementIconService == null ||
                _cacheService == null)
            {
                return;
            }

            var key = gameId.ToString();
            var data = _cacheService.LoadGameData(key);
            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            var unlockedIconOverrides = GameCustomDataLookup.GetAchievementUnlockedIconOverrides(gameId);
            var lockedIconOverrides = GameCustomDataLookup.GetAchievementLockedIconOverrides(gameId);
            var forceApiNames = new HashSet<string>(changedApiNames, StringComparer.OrdinalIgnoreCase);

            await _achievementIconService.PopulateAchievementIconCacheAsync(
                    data,
                    unlockedIconOverrides,
                    lockedIconOverrides,
                    cancel,
                    onIconProgress: null,
                    forceRefreshExistingTargets: false,
                    forceOverrideApiNames: forceApiNames)
                .ConfigureAwait(false);

            var writeResult = _cacheService.SaveGameData(key, data);
            if (writeResult == null || !writeResult.Success)
            {
                _logger?.Error(
                    $"Persisting icon override apply failed. key={key}, code={writeResult?.ErrorCode ?? "unknown"}, message={writeResult?.ErrorMessage ?? "Unknown cache persistence failure."}");
            }
        }

        private string ResolveFinalSuccessMessage(RebuildPayload payload, Func<RebuildPayload, string> finalMessage)
        {
            var defaultMessage = ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
            string resolvedMessage = null;

            if (finalMessage != null)
            {
                try
                {
                    var message = finalMessage(payload);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        resolvedMessage = message;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"));
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedMessage))
            {
                resolvedMessage = defaultMessage;
            }

            if (payload?.FaultedProviderKeys?.Count > 0)
            {
                var failedFormat = ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed");
                if (string.IsNullOrWhiteSpace(failedFormat))
                {
                    failedFormat = "Refresh failed: {0}";
                }

                resolvedMessage = string.Concat(
                    resolvedMessage,
                    " ",
                    string.Format(failedFormat, string.Join(", ", payload.FaultedProviderKeys)));
            }

            if (payload?.AuthRequired == true)
            {
                var suffix = ResourceProvider.GetString("LOCPlayAch_Status_RefreshCompleteAuthRequiredSuffix");
                if (string.IsNullOrWhiteSpace(suffix))
                {
                    suffix = "Some providers require authentication.";
                }

                return string.Concat(resolvedMessage, " ", suffix);
            }

            return resolvedMessage;
        }

        // -----------------------------
        // Public refresh methods
        // -----------------------------

        private Task StartManagedResolvedRequestAsync(
            RefreshRequestPlanner.ResolvedRequest resolved,
            RefreshAuthContext authContext = null,
            TargetSelectionCache targetSelectionCache = null,
            CancellationToken externalToken = default)
        {
            return RunManagedAsync(
                resolved.Mode,
                resolved.SingleGameId,
                externalToken,
                (operationId, cancel) => RefreshResolvedAsync(resolved, operationId, cancel, targetSelectionCache),
                payload => FormatRefreshCompletionForResolvedRequest(resolved, payload),
                resolved.ErrorLogMessage ?? "Refresh failed.",
                resolved.ProviderScope,
                authContext);
        }

        private async Task<RebuildPayload> RefreshResolvedAsync(
            RefreshRequestPlanner.ResolvedRequest resolved,
            Guid operationId,
            CancellationToken cancel,
            TargetSelectionCache targetSelectionCache = null)
        {
            var payload = new RebuildPayload();
            if (resolved == null)
            {
                return payload;
            }

            if (resolved.CurrentUserOptions != null || resolved.FriendOptions != null)
            {
                return await ExecuteRefreshPlanAsync(
                    resolved,
                    cancel,
                    operationId,
                    resolved.Mode,
                    resolved.SingleGameId,
                    targetSelectionCache).ConfigureAwait(false);
            }

            return payload;
        }

        private static string FormatFriendRefreshCompletionForResolvedRequest(
            RefreshRequestPlanner.ResolvedRequest resolved,
            RebuildPayload payload)
        {
            var format = ResourceProvider.GetString("LOCPlayAch_Status_FriendsRefreshCompleteWithModeAndCount");
            if (string.IsNullOrWhiteSpace(format))
            {
                format = "{0} complete. {1} friend game checks refreshed.";
            }

            return string.Format(
                format,
                GetRefreshModeShortName(resolved.Mode),
                Math.Max(0, payload?.FriendSummary?.CandidatesRefreshed ?? 0));
        }

        private static string GetRefreshModeShortName(RefreshModeType mode)
        {
            var resourceKey = mode == RefreshModeType.LibrarySelected
                ? "LOCPlayAch_RefreshModeShort_Selected"
                : mode.GetShortResourceKey();

            return ResourceProvider.GetString(resourceKey);
        }

        private static string FormatRefreshCompletionWithModeAndCount(RefreshModeType mode, int GamesRefreshed)
        {
            return string.Format(
                ResourceProvider.GetString("LOCPlayAch_Status_RefreshCompleteWithModeAndCount"),
                GetRefreshModeShortName(mode),
                Math.Max(0, GamesRefreshed));
        }

        private void ShowCustomRefreshMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _api?.Dialogs?.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static bool HasSteamTransientAuthFailure(RefreshAuthContext authContext)
        {
            var result = authContext?.GetProbeResult("Steam");
            return result != null &&
                   !result.IsSuccess &&
                   (result.Outcome == AuthOutcome.TimedOut ||
                    result.Outcome == AuthOutcome.ProbeFailed);
        }

        public Task ExecuteRefreshAsync(CustomRefreshOptions options, CancellationToken externalToken = default)
        {
            return ExecuteRefreshAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Custom,
                Options = RefreshOptions.FromCustom(options)
            }, externalToken);
        }

        /// <summary>
        /// Executes a refresh based on the specified refresh mode key.
        /// </summary>
        public Task ExecuteRefreshAsync(string modeKey, Guid? singleGameId = null, CancellationToken externalToken = default)
        {
            return ExecuteRefreshAsync(new RefreshRequest
            {
                ModeKey = modeKey,
                SingleGameId = singleGameId
            }, externalToken);
        }

        public virtual Task ExecuteRefreshAsync(RefreshRequest request, CancellationToken externalToken = default)
        {
            return ExecuteRefreshAsync(request, authContext: null, externalToken);
        }

        internal virtual async Task ExecuteRefreshAsync(
            RefreshRequest request,
            IReadOnlyList<IDataProvider> authenticatedProviders,
            CancellationToken externalToken = default)
        {
            var authContext = authenticatedProviders == null
                ? null
                : RefreshAuthContext.FromAuthenticatedProviders(authenticatedProviders);
            await ExecuteRefreshAsync(request, authContext, externalToken).ConfigureAwait(false);
        }

        internal virtual async Task ExecuteRefreshAsync(
            RefreshRequest request,
            RefreshAuthContext authContext,
            CancellationToken externalToken = default)
        {
            var effectiveAuthContext = authContext;
            if (effectiveAuthContext == null)
            {
                effectiveAuthContext = await GetRefreshAuthContextAsync(request, externalToken).ConfigureAwait(false);
            }

            var effectiveAuthenticatedProviders = MaterializeProviderScope(
                effectiveAuthContext?.AuthenticatedProviders);
            var targetSelectionCache = effectiveAuthContext?.TargetSelectionCache ?? new TargetSelectionCache();
            var resolved = _refreshRequestPlanner.Resolve(
                request,
                effectiveAuthenticatedProviders,
                targetSelectionCache);
            if (!resolved.ShouldExecute)
            {
                if (!string.IsNullOrWhiteSpace(resolved.EmptySelectionLogMessage))
                {
                    _logger.Info(resolved.EmptySelectionLogMessage);
                    Report(FormatRefreshCompletionWithModeAndCount(resolved.Mode, 0), 1, 1, mode: resolved.Mode);
                }

                if (!string.IsNullOrWhiteSpace(resolved.UserMessage))
                {
                    if (HasSteamTransientAuthFailure(effectiveAuthContext))
                    {
                        _logger.Warn("Refresh selection produced no targets because Steam web authentication could not be verified; suppressing generic no-target modal.");
                    }
                    else
                    {
                        ShowCustomRefreshMessage(resolved.UserMessage);
                    }
                }

                return;
            }

            resolved.ProviderScope ??= effectiveAuthenticatedProviders;
            await StartManagedResolvedRequestAsync(
                    resolved,
                    effectiveAuthContext,
                    targetSelectionCache,
                    externalToken)
                .ConfigureAwait(false);
        }

        public Task ExecuteRefreshForGamesAsync(IEnumerable<Guid> gameIds, CancellationToken externalToken = default)
        {
            return ExecuteRefreshAsync(new RefreshRequest
            {
                GameIds = gameIds?.ToList()
            }, externalToken);
        }

        /// <summary>
        /// Executes a refresh based on the specified refresh mode type.
        /// </summary>
        public Task ExecuteRefreshAsync(RefreshModeType mode, Guid? singleGameId = null, CancellationToken externalToken = default)
        {
            return ExecuteRefreshAsync(new RefreshRequest
            {
                Mode = mode,
                SingleGameId = singleGameId
            }, externalToken);
        }

        private static string FormatRefreshCompletionForResolvedRequest(
            RefreshRequestPlanner.ResolvedRequest resolved,
            RebuildPayload payload)
        {
            if (resolved?.CurrentUserOptions != null && resolved.FriendOptions != null)
            {
                var format = ResourceProvider.GetString("LOCPlayAch_Status_CombinedRefreshCompleteWithModeAndCounts");
                if (string.IsNullOrWhiteSpace(format))
                {
                    format = "{0} complete: {1} games refreshed; {2} friend game checks refreshed.";
                }

                return string.Format(
                    format,
                    GetRefreshModeShortName(resolved.Mode),
                    Math.Max(0, payload?.Summary?.GamesRefreshed ?? 0),
                    Math.Max(0, payload?.FriendSummary?.CandidatesRefreshed ?? 0));
            }

            if (resolved?.FriendOptions != null)
            {
                return FormatFriendRefreshCompletionForResolvedRequest(resolved, payload);
            }

            if (resolved.Mode == RefreshModeType.Single)
            {
                return ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
            }

            return FormatRefreshCompletionWithModeAndCount(resolved.Mode, payload?.Summary?.GamesRefreshed ?? 0);
        }

        public void CancelCurrentRebuild()
        {
            _logger.Info("Cancel refresh requested.");
            _refreshStateManager.CancelCurrentRebuild();
        }

        public async Task<bool> IsProviderAuthenticatedAsync(IDataProvider provider, CancellationToken ct = default)
        {
            var result = await ProbeProviderAuthStateAsync(provider, ct).ConfigureAwait(false);
            return result.IsSuccess;
        }

        public async Task<AuthProbeResult> ProbeProviderAuthStateAsync(IDataProvider provider, CancellationToken ct = default)
        {
            if (provider == null)
            {
                return AuthProbeResult.NotAuthenticated();
            }

            try
            {
                if (provider.AuthSession != null)
                {
                    return await provider.AuthSession.ProbeAuthStateAsync(ct).ConfigureAwait(false);
                }

                return provider.IsAuthenticated
                    ? AuthProbeResult.AlreadyAuthenticated()
                    : AuthProbeResult.NotAuthenticated();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Auth probe failed for provider '{provider.ProviderKey}'.");
                return AuthProbeResult.ProbeFailed();
            }
        }


        // -----------------------------
        // Friends refresh helpers
        // -----------------------------
        // The friend-refresh subsystem lives in FriendRefreshCoordinator (FriendRefreshCoordinator.cs,
        // with pure policy predicates in FriendRefreshWorkPolicy.cs); the members below delegate to it.

        private IFriendCacheManager _friendCache => _cacheService as IFriendCacheManager;

        private FriendRefreshCoordinator _friendRefreshCoordinator;

        // Created lazily so every constructor path (including the test-only partial-class
        // constructors) produces a coordinator over the same fields.
        private FriendRefreshCoordinator FriendRefresh =>
            _friendRefreshCoordinator ?? (_friendRefreshCoordinator = new FriendRefreshCoordinator(
                _api,
                _settings,
                _logger,
                _cacheService,
                _achievementIconService,
                _providerRegistry,
                _providers));

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<IDataProvider> providerScope,
            FriendRefreshOptions options,
            Action<string, int, int> reportProgress,
            CancellationToken cancel = default)
        {
            return FriendRefresh.RefreshAsync(providerScope, options, reportProgress, cancel);
        }

        /// <summary>
        /// Refreshes only the friend roster (the list of friends and their avatars) for each
        /// friend-capable provider, without fetching ownership, game definitions, or achievements.
        /// Used by the in-settings "refresh friends list" action. Returns the number of active friends
        /// saved across providers.
        /// </summary>
        public Task<int> RefreshFriendRosterAsync(
            IReadOnlyList<IDataProvider> providerScope,
            CancellationToken cancel = default)
        {
            return FriendRefresh.RefreshFriendRosterAsync(providerScope, cancel);
        }

    internal sealed class FriendRefreshProgressSession
    {
        internal const int TotalUnits = 10000;
        private const int MaxReportUnits = TotalUnits - 1;
        private const int RosterStart = 0;
        private const int RosterEnd = 1000;
        private const int LibraryStart = 1000;
        private const int LibraryEnd = 4200;
        private const int GameStart = 4200;
        // The game-check band is split into an ordered definitions sub-band [GameStart, GameChecksSplit]
        // and an achievement-scrape sub-band [GameChecksSplit, GameEnd]. Each sub-band's total is known
        // before it emits any completion, so the monotonic clamp never freezes mid-phase. The split is a
        // fixed midpoint (a fully proportional split is impossible because the scrape total is not known
        // until after the definitions phase runs); when there are no due definitions the scrape phase
        // collapses to use the full [GameStart, GameEnd] range so no dead gap appears.
        private const int GameChecksSplit = 7050;
        private const int GameEnd = 9900;

        private readonly Action<string, int, int> _reportProgress;
        private readonly object _sync = new object();
        private int _lastReportedUnits;
        private int _providerTotal = 1;
        private int _providersCompleted;
        private int _libraryTotal;
        private int _librariesCompleted;
        private int _definitionTotal;
        private int _definitionsCompleted;
        private int _scrapeTotal;
        private int _scrapeCompleted;
        private bool _definitionsEmittedAny;

        public FriendRefreshProgressSession(Action<string, int, int> reportProgress)
        {
            _reportProgress = reportProgress;
        }

        public FriendRefreshProgressSession(Action<string, int, int> reportProgress, int providerCount)
            : this(reportProgress)
        {
            InitializeProviderTotal(providerCount);
        }

        public void InitializeProviderTotal(int total)
        {
            lock (_sync)
            {
                _providerTotal = Math.Max(1, total);
                _providersCompleted = 0;
            }
        }

        public void ReportLoadingFriends()
        {
            ReportLoadingFriends(null, 0, _providerTotal);
        }

        public void ReportLoadingFriends(string providerKey, int providerIndex, int providerTotal)
        {
            var total = Math.Max(1, providerTotal);
            var current = Math.Max(0, Math.Min(providerIndex, total - 1));
            var message = Format(
                "LOCPlayAch_FriendsRefresh_Progress_LoadingFriends",
                "Loading friends {1}/{2}: {0}",
                string.IsNullOrWhiteSpace(providerKey) ? "provider" : providerKey.Trim(),
                current + 1,
                total);
            ReportCountAt(RosterStart, RosterEnd, current, total, message);
        }

        public void ReportProviderRosterLoaded(string providerKey)
        {
            int current;
            int total;
            lock (_sync)
            {
                total = Math.Max(1, _providerTotal);
                _providersCompleted = Math.Max(0, Math.Min(total, _providersCompleted + 1));
                current = _providersCompleted;
            }

            var message = Format(
                "LOCPlayAch_FriendsRefresh_Progress_LoadingFriends",
                "Loading friends {1}/{2}: {0}",
                string.IsNullOrWhiteSpace(providerKey) ? "provider" : providerKey.Trim(),
                current,
                total);
            ReportCountAt(RosterStart, RosterEnd, current, total, message);
        }

        public void InitializeFriendLibraryTotal(int total)
        {
            lock (_sync)
            {
                _libraryTotal = Math.Max(0, total);
                _librariesCompleted = 0;
            }
        }

        public void ReportFriendLibraryCompleted(string friendName = null)
        {
            int current;
            int total;
            lock (_sync)
            {
                total = Math.Max(1, _libraryTotal);
                _librariesCompleted = Math.Max(0, Math.Min(total, _librariesCompleted + 1));
                current = _librariesCompleted;
            }

            ReportCount(
                LibraryStart,
                LibraryEnd,
                current,
                total,
                friendName,
                "LOCPlayAch_FriendsRefresh_Progress_Libraries",
                "Refreshing friend libraries {0}/{1}",
                "LOCPlayAch_FriendsRefresh_Progress_LibrariesNamed",
                "Refreshing friend libraries {0}/{1}: {2}");
        }

        public void InitializeDefinitionChecksTotal(int total)
        {
            lock (_sync)
            {
                _definitionTotal = Math.Max(0, total);
                _definitionsCompleted = 0;
            }
        }

        public void InitializeAchievementScrapeTotal(int total)
        {
            lock (_sync)
            {
                _scrapeTotal = Math.Max(0, total);
                _scrapeCompleted = 0;
            }
        }

        public void ReportDefinitionCheckActive(string detail = null)
        {
            int current;
            int total;
            lock (_sync)
            {
                current = Math.Max(0, _definitionsCompleted);
                total = Math.Max(1, _definitionTotal);
            }

            ReportDefinitionChecks(current, total, detail);
        }

        public void ReportDefinitionCheckCompleted(string detail = null)
        {
            int current;
            int total;
            lock (_sync)
            {
                total = Math.Max(1, _definitionTotal);
                _definitionsCompleted = Math.Max(0, Math.Min(total, _definitionsCompleted + 1));
                current = _definitionsCompleted;
            }

            ReportDefinitionChecks(current, total, detail);
        }

        public void ReportAchievementScrapeCompleted(string detail = null)
        {
            int current;
            int total;
            int scrapeStart;
            lock (_sync)
            {
                total = Math.Max(1, _scrapeTotal);
                _scrapeCompleted = Math.Max(0, Math.Min(total, _scrapeCompleted + 1));
                current = _scrapeCompleted;
                scrapeStart = _definitionsEmittedAny ? GameChecksSplit : GameStart;
            }

            ReportCount(
                scrapeStart,
                GameEnd,
                current,
                total,
                detail,
                "LOCPlayAch_FriendsRefresh_Progress_GameChecks",
                "Refreshing friend games {0}/{1}",
                "LOCPlayAch_FriendsRefresh_Progress_GameChecksNamed",
                "Refreshing friend games {0}/{1}: {2}");
        }

        // Achievement-icon and game-image downloads are sub-steps of the game-definition check for a given
        // friend game. They advance the bar fractionally within that game's check unit but keep the SAME
        // "Checking friend games" status text, so the message never bounces between "checking games" and
        // "downloading images".
        public void ReportAchievementImages(int completed, int total, string gameName = null)
        {
            ReportImageProgress(completed, total, gameName);
        }

        public void ReportFriendGameImages(int completed, int total, string gameName = null)
        {
            ReportImageProgress(completed, total, gameName);
        }

        private void ReportDefinitionChecks(int completed, int total, string detail)
        {
            lock (_sync)
            {
                _definitionsEmittedAny = true;
            }

            ReportCount(
                GameStart,
                GameChecksSplit,
                completed,
                total,
                detail,
                "LOCPlayAch_FriendsRefresh_Progress_GameChecks",
                "Refreshing friend games {0}/{1}",
                "LOCPlayAch_FriendsRefresh_Progress_GameChecksNamed",
                "Refreshing friend games {0}/{1}: {2}");
        }

        private void ReportImageProgress(int completed, int total, string detail)
        {
            total = Math.Max(1, total);
            completed = Math.Max(0, Math.Min(completed, total));
            // Image downloads happen during the definitions phase (per due game), so their fractional
            // sub-progress maps into the definitions sub-band over the definitions total. This keeps
            // image progress from leaking into (and freezing) the achievement-scrape sub-band. The image
            // completed/total drive only the fractional bar position; the visible text reuses the shared
            // "Checking friend games" message (with the game-check counts) so the phrasing never changes.
            int definitionsCompleted;
            int definitionTotal;
            lock (_sync)
            {
                _definitionsEmittedAny = true;
                definitionsCompleted = Math.Max(0, _definitionsCompleted);
                definitionTotal = Math.Max(1, _definitionTotal);
            }

            var fractional = completed <= 0
                ? 0d
                : Math.Min(0.85d, completed / (double)total * 0.85d);
            var effectiveCompleted = Math.Min(definitionTotal, definitionsCompleted + fractional);
            var units = GameStart + (int)((GameChecksSplit - GameStart) * effectiveCompleted / definitionTotal);
            var message = FormatCount(
                "LOCPlayAch_FriendsRefresh_Progress_GameChecks",
                "Refreshing friend games {0}/{1}",
                "LOCPlayAch_FriendsRefresh_Progress_GameChecksNamed",
                "Checking friend games {0}/{1}: {2}",
                definitionsCompleted,
                definitionTotal,
                detail);
            ReportAt(units, message);
        }

        private void ReportCount(
            int start,
            int end,
            int completed,
            int total,
            string detail,
            string resourceKey,
            string fallback,
            string namedResourceKey,
            string namedFallback)
        {
            total = Math.Max(1, total);
            completed = Math.Max(0, Math.Min(completed, total));
            var localUnits = start + (int)((long)(end - start) * completed / total);
            var message = FormatCount(resourceKey, fallback, namedResourceKey, namedFallback, completed, total, detail);
            ReportAt(localUnits, message);
        }

        private void ReportCountAt(int start, int end, int completed, int total, string message)
        {
            total = Math.Max(1, total);
            completed = Math.Max(0, Math.Min(completed, total));
            var localUnits = start + (int)((long)(end - start) * completed / total);
            ReportAt(localUnits, message);
        }

        private void ReportAt(int localUnits, string message)
        {
            if (_reportProgress == null)
            {
                return;
            }

            var globalUnits = Math.Max(0, Math.Min(MaxReportUnits, localUnits));
            lock (_sync)
            {
                if (globalUnits < _lastReportedUnits)
                {
                    globalUnits = _lastReportedUnits;
                }
                else
                {
                    _lastReportedUnits = globalUnits;
                }
            }

            _reportProgress(message, globalUnits, TotalUnits);
        }

        private static string FormatCount(
            string resourceKey,
            string fallback,
            string namedResourceKey,
            string namedFallback,
            int current,
            int total,
            string detail)
        {
            if (!string.IsNullOrWhiteSpace(detail))
            {
                return Format(namedResourceKey, namedFallback, current, total, detail.Trim());
            }

            return Format(resourceKey, fallback, current, total);
        }

        private static string Format(string resourceKey, string fallback, params object[] args)
        {
            var format = ResourceProvider.GetString(resourceKey);
            if (string.IsNullOrWhiteSpace(format) ||
                (format.StartsWith("<!", StringComparison.Ordinal) && format.EndsWith("!>", StringComparison.Ordinal)))
            {
                format = fallback;
            }

            return string.Format(format, args ?? Array.Empty<object>());
        }
    }
}
}
