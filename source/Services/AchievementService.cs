using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Images;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Hydration;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// Manages user achievement refreshing and caching operations.
    /// </summary>
    public class AchievementService : IDisposable
    {
        private readonly object _runLock = new object();
        private readonly object _pointsColumnVisibilityLock = new object();
        private readonly object _reportLock = new object();
        private CancellationTokenSource _activeRunCts;
        private Guid? _activeOperationId;
        private RefreshModeType? _activeRefreshMode;
        private Guid? _activeSingleGameId;

        public event EventHandler<ProgressReport> RebuildProgress;

        /// <summary>
        /// Event raised when game data changes (exclusions, capstones, etc.).
        /// Argument is the list of game IDs that were affected.
        /// </summary>
        public event Action<List<Guid>> GameDataChanged;

        private ProgressReport _lastProgress;
        private string _lastStatus;

        /// <summary>
        /// Gets the list of game IDs that were refreshed in the most recent refresh operation.
        /// </summary>
        public List<Guid> LastRefreshedGameIds { get; private set; } = new List<Guid>();

        // Progress throttling
        private ProgressReport _pendingReport;
        private bool _pendingReportIsPriority;
        private long _lastReportTimestamp = -1; // Stopwatch.GetTimestamp() for high-precision throttling
        private System.Timers.Timer _reportThrottleTimer;
        private const int ReportThrottleIntervalMs = 1000;

        public ProgressReport GetLastRebuildProgress() => _lastProgress;
        public string GetLastRebuildStatus() => _lastStatus;

        public bool IsRebuilding
        {
            get { lock (_runLock) return _activeRunCts != null; }
        }

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ICacheManager _cacheService;
        private readonly DiskImageService _diskImageService;
        private readonly ProviderRegistry _providerRegistry;
        private int _savedGamesInCurrentRun;
        private long _lastCacheInvalidationTimestamp = -1;
        private const long CacheInvalidationThrottleMs = 500;
        private const string PointsColumnKey = "Points";
        private const string EpicProviderKey = "Epic";
        private const string RaProviderKey = "RetroAchievements";

        // Dependencies that need disposal
        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly GameDataHydrator _hydrator;
        // Tracks overall refresh progress for refresh and icon updates.
        private int _processedGamesInRun;
        private int _totalGamesInRun;

        public ICacheManager Cache => _cacheService;

        /// <summary>
        /// Gets the provider registry for checking/modifying provider enabled state.
        /// </summary>
        public ProviderRegistry ProviderRegistry => _providerRegistry;

        /// <summary>
        /// Checks if at least one provider is enabled and has valid authentication credentials configured.
        /// </summary>
        public bool HasAnyAuthenticatedProvider() => _providers.Any(p =>
            _providerRegistry.IsProviderEnabled(p.ProviderKey) && p.IsAuthenticated);

        /// <summary>
        /// Validates that a refresh can proceed. Returns true if authenticated, otherwise shows dialog.
        /// Call this before showing any progress UI.
        /// </summary>
        public bool ValidateCanStartRefresh()
        {
            if (HasAnyAuthenticatedProvider())
            {
                return true;
            }

            _logger.Info("Refresh attempted but no platforms are authenticated.");
            _api.Dialogs.ShowMessage(
                ResourceProvider.GetString("LOCPlayAch_Error_NoAuthenticatedProviders"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        /// <summary>
        /// Gets the list of available data providers.
        /// </summary>
        public IReadOnlyList<IDataProvider> GetProviders() => _providers;

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

        public AchievementService(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            PlayniteAchievementsPlugin plugin,
            IEnumerable<IDataProvider> providers,
            DiskImageService diskImageService,
            ProviderRegistry providerRegistry)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            if (providers == null) throw new ArgumentNullException(nameof(providers));
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));

            _cacheService = new CacheManager(api, logger, _plugin);
            _providers = providers.ToList();
            _hydrator = new GameDataHydrator(api, _settings.Persisted);

            _ = Task.Run(() =>
            {
                try
                {
                    using (PerfScope.Start(_logger, "AchievementService.InitializePointsColumnVisibilityDefaults.Async", thresholdMs: 25))
                    {
                        InitializePointsColumnVisibilityDefaults();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to initialize Points-column visibility defaults asynchronously.");
                }
            });
        }

        public void Dispose()
        {
            // Dispose throttle timer
            lock (_reportLock)
            {
                if (_reportThrottleTimer != null)
                {
                    try
                    {
                        _reportThrottleTimer.Stop();
                        _reportThrottleTimer.Elapsed -= OnThrottleTimerElapsed;
                        _reportThrottleTimer.Dispose();
                        _reportThrottleTimer = null;
                    }
                    catch { }
                }
            }

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
            lock (_runLock)
            {
                return (_activeOperationId, _activeRefreshMode, _activeSingleGameId);
            }
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

            _lastProgress = report;
            if (!string.IsNullOrWhiteSpace(report.Message))
            {
                _lastStatus = report.Message;
            }

            var handler = RebuildProgress;
            if (handler == null) return;

            // Always report immediately for final/canceled updates
            var isFinal = report.IsCanceled || (report.TotalSteps > 0 && report.CurrentStep >= report.TotalSteps);

            // Fast path: use lock-free timestamp check for throttling decision
            var nowTimestamp = Stopwatch.GetTimestamp();
            if (!isFinal)
            {
                // Check if we're within the throttle window without taking the lock
                var lastTimestamp = Interlocked.Read(ref _lastReportTimestamp);
                if (lastTimestamp >= 0)
                {
                    var elapsedMs = (nowTimestamp - lastTimestamp) * 1000L / Stopwatch.Frequency;
                    if (elapsedMs < ReportThrottleIntervalMs)
                    {
                        // Within throttle window - update pending report under lock
                        lock (_reportLock)
                        {
                            if (_pendingReport == null || prioritizePending || !_pendingReportIsPriority)
                            {
                                _pendingReport = report;
                                _pendingReportIsPriority = prioritizePending;
                            }

                            if (_reportThrottleTimer == null)
                            {
                                _reportThrottleTimer = new System.Timers.Timer(ReportThrottleIntervalMs);
                                _reportThrottleTimer.AutoReset = false;
                                _reportThrottleTimer.Elapsed += OnThrottleTimerElapsed;
                            }
                            if (!_reportThrottleTimer.Enabled)
                            {
                                _reportThrottleTimer.Start();
                            }
                        }
                        return;
                    }
                }
            }

            // Send immediately (either final report or throttle window elapsed)
            lock (_reportLock)
            {
                Interlocked.Exchange(ref _lastReportTimestamp, nowTimestamp);
                _pendingReport = null;
                _pendingReportIsPriority = false;
                StopThrottleTimer();
                SendReportToUi(report, handler);
            }
        }

        private void OnThrottleTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            EventHandler<ProgressReport> handler;
            ProgressReport reportToSend;

            lock (_reportLock)
            {
                handler = RebuildProgress;
                reportToSend = _pendingReport;
                _pendingReport = null;
                _pendingReportIsPriority = false;
                Interlocked.Exchange(ref _lastReportTimestamp, Stopwatch.GetTimestamp());
            }

            if (reportToSend != null && handler != null)
            {
                SendReportToUi(reportToSend, handler);
            }
        }

        private void StopThrottleTimer()
        {
            if (_reportThrottleTimer != null)
            {
                try
                {
                    _reportThrottleTimer.Stop();
                }
                catch { }
            }
        }

        private void SendReportToUi(ProgressReport report, EventHandler<ProgressReport> handler)
        {
            PostToUi(() =>
            {
                foreach (EventHandler<ProgressReport> subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        subscriber(this, report);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, ResourceProvider.GetString("LOCPlayAch_Error_NotifySubscribers"));
                    }
                }
            });
        }

        /// <summary>
        /// Calculates percentage for a refresh progress report.
        /// </summary>
        public double CalculateProgressPercent(ProgressReport report)
        {
            if (report == null)
            {
                return 0;
            }

            var pct = report.PercentComplete;
            if ((pct <= 0 || double.IsNaN(pct)) && report.TotalSteps > 0)
            {
                pct = Math.Max(0, Math.Min(100, (report.CurrentStep * 100.0) / report.TotalSteps));
            }

            if (double.IsNaN(pct))
            {
                return 0;
            }

            return Math.Max(0, Math.Min(100, pct));
        }

        /// <summary>
        /// Determines if the provided report represents a final refresh state.
        /// </summary>
        public bool IsFinalProgressReport(ProgressReport report)
        {
            return IsFinalProgressReport(report, CalculateProgressPercent(report));
        }

        /// <summary>
        /// Resolves the user-facing refresh status message from report + manager state.
        /// </summary>
        public string ResolveProgressMessage(ProgressReport report = null)
        {
            var effectiveReport = report ?? _lastProgress;
            var isFinal = IsFinalProgressReport(effectiveReport);
            return ResolveProgressMessage(effectiveReport, isFinal);
        }

        /// <summary>
        /// Gets a centralized refresh status snapshot for UI consumers.
        /// </summary>
        public RefreshStatusSnapshot GetRefreshStatusSnapshot(ProgressReport report = null)
        {
            var effectiveReport = report ?? _lastProgress;
            var progressPercent = CalculateProgressPercent(effectiveReport);
            var isFinal = IsFinalProgressReport(effectiveReport, progressPercent);

            return new RefreshStatusSnapshot
            {
                IsRefreshing = IsRebuilding,
                IsFinal = isFinal,
                IsCanceled = effectiveReport?.IsCanceled == true,
                ProgressPercent = progressPercent,
                Message = ResolveProgressMessage(effectiveReport, isFinal)
            };
        }

        /// <summary>
        /// Gets a transient "starting refresh" snapshot for immediate UI updates.
        /// </summary>
        public RefreshStatusSnapshot GetStartingRefreshStatusSnapshot()
        {
            return new RefreshStatusSnapshot
            {
                IsRefreshing = IsRebuilding,
                IsFinal = false,
                IsCanceled = false,
                ProgressPercent = 0,
                Message = ResourceProvider.GetString("LOCPlayAch_Status_Starting")
            };
        }

        private bool IsFinalProgressReport(ProgressReport report, double progressPercent)
        {
            if (report == null)
            {
                return false;
            }

            return report.IsCanceled ||
                   (report.TotalSteps > 0 && report.CurrentStep >= report.TotalSteps) ||
                   progressPercent >= 100;
        }

        private string ResolveProgressMessage(ProgressReport report, bool isFinal)
        {
            if (!string.IsNullOrWhiteSpace(report?.Message))
            {
                return report.Message;
            }

            if (report?.IsCanceled == true)
            {
                return ResourceProvider.GetString("LOCPlayAch_Status_Canceled");
            }

            if (isFinal)
            {
                return ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
            }

            if (!string.IsNullOrWhiteSpace(_lastStatus))
            {
                return _lastStatus;
            }

            return ResourceProvider.GetString("LOCPlayAch_Status_Starting");
        }

        // -----------------------------
        // Refresh option builders
        // -----------------------------

        private CacheRefreshOptions FullRefreshOptions()
        {
            return new CacheRefreshOptions
            {
                RecentRefreshMode = false,
                IncludeUnplayedGames = _settings.Persisted.IncludeUnplayedGames
            };
        }

        private CacheRefreshOptions SingleGameOptions(Guid playniteGameId)
        {
            return new CacheRefreshOptions
            {
                PlayniteGameIds = new[] { playniteGameId },
                IncludeUnplayedGames = true,
                BypassExclusions = true
            };
        }

        private CacheRefreshOptions RecentRefreshOptions()
        {
            return new CacheRefreshOptions
            {
                RecentRefreshMode = true,
                RecentRefreshGamesCount = _settings?.Persisted?.RecentRefreshGamesCount ?? 10,
                IncludeUnplayedGames = _settings.Persisted.IncludeUnplayedGames
            };
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
            lock (_runLock)
            {
                if (_activeRunCts != null)
                {
                    _logger.Info("Refresh already in progress.");
                    cts = null;
                    Report(
                        _lastStatus ?? ResourceProvider.GetString("LOCPlayAch_Status_UpdatingCache"),
                        0,
                        1,
                        operationId: _activeOperationId,
                        mode: _activeRefreshMode,
                        currentGameId: _activeSingleGameId);
                    return false;
                }

                _logger.Info("Starting new refresh.");

                // Link external token if provided so external cancellation propagates to internal CTS
                if (externalToken != CancellationToken.None)
                {
                    _activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
                }
                else
                {
                    _activeRunCts = new CancellationTokenSource();
                }

                _activeOperationId = operationId;
                _activeRefreshMode = mode;
                _activeSingleGameId = singleGameId;
                cts = _activeRunCts;
                return true;
            }
        }

        private void EndRun()
        {
            _logger.Info("Refresh ended.");
            lock (_runLock)
            {
                _activeRunCts?.Dispose();
                _activeRunCts = null;
                _activeOperationId = null;
                _activeRefreshMode = null;
                _activeSingleGameId = null;
            }
        }

        private async Task RunManagedAsync(
            RefreshModeType mode,
            Guid? singleGameId,
            CancellationToken externalToken,
            Func<Guid, CancellationToken, Task<RebuildPayload>> runner,
            Func<RebuildPayload, string> finalMessage,
            string errorLogMessage)
        {
            var operationId = Guid.NewGuid();

            if (!HasAnyAuthenticatedProvider())
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

            Interlocked.Exchange(ref _processedGamesInRun, 0);
            _totalGamesInRun = 0;
            Interlocked.Exchange(ref _savedGamesInCurrentRun, 0);
            Interlocked.Exchange(ref _lastCacheInvalidationTimestamp, -1);

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
                var totalGames = Math.Max(1, _totalGamesInRun);
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
                    NotifyCacheInvalidatedThrottled(force: true);
                }

                _totalGamesInRun = 0;
                Interlocked.Exchange(ref _processedGamesInRun, 0);
            }
        }

        private sealed class RefreshGameTarget
        {
            public Game Game { get; set; }
            public IDataProvider Provider { get; set; }
        }

        private sealed class CustomRefreshResolution
        {
            public IReadOnlyList<IDataProvider> Providers { get; set; }
            public IReadOnlyList<Guid> TargetGameIds { get; set; }
            public bool RunProvidersInParallel { get; set; }
        }

        private IReadOnlyList<IDataProvider> GetAuthenticatedProviders()
        {
            return _providers
                .Where(p => p != null &&
                    _providerRegistry.IsProviderEnabled(p.ProviderKey) &&
                    p.IsAuthenticated)
                .ToList();
        }

        private IDataProvider ResolveProviderForGame(Game game, IReadOnlyList<IDataProvider> providers)
        {
            if (game == null || providers == null || providers.Count == 0)
            {
                return null;
            }

            foreach (var provider in providers)
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

        private List<RefreshGameTarget> GetRefreshTargets(CacheRefreshOptions options, IReadOnlyList<IDataProvider> providers)
        {
            options ??= new CacheRefreshOptions();

            // Get excluded games from settings (survives cache clear)
            HashSet<Guid> excludedGameIds = null;
            if (options.SkipNoAchievementsGames && !options.BypassExclusions)
            {
                excludedGameIds = _settings.Persisted.ExcludedGameIds;
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

            var targets = new List<RefreshGameTarget>();
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

                // Skip games already marked as having no achievements or excluded by user
                if (excludedGameIds != null &&
                    excludedGameIds.Contains(game.Id))
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

                targets.Add(new RefreshGameTarget { Game = game, Provider = provider });

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

        private async Task<RebuildPayload> RefreshAsync(
            CacheRefreshOptions options,
            CancellationToken cancel,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId = null,
            IReadOnlyList<IDataProvider> providerScope = null,
            bool? runProvidersInParallelOverride = null)
        {
            options ??= new CacheRefreshOptions();

            var authenticatedProviders = (providerScope ?? GetAuthenticatedProviders())
                .Where(provider => provider != null)
                .ToList();
            if (authenticatedProviders.Count == 0)
            {
                _logger?.Warn("No authenticated platforms available for refresh.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var refreshTargets = GetRefreshTargets(options, authenticatedProviders);
            var providerOrder = authenticatedProviders
                .Select((provider, index) => new { provider, index })
                .ToDictionary(x => x.provider, x => x.index);

            var providerPlans = refreshTargets
                .GroupBy(x => x.Provider)
                .OrderBy(group => providerOrder.TryGetValue(group.Key, out var index) ? index : int.MaxValue)
                .Select(group => new RefreshPipeline.ProviderExecutionPlan
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

            _totalGamesInRun = refreshTargets.Count;
            Interlocked.Exchange(ref _processedGamesInRun, 0);

            var runProvidersInParallel = runProvidersInParallelOverride ?? (_settings?.Persisted?.EnableParallelProviderRefresh ?? true);
            var providerResults = await RefreshPipeline.ExecuteProvidersAsync(
                providerPlans,
                runProvidersInParallel,
                plan => plan.Provider.RefreshAsync(
                    plan.Games,
                    game => ReportGameStarting(game, operationId, mode, singleGameId),
                    (game, data) => OnProviderGameCompleted(plan.Provider, game, data, operationId, mode, singleGameId, cancel),
                    cancel),
                cancel).ConfigureAwait(false);

            var mergedSummary = new RebuildSummary();
            var authRequired = false;

            foreach (var result in providerResults)
            {
                if (result?.Payload == null)
                {
                    continue;
                }

                authRequired |= result.Payload.AuthRequired;

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
                AuthRequired = authRequired
            };
        }

        private void ReportGameStarting(
            Game game,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId)
        {
            var totalGames = Math.Max(1, _totalGamesInRun);
            var completedGames = Math.Min(Volatile.Read(ref _processedGamesInRun), totalGames);
            var displayIndex = Math.Min(totalGames, completedGames + 1);
            var currentGameId = game?.Id ?? singleGameId;
            var gameName = game?.Name;
            var message = BuildRefreshingGameMessage(gameName, displayIndex, totalGames);

            Report(
                message,
                completedGames,
                totalGames,
                operationId: operationId,
                mode: mode,
                currentGameId: currentGameId);
        }

        private void ReportIconProgress(
            Game game,
            GameAchievementData data,
            int iconsDownloaded,
            int totalIcons,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId)
        {
            if (totalIcons <= 0)
            {
                return;
            }

            var totalGames = Math.Max(1, _totalGamesInRun);
            var completedGames = Math.Min(Volatile.Read(ref _processedGamesInRun), totalGames);
            var displayIndex = Math.Min(totalGames, completedGames + 1);
            var currentGameId = data?.PlayniteGameId ?? game?.Id ?? singleGameId;
            var gameName = data?.GameName ?? game?.Name;
            var message = BuildRefreshingGameMessage(gameName, displayIndex, totalGames, iconsDownloaded, totalIcons);
            var report = new ProgressReport
            {
                Message = message,
                CurrentStep = completedGames,
                TotalSteps = totalGames,
                OperationId = operationId,
                Mode = mode,
                CurrentGameId = currentGameId
            };

            Report(report, prioritizePending: true);
        }

        private string BuildRefreshingGameMessage(
            string gameName,
            int currentIndex,
            int totalGames,
            int iconsDownloaded = 0,
            int totalIcons = 0)
        {
            var safeGameName = string.IsNullOrWhiteSpace(gameName)
                ? ResourceProvider.GetString("LOCPlayAch_Text_Ellipsis")
                : gameName;

            var countsText = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Format_Counts"),
                Math.Max(0, currentIndex),
                Math.Max(1, totalGames));

            if (totalIcons > 0)
            {
                return string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Targeted_RefreshingGameWithIcons"),
                    safeGameName,
                    countsText,
                    Math.Max(0, iconsDownloaded),
                    Math.Max(0, totalIcons));
            }

            return string.Format(
                ResourceProvider.GetString("LOCPlayAch_Targeted_RefreshingGameWithCounts"),
                safeGameName,
                countsText);
        }

        private async Task OnProviderGameCompleted(
            IDataProvider provider,
            Game game,
            GameAchievementData data,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId,
            CancellationToken cancel)
        {
            try
            {
                if (data != null)
                {
                    await OnGameRefreshed(provider, game, data, operationId, mode, singleGameId, cancel).ConfigureAwait(false);
                }
            }
            finally
            {
                var totalGames = Math.Max(1, _totalGamesInRun);
                var completedGames = Interlocked.Increment(ref _processedGamesInRun);
                if (completedGames > totalGames)
                {
                    completedGames = totalGames;
                }

                var currentGameId = data?.PlayniteGameId ?? game?.Id ?? singleGameId;
                var gameName = data?.GameName ?? game?.Name;
                var message = BuildRefreshingGameMessage(gameName, completedGames, totalGames);

                Report(
                    message,
                    completedGames,
                    totalGames,
                    operationId: operationId,
                    mode: mode,
                    currentGameId: currentGameId);
            }
        }

        private async Task OnGameRefreshed(
            IDataProvider provider,
            Game game,
            GameAchievementData data,
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId,
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

            TryAutoEnablePointsColumnForPointsProvider(provider, data);

            await PopulateAchievementIconCacheAsync(
                data,
                cancel,
                (downloaded, total) => ReportIconProgress(game, data, downloaded, total, operationId, mode, singleGameId))
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
                NotifyCacheInvalidatedThrottled(force: false);
            }
        }

        private void InitializePointsColumnVisibilityDefaults()
        {
            if (_settings?.Persisted == null)
            {
                return;
            }

            var hasPointsProviderAchievements = HasCachedPointsProviderAchievements();
            var changed = false;
            lock (_pointsColumnVisibilityLock)
            {
                var map = _settings.Persisted.DataGridColumnVisibility;
                if (map == null)
                {
                    map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    _settings.Persisted.DataGridColumnVisibility = map;
                }

                if (!map.TryGetValue(PointsColumnKey, out var pointsVisible))
                {
                    map[PointsColumnKey] = hasPointsProviderAchievements;
                    changed = true;
                }
                else if (pointsVisible && !_settings.Persisted.PointsColumnAutoEnabled)
                {
                    _settings.Persisted.PointsColumnAutoEnabled = true;
                    changed = true;
                }

                if (hasPointsProviderAchievements && !_settings.Persisted.PointsColumnAutoEnabled)
                {
                    _settings.Persisted.PointsColumnAutoEnabled = true;
                    changed = true;
                }
            }

            if (changed)
            {
                TryPersistSettings(notifySettingsSaved: false);
            }
        }

        private void TryAutoEnablePointsColumnForPointsProvider(IDataProvider provider, GameAchievementData data)
        {
            var isPointsProvider = IsEpicProvider(provider, data) || IsRaProvider(provider, data);
            if (!isPointsProvider || data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            var changed = false;
            lock (_pointsColumnVisibilityLock)
            {
                if (_settings?.Persisted == null || _settings.Persisted.PointsColumnAutoEnabled)
                {
                    return;
                }

                var map = _settings.Persisted.DataGridColumnVisibility;
                if (map == null)
                {
                    map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    _settings.Persisted.DataGridColumnVisibility = map;
                }

                _settings.Persisted.PointsColumnAutoEnabled = true;
                changed = true;

                if (!map.TryGetValue(PointsColumnKey, out var pointsVisible) || !pointsVisible)
                {
                    map[PointsColumnKey] = true;
                    changed = true;
                }
            }

            if (changed)
            {
                TryPersistSettings(notifySettingsSaved: true);
            }
        }

        private bool HasCachedPointsProviderAchievements()
        {
            try
            {
                var allGameData = GetAllGameAchievementData();
                if (allGameData == null || allGameData.Count == 0)
                {
                    return false;
                }

                return allGameData.Any(data =>
                    data?.Achievements != null &&
                    data.Achievements.Count > 0 &&
                    (IsEpicProvider(provider: null, data) || IsRaProvider(provider: null, data)));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed while checking cached achievement data for Points-column defaults.");
                return false;
            }
        }

        private static bool IsEpicProvider(IDataProvider provider, GameAchievementData data)
        {
            if (provider != null &&
                string.Equals(provider.ProviderKey, EpicProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ContainsEpicToken(data?.ProviderKey) || ContainsEpicToken(data?.LibrarySourceName);
        }

        private static bool ContainsEpicToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf("epic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRaProvider(IDataProvider provider, GameAchievementData data)
        {
            if (provider != null &&
                string.Equals(provider.ProviderKey, RaProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(data?.ProviderKey) &&
                   data.ProviderKey.IndexOf("RetroAchievements", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void TryPersistSettings(bool notifySettingsSaved)
        {
            try
            {
                _plugin?.SavePluginSettings(_settings);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist Points-column visibility defaults.");
            }

            if (notifySettingsSaved)
            {
                PostToUi(() => PlayniteAchievementsPlugin.NotifySettingsSaved());
            }
        }

        public void PersistSettingsForUi()
        {
            TryPersistSettings(notifySettingsSaved: true);
        }

        private void NotifyCacheInvalidatedThrottled(bool force)
        {
            var nowTimestamp = Stopwatch.GetTimestamp();
            if (!force)
            {
                while (true)
                {
                    var lastTimestamp = Interlocked.Read(ref _lastCacheInvalidationTimestamp);
                    if (lastTimestamp >= 0)
                    {
                        var elapsedMs = (nowTimestamp - lastTimestamp) * 1000L / Stopwatch.Frequency;
                        if (elapsedMs < CacheInvalidationThrottleMs)
                        {
                            return;
                        }
                    }

                    if (Interlocked.CompareExchange(ref _lastCacheInvalidationTimestamp, nowTimestamp, lastTimestamp) == lastTimestamp)
                    {
                        break;
                    }
                }
            }
            else
            {
                Interlocked.Exchange(ref _lastCacheInvalidationTimestamp, nowTimestamp);
            }

            _cacheService.NotifyCacheInvalidated();
        }

        private static bool IsHttpIconPath(string iconPath)
        {
            return !string.IsNullOrWhiteSpace(iconPath) &&
                   (iconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    iconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsLocalIconPath(string iconPath)
        {
            return !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath);
        }

        /// <summary>
        /// Downloads and caches achievement icons for a GameAchievementData object.
        /// Updates icon paths in-place to point to local cached files.
        /// </summary>
        public async Task DownloadAchievementIconsAsync(
            GameAchievementData data,
            CancellationToken cancel = default)
        {
            await PopulateAchievementIconCacheAsync(data, cancel).ConfigureAwait(false);
        }

        private async Task PopulateAchievementIconCacheAsync(
            GameAchievementData data,
            CancellationToken cancel,
            Action<int, int> onIconProgress = null)
        {
            // Track icon progress per game so parallel provider refreshes don't clobber each other.
            var iconProgressTracker = new IconProgressTracker();

            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            var gameIdStr = data.PlayniteGameId?.ToString();
            var groupedByIcon = new Dictionary<string, List<AchievementDetail>>(StringComparer.OrdinalIgnoreCase);

            foreach (var achievement in data.Achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var iconPath = achievement.UnlockedIconPath;
                // Include both HTTP URLs and local file paths
                if (!IsHttpIconPath(iconPath) && !IsLocalIconPath(iconPath))
                {
                    continue;
                }

                if (!groupedByIcon.TryGetValue(iconPath, out var grouped))
                {
                    grouped = new List<AchievementDetail>();
                    groupedByIcon[iconPath] = grouped;
                }

                grouped.Add(achievement);
            }

            if (groupedByIcon.Count == 0)
            {
                return;
            }

            const int iconDecodeSize = 128;

            // Pre-filter icons: compute cache paths once, then check existence
            // This avoids computing SHA256 hash twice per icon
            var cachedIconPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var iconsToProcess = new List<string>();

            foreach (var iconPath in groupedByIcon.Keys)
            {
                var cachedPath = _diskImageService.GetIconCachePathFromUri(iconPath, iconDecodeSize, gameIdStr);
                if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                {
                    cachedIconPaths[iconPath] = cachedPath;
                    continue;
                }
                iconsToProcess.Add(iconPath);
            }

            // Resolve cached icons without progress tracking
            foreach (var kvp in cachedIconPaths)
            {
                if (groupedByIcon.TryGetValue(kvp.Key, out var grouped))
                {
                    foreach (var achievement in grouped)
                    {
                        achievement.UnlockedIconPath = kvp.Value;
                    }
                }
            }

            // If no icons need downloading, we're done
            if (iconsToProcess.Count == 0)
            {
                return;
            }

            // Only track progress for icons that actually need downloading
            iconProgressTracker.IncrementTotal(iconsToProcess.Count);

            var iconTasks = iconsToProcess.Select(async iconPath =>
            {
                var result = await ResolveIconPathAsync(iconPath, gameIdStr, cancel).ConfigureAwait(false);

                // Only emit progress if not cancelled
                if (!cancel.IsCancellationRequested)
                {
                    iconProgressTracker.IncrementDownloaded();

                    var (downloaded, total) = iconProgressTracker.GetSnapshot();
                    onIconProgress?.Invoke(downloaded, total);
                }

                return result;
            }).ToArray();

            var resolvedIconPaths = await Task.WhenAll(iconTasks).ConfigureAwait(false);
            foreach (var resolved in resolvedIconPaths)
            {
                if (string.IsNullOrWhiteSpace(resolved.OriginalPath) ||
                    string.IsNullOrWhiteSpace(resolved.LocalPath))
                {
                    continue;
                }

                if (!groupedByIcon.TryGetValue(resolved.OriginalPath, out var grouped))
                {
                    continue;
                }

                foreach (var achievement in grouped)
                {
                    achievement.UnlockedIconPath = resolved.LocalPath;
                }
            }
        }

        private async Task<(string OriginalPath, string LocalPath)> ResolveIconPathAsync(string originalPath, string gameIdStr, CancellationToken cancel)
        {
            // Must be either HTTP URL or local file path
            if (!IsHttpIconPath(originalPath) && !IsLocalIconPath(originalPath))
            {
                return default;
            }

            const int iconDecodeSize = 128;

            try
            {
                // Check if already cached
                if (_diskImageService.IsIconCached(originalPath, iconDecodeSize, gameIdStr))
                {
                    var cachedPath = _diskImageService.GetIconCachePathFromUri(originalPath, iconDecodeSize, gameIdStr);
                    if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                    {
                        return (originalPath, cachedPath);
                    }
                }

                // Branch based on path type
                string localPath;
                if (IsHttpIconPath(originalPath))
                {
                    localPath = await _diskImageService
                        .GetOrDownloadIconAsync(originalPath, iconDecodeSize, cancel, gameIdStr)
                        .ConfigureAwait(false);
                }
                else
                {
                    localPath = await _diskImageService
                        .GetOrCopyLocalIconAsync(originalPath, iconDecodeSize, cancel, gameIdStr)
                        .ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    return (originalPath, localPath);
                }

                return default;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, string.Format(
                    "Failed to resolve achievement icon path for {0}.",
                    originalPath));
                return default;
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
            CancellationToken externalToken = default)
        {
            return RunManagedAsync(
                mode,
                singleGameId,
                externalToken,
                (operationId, cancel) => RefreshAsync(options, cancel, operationId, mode, singleGameId),
                finalMessage,
                errorLogMessage
            );
        }

        private Task StartManagedGameIdRefreshAsync(
            RefreshModeType mode,
            List<Guid> gameIds,
            Func<RebuildPayload, string> finalMessage,
            string errorLogMessage,
            string emptySelectionLogMessage = null,
            bool bypassExclusions = false,
            CancellationToken externalToken = default)
        {
            if (gameIds == null || gameIds.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(emptySelectionLogMessage))
                {
                    _logger.Info(emptySelectionLogMessage);
                }

                Report(FormatRefreshCompletionWithModeAndCount(mode, 0), 1, 1, mode: mode);
                return Task.CompletedTask;
            }

            return StartManagedRefreshCoreAsync(
                mode,
                new CacheRefreshOptions { PlayniteGameIds = gameIds, IncludeUnplayedGames = true, BypassExclusions = bypassExclusions },
                finalMessage,
                errorLogMessage,
                externalToken: externalToken
            );
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

        private List<Guid> GetInstalledGameIds()
        {
            return _api.Database.Games
                .Where(g => g != null && g.IsInstalled)
                .Select(g => g.Id)
                .ToList();
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

        private List<Guid> GetMissingGameIds(IReadOnlyList<IDataProvider> providerScope = null)
        {
            var authenticatedProviders = (providerScope ?? GetAuthenticatedProviders())
                .Where(provider => provider != null)
                .ToList();
            if (authenticatedProviders.Count == 0)
            {
                _logger.Info("No authenticated platforms available for missing refresh.");
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

                var provider = ResolveProviderForGame(game, authenticatedProviders);
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
                _logger.Info("No games missing achievement data found.");
                return missingGameIds;
            }

            _logger.Info(string.Format(
                "Found {0} games missing achievement data.",
                missingGameIds.Count));
            return missingGameIds;
        }

        private IReadOnlyList<IDataProvider> ResolveCustomProviders(CustomRefreshOptions options)
        {
            var authenticatedProviders = GetAuthenticatedProviders();
            if (authenticatedProviders.Count == 0)
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
                return authenticatedProviders;
            }

            var requestedSet = new HashSet<string>(requestedKeys, StringComparer.OrdinalIgnoreCase);
            return authenticatedProviders
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

        private CustomRefreshResolution ResolveCustomRefresh(CustomRefreshOptions options)
        {
            var resolvedOptions = options?.Clone() ?? new CustomRefreshOptions();
            var providers = ResolveCustomProviders(resolvedOptions);
            var runProvidersInParallel = resolvedOptions.RunProvidersInParallelOverride ?? (_settings?.Persisted?.EnableParallelProviderRefresh ?? true);

            if (providers.Count == 0)
            {
                return new CustomRefreshResolution
                {
                    Providers = Array.Empty<IDataProvider>(),
                    TargetGameIds = Array.Empty<Guid>(),
                    RunProvidersInParallel = runProvidersInParallel
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
                var excludedByUser = _settings?.Persisted?.ExcludedGameIds;
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

            var capableGameIds = resolvedGames
                .Where(game => ResolveProviderForGame(game, providers) != null)
                .Select(game => game.Id)
                .ToList();

            return new CustomRefreshResolution
            {
                Providers = providers,
                TargetGameIds = capableGameIds,
                RunProvidersInParallel = runProvidersInParallel
            };
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
            var resolution = ResolveCustomRefresh(options);
            if (resolution.Providers.Count == 0)
            {
                _logger.Info("Custom refresh aborted: no matching platforms.");
                ShowCustomRefreshMessage(ResourceProvider.GetString("LOCPlayAch_CustomRefresh_NoMatchingProviders"));
                return Task.CompletedTask;
            }

            if (resolution.TargetGameIds.Count == 0)
            {
                _logger.Info("Custom refresh aborted: no matching games.");
                ShowCustomRefreshMessage(ResourceProvider.GetString("LOCPlayAch_CustomRefresh_NoMatchingGames"));
                return Task.CompletedTask;
            }

            return RunManagedAsync(
                RefreshModeType.Custom,
                singleGameId: null,
                externalToken,
                (operationId, cancel) => RefreshAsync(
                    new CacheRefreshOptions
                    {
                        PlayniteGameIds = resolution.TargetGameIds,
                        IncludeUnplayedGames = true,
                        BypassExclusions = true
                    },
                    cancel,
                    operationId,
                    RefreshModeType.Custom,
                    singleGameId: null,
                    providerScope: resolution.Providers,
                    runProvidersInParallelOverride: resolution.RunProvidersInParallel),
                payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.Custom, payload?.Summary?.GamesRefreshed ?? 0),
                "Custom refresh failed.");
        }

        private async Task StartManagedMissingRefreshAsync(CancellationToken externalToken = default)
        {
            var missingGameIds = await Task.Run(() => GetMissingGameIds()).ConfigureAwait(false);
            await StartManagedGameIdRefreshAsync(
                RefreshModeType.Missing,
                missingGameIds,
                payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.Missing, payload?.Summary?.GamesRefreshed ?? 0),
                "Missing games refresh failed.",
                externalToken: externalToken)
                .ConfigureAwait(false);
        }

        private Task StartManagedRebuildAsync(CancellationToken externalToken = default)
        {
            return StartManagedRefreshCoreAsync(
                RefreshModeType.Full,
                FullRefreshOptions(),
                payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.Full, payload?.Summary?.GamesRefreshed ?? 0),
                "Full achievement refresh failed.",
                externalToken: externalToken
            );
        }

        private Task StartManagedSingleGameRefreshAsync(Guid playniteGameId, CancellationToken externalToken = default)
        {
            return StartManagedRefreshCoreAsync(
                RefreshModeType.Single,
                SingleGameOptions(playniteGameId),
                payload => ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete"),
                "Single game refresh failed.",
                playniteGameId,
                externalToken
            );
        }

        private Task StartManagedRecentRefreshAsync(CancellationToken externalToken = default)
        {
            return StartManagedRefreshCoreAsync(
                RefreshModeType.Recent,
                RecentRefreshOptions(),
                payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.Recent, payload?.Summary?.GamesRefreshed ?? 0),
                "Recent refresh failed.",
                externalToken: externalToken
            );
        }

        /// <summary>
        /// Executes a refresh based on the specified refresh mode key.
        /// </summary>
        public Task ExecuteRefreshAsync(string modeKey, Guid? singleGameId = null, CancellationToken externalToken = default)
        {
            // Parse string to enum, default to Recent if invalid
            if (!Enum.TryParse<RefreshModeType>(modeKey, out var mode))
            {
                _logger.Warn(string.Format(
                    "Unknown refresh mode key: {0}. Falling back to Recent.",
                    modeKey));
                mode = RefreshModeType.Recent;
            }

            return ExecuteRefreshAsync(mode, singleGameId, externalToken);
        }

        public Task ExecuteRefreshAsync(RefreshRequest request, CancellationToken externalToken = default)
        {
            request ??= new RefreshRequest();

            if (request.GameIds != null && request.GameIds.Count > 0)
            {
                return ExecuteRefreshForGamesAsync(request.GameIds, externalToken);
            }

            if (request.Mode.HasValue)
            {
                if (request.Mode.Value == RefreshModeType.Custom)
                {
                    return ExecuteRefreshAsync(request.CustomOptions, externalToken);
                }

                return ExecuteRefreshAsync(request.Mode.Value, request.SingleGameId, externalToken);
            }

            if (!string.IsNullOrWhiteSpace(request.ModeKey))
            {
                if (Enum.TryParse(request.ModeKey, out RefreshModeType parsedMode) &&
                    parsedMode == RefreshModeType.Custom)
                {
                    return ExecuteRefreshAsync(request.CustomOptions, externalToken);
                }

                return ExecuteRefreshAsync(request.ModeKey, request.SingleGameId, externalToken);
            }

            return ExecuteRefreshAsync(RefreshModeType.Recent, request.SingleGameId, externalToken);
        }

        public Task ExecuteRefreshForGamesAsync(IEnumerable<Guid> gameIds, CancellationToken externalToken = default)
        {
            var ids = gameIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList() ?? new List<Guid>();

            return StartManagedGameIdRefreshAsync(
                RefreshModeType.LibrarySelected,
                ids,
                payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.LibrarySelected, payload?.Summary?.GamesRefreshed ?? 0),
                "Library selected games refresh failed.",
                "No games selected in Playnite library for refresh.",
                bypassExclusions: true,
                externalToken: externalToken);
        }

        /// <summary>
        /// Executes a refresh based on the specified refresh mode type.
        /// </summary>
        public Task ExecuteRefreshAsync(RefreshModeType mode, Guid? singleGameId = null, CancellationToken externalToken = default)
        {
            switch (mode)
            {
                case RefreshModeType.Recent:
                    return StartManagedRecentRefreshAsync(externalToken);

                case RefreshModeType.Full:
                    return StartManagedRebuildAsync(externalToken);

                case RefreshModeType.Installed:
                    return StartManagedGameIdRefreshAsync(
                        RefreshModeType.Installed,
                        GetInstalledGameIds(),
                        payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.Installed, payload?.Summary?.GamesRefreshed ?? 0),
                        "Installed games refresh failed.",
                        "No installed games found for refresh.",
                        externalToken: externalToken);

                case RefreshModeType.Favorites:
                    return StartManagedGameIdRefreshAsync(
                        RefreshModeType.Favorites,
                        GetFavoriteGameIds(),
                        payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.Favorites, payload?.Summary?.GamesRefreshed ?? 0),
                        "Favorites refresh failed.",
                        "No favorite games found for refresh.",
                        externalToken: externalToken);

                case RefreshModeType.Single:
                    if (singleGameId.HasValue)
                        return StartManagedSingleGameRefreshAsync(singleGameId.Value, externalToken);
                    _logger.Info("Single refresh mode requested but no game ID provided.");
                    return Task.CompletedTask;

                case RefreshModeType.LibrarySelected:
                    return StartManagedGameIdRefreshAsync(
                        RefreshModeType.LibrarySelected,
                        GetLibrarySelectedGameIds(),
                        payload => FormatRefreshCompletionWithModeAndCount(RefreshModeType.LibrarySelected, payload?.Summary?.GamesRefreshed ?? 0),
                        "Library selected games refresh failed.",
                        "No games selected in Playnite library for refresh.",
                        bypassExclusions: true,
                        externalToken: externalToken);

                case RefreshModeType.Missing:
                    return StartManagedMissingRefreshAsync(externalToken);

                case RefreshModeType.Custom:
                    return ExecuteRefreshAsync(options: null, externalToken: externalToken);

                default:
                    _logger.Warn(string.Format(
                        "Unknown refresh mode: {0}. Falling back to Recent.",
                        mode));
                    return StartManagedRecentRefreshAsync(externalToken);
            }
        }

        public void CancelCurrentRebuild()
        {
            _logger.Info("Cancel refresh requested.");
            lock (_runLock)
            {
                _activeRunCts?.Cancel();
            }
        }

        public void RemoveGameCache(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            try
            {
                _cacheService.RemoveGameData(playniteGameId);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to clear achievement cache for game '{playniteGameId}'.");
            }

            try
            {
                _diskImageService.ClearGameCache(playniteGameId.ToString());
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to remove icon cache for game '{playniteGameId}'.");
            }

            NotifyCacheInvalidatedThrottled(force: true);
        }

        public CacheWriteResult SetCapstone(Guid playniteGameId, string capstoneApiName)
        {
            if (playniteGameId == Guid.Empty)
            {
                return CacheWriteResult.CreateFailure(
                    string.Empty,
                    "invalid_game_id",
                    ResourceProvider.GetString("LOCPlayAch_Capstone_Error_InvalidGame"));
            }

            try
            {
                // Manual capstones are stored only in settings and applied as an overlay
                // via ApplyUserPreferences() when loading cached data
                if (string.IsNullOrWhiteSpace(capstoneApiName))
                {
                    _settings.Persisted.ManualCapstones.Remove(playniteGameId);
                }
                else
                {
                    _settings.Persisted.ManualCapstones[playniteGameId] = capstoneApiName.Trim();
                }

                TryPersistSettings(notifySettingsSaved: true);
                NotifyCacheInvalidatedThrottled(force: true);

                // Notify that game data changed for tag syncing
                GameDataChanged?.Invoke(new List<Guid> { playniteGameId });

                return CacheWriteResult.CreateSuccess(playniteGameId.ToString(), DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed setting capstone for gameId={playniteGameId}.");
                return CacheWriteResult.CreateFailure(
                    playniteGameId.ToString(),
                    "settings_save_failed",
                    ex.Message,
                    ex);
            }
        }

        public void SetAchievementOrderOverride(Guid gameId, IReadOnlyList<string> orderedApiNames)
        {
            if (gameId == Guid.Empty || _settings?.Persisted == null)
            {
                return;
            }

            var normalizedOrder = AchievementOrderHelper.NormalizeApiNames(orderedApiNames);
            var updated = _settings.Persisted.AchievementOrderOverrides != null
                ? _settings.Persisted.AchievementOrderOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null ? new List<string>(kvp.Value) : new List<string>())
                : new Dictionary<Guid, List<string>>();

            if (normalizedOrder.Count == 0)
            {
                updated.Remove(gameId);
            }
            else
            {
                updated[gameId] = normalizedOrder;
            }

            _settings.Persisted.AchievementOrderOverrides = updated;
            TryPersistSettings(notifySettingsSaved: true);
            NotifyCacheInvalidatedThrottled(force: true);
        }

        public void SetAchievementCategoryOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryOverrides)
        {
            if (gameId == Guid.Empty || _settings?.Persisted == null)
            {
                return;
            }

            var normalizedCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (categoryOverrides != null)
            {
                foreach (var pair in categoryOverrides)
                {
                    var apiName = (pair.Key ?? string.Empty).Trim();
                    var category = (pair.Value ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                    {
                        continue;
                    }

                    normalizedCategories[apiName] = category;
                }
            }

            var updated = _settings.Persisted.AchievementCategoryOverrides != null
                ? _settings.Persisted.AchievementCategoryOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null
                        ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                : new Dictionary<Guid, Dictionary<string, string>>();

            if (normalizedCategories.Count == 0)
            {
                updated.Remove(gameId);
            }
            else
            {
                updated[gameId] = normalizedCategories;
            }

            _settings.Persisted.AchievementCategoryOverrides = updated;
            TryPersistSettings(notifySettingsSaved: true);
            NotifyCacheInvalidatedThrottled(force: true);
        }

        public void SetAchievementCategoryTypeOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            if (gameId == Guid.Empty || _settings?.Persisted == null)
            {
                return;
            }

            var normalizedCategoryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (categoryTypeOverrides != null)
            {
                foreach (var pair in categoryTypeOverrides)
                {
                    var apiName = (pair.Key ?? string.Empty).Trim();
                    var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                    if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(categoryType))
                    {
                        continue;
                    }

                    normalizedCategoryTypes[apiName] = categoryType;
                }
            }

            var updated = _settings.Persisted.AchievementCategoryTypeOverrides != null
                ? _settings.Persisted.AchievementCategoryTypeOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null
                        ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                : new Dictionary<Guid, Dictionary<string, string>>();

            if (normalizedCategoryTypes.Count == 0)
            {
                updated.Remove(gameId);
            }
            else
            {
                updated[gameId] = normalizedCategoryTypes;
            }

            _settings.Persisted.AchievementCategoryTypeOverrides = updated;
            TryPersistSettings(notifySettingsSaved: true);
            NotifyCacheInvalidatedThrottled(force: true);
        }

        /// <summary>
        /// Sets the ExcludedByUser flag for a game.
        /// This excludes the game from future achievement tracking until re-included.
        /// The exclusion is persisted in settings and survives cache clears.
        /// </summary>
        public void SetExcludedByUser(Guid playniteGameId, bool excluded)
        {
            SetExcludedByUser(playniteGameId, excluded, clearCachedDataWhenExcluding: true);
        }

        /// <summary>
        /// Sets the ExcludedByUser flag for a game and optionally clears cached data when excluding.
        /// </summary>
        public void SetExcludedByUser(Guid playniteGameId, bool excluded, bool clearCachedDataWhenExcluding)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            // Update settings (survives cache clear)
            if (excluded)
            {
                _settings.Persisted.ExcludedGameIds.Add(playniteGameId);
                if (clearCachedDataWhenExcluding)
                {
                    _cacheService.RemoveGameData(playniteGameId);
                }
            }
            else
            {
                _settings.Persisted.ExcludedGameIds.Remove(playniteGameId);
            }

            TryPersistSettings(notifySettingsSaved: true);
            NotifyCacheInvalidatedThrottled(force: true);

            // Notify that game data changed for tag syncing
            GameDataChanged?.Invoke(new List<Guid> { playniteGameId });
        }

        /// <summary>
        /// Applies tracking exclusion directly from Playnite's hidden state.
        /// Hiding excludes the game and clears cached data; unhiding includes it again.
        /// </summary>
        public void SetExcludedFromHiddenState(Guid playniteGameId, bool hidden)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            if (hidden)
            {
                _settings.Persisted.ExcludedGameIds.Add(playniteGameId);
                _cacheService.RemoveGameData(playniteGameId);
            }
            else
            {
                _settings.Persisted.ExcludedGameIds.Remove(playniteGameId);
            }

            TryPersistSettings(notifySettingsSaved: true);
            NotifyCacheInvalidatedThrottled(force: true);

            // Notify that game data changed for tag syncing
            GameDataChanged?.Invoke(new List<Guid> { playniteGameId });
        }

        /// <summary>
        /// Sets whether a game is excluded from summary surfaces (sidebar and all-games snapshots).
        /// The exclusion is persisted in settings and survives cache clears.
        /// </summary>
        public void SetExcludedFromSummaries(Guid playniteGameId, bool excluded)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            if (excluded)
            {
                _settings.Persisted.ExcludedFromSummariesGameIds.Add(playniteGameId);
            }
            else
            {
                _settings.Persisted.ExcludedFromSummariesGameIds.Remove(playniteGameId);
            }

            TryPersistSettings(notifySettingsSaved: true);
            NotifyCacheInvalidatedThrottled(force: true);

            // Notify that game data changed for tag syncing
            GameDataChanged?.Invoke(new List<Guid> { playniteGameId });
        }

        // -----------------------------
        // Get combined achievement data
        // -----------------------------

        /// <summary>
        /// Gets combined achievement data (schema + unlocked) for a single game.
        /// </summary>
        public GameAchievementData GetGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
                return null;

            try
            {
                var data = _cacheService.LoadGameData(playniteGameId);
                _hydrator.Hydrate(data);
                return data;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        /// <summary>
        /// Gets raw cached achievement data for a single game without hydration overlays.
        /// Used by Game Options tooling that needs to distinguish provider/base values
        /// from runtime setting overlays.
        /// </summary>
        public GameAchievementData GetRawGameAchievementData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                return _cacheService.LoadGameData(playniteGameId.ToString());
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        /// <summary>
        /// Gets combined achievement data (schema + unlocked) for a single game by Playnite Game ID.
        /// </summary>
        public GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GetGameAchievementData(playniteGameId.ToString());
        }

        /// <summary>
        /// Gets combined achievement data for all cached games.
        /// </summary>
        public List<GameAchievementData> GetAllGameAchievementData()
        {
            try
            {
                List<GameAchievementData> result;
                if (_cacheService is CacheManager optimizedCacheManager)
                {
                    result = optimizedCacheManager.LoadAllGameDataFast() ?? new List<GameAchievementData>();
                }
                else
                {
                    var gameIds = _cacheService.GetCachedGameIds();
                    result = new List<GameAchievementData>();
                    foreach (var gameId in gameIds)
                    {
                        var gameData = _cacheService.LoadGameData(gameId);
                        if (gameData != null)
                        {
                            result.Add(gameData);
                        }
                    }
                }

                // Hydrate with non-persisted properties from settings and Playnite DB
                _hydrator.HydrateAll(result);

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all achievement data");
                return new();
            }
        }

    }
}

