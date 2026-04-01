using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.ProgressReporting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// Manages user achievement refreshing and caching operations.
    /// </summary>
    public class RefreshRuntime : IDisposable
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
        private readonly ProviderRegistry _providerRegistry;
        private readonly Action<RebuildPayload> _onRefreshCompleted;
        private int _savedGamesInCurrentRun;
        private volatile List<string> _lastFailedAuthProviderKeys = new List<string>();

        // Dependencies that need disposal
        private readonly IReadOnlyList<IDataProvider> _providers;

        public ICacheManager Cache => _cacheService;

        /// <summary>
        /// Gets the provider registry for checking/modifying provider enabled state.
        /// </summary>
        public ProviderRegistry ProviderRegistry => _providerRegistry;

        internal async Task<IReadOnlyList<IDataProvider>> GetAuthenticatedProvidersOrShowDialogAsync(CancellationToken ct = default)
        {
            var authenticatedProviders = await GetAuthenticatedProvidersAsync(ct).ConfigureAwait(false);
            if (authenticatedProviders.Count > 0)
            {
                return authenticatedProviders;
            }

            _logger.Info("Refresh attempted but no platforms are authenticated.");
            _api.Dialogs.ShowMessage(
                ResourceProvider.GetString("LOCPlayAch_Error_NoAuthenticatedProviders"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return Array.Empty<IDataProvider>();
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

        public event EventHandler CacheInvalidated
        {
            add => _cacheService.CacheInvalidated += value;
            remove => _cacheService.CacheInvalidated -= value;
        }

        public RefreshRuntime(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            PlayniteAchievementsPlugin plugin,
            IEnumerable<IDataProvider> providers,
            DiskImageService diskImageService,
            ManagedCustomIconService managedCustomIconService,
            ProviderRegistry providerRegistry,
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
            _refreshProgressReporter = new RefreshProgressReporter((report, prioritizePending) => Report(report, prioritizePending));
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
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

        private async Task RunManagedAsync(
            RefreshModeType mode,
            Guid? singleGameId,
            CancellationToken externalToken,
            Func<Guid, CancellationToken, Task<RebuildPayload>> runner,
            Func<RebuildPayload, string> finalMessage,
            string errorLogMessage,
            IReadOnlyList<IDataProvider> providerScope = null)
        {
            var operationId = Guid.NewGuid();

            var effectiveProviderScope = MaterializeProviderScope(providerScope);
            if (effectiveProviderScope.Count == 0 && providerScope == null)
            {
                effectiveProviderScope = MaterializeProviderScope(
                    await GetAuthenticatedProvidersAsync(externalToken).ConfigureAwait(false));
            }

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
                var hasSavedGames = Interlocked.Exchange(ref _savedGamesInCurrentRun, 0) > 0;
                var wasCanceled = cts.IsCancellationRequested;
                var totalGames = _refreshProgressReporter.TotalGames;
                EndRun();

                // Send final completion report AFTER EndRun so IsRebuilding is false when UI processes it
                if (!wasCanceled && payload != null)
                {
                    var msg = ResolveFinalSuccessMessage(payload, finalMessage);
                    Report(
                        msg,
                        totalGames,
                        totalGames,
                        operationId: operationId,
                        mode: mode,
                        currentGameId: singleGameId);
                }

                if (hasSavedGames)
                {
                    _cacheService.NotifyCacheInvalidated();
                }

                // Notify refresh completion subscribers (e.g., auth failure notifications).
                if (!wasCanceled && payload != null)
                {
                    try { _onRefreshCompleted?.Invoke(payload); } catch { }
                }

                _refreshProgressReporter.Reset();
            }
        }

        private sealed class RefreshGameTarget
        {
            public Game Game { get; set; }
            public IDataProvider Provider { get; set; }
        }

        public async Task<IReadOnlyList<IDataProvider>> GetAuthenticatedProvidersAsync(CancellationToken ct = default)
        {
            var authenticatedProviders = new List<IDataProvider>();

            foreach (var provider in _providers)
            {
                if (provider == null || !_providerRegistry.IsProviderEnabled(provider.ProviderKey))
                {
                    continue;
                }

                if (await IsProviderAuthenticatedAsync(provider, ct).ConfigureAwait(false))
                {
                    authenticatedProviders.Add(provider);
                }
            }

            return authenticatedProviders;
        }

        private List<RefreshGameTarget> GetRefreshTargets(CacheRefreshOptions options, IReadOnlyList<IDataProvider> providers)
        {
            return _targetSelectionResolver.GetRefreshTargets(options, providers)
                .Select(target => new RefreshGameTarget { Game = target.Game, Provider = target.Provider })
                .ToList();
        }

        private async Task<RebuildPayload> RefreshAsync(
            CacheRefreshOptions options,
            CancellationToken cancel,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId = null,
            bool forceIconRefresh = false,
            IReadOnlyList<IDataProvider> providerScope = null,
            bool? runProvidersInParallelOverride = null)
        {
            options ??= new CacheRefreshOptions();

            var authenticatedProviders = providerScope == null
                ? MaterializeProviderScope(await GetAuthenticatedProvidersAsync(cancel).ConfigureAwait(false))
                : MaterializeProviderScope(providerScope);
            if (authenticatedProviders.Count == 0)
            {
                _logger?.Warn("No authenticated platforms available for refresh.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var refreshTargets = GetRefreshTargets(options, authenticatedProviders);
            var orderedProviders = _targetSelectionResolver.OrderProvidersForRefresh(authenticatedProviders);
            var providerOrder = orderedProviders
                .Select((provider, index) => new { provider, index })
                .ToDictionary(x => x.provider, x => x.index);

            var providerPlans = refreshTargets
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
                providerPlans.Count));

            if (providerPlans.Count == 0)
            {
                _logger?.Warn("No matching platforms available for refresh options.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            _refreshProgressReporter.Initialize(refreshTargets.Count);
            var progressScope = new RefreshProgressScope(operationId, mode, singleGameId);

            var runProvidersInParallel = runProvidersInParallelOverride ?? (_settings?.Persisted?.EnableParallelProviderRefresh ?? true);
            var providerResults = await ProviderRefreshExecutor.ExecuteProvidersAsync(
                providerPlans,
                runProvidersInParallel,
                plan => plan.Provider.RefreshAsync(
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
                    cancel),
                cancel).ConfigureAwait(false);

            var mergedSummary = new RebuildSummary();
            var authRequired = false;
            var failedProviderKeys = new List<string>();

            foreach (var result in providerResults)
            {
                if (result?.Payload == null)
                {
                    continue;
                }

                if (result.Payload.AuthRequired)
                {
                    authRequired = true;
                    var key = result.Provider?.ProviderKey;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        failedProviderKeys.Add(key);
                    }
                }

                if (result.Payload.Summary == null)
                {
                    continue;
                }

                mergedSummary.GamesRefreshed += result.Payload.Summary.GamesRefreshed;
                mergedSummary.GamesWithAchievements += result.Payload.Summary.GamesWithAchievements;
                mergedSummary.GamesWithoutAchievements += result.Payload.Summary.GamesWithoutAchievements;

                if (result.Payload.Summary.RefreshedGameIds != null)
                {
                    mergedSummary.RefreshedGameIds.AddRange(result.Payload.Summary.RefreshedGameIds);
                }
            }

            return new RebuildPayload
            {
                Summary = mergedSummary,
                AuthRequired = authRequired,
                FailedProviderKeys = failedProviderKeys
            };
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
            catch
            {
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
                _cacheService.NotifyCacheInvalidated();

                // Fire per-game refresh event for amortized tag syncing
                try { GameRefreshed?.Invoke(game.Id); } catch { }
            }
        }

        /// <summary>
        /// Downloads and caches achievement icons for a GameAchievementData object.
        /// Updates icon paths in-place to point to local cached files.
        /// </summary>
        public async Task DownloadAchievementIconsAsync(
            GameAchievementData data,
            CancellationToken cancel = default)
        {
            var unlockedIconOverrides = data?.PlayniteGameId != null
                ? GameCustomDataLookup.GetAchievementUnlockedIconOverrides(data.PlayniteGameId.Value)
                : null;
            var lockedIconOverrides = data?.PlayniteGameId != null
                ? GameCustomDataLookup.GetAchievementLockedIconOverrides(data.PlayniteGameId.Value)
                : null;
            await _achievementIconService
                .DownloadAchievementIconsAsync(data, unlockedIconOverrides, lockedIconOverrides, cancel)
                .ConfigureAwait(false);
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

        private Task StartManagedRefreshCoreAsync(
            RefreshModeType mode,
            CacheRefreshOptions options,
            Func<RebuildPayload, string> finalMessage,
            string errorLogMessage,
            Guid? singleGameId = null,
            bool forceIconRefresh = false,
            IReadOnlyList<IDataProvider> providerScope = null,
            bool? runProvidersInParallelOverride = null,
            CancellationToken externalToken = default)
        {
            return RunManagedAsync(
                mode,
                singleGameId,
                externalToken,
                (operationId, cancel) => RefreshAsync(
                    options,
                    cancel,
                    operationId,
                    mode,
                    singleGameId,
                    forceIconRefresh,
                    providerScope,
                    runProvidersInParallelOverride),
                finalMessage,
                errorLogMessage,
                providerScope
            );
        }

        private Task StartManagedResolvedRequestAsync(
            RefreshRequestPlanner.ResolvedRequest resolved,
            CancellationToken externalToken = default)
        {
            return StartManagedRefreshCoreAsync(
                resolved.Mode,
                resolved.Options,
                payload => FormatRefreshCompletionForResolvedRequest(resolved, payload),
                resolved.ErrorLogMessage ?? "Refresh failed.",
                singleGameId: resolved.SingleGameId,
                forceIconRefresh: resolved.ForceIconRefresh,
                providerScope: resolved.ProviderScope,
                runProvidersInParallelOverride: resolved.RunProvidersInParallelOverride,
                externalToken: externalToken);
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

        public Task ExecuteRefreshAsync(CustomRefreshOptions options, CancellationToken externalToken = default)
        {
            return ExecuteRefreshAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Custom,
                CustomOptions = options
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

        public Task ExecuteRefreshAsync(RefreshRequest request, CancellationToken externalToken = default)
        {
            return ExecuteRefreshAsync(request, authenticatedProviders: null, externalToken);
        }

        internal async Task ExecuteRefreshAsync(
            RefreshRequest request,
            IReadOnlyList<IDataProvider> authenticatedProviders,
            CancellationToken externalToken = default)
        {
            var effectiveAuthenticatedProviders = MaterializeProviderScope(authenticatedProviders);
            if (effectiveAuthenticatedProviders.Count == 0 && authenticatedProviders == null)
            {
                effectiveAuthenticatedProviders = MaterializeProviderScope(
                    await GetAuthenticatedProvidersAsync(externalToken).ConfigureAwait(false));
            }

            var resolved = _refreshRequestPlanner.Resolve(request, effectiveAuthenticatedProviders);
            if (!resolved.ShouldExecute)
            {
                if (!string.IsNullOrWhiteSpace(resolved.EmptySelectionLogMessage))
                {
                    _logger.Info(resolved.EmptySelectionLogMessage);
                    Report(FormatRefreshCompletionWithModeAndCount(resolved.Mode, 0), 1, 1, mode: resolved.Mode);
                }

                if (!string.IsNullOrWhiteSpace(resolved.UserMessage))
                {
                    ShowCustomRefreshMessage(resolved.UserMessage);
                }

                return;
            }

            resolved.ProviderScope ??= effectiveAuthenticatedProviders;
            await StartManagedResolvedRequestAsync(resolved, externalToken).ConfigureAwait(false);
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

    }
}
