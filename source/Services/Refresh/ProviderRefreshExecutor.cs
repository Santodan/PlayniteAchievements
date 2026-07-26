using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Refresh
{
    internal static class ProviderRefreshExecutor
    {
        internal sealed class ProviderExecutionPlan
        {
            public IDataProvider Provider { get; set; }
            public IReadOnlyList<Game> Games { get; set; }
        }

        internal sealed class ProviderExecutionResult
        {
            public IDataProvider Provider { get; set; }
            public RebuildPayload Payload { get; set; }

            // Non-cancellation exception that aborted this provider's execution. Contained per
            // provider so sibling providers finish their runs; consumers surface it to the user.
            public Exception Fault { get; set; }
        }

        // A single game's cache-write failure is treated like any other per-game error so one bad
        // row cannot abort a whole run; this many consecutive persistence failures indicate a
        // systemic problem (disk full, locked database) and abort the provider instead.
        private const int MaxConsecutivePersistenceErrors = 3;

        internal sealed class ProviderGameResult
        {
            public bool CountInSummary { get; set; } = true;
            public GameAchievementData Data { get; set; }

            public static ProviderGameResult Skipped()
            {
                return new ProviderGameResult
                {
                    CountInSummary = false,
                    Data = null
                };
            }
        }

        /// <summary>
        /// Overload for scanners pacing requests with a RateLimiter: supplies the standard
        /// between-games and after-error delay callbacks.
        /// </summary>
        public static Task<RebuildPayload> RunProviderGamesAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, CancellationToken, Task<ProviderGameResult>> processGameAsync,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            Func<Exception, bool> isAuthRequiredException,
            Action<Game, Exception, int> onGameError,
            RateLimiter rateLimiter,
            CancellationToken cancel)
        {
            if (rateLimiter == null)
            {
                throw new ArgumentNullException(nameof(rateLimiter));
            }

            return RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                processGameAsync,
                onGameCompleted,
                isAuthRequiredException,
                onGameError,
                delayBetweenGamesAsync: (index, token) => rateLimiter.DelayBeforeNextAsync(token),
                delayAfterErrorAsync: (consecutiveErrors, token) => rateLimiter.DelayAfterErrorAsync(consecutiveErrors, token),
                cancel);
        }

        public static async Task<RebuildPayload> RunProviderGamesAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, CancellationToken, Task<ProviderGameResult>> processGameAsync,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            Func<Exception, bool> isAuthRequiredException,
            Action<Game, Exception, int> onGameError,
            Func<int, CancellationToken, Task> delayBetweenGamesAsync,
            Func<int, CancellationToken, Task> delayAfterErrorAsync,
            CancellationToken cancel)
        {
            var summary = new RebuildSummary();
            var payload = new RebuildPayload { Summary = summary };

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return payload;
            }

            if (processGameAsync == null)
            {
                throw new ArgumentNullException(nameof(processGameAsync));
            }

            var consecutiveErrors = 0;
            var consecutivePersistenceErrors = 0;
            var authRequiredTriggered = false;

            for (var i = 0; i < gamesToRefresh.Count; i++)
            {
                if (cancel.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProviderRefreshExecutor] Cancellation detected at game {i}/{gamesToRefresh.Count}");
                }
                cancel.ThrowIfCancellationRequested();

                var game = gamesToRefresh[i];
                onGameStarting?.Invoke(game);
                var result = ProviderGameResult.Skipped();
                var callbackInvoked = false;

                if (authRequiredTriggered)
                {
                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, null).ConfigureAwait(false);
                    }

                    continue;
                }

                try
                {
                    result = await processGameAsync(game, cancel).ConfigureAwait(false) ?? ProviderGameResult.Skipped();

                    if (onGameCompleted != null)
                    {
                        callbackInvoked = true;
                        await onGameCompleted(game, result.Data).ConfigureAwait(false);
                    }

                    if (!result.CountInSummary)
                    {
                        continue;
                    }

                    summary.GamesRefreshed++;
                    summary.RefreshedGameIds.Add(game.Id);

                    if (result.Data != null && result.Data.HasAchievements)
                    {
                        summary.GamesWithAchievements++;
                    }
                    else
                    {
                        summary.GamesWithoutAchievements++;
                    }

                    consecutiveErrors = 0;
                    consecutivePersistenceErrors = 0;

                    if (delayBetweenGamesAsync != null && i < gamesToRefresh.Count - 1)
                    {
                        await delayBetweenGamesAsync(i, cancel).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Timeout-shaped OperationCanceledExceptions (HttpClient timeouts) that arrive
                    // without the run token being cancelled fall through here and are treated as
                    // per-game errors rather than aborting the run as a cancel.
                    if (isAuthRequiredException != null && isAuthRequiredException(ex))
                    {
                        if (!callbackInvoked && onGameCompleted != null)
                        {
                            callbackInvoked = true;
                            await onGameCompleted(game, result.Data).ConfigureAwait(false);
                        }

                        payload.AuthRequired = true;
                        authRequiredTriggered = true;
                        continue;
                    }

                    if (!callbackInvoked && onGameCompleted != null)
                    {
                        callbackInvoked = true;
                        await onGameCompleted(game, result.Data).ConfigureAwait(false);
                    }

                    consecutiveErrors++;
                    onGameError?.Invoke(game, ex, consecutiveErrors);

                    if (ex is CachePersistenceException)
                    {
                        consecutivePersistenceErrors++;
                        if (consecutivePersistenceErrors >= MaxConsecutivePersistenceErrors)
                        {
                            throw;
                        }
                    }
                    else
                    {
                        consecutivePersistenceErrors = 0;
                    }

                    if (delayAfterErrorAsync != null && consecutiveErrors >= 3)
                    {
                        await delayAfterErrorAsync(consecutiveErrors, cancel).ConfigureAwait(false);
                    }
                }
            }

            return payload;
        }

        public static async Task<IReadOnlyList<ProviderExecutionResult>> ExecuteProvidersAsync(
            IReadOnlyList<ProviderExecutionPlan> plans,
            bool runInParallel,
            Func<ProviderExecutionPlan, Task<RebuildPayload>> executeProviderAsync,
            CancellationToken cancel)
        {
            if (executeProviderAsync == null)
            {
                throw new ArgumentNullException(nameof(executeProviderAsync));
            }

            if (plans == null || plans.Count == 0)
            {
                return Array.Empty<ProviderExecutionResult>();
            }

            if (!runInParallel || plans.Count == 1)
            {
                var sequentialResults = new List<ProviderExecutionResult>(plans.Count);
                for (var i = 0; i < plans.Count; i++)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ProviderRefreshExecutor] Cancellation detected in sequential provider execution at plan {i}/{plans.Count}");
                    }
                    cancel.ThrowIfCancellationRequested();

                    var plan = plans[i];
                    sequentialResults.Add(await ExecutePlanContainedAsync(plan, executeProviderAsync, cancel).ConfigureAwait(false));
                }

                return sequentialResults;
            }

            var tasks = plans
                .Select(async plan =>
                {
                    cancel.ThrowIfCancellationRequested();
                    return await ExecutePlanContainedAsync(plan, executeProviderAsync, cancel).ConfigureAwait(false);
                })
                .ToArray();

            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs one provider plan, containing any non-cancellation exception in the result's
        /// Fault so a faulted provider cannot abort sibling providers or the whole run.
        /// A timeout-shaped OperationCanceledException without the run token cancelled is a
        /// fault, not a cancel, so it is contained (and logged with its stack) as well.
        /// </summary>
        private static async Task<ProviderExecutionResult> ExecutePlanContainedAsync(
            ProviderExecutionPlan plan,
            Func<ProviderExecutionPlan, Task<RebuildPayload>> executeProviderAsync,
            CancellationToken cancel)
        {
            try
            {
                var payload = await executeProviderAsync(plan).ConfigureAwait(false) ?? new RebuildPayload();
                return new ProviderExecutionResult
                {
                    Provider = plan.Provider,
                    Payload = payload
                };
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ProviderExecutionResult
                {
                    Provider = plan.Provider,
                    Payload = new RebuildPayload { Summary = new RebuildSummary() },
                    Fault = ex
                };
            }
        }
    }
}
