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
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Services
{
    internal sealed class InGameAchievementPoller : IDisposable
    {
        private const int StartupDelaySeconds = 20;

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
        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly Func<RefreshRequest, RefreshExecutionPolicy, Task> _executeRefreshAsync;
        /// <summary>
        /// Per-game polling state. Each running game keeps its own session start, startup grace,
        /// tick counter, friend cursor, and friend toast/baseline dedup so starting or stopping
        /// one game never disturbs another game's session.
        /// </summary>
        private sealed class GamePollState
        {
            public Game Game;
            public DateTime SessionStartUtc;
            public DateTime FirstPollUtc;
            public int FriendCursor;
            public int TickCount;
            public readonly Dictionary<string, HashSet<string>> ToastedFriendKeys =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<FriendAchievementRow>> FriendBaselines =
                new Dictionary<string, List<FriendAchievementRow>>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly Action<AchievementUnlockedEventArgs> _notifyUnlocked;
        private readonly AchievementUnlockDiffer _differ;
        private readonly object _stateLock = new object();
        private readonly SemaphoreSlim _tickSemaphore = new SemaphoreSlim(1, 1);
        private readonly Dictionary<Guid, GamePollState> _games = new Dictionary<Guid, GamePollState>();

        private CancellationTokenSource _cts;
        private Task _loopTask;

        public InGameAchievementPoller(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            ICacheManager cacheManager,
            RefreshRuntime refreshRuntime,
            IReadOnlyList<IDataProvider> providers,
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
            _providers = providers ?? Array.Empty<IDataProvider>();
            _executeRefreshAsync = executeRefreshAsync ?? throw new ArgumentNullException(nameof(executeRefreshAsync));
            _notifyUnlocked = notifyUnlocked;
            _differ = differ ?? new AchievementUnlockDiffer();
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

            lock (_stateLock)
            {
                var now = DateTime.UtcNow;
                _games[game.Id] = new GamePollState
                {
                    Game = game,
                    SessionStartUtc = now,
                    FirstPollUtc = now.AddSeconds(StartupDelaySeconds)
                };

                if (_cts == null)
                {
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    _loopTask = Task.Run(() => PollLoopAsync(token), token);
                }
            }

            _logger?.Info(
                $"[InGamePolling] Started for {game.Name}; startup delay={StartupDelaySeconds}s, interval={GetPollInterval().TotalSeconds:F0}s.");
        }

        public void Stop(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            CancellationTokenSource cts = null;
            lock (_stateLock)
            {
                if (!_games.Remove(game.Id))
                {
                    return;
                }

                if (_games.Count == 0)
                {
                    cts = _cts;
                    _cts = null;
                    _loopTask = null;
                }
            }

            _logger?.Info($"[InGamePolling] Stopped for {game.Name}.");
            cts?.Cancel();
            cts?.Dispose();
        }

        public void StopAll()
        {
            CancellationTokenSource cts;
            lock (_stateLock)
            {
                _games.Clear();
                cts = _cts;
                _cts = null;
                _loopTask = null;
            }

            cts?.Cancel();
            cts?.Dispose();
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
            // Give games time to launch and settle before their first poll so startup churn
            // (splash screens, initial sync) doesn't trigger a wave of stale unlock toasts.
            // Each game carries its own grace via FirstPollUtc; this initial delay lines the
            // first tick up with the first game's grace expiring.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _logger?.Info("[InGamePolling] Stopped during startup delay.");
                return;
            }

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
                    _logger?.Debug(ex, "[InGamePolling] Tick failed.");
                }

                try
                {
                    await Task.Delay(GetPollInterval(), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }

            _logger?.Info("[InGamePolling] Poll loop exited.");
        }

        private async Task RunTickAsync(CancellationToken token)
        {
            List<GamePollState> eligible;
            lock (_stateLock)
            {
                var now = DateTime.UtcNow;
                eligible = _games.Values.Where(state => state.FirstPollUtc <= now).ToList();
            }

            eligible = eligible.Where(state => ShouldPollGame(state.Game, logReason: false)).ToList();
            if (eligible.Count == 0)
            {
                return;
            }

            await RunUserTickAsync(eligible, token).ConfigureAwait(false);

            // Friend completion notifications fire after every friend's unlock events, mirroring
            // the user phases: user unlocks, user completions, friend unlocks, friend completions.
            var friendCompletions = new List<AchievementUnlockedEventArgs>();
            foreach (var state in eligible)
            {
                if (!IsTracked(state.Game.Id))
                {
                    continue;
                }

                state.TickCount++;
                if (ShouldRunFriendTick(state.TickCount))
                {
                    friendCompletions.AddRange(await RunFriendTickAsync(state, token).ConfigureAwait(false));
                }
            }

            foreach (var completion in friendCompletions)
            {
                _notifyUnlocked?.Invoke(completion);
            }
        }

        private async Task RunUserTickAsync(IReadOnlyList<GamePollState> states, CancellationToken token)
        {
            if (_refreshRuntime?.IsRebuilding == true)
            {
                _logger?.Debug("[InGamePolling] User tick skipped: refresh already running.");
                return;
            }

            var interval = GetPollInterval();
            var beforeByGame = new Dictionary<Guid, GameAchievementData>();
            foreach (var state in states)
            {
                beforeByGame[state.Game.Id] = _cacheManager?.LoadGameData(state.Game.Id.ToString());
            }

            // One refresh execution covers every polled game so each tick produces a single
            // save/invalidation cycle regardless of how many games are running.
            var request = states.Count == 1
                ? new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = states[0].Game.Id
                }
                : new RefreshRequest
                {
                    GameIds = states.Select(state => state.Game.Id).ToList()
                };

            var timer = Stopwatch.StartNew();
            await _executeRefreshAsync(
                request,
                new RefreshExecutionPolicy
                {
                    ValidateAuthentication = false,
                    UseProgressWindow = false,
                    SwallowExceptions = true,
                    ExternalCancellationToken = token,
                    ErrorLogMessage = "[InGamePolling] Poll refresh failed."
                }).ConfigureAwait(false);

            timer.Stop();

            // Slow ticks widen the unlock-to-toast gap and therefore stretch recording clips;
            // the Warn makes over-interval ticks visible in the plugin log.
            _logger?.Debug(
                $"[PollerTiming] tick took {timer.Elapsed.TotalSeconds:F1}s, interval {interval.TotalSeconds:F0}s, games={states.Count}");
            if (timer.Elapsed > interval)
            {
                _logger?.Warn(
                    $"[PollerTiming] tick took {timer.Elapsed.TotalSeconds:F1}s, exceeding the {interval.TotalSeconds:F0}s poll interval; unlock detection (and clip length) lags accordingly.");
            }

            // Completion notifications are collected and fired only after every game's unlock
            // events so they always land in their own wave behind the achievement toasts.
            var completions = new List<AchievementUnlockedEventArgs>();
            foreach (var state in states)
            {
                // A game stopped mid-tick must not toast unlocks from its final refresh.
                if (!IsTracked(state.Game.Id))
                {
                    continue;
                }

                var completion = EmitUserUnlocks(state.Game, beforeByGame[state.Game.Id], interval, timer.ElapsedMilliseconds);
                if (completion != null)
                {
                    completions.Add(completion);
                }
            }

            foreach (var completion in completions)
            {
                _notifyUnlocked?.Invoke(completion);
            }
        }

        private AchievementUnlockedEventArgs EmitUserUnlocks(Game game, GameAchievementData before, TimeSpan interval, long elapsedMs)
        {
            var after = _cacheManager?.LoadGameData(game.Id.ToString());
            HydrateForToast(after);
            var unlocks = _differ.DiffUserUnlocks(before, after)
                .Where(a => a?.IsFiltered != true)
                .ToList();
            _logger?.Debug(
                $"[InGamePolling] User tick complete: game={game.Name}, interval={interval.TotalSeconds:F0}s, elapsedMs={elapsedMs}, unlocks={unlocks.Count}.");

            // This batch completes the game when the data crossed from incomplete to complete
            // (all unlocked, or the capstone unlocked) with at least one new unlock in hand.
            var completesGame = unlocks.Count > 0 && before?.IsCompleted != true && after?.IsCompleted == true;

            var numberByApiName = BuildAchievementNumberMap(after);
            foreach (var achievement in unlocks)
            {
                _notifyUnlocked?.Invoke(CreateUserEventArgs(game, after, achievement, ResolveAchievementNumber(numberByApiName, achievement)));
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
                _logger?.Debug("[InGamePolling] Friend tick skipped: refresh already running.");
                return completions;
            }

            var roster = LoadFriendRoster(game);
            if (roster.Count == 0)
            {
                _logger?.Debug($"[InGamePolling] Friend tick skipped: no active friends own {game.Name}.");
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
                // Polling ticks must be fast: reuse the cached schema and fetch only unlock rows.
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
                    ErrorLogMessage = "[InGamePolling] Friend selected-game refresh failed."
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
                $"[InGamePolling] Friend tick complete: game={game.Name}, elapsedMs={timer.ElapsedMilliseconds}, batch={batch.Count}, roster={roster.Count}, cursor={state.FriendCursor}, unlocks={totalUnlocks}.");
            return completions;
        }

        private List<FriendPollTarget> LoadFriendRoster(Game game)
        {
            var providers = GetCapableProviderKeys(game);
            var result = new List<FriendPollTarget>();
            foreach (var providerKey in providers)
            {
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

            foreach (var row in fresh)
            {
                _notifyUnlocked?.Invoke(CreateFriendEventArgs(state.Game, target, rows, row, completeNow));
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
                if (logReason) _logger?.Debug("[InGamePolling] Disabled in settings.");
                return false;
            }

            if (persisted.FirstTimeSetupCompleted != true || persisted.SeenThemeMigration != true)
            {
                if (logReason) _logger?.Debug("[InGamePolling] Skipped: first-time setup/theme migration is incomplete.");
                return false;
            }

            if (GetCapableProviderKeys(game).Count == 0)
            {
                if (logReason) _logger?.Debug($"[InGamePolling] Skipped: no provider is capable for {game?.Name}.");
                return false;
            }

            // Polling issues automatic Single/multi-game refreshes, which bypass user exclusions;
            // an excluded game must not be refreshed while it runs, so gate it here.
            if (game != null &&
                GameCustomDataLookup.GetExcludedRefreshGameIds(persisted)?.Contains(game.Id) == true)
            {
                if (logReason) _logger?.Debug($"[InGamePolling] Skipped: {game.Name} is excluded from refreshes.");
                return false;
            }

            return true;
        }

        private bool ShouldRunFriendTick(int tick)
        {
            var persisted = _settings?.Persisted;
            if (persisted?.InGamePollRefreshFriends != true)
            {
                return false;
            }

            var multiplier = Math.Max(1, persisted.InGameFriendRefreshMultiplier);
            return tick > 0 && tick % multiplier == 0;
        }

        private TimeSpan GetPollInterval()
        {
            return TimeSpan.FromSeconds(Math.Max(10, _settings?.Persisted?.InGamePollIntervalSeconds ?? 15));
        }

        private List<string> GetCapableProviderKeys(Game game)
        {
            return _providers
                .Where(provider => provider != null && SafeIsCapable(provider, game))
                .Select(provider => provider.ProviderKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool SafeIsCapable(IDataProvider provider, Game game)
        {
            try
            {
                return provider?.IsCapable(game) == true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[InGamePolling] Provider capability check failed for {provider?.ProviderKey}.");
                return false;
            }
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
                _logger?.Debug(ex, "[InGamePolling] Failed to hydrate game data for unlock toast.");
            }
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
                _logger?.Debug(ex, "[InGamePolling] Failed to resolve game art path for toast.");
                return null;
            }
        }

        private AchievementUnlockedEventArgs CreateUserEventArgs(
            Game game,
            GameAchievementData data,
            AchievementDetail achievement,
            int achievementNumber)
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
                IsCompletionAchievement = data?.IsCompleted == true
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
                FriendDisplayName = target?.Friend?.DisplayName,
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
                FriendDisplayName = target?.Friend?.DisplayName,
                FriendAvatarPath = target?.Friend?.AvatarPath,
                FriendAvatarUrl = target?.Friend?.AvatarUrl
            };
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
            StopAll();
            _tickSemaphore.Dispose();
        }
    }
}
