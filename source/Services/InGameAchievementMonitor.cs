using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using PlayniteAchievements.Services.InGameMonitoring;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Services
{
    internal sealed class InGameAchievementMonitor : IDisposable
    {
        private const int StartupDelaySeconds = 20;
        private static readonly TimeSpan SchedulerResolution = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan FileDebounce = TimeSpan.FromMilliseconds(150);
        private static readonly int[] StableReadRetryMilliseconds = { 100, 250, 500, 1000 };

        private sealed class FriendPollTarget
        {
            public string ProviderKey { get; set; }
            public FriendIdentity Friend { get; set; }
            public int AppId { get; set; }
            public string ProviderGameKey { get; set; }
            public Guid? PlayniteGameId { get; set; }
            public string GameName { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly ICacheManager _cacheManager;
        private readonly IFriendCacheManager _friendCache;
        private readonly RefreshRuntime _refreshRuntime;
        private readonly Func<RefreshRequest, RefreshExecutionPolicy, Task> _executeRefreshAsync;
        private sealed class GamePollState
        {
            public GamePollState()
            {
                SessionToken = SessionCancellation.Token;
            }

            public Game Game;
            public DateTime SessionStartUtc;
            public DateTime FirstPollUtc;
            public DateTime NextFriendDueUtc;
            public DateTime RecoveryCooldownUtc;
            public IDataProvider Provider;
            public IInGameProgressSource ProgressSource;
            public InGameProgressRegistration Registration;
            public GameAchievementData CachedSchema;
            public bool QueryInFlight;
            public bool FriendInFlight;
            public bool ForceFallback;
            public int Generation;
            public int FriendCursor;
            public readonly CancellationTokenSource SessionCancellation = new CancellationTokenSource();
            public readonly CancellationToken SessionToken;
            public readonly InGameReadSchedule Schedule = new InGameReadSchedule();
            public readonly List<IDisposable> WatchSubscriptions = new List<IDisposable>();
            public readonly HashSet<string> ToastedUserKeys =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, HashSet<string>> ToastedFriendKeys =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<FriendAchievementRow>> FriendBaselines =
                new Dictionary<string, List<FriendAchievementRow>>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly Action<AchievementUnlockedEventArgs> _notifyUnlocked;
        private readonly AchievementUnlockDiffer _differ;
        private readonly IInGameProgressCacheWriter _progressWriter;
        private readonly ExactFileWatcherPool _watchers;
        private readonly object _stateLock = new object();
        private readonly SemaphoreSlim _tickSemaphore = new SemaphoreSlim(1, 1);
        private readonly Dictionary<Guid, GamePollState> _games = new Dictionary<Guid, GamePollState>();
        private readonly HashSet<IInGameProgressSource> _sourcesInFlight =
            new HashSet<IInGameProgressSource>();

        private CancellationTokenSource _cts;
        private Task _loopTask;
        private int _fallbackInFlight;

        public event Action<Guid> ProgressApplied;

        public InGameAchievementMonitor(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            ICacheManager cacheManager,
            RefreshRuntime refreshRuntime,
            Func<RefreshRequest, RefreshExecutionPolicy, Task> executeRefreshAsync,
            Action<AchievementUnlockedEventArgs> notifyUnlocked,
            AchievementUnlockDiffer differ = null)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _cacheManager = cacheManager;
            _friendCache = cacheManager as IFriendCacheManager;
            _refreshRuntime = refreshRuntime;
            _executeRefreshAsync = executeRefreshAsync ?? throw new ArgumentNullException(nameof(executeRefreshAsync));
            _notifyUnlocked = notifyUnlocked;
            _differ = differ ?? new AchievementUnlockDiffer();
            _progressWriter = cacheManager as IInGameProgressCacheWriter;
            _watchers = new ExactFileWatcherPool(logger);
        }

        public IReadOnlyList<Game> RunningGames
        {
            get
            {
                lock (_stateLock)
                {
                    return _games.Values.Select(state => state.Game).ToList();
                }
            }
        }

        public void Start(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            if (!ShouldPollGame(game, logReason: true))
            {
                return;
            }

            GamePollState state;
            lock (_stateLock)
            {
                if (_games.TryGetValue(game.Id, out var existing))
                {
                    existing.Game = game;
                    return;
                }

                var now = DateTime.UtcNow;
                state = new GamePollState
                {
                    Game = game,
                    SessionStartUtc = now,
                    FirstPollUtc = now.AddSeconds(StartupDelaySeconds),
                    NextFriendDueUtc = now.AddSeconds(StartupDelaySeconds).Add(GetFriendInterval())
                };
                _games[game.Id] = state;

            }

            if (!ConfigureState(state, preservePrimeWhenEquivalent: false))
            {
                Stop(game);
                _logger?.Debug($"[InGameMonitor] Skipped: no effective provider for {game.Name}.");
                return;
            }

            lock (_stateLock)
            {
                if (_games.TryGetValue(game.Id, out var tracked) &&
                    ReferenceEquals(state, tracked) &&
                    _cts == null)
                {
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    _loopTask = Task.Run(() => PollLoopAsync(token), token);
                }
            }

            _logger?.Info(
                $"[InGameMonitor] Started for {game.Name}; provider={state.Provider?.ProviderKey ?? "none"}, " +
                $"mode={(state.ProgressSource == null ? "fallback" : state.Registration?.IsRemote == true ? "feed" : "file")}.");
        }

        public void Stop(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            CancellationTokenSource cts = null;
            CancellationTokenSource sessionCancellation = null;
            List<IDisposable> subscriptions = null;
            lock (_stateLock)
            {
                if (!_games.TryGetValue(game.Id, out var state))
                {
                    return;
                }

                state.Generation++;
                sessionCancellation = state.SessionCancellation;
                subscriptions = state.WatchSubscriptions.ToList();
                state.WatchSubscriptions.Clear();
                _games.Remove(game.Id);
                if (_games.Count == 0)
                {
                    cts = _cts;
                    _cts = null;
                    _loopTask = null;
                }
            }

            sessionCancellation?.Cancel();
            DisposeSubscriptions(subscriptions);
            _logger?.Info($"[InGameMonitor] Stopped for {game.Name}.");
            cts?.Cancel();
            sessionCancellation?.Dispose();
            cts?.Dispose();
        }

        public void StopAll()
        {
            CancellationTokenSource cts;
            List<CancellationTokenSource> sessionCancellations;
            List<IDisposable> subscriptions;
            lock (_stateLock)
            {
                sessionCancellations = _games.Values
                    .Select(state => state.SessionCancellation)
                    .ToList();
                subscriptions = _games.Values
                    .SelectMany(state =>
                    {
                        state.Generation++;
                        return state.WatchSubscriptions;
                    })
                    .ToList();
                _games.Clear();
                cts = _cts;
                _cts = null;
                _loopTask = null;
            }

            foreach (var cancellation in sessionCancellations)
            {
                cancellation.Cancel();
            }
            DisposeSubscriptions(subscriptions);
            cts?.Cancel();
            foreach (var cancellation in sessionCancellations)
            {
                cancellation.Dispose();
            }
            cts?.Dispose();
        }

        public void Reconfigure()
        {
            if (_settings?.Persisted?.EnableInGamePolling != true)
            {
                StopAll();
                return;
            }

            List<GamePollState> states;
            lock (_stateLock)
            {
                states = _games.Values.ToList();
            }

            foreach (var state in states)
            {
                if (!ShouldPollGame(state.Game, logReason: false))
                {
                    Stop(state.Game);
                    continue;
                }

                if (!ConfigureState(state, preservePrimeWhenEquivalent: true))
                {
                    Stop(state.Game);
                }
            }
        }

        private bool IsTracked(Guid gameId)
        {
            lock (_stateLock)
            {
                return _games.ContainsKey(gameId);
            }
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _tickSemaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await RunTickAsync(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _tickSemaphore.Release();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[InGameMonitor] Scheduler tick failed.");
                }

                try
                {
                    await Task.Delay(SchedulerResolution, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }

            _logger?.Info("[InGameMonitor] Scheduler exited.");
        }

        private Task RunTickAsync(CancellationToken token)
        {
            List<IGrouping<IInGameProgressSource, GamePollState>> sourceGroups;
            List<GamePollState> fallbackStates;
            List<GamePollState> friendStates;
            lock (_stateLock)
            {
                var now = DateTime.UtcNow;
                var dueSources = _games.Values
                    .Where(state =>
                        state.ProgressSource != null &&
                        !state.ForceFallback &&
                        !_sourcesInFlight.Contains(state.ProgressSource) &&
                        !state.QueryInFlight &&
                        state.Schedule.NextDueUtc <= now)
                    .ToList();
                foreach (var state in dueSources)
                {
                    state.QueryInFlight = true;
                    state.Schedule.BeginRead();
                }

                sourceGroups = dueSources
                    .GroupBy(state => state.ProgressSource)
                    .ToList();
                foreach (var group in sourceGroups)
                {
                    _sourcesInFlight.Add(group.Key);
                }

                fallbackStates = _games.Values
                    .Where(state =>
                        (state.ProgressSource == null || state.ForceFallback) &&
                        state.FirstPollUtc <= now &&
                        state.Schedule.NextDueUtc <= now)
                    .ToList();
                foreach (var state in fallbackStates)
                {
                    state.Schedule.DueAt(now.Add(GetPollInterval()));
                }

                friendStates = _games.Values
                    .Where(state =>
                        !state.FriendInFlight &&
                        state.NextFriendDueUtc <= now &&
                        ShouldRunFriendRefresh())
                    .ToList();
                foreach (var state in friendStates)
                {
                    state.FriendInFlight = true;
                    state.NextFriendDueUtc = now.Add(GetFriendInterval());
                }
            }

            foreach (var group in sourceGroups)
            {
                _ = RunProgressSourceBatchAsync(
                    group.Key,
                    group.ToList(),
                    token);
            }

            if (fallbackStates.Count > 0 &&
                Interlocked.CompareExchange(ref _fallbackInFlight, 1, 0) == 0)
            {
                _ = RunFallbackBatchAsync(fallbackStates, token);
            }
            else if (fallbackStates.Count > 0)
            {
                lock (_stateLock)
                {
                    foreach (var state in fallbackStates)
                    {
                        if (_games.TryGetValue(state.Game.Id, out var tracked) &&
                            ReferenceEquals(state, tracked))
                        {
                            state.Schedule.DueAt(DateTime.UtcNow.AddMilliseconds(250));
                        }
                    }
                }
            }

            foreach (var state in friendStates)
            {
                _ = RunFriendDueAsync(state, token);
            }

            return Task.CompletedTask;
        }

        private async Task RunProgressSourceBatchAsync(
            IInGameProgressSource source,
            IReadOnlyList<GamePollState> states,
            CancellationToken token)
        {
            var generations = states.ToDictionary(state => state.Game.Id, state => state.Generation);
            var contexts = states.Select(state => new InGameTrackingContext
            {
                Game = state.Game,
                CachedSchema = state.CachedSchema,
                Registration = state.Registration,
                SessionStartUtc = state.SessionStartUtc
            }).ToList();

            IReadOnlyList<InGameProgressQueryResult> results;
            var timer = Stopwatch.StartNew();
            using (var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                new[] { token }.Concat(states.Select(state => state.SessionToken)).ToArray()))
            try
            {
                results = await source.QueryAsync(contexts, queryCancellation.Token).ConfigureAwait(false) ??
                          Array.Empty<InGameProgressQueryResult>();
            }
            catch (OperationCanceledException) when (queryCancellation.IsCancellationRequested)
            {
                CompleteSourceQuery(
                    source,
                    states,
                    retryActiveImmediately: !token.IsCancellationRequested);
                return;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[InGameMonitor] Fast progress query failed.");
                results = states
                    .Select(state => InGameProgressQueryResult.Failed(state.Game.Id, "query_failed"))
                    .ToList();
            }
            finally
            {
                timer.Stop();
            }

            try
            {
                var byGame = results
                    .Where(result => result != null && result.GameId != Guid.Empty)
                    .GroupBy(result => result.GameId)
                    .ToDictionary(group => group.Key, group => group.Last());
                foreach (var state in states)
                {
                    if (!byGame.TryGetValue(state.Game.Id, out var result))
                    {
                        result = InGameProgressQueryResult.Failed(state.Game.Id, "result_missing");
                    }

                    if (!IsTracked(state.Game.Id))
                    {
                        continue;
                    }

                    lock (_stateLock)
                    {
                        if (!_games.TryGetValue(state.Game.Id, out var tracked) ||
                            !ReferenceEquals(state, tracked) ||
                            !generations.TryGetValue(state.Game.Id, out var generation) ||
                            state.Generation != generation)
                        {
                            continue;
                        }
                    }

                    if (!result.Success)
                    {
                        ScheduleSourceFailure(state, result.FailureReason);
                        continue;
                    }

                    ApplyProgressResult(state, result, timer.ElapsedMilliseconds);
                }
            }
            finally
            {
                CompleteSourceQuery(source, states, retryActiveImmediately: false);
            }
        }

        private void CompleteSourceQuery(
            IInGameProgressSource source,
            IEnumerable<GamePollState> states,
            bool retryActiveImmediately)
        {
            lock (_stateLock)
            {
                _sourcesInFlight.Remove(source);
                foreach (var state in states ?? Enumerable.Empty<GamePollState>())
                {
                    if (_games.TryGetValue(state.Game.Id, out var tracked) &&
                        ReferenceEquals(state, tracked))
                    {
                        state.QueryInFlight = false;
                        if (retryActiveImmediately)
                        {
                            state.Schedule.DueAt(DateTime.UtcNow);
                        }
                    }
                }
            }
        }

        private void ApplyProgressResult(
            GamePollState state,
            InGameProgressQueryResult query,
            long elapsedMilliseconds)
        {
            var before = _cacheManager?.LoadGameData(state.Game.Id.ToString());
            var write = _progressWriter?.ApplyInGameProgress(
                state.Game.Id.ToString(),
                state.Provider?.ProviderKey,
                query.Achievements);
            if (write?.Success != true)
            {
                ScheduleSourceFailure(state, write?.ErrorCode ?? "writer_unavailable");
                return;
            }

            var after = _cacheManager?.LoadGameData(state.Game.Id.ToString()) ?? before;
            bool emitUnlocks;
            DateTime eventUtc;
            lock (_stateLock)
            {
                if (!_games.TryGetValue(state.Game.Id, out var tracked) ||
                    !ReferenceEquals(state, tracked))
                {
                    return;
                }

                emitUnlocks = state.Schedule.ShouldEmitUnlocks();
                state.Schedule.Succeeded(
                    DateTime.UtcNow,
                    state.Registration?.PollInterval ?? TimeSpan.FromSeconds(60));
                state.CachedSchema = after;
                eventUtc = state.Schedule.LastFileEventUtc;

                if (write.UnmatchedKeys.Count > 0 &&
                    DateTime.UtcNow >= state.RecoveryCooldownUtc)
                {
                    state.ForceFallback = true;
                    state.RecoveryCooldownUtc = DateTime.UtcNow.AddMinutes(2);
                    state.Schedule.DueAt(state.FirstPollUtc > DateTime.UtcNow
                        ? state.FirstPollUtc
                        : DateTime.UtcNow);
                }
            }

            if (write.Changed)
            {
                ProgressApplied?.Invoke(state.Game.Id);
            }

            AchievementUnlockedEventArgs completion = null;
            if (emitUnlocks && write.NewlyUnlockedKeys.Count > 0)
            {
                completion = EmitUserUnlocks(
                    state,
                    before,
                    after,
                    write.NewlyUnlockedKeys,
                    elapsedMilliseconds);
            }

            if (completion != null)
            {
                _notifyUnlocked?.Invoke(completion);
            }

            var totalLatencyMs = eventUtc == default
                ? elapsedMilliseconds
                : Math.Max(0, (long)(DateTime.UtcNow - eventUtc).TotalMilliseconds);
            _logger?.Debug(
                $"[InGameMonitor] Progress applied: game={state.Game.Name}, provider={state.Provider?.ProviderKey}, " +
                $"observed={query.Achievements.Count}, new={write.NewlyUnlockedKeys.Count}, " +
                $"unmatched={write.UnmatchedKeys.Count}, latencyMs={totalLatencyMs}.");
        }

        private void ScheduleSourceFailure(GamePollState state, string reason)
        {
            lock (_stateLock)
            {
                if (!_games.TryGetValue(state.Game.Id, out var tracked) ||
                    !ReferenceEquals(state, tracked))
                {
                    return;
                }

                state.Schedule.Failed(
                    DateTime.UtcNow,
                    StableReadRetryMilliseconds,
                    GetPollInterval());
            }

            _logger?.Debug(
                $"[InGameMonitor] Progress read deferred for {state.Game?.Name}: {reason ?? "unknown"}.");
        }

        private bool ConfigureState(GamePollState state, bool preservePrimeWhenEquivalent)
        {
            if (state?.Game == null)
            {
                return false;
            }

            IDataProvider provider;
            try
            {
                provider = _refreshRuntime?.ResolveInGameProvider(state.Game);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[InGameMonitor] Provider resolution failed for {state.Game.Name}.");
                provider = null;
            }

            if (provider == null)
            {
                return false;
            }

            var cached = _cacheManager?.LoadGameData(state.Game.Id.ToString());
            IInGameProgressSource progressSource = null;
            InGameProgressRegistration registration = null;
            if (_progressWriter != null &&
                provider is IInGameProgressSource candidate &&
                cached?.Achievements?.Count > 0 &&
                string.Equals(cached.ProviderKey, provider.ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    registration = candidate.TryRegister(state.Game, cached);
                    if (registration != null &&
                        (registration.IsRemote || registration.WatchTargets?.Count > 0))
                    {
                        progressSource = candidate;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[InGameMonitor] Fast-source registration failed for {state.Game.Name}.");
                    registration = null;
                }
            }

            var nextTargets = NormalizeTargets(registration?.WatchTargets);
            var previousTargets = NormalizeTargets(state.Registration?.WatchTargets);
            var equivalent =
                preservePrimeWhenEquivalent &&
                string.Equals(state.Provider?.ProviderKey, provider.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(state.ProgressSource, progressSource) &&
                previousTargets.SequenceEqual(nextTargets, StringComparer.OrdinalIgnoreCase) &&
                state.Registration?.IsRemote == registration?.IsRemote;

            List<IDisposable> oldSubscriptions = null;
            int generation;
            lock (_stateLock)
            {
                if (!_games.TryGetValue(state.Game.Id, out var tracked) ||
                    !ReferenceEquals(state, tracked))
                {
                    return false;
                }

                if (!equivalent)
                {
                    state.Generation++;
                }
                generation = state.Generation;
                state.Provider = provider;
                state.ProgressSource = progressSource;
                state.Registration = registration;
                state.CachedSchema = cached;
                state.ForceFallback = false;
                if (!equivalent)
                {
                    oldSubscriptions = state.WatchSubscriptions.ToList();
                    state.WatchSubscriptions.Clear();
                }

                var now = DateTime.UtcNow;
                state.Schedule.Configure(
                    now,
                    state.FirstPollUtc,
                    progressSource != null,
                    registration?.IsRemote == true,
                    equivalent);
                var configuredFriendDue = now.Add(GetFriendInterval());
                if (state.NextFriendDueUtc == default || state.NextFriendDueUtc > configuredFriendDue)
                {
                    state.NextFriendDueUtc = configuredFriendDue;
                }
            }

            DisposeSubscriptions(oldSubscriptions);
            if (!equivalent && progressSource != null && registration?.IsRemote != true)
            {
                var subscriptions = new List<IDisposable>();
                foreach (var target in nextTargets)
                {
                    var subscription = _watchers.Subscribe(
                        target,
                        (path, watcherError) => OnFileSignal(
                            state.Game.Id,
                            generation,
                            path,
                            watcherError));
                    if (subscription != null)
                    {
                        subscriptions.Add(subscription);
                    }
                }

                lock (_stateLock)
                {
                    if (_games.TryGetValue(state.Game.Id, out var tracked) &&
                        ReferenceEquals(state, tracked) &&
                        state.Generation == generation)
                    {
                        state.WatchSubscriptions.AddRange(subscriptions);
                        state.Schedule.SourceAttached(DateTime.UtcNow);
                        subscriptions = null;
                    }
                }

                DisposeSubscriptions(subscriptions);
            }

            _logger?.Debug(
                $"[InGameMonitor] Configured game={state.Game.Name}, provider={provider.ProviderKey}, " +
                $"source={(progressSource == null ? "fallback" : registration.IsRemote ? "feed" : "file")}, " +
                $"targets={nextTargets.Count}.");
            return true;
        }

        private void OnFileSignal(Guid gameId, int generation, string path, bool watcherError)
        {
            lock (_stateLock)
            {
                if (!_games.TryGetValue(gameId, out var state) ||
                    state.Generation != generation ||
                    state.ProgressSource == null)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                state.Schedule.SignalFile(now, watcherError, FileDebounce);
            }

            _logger?.Debug(
                $"[InGameMonitor] {(watcherError ? "Watcher recovery" : "File change")} queued: {path}.");
        }

        private async Task RunFallbackBatchAsync(
            IReadOnlyList<GamePollState> states,
            CancellationToken token)
        {
            try
            {
                if (_refreshRuntime?.IsRebuilding == true)
                {
                    lock (_stateLock)
                    {
                        foreach (var state in states)
                        {
                            state.Schedule.DueAt(DateTime.UtcNow.AddSeconds(1));
                        }
                    }
                    return;
                }

                foreach (var providerGroup in states
                    .Where(state => state.Provider != null)
                    .GroupBy(state => state.Provider.ProviderKey, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var batch = providerGroup.ToList();
                    var beforeByGame = batch.ToDictionary(
                        state => state.Game.Id,
                        state => _cacheManager?.LoadGameData(state.Game.Id.ToString()));
                    var gameIds = batch.Select(state => state.Game.Id).ToList();
                    var timer = Stopwatch.StartNew();
                    await _executeRefreshAsync(
                        new RefreshRequest
                        {
                            GameIds = gameIds,
                            Options = new RefreshOptions
                            {
                                Subjects = RefreshSubjects.CurrentUser,
                                Scope = RefreshGameScope.Explicit,
                                ProviderKeys = new[] { providerGroup.Key },
                                PlayniteGameIds = gameIds,
                                PreferCachedDefinitions = true,
                                RespectUserExclusions = true
                            }
                        },
                        new RefreshExecutionPolicy
                        {
                            ValidateAuthentication = false,
                            UseProgressWindow = false,
                            SwallowExceptions = true,
                            ExternalCancellationToken = token,
                            ErrorLogMessage = "[InGameMonitor] Provider fallback refresh failed."
                        }).ConfigureAwait(false);
                    timer.Stop();

                    foreach (var state in batch)
                    {
                        if (!IsTracked(state.Game.Id))
                        {
                            continue;
                        }

                        var before = beforeByGame[state.Game.Id];
                        var after = _cacheManager?.LoadGameData(state.Game.Id.ToString());
                        var keys = _differ.DiffUserUnlocks(before, after)
                            .Where(achievement =>
                                achievement != null &&
                                !string.IsNullOrWhiteSpace(achievement.ApiName) &&
                                (state.Schedule.Primed ||
                                 (achievement.UnlockTimeUtc.HasValue &&
                                  achievement.UnlockTimeUtc.Value.ToUniversalTime() >= state.SessionStartUtc)))
                            .Select(achievement => achievement.ApiName)
                            .ToList();

                        lock (_stateLock)
                        {
                            state.ForceFallback = false;
                            state.CachedSchema = after;
                            state.Schedule.MarkFallbackSuccess(DateTime.UtcNow, GetPollInterval());
                        }

                        var completion = keys.Count == 0
                            ? null
                            : EmitUserUnlocks(state, before, after, keys, timer.ElapsedMilliseconds);
                        if (completion != null)
                        {
                            _notifyUnlocked?.Invoke(completion);
                        }

                        if (state.ProgressSource == null && after?.Achievements?.Count > 0)
                        {
                            ConfigureState(state, preservePrimeWhenEquivalent: true);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[InGameMonitor] Fallback batch failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _fallbackInFlight, 0);
            }
        }

        private async Task RunFriendDueAsync(GamePollState state, CancellationToken token)
        {
            try
            {
                var completions = await RunFriendTickAsync(state, token).ConfigureAwait(false);
                if (!IsTracked(state.Game.Id))
                {
                    return;
                }

                foreach (var completion in completions)
                {
                    _notifyUnlocked?.Invoke(completion);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[InGameMonitor] Friend refresh failed for {state.Game?.Name}.");
            }
            finally
            {
                lock (_stateLock)
                {
                    if (_games.TryGetValue(state.Game.Id, out var tracked) &&
                        ReferenceEquals(state, tracked))
                    {
                        state.FriendInFlight = false;
                    }
                }
            }
        }

        private static List<string> NormalizeTargets(IReadOnlyList<string> targets)
        {
            return (targets ?? Array.Empty<string>())
                .Select(ExactFileWatcherPool.Normalize)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void DisposeSubscriptions(IEnumerable<IDisposable> subscriptions)
        {
            foreach (var subscription in subscriptions ?? Enumerable.Empty<IDisposable>())
            {
                try
                {
                    subscription?.Dispose();
                }
                catch
                {
                }
            }
        }

        private AchievementUnlockedEventArgs EmitUserUnlocks(
            GamePollState state,
            GameAchievementData before,
            GameAchievementData after,
            IReadOnlyList<string> allowedKeys,
            long elapsedMs)
        {
            var game = state.Game;
            HydrateForToast(after);
            var allowed = new HashSet<string>(
                allowedKeys ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var unlocks = (after?.Achievements ?? new List<AchievementDetail>())
                .Where(achievement =>
                    achievement?.Unlocked == true &&
                    !string.IsNullOrWhiteSpace(achievement.ApiName) &&
                    allowed.Contains(achievement.ApiName) &&
                    state.ToastedUserKeys.Add(achievement.ApiName))
                .Where(a => a?.IsFiltered != true)
                .ToList();
            _logger?.Debug(
                $"[InGameMonitor] User progress complete: game={game.Name}, elapsedMs={elapsedMs}, unlocks={unlocks.Count}.");

            // This batch completes the game when the data crossed from incomplete to complete
            // (all unlocked, or the capstone unlocked) with at least one new unlock in hand.
            var completesGame = unlocks.Count > 0 && before?.IsCompleted != true && after?.IsCompleted == true;

            // The single unlock that finished the game: the newly-unlocked capstone (a capstone
            // unlock alone marks completion), otherwise the last achievement to unlock (100%
            // reached). Null when this batch does not complete the game — so a regular unlock
            // landing after the game was already complete is never flagged as the completion.
            var completingApiName = completesGame ? ResolveCompletingApiName(unlocks) : null;

            var numberByApiName = BuildAchievementNumberMap(after);
            foreach (var achievement in unlocks)
            {
                var isCompletionAchievement = completingApiName != null &&
                    string.Equals(achievement?.ApiName, completingApiName, StringComparison.OrdinalIgnoreCase);
                _notifyUnlocked?.Invoke(CreateUserEventArgs(game, after, achievement, ResolveAchievementNumber(numberByApiName, achievement), isCompletionAchievement));
            }

            // The completion time is the triggering achievement's unlock time — the latest in the
            // completing batch. Null when the provider supplies no timestamps, so the completion
            // toast shows no datetime exactly when its unlocks don't.
            var completionTimeUtc = unlocks.Select(a => a?.UnlockTimeUtc).Max();
            return completesGame ? CreateUserCompletionEventArgs(game, after, completionTimeUtc) : null;
        }

        /// <summary>
        /// Maps each achievement's ApiName to its 1-based position in the game's provider/custom
        /// sort order (custom order first via the per-game order, provider/source order as
        /// fallback). Used for stable, interpretable screenshot filenames.
        /// </summary>
        private static Dictionary<string, int> BuildAchievementNumberMap(GameAchievementData data)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return map;
            }

            var ordered = AchievementOrderHelper.ApplyOrder(
                data.Achievements,
                a => a?.ApiName,
                data.AchievementOrder);
            for (var i = 0; i < ordered.Count; i++)
            {
                var apiName = ordered[i]?.ApiName?.Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && !map.ContainsKey(apiName))
                {
                    map[apiName] = i + 1;
                }
            }

            return map;
        }

        private static int ResolveAchievementNumber(
            IReadOnlyDictionary<string, int> numberByApiName,
            AchievementDetail achievement)
        {
            var apiName = achievement?.ApiName?.Trim();
            return !string.IsNullOrWhiteSpace(apiName) && numberByApiName.TryGetValue(apiName, out var number)
                ? number
                : 0;
        }

        private async Task<List<AchievementUnlockedEventArgs>> RunFriendTickAsync(GamePollState state, CancellationToken token)
        {
            var completions = new List<AchievementUnlockedEventArgs>();
            var game = state.Game;
            if (_friendCache == null)
            {
                return completions;
            }

            if (_refreshRuntime?.IsRebuilding == true)
            {
                _logger?.Debug("[InGameMonitor] Friend refresh skipped: refresh already running.");
                return completions;
            }

            var roster = LoadFriendRoster(game, state.Provider?.ProviderKey);
            if (roster.Count == 0)
            {
                _logger?.Debug($"[InGameMonitor] Friend refresh skipped: no active friends own {game.Name}.");
                return completions;
            }

            var batch = SelectFriendBatch(state, roster);
            if (batch.Count == 0)
            {
                return completions;
            }

            var providerKeys = batch
                .Select(target => target.ProviderKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var friendIds = batch
                .Select(target => target.Friend?.ExternalUserId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var timer = Stopwatch.StartNew();
            var options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
            {
                ProviderKeys = providerKeys,
                Scope = FriendRefreshScope.SelectedGame,
                PlayniteGameIds = new[] { game.Id },
                FriendExternalUserIds = friendIds,
                // Monitoring refreshes must be fast: reuse cached definitions and fetch only unlock rows.
                PreferCachedDefinitions = true
            });
            options.RunProvidersInParallelOverride = false;

            await _executeRefreshAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = options
                },
                new RefreshExecutionPolicy
                {
                    ValidateAuthentication = false,
                    UseProgressWindow = false,
                    SwallowExceptions = true,
                    ExternalCancellationToken = token,
                    ErrorLogMessage = "[InGameMonitor] Friend selected-game refresh failed."
                }).ConfigureAwait(false);
            timer.Stop();

            var totalUnlocks = 0;
            foreach (var target in batch)
            {
                var (count, completion) = EmitFriendUnlocks(state, target);
                totalUnlocks += count;
                if (completion != null)
                {
                    completions.Add(completion);
                }
            }

            _logger?.Debug(
                $"[InGameMonitor] Friend refresh complete: game={game.Name}, elapsedMs={timer.ElapsedMilliseconds}, batch={batch.Count}, roster={roster.Count}, cursor={state.FriendCursor}, unlocks={totalUnlocks}.");
            return completions;
        }

        private List<FriendPollTarget> LoadFriendRoster(Game game, string providerKey)
        {
            var result = new List<FriendPollTarget>();
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return result;
            }

            var candidates = _friendCache.LoadFriendRefreshCandidates(
                providerKey,
                new FriendRefreshOptions
                {
                    Scope = FriendRefreshScope.SelectedGame,
                    PlayniteGameIds = new[] { game.Id }
                }) ?? new List<FriendRefreshCandidate>();

            foreach (var candidate in candidates)
            {
                if (candidate?.Friend == null || string.IsNullOrWhiteSpace(candidate.Friend.ExternalUserId))
                {
                    continue;
                }

                result.Add(new FriendPollTarget
                {
                    ProviderKey = providerKey,
                    Friend = candidate.Friend,
                    AppId = candidate.AppId,
                    ProviderGameKey = candidate.ProviderGameKey,
                    PlayniteGameId = candidate.PlayniteGameId,
                    GameName = candidate.GameName
                });
            }

            return result
                .GroupBy(target => BuildFriendTargetKey(target), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private List<FriendPollTarget> SelectFriendBatch(GamePollState state, List<FriendPollTarget> roster)
        {
            if (roster == null || roster.Count == 0)
            {
                return new List<FriendPollTarget>();
            }

            var batchSize = Math.Max(0, _settings?.Persisted?.InGameFriendBatchSize ?? 10);
            if (batchSize == 0 || batchSize >= roster.Count)
            {
                state.FriendCursor = 0;
                return roster.ToList();
            }

            var result = new List<FriendPollTarget>(batchSize);
            var start = state.FriendCursor % roster.Count;
            for (var i = 0; i < batchSize; i++)
            {
                result.Add(roster[(start + i) % roster.Count]);
            }

            state.FriendCursor = (start + batchSize) % roster.Count;
            return result;
        }

        private (int Count, AchievementUnlockedEventArgs Completion) EmitFriendUnlocks(GamePollState state, FriendPollTarget target)
        {
            var rows = _friendCache.LoadFriendGameAchievements(
                target.ProviderKey,
                target.Friend.ExternalUserId,
                target.AppId,
                target.ProviderGameKey) ?? new List<FriendAchievementRow>();

            var toasted = GetToastedFriendSet(state, target);
            var timestampRows = rows.Where(row => row?.UnlockTimeUtc.HasValue == true).ToList();
            var fresh = _differ.DiffFriendSessionUnlocks(timestampRows, state.SessionStartUtc, toasted).ToList();

            var nullTimestampRows = rows
                .Where(row => row?.Unlocked == true && !row.UnlockTimeUtc.HasValue)
                .ToList();
            var baselineKey = BuildFriendTargetKey(target);
            if (nullTimestampRows.Count > 0)
            {
                if (state.FriendBaselines.TryGetValue(baselineKey, out var baseline))
                {
                    fresh.AddRange(_differ.DiffFriendBaselineUnlocks(baseline, nullTimestampRows, toasted));
                    state.FriendBaselines[baselineKey] = rows;
                }
                else
                {
                    state.FriendBaselines[baselineKey] = rows;
                }
            }

            if (fresh.Count == 0)
            {
                return (0, null);
            }

            // This batch completes the friend's game when the rows are complete now (all
            // unlocked, or an unlocked capstone) and were not complete before these fresh
            // unlocks landed.
            var freshKeys = new HashSet<string>(
                fresh.Where(row => row != null).Select(row => row.ApiName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            var unlockedNow = rows.Count(row => row?.Unlocked == true);
            var completeNow =
                (rows.Count > 0 && unlockedNow >= rows.Count) ||
                rows.Any(row => row?.IsCapstone == true && row.Unlocked);
            var completeBefore =
                (rows.Count > 0 && unlockedNow - fresh.Count >= rows.Count) ||
                rows.Any(row => row?.IsCapstone == true && row.Unlocked && !freshKeys.Contains(row.ApiName ?? string.Empty));
            var completesGame = completeNow && !completeBefore;

            // The single fresh unlock that finished the friend's game: the newly-unlocked capstone,
            // otherwise the last to unlock by timestamp (falling back to the last item). Null unless
            // this batch completes the game, so a fresh unlock landing after the friend's game was
            // already complete is never flagged as the completion.
            var completingFriendApiName = completesGame
                ? (fresh.LastOrDefault(row => row?.IsCapstone == true)
                    ?? fresh
                        .OrderBy(row => row?.UnlockTimeUtc ?? DateTime.MinValue)
                        .LastOrDefault())?.ApiName
                : null;

            foreach (var row in fresh)
            {
                var isCompletionAchievement = completingFriendApiName != null &&
                    string.Equals(row?.ApiName, completingFriendApiName, StringComparison.OrdinalIgnoreCase);
                _notifyUnlocked?.Invoke(CreateFriendEventArgs(state.Game, target, rows, row, isCompletionAchievement));
            }

            // The completion time is the triggering achievement's unlock time — the latest in the
            // completing batch — and null when the provider supplies no timestamps.
            var completionTimeUtc = fresh.Select(row => row?.UnlockTimeUtc).Max();
            return (fresh.Count, completesGame ? CreateFriendCompletionEventArgs(state.Game, target, rows, completionTimeUtc) : null);
        }

        private HashSet<string> GetToastedFriendSet(GamePollState state, FriendPollTarget target)
        {
            var key = BuildFriendTargetKey(target);
            if (!state.ToastedFriendKeys.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                state.ToastedFriendKeys[key] = set;
            }

            return set;
        }

        private bool ShouldPollGame(Game game, bool logReason)
        {
            var persisted = _settings?.Persisted;
            if (persisted?.EnableInGamePolling != true)
            {
                if (logReason) _logger?.Debug("[InGameMonitor] Disabled in settings.");
                return false;
            }

            if (persisted.FirstTimeSetupCompleted != true || persisted.SeenThemeMigration != true)
            {
                if (logReason) _logger?.Debug("[InGameMonitor] Skipped: first-time setup/theme migration is incomplete.");
                return false;
            }

            // Monitoring issues automatic Single/multi-game refreshes, which bypass user exclusions;
            // an excluded game must not be refreshed while it runs, so gate it here.
            if (game != null &&
                GameCustomDataLookup.GetExcludedRefreshGameIds(persisted)?.Contains(game.Id) == true)
            {
                if (logReason) _logger?.Debug($"[InGameMonitor] Skipped: {game.Name} is excluded from refreshes.");
                return false;
            }

            return true;
        }

        private bool ShouldRunFriendRefresh()
        {
            return _settings?.Persisted?.InGamePollRefreshFriends == true;
        }

        private TimeSpan GetPollInterval()
        {
            return TimeSpan.FromSeconds(Math.Max(10, _settings?.Persisted?.InGamePollIntervalSeconds ?? 15));
        }

        private TimeSpan GetFriendInterval()
        {
            var multiplier = Math.Max(
                1,
                _settings?.Persisted?.InGameFriendRefreshMultiplier ?? 4);
            return TimeSpan.FromTicks(GetPollInterval().Ticks * multiplier);
        }

        /// <summary>
        /// Applies the read-time custom-data overlay (user category/category-type overrides,
        /// manual capstone, icon overrides, notes) to a freshly loaded cache snapshot so unlock
        /// toasts reflect the same categories and capstone state the user sees elsewhere. The
        /// SQLite cache stores raw provider data; these overrides live in the custom data store.
        /// </summary>
        private void HydrateForToast(GameAchievementData data)
        {
            if (data == null || _api == null)
            {
                return;
            }

            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            try
            {
                var hydrator = new GameDataHydrator(
                    _api,
                    persisted,
                    PlayniteAchievementsPlugin.Instance?.GameCustomDataStore);
                hydrator.Hydrate(data);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[InGameMonitor] Failed to hydrate game data for unlock toast.");
            }
        }

        /// <summary>
        /// Fires the full notification flow (notification, screenshot, recording) for a
        /// most-recently-earned achievement, exactly as a live unlock would. Backs the
        /// test-notification hotkey. When a game is running its id is passed and that game's most
        /// recent unlock is used; otherwise (<see cref="Guid.Empty"/>) the library-wide most recent
        /// unlock is used, matching the Overview "recent achievements" ordering. Writes nothing to
        /// the cache and bypasses the per-session dedupe, so it can be re-fired for an already-earned
        /// achievement; output is routed to a separate "Test" capture folder via
        /// <see cref="AchievementUnlockedEventArgs.IsTestFire"/>.
        /// </summary>
        public void FireTestNotification(Guid runningGameId)
        {
            try
            {
                var gameId = runningGameId;
                string preferredApiName = null;
                if (gameId == Guid.Empty)
                {
                    var recent = (_cacheManager as ICacheReadOptimizations)?
                        .LoadCachedSummaryDataFast(1)?.RecentUnlocks;
                    var mostRecent = recent != null && recent.Count > 0 ? recent[0] : null;
                    if (mostRecent?.PlayniteGameId == null || mostRecent.PlayniteGameId == Guid.Empty)
                    {
                        _logger?.Debug("[InGameMonitor] Test notification skipped: no recent unlock in the library.");
                        return;
                    }

                    gameId = mostRecent.PlayniteGameId.Value;
                    preferredApiName = mostRecent.ApiName;
                }

                var data = _cacheManager?.LoadGameData(gameId.ToString());
                if (data?.Achievements == null || data.Achievements.Count == 0)
                {
                    _logger?.Debug($"[InGameMonitor] Test notification skipped: no cached achievements for game {gameId}.");
                    return;
                }

                HydrateForToast(data);

                var achievement = ResolvePreferredAchievement(data, preferredApiName) ?? SelectLastUnlocked(data);
                if (achievement == null)
                {
                    _logger?.Debug($"[InGameMonitor] Test notification skipped: no unlocked achievement for game {gameId}.");
                    return;
                }

                var game = _api?.Database?.Games?.Get(gameId);
                var numberByApiName = BuildAchievementNumberMap(data);
                var args = CreateUserEventArgs(
                    game,
                    data,
                    achievement,
                    ResolveAchievementNumber(numberByApiName, achievement),
                    isCompletionAchievement: false);
                args.IsTestFire = true;

                _logger?.Debug(
                    $"[InGameMonitor] Firing test notification for game={game?.Name ?? data.GameName}, achievement={achievement.ApiName}.");
                _notifyUnlocked?.Invoke(args);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[InGameMonitor] Failed to fire test notification.");
            }
        }

        /// <summary>
        /// The unlocked, non-filtered achievement matching <paramref name="apiName"/> in the loaded
        /// data (used to resolve the library-wide most-recent unlock the recent-unlocks query
        /// identified). Null when the name is blank or absent.
        /// </summary>
        private static AchievementDetail ResolvePreferredAchievement(GameAchievementData data, string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName) || data?.Achievements == null)
            {
                return null;
            }

            return data.Achievements.FirstOrDefault(a =>
                a?.Unlocked == true &&
                string.Equals(a.ApiName, apiName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The game's most-recently-earned, non-filtered achievement: newest by unlock time when
        /// timestamps are present, otherwise the last unlocked in sort order (some providers supply
        /// no unlock times). Null when nothing is unlocked.
        /// </summary>
        private static AchievementDetail SelectLastUnlocked(GameAchievementData data)
        {
            var unlocked = (data?.Achievements ?? new List<AchievementDetail>())
                .Where(a => a?.Unlocked == true && a.IsFiltered != true)
                .ToList();
            if (unlocked.Count == 0)
            {
                return null;
            }

            var newestByTime = unlocked
                .Where(a => a.UnlockTimeUtc.HasValue)
                .OrderByDescending(a => a.UnlockTimeUtc.Value)
                .FirstOrDefault();
            return newestByTime ?? unlocked.LastOrDefault();
        }

        private static bool IsHardcoreCategory(string categoryType)
        {
            return AchievementCategoryTypeHelper
                .ParseValues(categoryType)
                .Contains(AchievementCategoryTypeHelper.HardcoreCategoryType);
        }

        /// <summary>
        /// Resolves a Playnite database art reference (game icon/cover) to an absolute local
        /// file path for template bindings; null when the game has no art.
        /// </summary>
        private string ResolveGameArtPath(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                return null;
            }

            try
            {
                return _api?.Database?.GetFullFilePath(databasePath);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[InGameMonitor] Failed to resolve game art path for toast.");
                return null;
            }
        }

        /// <summary>
        /// The ApiName of the achievement that finished the game within a completing batch: the
        /// newly-unlocked capstone (an unlocked capstone alone marks completion), otherwise the
        /// last achievement to unlock by timestamp (falling back to the last item when the
        /// provider supplies no unlock times). Callers pass only batches that complete the game.
        /// </summary>
        private static string ResolveCompletingApiName(IReadOnlyList<AchievementDetail> unlocks)
        {
            if (unlocks == null || unlocks.Count == 0)
            {
                return null;
            }

            var completing = unlocks.LastOrDefault(a => a?.IsCapstone == true)
                ?? unlocks
                    .OrderBy(a => a?.UnlockTimeUtc ?? DateTime.MinValue)
                    .LastOrDefault();
            return completing?.ApiName;
        }

        private AchievementUnlockedEventArgs CreateUserEventArgs(
            Game game,
            GameAchievementData data,
            AchievementDetail achievement,
            int achievementNumber,
            bool isCompletionAchievement)
        {
            return new AchievementUnlockedEventArgs
            {
                PlayniteGameId = game?.Id ?? data?.PlayniteGameId ?? Guid.Empty,
                GameName = data?.GameName ?? game?.Name,
                GameIconPath = ResolveGameArtPath(game?.Icon),
                GameCoverPath = ResolveGameArtPath(game?.CoverImage),
                ProviderKey = achievement?.ProviderKey ?? data?.ProviderKey,
                ApiName = achievement?.ApiName,
                DisplayName = achievement?.DisplayName,
                Description = achievement?.Description,
                Category = achievement?.Category,
                IconPath = achievement?.UnlockedIconPath,
                GlobalPercent = achievement?.GlobalPercentUnlocked,
                RarityTier = achievement?.Rarity.ToString(),
                TrophyType = achievement?.TrophyType,
                IsCapstone = achievement?.IsCapstone == true,
                IsHardcore = IsHardcoreCategory(achievement?.CategoryType),
                Points = achievement?.Points,
                ScaledPoints = achievement?.ScaledPoints,
                UnlockTimeUtc = achievement?.UnlockTimeUtc,
                UnlockedCount = data?.UnlockedCount ?? 0,
                TotalCount = data?.AchievementCount ?? 0,
                AchievementNumber = achievementNumber,
                IsCompletionAchievement = isCompletionAchievement
            };
        }

        private AchievementUnlockedEventArgs CreateUserCompletionEventArgs(
            Game game,
            GameAchievementData data,
            DateTime? completionTimeUtc)
        {
            return new AchievementUnlockedEventArgs
            {
                PlayniteGameId = game?.Id ?? data?.PlayniteGameId ?? Guid.Empty,
                GameName = data?.GameName ?? game?.Name,
                GameIconPath = ResolveGameArtPath(game?.Icon),
                GameCoverPath = ResolveGameArtPath(game?.CoverImage),
                ProviderKey = data?.ProviderKey,
                UnlockTimeUtc = completionTimeUtc,
                UnlockedCount = data?.UnlockedCount ?? 0,
                TotalCount = data?.AchievementCount ?? 0,
                IsGameCompleted = true
            };
        }

        private AchievementUnlockedEventArgs CreateFriendEventArgs(
            Game game,
            FriendPollTarget target,
            IReadOnlyList<FriendAchievementRow> allRows,
            FriendAchievementRow row,
            bool gameCompleted)
        {
            return new AchievementUnlockedEventArgs
            {
                PlayniteGameId = target?.PlayniteGameId ?? game?.Id ?? Guid.Empty,
                GameName = target?.GameName ?? game?.Name,
                GameIconPath = ResolveGameArtPath(game?.Icon),
                GameCoverPath = ResolveGameArtPath(game?.CoverImage),
                ProviderKey = target?.ProviderKey,
                ApiName = row?.ApiName,
                DisplayName = row?.DisplayName,
                Description = row?.Description,
                Category = row?.Category,
                IconPath = row?.UnlockedIconUrl ?? row?.IconUrl,
                GlobalPercent = row?.GlobalPercentUnlocked,
                RarityTier = row?.Rarity?.ToString(),
                TrophyType = row?.TrophyType,
                IsCapstone = row?.IsCapstone == true,
                IsHardcore = IsHardcoreCategory(row?.CategoryType),
                Points = row?.Points,
                ScaledPoints = row?.ScaledPoints,
                UnlockTimeUtc = row?.UnlockTimeUtc,
                UnlockedCount = allRows?.Count(r => r?.Unlocked == true) ?? 0,
                TotalCount = allRows?.Count ?? 0,
                IsCompletionAchievement = gameCompleted,
                IsFriendUnlock = true,
                FriendExternalUserId = target?.Friend?.ExternalUserId,
                FriendDisplayName = ResolveFriendDisplayName(target),
                FriendAvatarPath = target?.Friend?.AvatarPath,
                FriendAvatarUrl = target?.Friend?.AvatarUrl
            };
        }

        private AchievementUnlockedEventArgs CreateFriendCompletionEventArgs(
            Game game,
            FriendPollTarget target,
            IReadOnlyList<FriendAchievementRow> allRows,
            DateTime? completionTimeUtc)
        {
            return new AchievementUnlockedEventArgs
            {
                PlayniteGameId = target?.PlayniteGameId ?? game?.Id ?? Guid.Empty,
                GameName = target?.GameName ?? game?.Name,
                GameIconPath = ResolveGameArtPath(game?.Icon),
                GameCoverPath = ResolveGameArtPath(game?.CoverImage),
                ProviderKey = target?.ProviderKey,
                UnlockTimeUtc = completionTimeUtc,
                UnlockedCount = allRows?.Count(r => r?.Unlocked == true) ?? 0,
                TotalCount = allRows?.Count ?? 0,
                IsGameCompleted = true,
                IsFriendUnlock = true,
                FriendExternalUserId = target?.Friend?.ExternalUserId,
                FriendDisplayName = ResolveFriendDisplayName(target),
                FriendAvatarPath = target?.Friend?.AvatarPath,
                FriendAvatarUrl = target?.Friend?.AvatarUrl
            };
        }

        // Notifications resolve at the account level (manual rename, then the configured
        // persona/nickname mode), matching the per-achievement behavior in the overview.
        private string ResolveFriendDisplayName(FriendPollTarget target)
        {
            var friend = target?.Friend;
            if (friend == null)
            {
                return null;
            }

            var persisted = _settings?.Persisted;
            var entry = persisted?.GetFriendSetting(target.ProviderKey ?? friend.ProviderKey, friend.ExternalUserId);
            return FriendDisplayNameResolver.Resolve(
                friend,
                entry,
                persisted?.FriendNameDisplayMode ?? FriendNameDisplayMode.PersonaAndNickname);
        }

        private static string BuildFriendTargetKey(FriendPollTarget target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            var gameKey = !string.IsNullOrWhiteSpace(target.ProviderGameKey)
                ? target.ProviderGameKey.Trim()
                : target.AppId.ToString();
            return $"{target.ProviderKey}|{target.Friend?.ExternalUserId}|{gameKey}";
        }

        public void Dispose()
        {
            Task loopTask;
            lock (_stateLock)
            {
                loopTask = _loopTask;
            }

            StopAll();
            _watchers.Dispose();
            try
            {
                loopTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex)
            {
                _logger?.Debug(ex.Flatten(), "[InGameMonitor] Scheduler shutdown wait failed.");
            }
            _tickSemaphore.Dispose();
        }
    }
}
