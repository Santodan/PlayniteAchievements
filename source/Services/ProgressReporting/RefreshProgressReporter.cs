using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
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

        public RefreshProgressReporter(Action<ProgressReport, bool> emit)
        {
            _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        }

        public int TotalGames => Math.Max(1, Volatile.Read(ref _totalGamesInRun));

        public void Reset()
        {
            Interlocked.Exchange(ref _processedGamesInRun, 0);
            Interlocked.Exchange(ref _totalGamesInRun, 0);
        }

        public void Initialize(int totalGames)
        {
            Interlocked.Exchange(ref _processedGamesInRun, 0);
            Interlocked.Exchange(ref _totalGamesInRun, Math.Max(0, totalGames));
        }

        public void ReportGameStarting(Game game, RefreshProgressScope scope)
        {
            var totalGames = TotalGames;
            var completedGames = Math.Min(Volatile.Read(ref _processedGamesInRun), totalGames);
            var displayIndex = Math.Min(totalGames, completedGames + 1);
            var currentGameId = game?.Id ?? scope.SingleGameId;
            var gameName = game?.Name;
            var message = BuildRefreshingGameMessage(gameName, displayIndex, totalGames);

            _emit(new ProgressReport
            {
                Message = message,
                CurrentStep = completedGames,
                TotalSteps = totalGames,
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
            var message = BuildRefreshingGameMessage(gameName, displayIndex, totalGames, iconsDownloaded, totalIcons);

            _emit(new ProgressReport
            {
                Message = message,
                CurrentStep = completedGames,
                TotalSteps = totalGames,
                OperationId = scope.OperationId,
                Mode = scope.Mode,
                CurrentGameId = currentGameId
            }, true);
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
                var completedGames = Interlocked.Increment(ref _processedGamesInRun);
                if (completedGames > totalGames)
                {
                    completedGames = totalGames;
                }

                var currentGameId = data?.PlayniteGameId ?? game?.Id ?? scope.SingleGameId;
                var gameName = data?.GameName ?? game?.Name;
                var message = BuildRefreshingGameMessage(gameName, completedGames, totalGames);

                _emit(new ProgressReport
                {
                    Message = message,
                    CurrentStep = completedGames,
                    TotalSteps = totalGames,
                    OperationId = scope.OperationId,
                    Mode = scope.Mode,
                    CurrentGameId = currentGameId
                }, false);
            }
        }

        private static string BuildRefreshingGameMessage(
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
    }
}
