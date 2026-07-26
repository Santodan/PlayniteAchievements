using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.ProgressReporting
{
    /// <summary>
    /// Thread-safe icon download progress state used by refresh reporting.
    /// </summary>
    internal sealed class IconDownloadProgress
    {
        private readonly int _total;
        private int _downloaded;

        public IconDownloadProgress(int total)
        {
            _total = Math.Max(0, total);
        }

        public bool HasWork => _total > 0;

        public (int Downloaded, int Total) AdvanceAndGetSnapshot()
        {
            if (_total <= 0)
            {
                return (0, 0);
            }

            var downloaded = Interlocked.Increment(ref _downloaded);
            if (downloaded > _total)
            {
                downloaded = _total;
            }

            return (downloaded, _total);
        }
    }

    internal readonly struct RefreshProgressScope
    {
        public Guid OperationId { get; }
        public RefreshModeType Mode { get; }
        public Guid? SingleGameId { get; }

        public RefreshProgressScope(Guid operationId, RefreshModeType mode, Guid? singleGameId)
        {
            OperationId = operationId;
            Mode = mode;
            SingleGameId = singleGameId;
        }
    }

    internal sealed class RefreshProgressReporter
    {
        private readonly Action<ProgressReport, bool> _emit;
        private int _processedGamesInRun;
        private int _totalGamesInRun;

        // Refresh targets are (game, provider) pairs, so a game serviced by several providers completes
        // several passes. Progress counts distinct games: a game counts as processed only when its last
        // pending provider pass completes, keeping "n/total" and the completion count in game units.
        private readonly object _pendingPassesGate = new object();
        private Dictionary<Guid, int> _pendingProviderPassesByGame = new Dictionary<Guid, int>();
        private bool _useWeightedProgress;
        private int _weightedStartUnits;
        private int _weightedEndUnits;
        private int _weightedTotalUnits;
        private int _completionTotalSteps;
        private int _lastReportedStep;

        // Preparation-aggregate mode: used by the combined current-user + friends path so that
        // concurrently-running current-game refreshes and friend-roster loads advance ONE shared counter
        // over [0, _preparationEndUnits] instead of colliding in the same band. Both game completions and
        // roster completions increment _preparationCompleted; the emitted step is that aggregate fraction.
        private bool _preparationMode;
        private int _preparationTotal;
        private int _preparationCompleted;
        private int _preparationEndUnits;
        private int _preparationScaleUnits;

        public RefreshProgressReporter(Action<ProgressReport, bool> emit)
        {
            _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        }

        public int TotalGames => Math.Max(1, Volatile.Read(ref _totalGamesInRun));

        public int CompletionTotalSteps
        {
            get
            {
                var remembered = Volatile.Read(ref _completionTotalSteps);
                if (remembered > 0)
                {
                    return remembered;
                }

                return _useWeightedProgress
                    ? Math.Max(1, Volatile.Read(ref _weightedTotalUnits))
                    : TotalGames;
            }
        }

        public void Reset()
        {
            lock (_pendingPassesGate)
            {
                _pendingProviderPassesByGame = new Dictionary<Guid, int>();
            }

            Interlocked.Exchange(ref _processedGamesInRun, 0);
            Interlocked.Exchange(ref _totalGamesInRun, 0);
            _useWeightedProgress = false;
            _weightedStartUnits = 0;
            _weightedEndUnits = 0;
            _weightedTotalUnits = 0;
            _preparationMode = false;
            _preparationEndUnits = 0;
            _preparationScaleUnits = 0;
            Interlocked.Exchange(ref _preparationTotal, 0);
            Interlocked.Exchange(ref _preparationCompleted, 0);
            Interlocked.Exchange(ref _completionTotalSteps, 0);
            Interlocked.Exchange(ref _lastReportedStep, 0);
        }

        /// <summary>
        /// Initializes game progress from the run's (game, provider) targets: one id per target,
        /// repeated when several providers service the same game. Totals count distinct games.
        /// </summary>
        public void Initialize(IEnumerable<Guid> targetGameIds)
        {
            var pendingPasses = new Dictionary<Guid, int>();
            foreach (var gameId in targetGameIds ?? Enumerable.Empty<Guid>())
            {
                pendingPasses.TryGetValue(gameId, out var passes);
                pendingPasses[gameId] = passes + 1;
            }

            lock (_pendingPassesGate)
            {
                _pendingProviderPassesByGame = pendingPasses;
            }

            Interlocked.Exchange(ref _processedGamesInRun, 0);
            Interlocked.Exchange(ref _totalGamesInRun, pendingPasses.Count);
            if (!_useWeightedProgress)
            {
                RememberCompletionTotal(TotalGames);
            }
        }

        /// <summary>
        /// Records completion of one provider pass for a game and returns the number of fully
        /// processed games. A game is processed once all of its pending provider passes complete.
        /// </summary>
        private int CompleteProviderPass(Guid gameId)
        {
            var gameCompleted = false;
            lock (_pendingPassesGate)
            {
                if (_pendingProviderPassesByGame.TryGetValue(gameId, out var passes))
                {
                    if (passes <= 1)
                    {
                        _pendingProviderPassesByGame.Remove(gameId);
                        gameCompleted = true;
                    }
                    else
                    {
                        _pendingProviderPassesByGame[gameId] = passes - 1;
                    }
                }
            }

            return gameCompleted
                ? Interlocked.Increment(ref _processedGamesInRun)
                : Volatile.Read(ref _processedGamesInRun);
        }

        public void ConfigureWeightedProgress(int totalUnits, int startUnits, int endUnits)
        {
            var safeTotal = Math.Max(1, totalUnits);
            _weightedTotalUnits = safeTotal;
            _weightedStartUnits = Math.Max(0, Math.Min(startUnits, safeTotal));
            _weightedEndUnits = Math.Max(_weightedStartUnits, Math.Min(endUnits, safeTotal));
            _useWeightedProgress = true;
            // Entering (or continuing into) weighted mode ends the preparation aggregate; the combined
            // path calls this at the phase-1 -> phase-2 boundary.
            _preparationMode = false;
            RememberCompletionTotal(safeTotal);
        }

        /// <summary>
        /// Begins the combined-path preparation aggregate. Current-game completions and friend-roster
        /// completions both advance one shared counter of <paramref name="preparationTotal"/> units,
        /// mapped into [0, <paramref name="endUnits"/>] of a <paramref name="totalUnits"/>-unit run scale.
        /// This runs before <see cref="ConfigureWeightedProgress"/> takes over for the friend phase.
        /// </summary>
        public void InitializePreparation(int preparationTotal, int endUnits, int totalUnits)
        {
            _preparationScaleUnits = Math.Max(1, totalUnits);
            _preparationEndUnits = Math.Max(0, Math.Min(endUnits, _preparationScaleUnits));
            Interlocked.Exchange(ref _preparationTotal, Math.Max(1, preparationTotal));
            Interlocked.Exchange(ref _preparationCompleted, 0);
            _preparationMode = true;
            RememberCompletionTotal(_preparationScaleUnits);
        }

        /// <summary>
        /// Reports completion of one non-game preparation unit (e.g. a provider's friend roster load).
        /// No-op unless preparation-aggregate mode is active.
        /// </summary>
        public void ReportPreparationUnitCompleted(string message, RefreshProgressScope scope)
        {
            if (!_preparationMode)
            {
                return;
            }

            var completed = Interlocked.Increment(ref _preparationCompleted);
            Emit(new ProgressReport
            {
                Message = message,
                CurrentStep = ResolvePreparationStep(completed),
                TotalSteps = _preparationScaleUnits,
                OperationId = scope.OperationId,
                Mode = scope.Mode,
                CurrentGameId = scope.SingleGameId
            }, false);
        }

        private int ResolvePreparationStep(int completed)
        {
            var total = Math.Max(1, Volatile.Read(ref _preparationTotal));
            var safeCompleted = Math.Max(0, Math.Min(completed, total));
            var units = (int)Math.Round((double)_preparationEndUnits * safeCompleted / total);
            return Math.Max(0, Math.Min(_preparationEndUnits, units));
        }

        // Emits the current preparation-aggregate position without advancing it (used for a game's
        // start/icon updates during the combined preparation phase, which show text but do not complete
        // a unit).
        private void EmitPreparationSnapshot(
            string message,
            Guid? currentGameId,
            RefreshProgressScope scope,
            bool prioritizePending = false)
        {
            Emit(new ProgressReport
            {
                Message = message,
                CurrentStep = ResolvePreparationStep(Volatile.Read(ref _preparationCompleted)),
                TotalSteps = _preparationScaleUnits,
                OperationId = scope.OperationId,
                Mode = scope.Mode,
                CurrentGameId = currentGameId
            }, prioritizePending);
        }

        public void ReportGameStarting(Game game, RefreshProgressScope scope)
        {
            var totalGames = TotalGames;
            var completedGames = Math.Min(Volatile.Read(ref _processedGamesInRun), totalGames);
            var displayIndex = Math.Min(totalGames, completedGames + 1);
            var currentGameId = game?.Id ?? scope.SingleGameId;
            var gameName = game?.Name;
            var message = BuildRefreshingGameMessage(gameName, displayIndex, totalGames);

            if (_preparationMode)
            {
                EmitPreparationSnapshot(message, currentGameId, scope);
                return;
            }

            var totalSteps = ResolveTotalSteps(totalGames);
            RememberCompletionTotal(totalSteps);

            Emit(new ProgressReport
            {
                Message = message,
                CurrentStep = ResolveCurrentStep(completedGames, totalGames),
                TotalSteps = totalSteps,
                OperationId = scope.OperationId,
                Mode = scope.Mode,
                CurrentGameId = currentGameId
            }, false);
        }

        public void ReportIconProgress(
            Game game,
            GameAchievementData data,
            int iconsDownloaded,
            int totalIcons,
            RefreshProgressScope scope)
        {
            if (totalIcons <= 0)
            {
                return;
            }

            var totalGames = TotalGames;
            var completedGames = Math.Min(Volatile.Read(ref _processedGamesInRun), totalGames);
            var displayIndex = Math.Min(totalGames, completedGames + 1);
            var currentGameId = data?.PlayniteGameId ?? game?.Id ?? scope.SingleGameId;
            var gameName = data?.GameName ?? game?.Name;
            // Standardized: icon downloads advance the bar fractionally but keep the same "Refreshing
            // achievements {n}/{N}: {game}" text as the game itself, matching the friend-refresh phases;
            // the icon count is no longer shown separately.
            var message = BuildRefreshingGameMessage(gameName, displayIndex, totalGames);

            if (_preparationMode)
            {
                // During combined preparation, icon downloads show text but do not complete a unit.
                EmitPreparationSnapshot(message, currentGameId, scope, prioritizePending: true);
                return;
            }

            var totalSteps = ResolveTotalSteps(totalGames);
            RememberCompletionTotal(totalSteps);

            // Advance the bar fractionally within the current game as its icons download, capped below the
            // next whole game so OnProviderGameCompletedAsync remains the step that crosses into it. The
            // owned-game path runs in weighted units (see ExecuteCurrentProviderPlansAsync), so a sub-game
            // fraction is representable rather than being truncated to the game-count step.
            var iconFraction = Math.Min(0.85d, (double)iconsDownloaded / totalIcons * 0.85d);
            var effectiveCompleted = Math.Min(totalGames, completedGames + iconFraction);

            Emit(new ProgressReport
            {
                Message = message,
                CurrentStep = ResolveCurrentStep(effectiveCompleted, totalGames),
                TotalSteps = totalSteps,
                OperationId = scope.OperationId,
                Mode = scope.Mode,
                CurrentGameId = currentGameId
            }, true);
        }

        public void ReportFriendProgress(
            string message,
            int current,
            int total,
            RefreshProgressScope scope)
        {
            total = Math.Max(1, total);
            current = Math.Max(0, Math.Min(current, total));
            var totalSteps = ResolveTotalSteps(total);
            RememberCompletionTotal(totalSteps);
            Emit(new ProgressReport
            {
                Message = message,
                CurrentStep = ResolveCurrentStep(current, total),
                TotalSteps = totalSteps,
                OperationId = scope.OperationId,
                Mode = scope.Mode,
                CurrentGameId = scope.SingleGameId
            }, false);
        }

        public async Task OnProviderGameCompletedAsync(
            IDataProvider provider,
            Game game,
            GameAchievementData data,
            RefreshProgressScope scope,
            CancellationToken cancel,
            Func<IDataProvider, Game, GameAchievementData, RefreshProgressScope, CancellationToken, Task> onGameRefreshed)
        {
            try
            {
                if (data != null && onGameRefreshed != null)
                {
                    await onGameRefreshed(provider, game, data, scope, cancel).ConfigureAwait(false);
                }
            }
            finally
            {
                var totalGames = TotalGames;
                var completedGames = CompleteProviderPass(game?.Id ?? data?.PlayniteGameId ?? scope.SingleGameId ?? Guid.Empty);
                if (completedGames > totalGames)
                {
                    completedGames = totalGames;
                }

                var currentGameId = data?.PlayniteGameId ?? game?.Id ?? scope.SingleGameId;
                var gameName = data?.GameName ?? game?.Name;
                var message = BuildRefreshingGameMessage(gameName, completedGames, totalGames);

                if (_preparationMode)
                {
                    // Game completion advances the shared preparation aggregate (alongside roster loads).
                    // (No return here: return is illegal inside a finally block.)
                    var prepCompleted = Interlocked.Increment(ref _preparationCompleted);
                    Emit(new ProgressReport
                    {
                        Message = message,
                        CurrentStep = ResolvePreparationStep(prepCompleted),
                        TotalSteps = _preparationScaleUnits,
                        OperationId = scope.OperationId,
                        Mode = scope.Mode,
                        CurrentGameId = currentGameId
                    }, false);
                }
                else
                {
                    var totalSteps = ResolveTotalSteps(totalGames);
                    RememberCompletionTotal(totalSteps);

                    Emit(new ProgressReport
                    {
                        Message = message,
                        CurrentStep = ResolveCurrentStep(completedGames, totalGames),
                        TotalSteps = totalSteps,
                        OperationId = scope.OperationId,
                        Mode = scope.Mode,
                        CurrentGameId = currentGameId
                    }, false);
                }
            }
        }

        private void Emit(ProgressReport report, bool prioritizePending)
        {
            if (report == null)
            {
                return;
            }

            var totalSteps = Math.Max(1, report.TotalSteps);
            var currentStep = Math.Max(0, Math.Min(report.CurrentStep, totalSteps));
            while (true)
            {
                var observed = Volatile.Read(ref _lastReportedStep);
                if (currentStep <= observed)
                {
                    currentStep = Math.Min(observed, totalSteps);
                    break;
                }

                if (Interlocked.CompareExchange(ref _lastReportedStep, currentStep, observed) == observed)
                {
                    break;
                }
            }

            report.CurrentStep = currentStep;
            report.TotalSteps = totalSteps;
            _emit(report, prioritizePending);
        }

        private void RememberCompletionTotal(int totalSteps)
        {
            Interlocked.Exchange(ref _completionTotalSteps, Math.Max(1, totalSteps));
        }

        private int ResolveTotalSteps(int fallbackTotal)
        {
            return _useWeightedProgress
                ? Math.Max(1, Volatile.Read(ref _weightedTotalUnits))
                : Math.Max(1, fallbackTotal);
        }

        private int ResolveCurrentStep(double completed, double total)
        {
            if (!_useWeightedProgress)
            {
                return Math.Max(0, (int)Math.Min(completed, Math.Max(1, total)));
            }

            total = Math.Max(1, total);
            completed = Math.Max(0, Math.Min(completed, total));
            var span = Math.Max(0, Volatile.Read(ref _weightedEndUnits) - Volatile.Read(ref _weightedStartUnits));
            var current = Volatile.Read(ref _weightedStartUnits) + (int)Math.Round(span * (completed / total));
            return Math.Max(0, Math.Min(current, ResolveTotalSteps((int)total)));
        }

        private static string BuildRefreshingGameMessage(
            string gameName,
            int currentIndex,
            int totalGames)
        {
            var safeGameName = string.IsNullOrWhiteSpace(gameName)
                ? ResourceProvider.GetString("LOCPlayAch_Text_Ellipsis")
                : gameName;

            var countsText = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Format_Counts"),
                Math.Max(0, currentIndex),
                Math.Max(1, totalGames));

            return string.Format(
                ResourceProvider.GetString("LOCPlayAch_Targeted_RefreshingGameWithCounts"),
                safeGameName,
                countsText);
        }
    }
}
