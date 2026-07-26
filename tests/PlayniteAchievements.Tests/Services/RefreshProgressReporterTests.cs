using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.ProgressReporting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class RefreshProgressReporterTests
    {
        [TestMethod]
        public async Task OnProviderGameCompletedAsync_CountsDistinctGamesAcrossProviderPasses()
        {
            var reports = new List<ProgressReport>();
            var reporter = new RefreshProgressReporter((report, prioritizePending) => reports.Add(report));
            var scope = new RefreshProgressScope(Guid.NewGuid(), RefreshModeType.Full, null);
            var gameA = new Game("Game A");
            var gameB = new Game("Game B");

            // Refresh targets are (game, provider) pairs: gameA is serviced by two providers,
            // gameB by one. Progress totals must count distinct games, not passes.
            reporter.Initialize(new[] { gameA.Id, gameA.Id, gameB.Id });

            Assert.AreEqual(2, reporter.TotalGames);

            await reporter.OnProviderGameCompletedAsync(null, gameA, null, scope, CancellationToken.None, null)
                .ConfigureAwait(false);
            Assert.AreEqual(0, reports.Last().CurrentStep, "First of two passes must not complete gameA.");
            Assert.AreEqual(2, reports.Last().TotalSteps);

            await reporter.OnProviderGameCompletedAsync(null, gameB, null, scope, CancellationToken.None, null)
                .ConfigureAwait(false);
            Assert.AreEqual(1, reports.Last().CurrentStep, "gameB's only pass completes it.");

            await reporter.OnProviderGameCompletedAsync(null, gameA, null, scope, CancellationToken.None, null)
                .ConfigureAwait(false);
            Assert.AreEqual(2, reports.Last().CurrentStep, "gameA's last pass completes it.");
            Assert.AreEqual(2, reports.Last().TotalSteps);
        }

        [TestMethod]
        public async Task OnProviderGameCompletedAsync_SingleProviderTargetsAdvancePerGame()
        {
            var reports = new List<ProgressReport>();
            var reporter = new RefreshProgressReporter((report, prioritizePending) => reports.Add(report));
            var scope = new RefreshProgressScope(Guid.NewGuid(), RefreshModeType.Installed, null);
            var games = new[] { new Game("A"), new Game("B"), new Game("C") };

            reporter.Initialize(games.Select(game => game.Id));

            Assert.AreEqual(3, reporter.TotalGames);

            for (var i = 0; i < games.Length; i++)
            {
                await reporter.OnProviderGameCompletedAsync(null, games[i], null, scope, CancellationToken.None, null)
                    .ConfigureAwait(false);
                Assert.AreEqual(i + 1, reports.Last().CurrentStep);
                Assert.AreEqual(3, reports.Last().TotalSteps);
            }
        }

        [TestMethod]
        public void Initialize_TotalGamesCountsDistinctGameIds()
        {
            var reporter = new RefreshProgressReporter((report, prioritizePending) => { });
            var gameId = Guid.NewGuid();

            reporter.Initialize(new[] { gameId, gameId, gameId, Guid.NewGuid() });

            Assert.AreEqual(2, reporter.TotalGames);

            reporter.Reset();
            Assert.AreEqual(1, reporter.TotalGames, "TotalGames clamps to at least 1 after reset.");
        }
    }
}
