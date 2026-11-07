using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class GameplayRefreshDeferralTests
    {
        [TestMethod]
        public async Task NoGameplayOrDisabledOptionDoesNotDelayScheduler()
        {
            await GameplayRefreshDeferral.WaitAsync(() => false,
                _ => throw new InvalidOperationException("Unexpected delay"), CancellationToken.None);
        }

        [TestMethod]
        public async Task WaitsUntilLastGameStopsOrSettingIsDisabled()
        {
            int gamesRunning = 2, waits = 0;
            await GameplayRefreshDeferral.WaitAsync(() => gamesRunning > 0, _ =>
            {
                waits++;
                gamesRunning--;
                return Task.CompletedTask;
            }, CancellationToken.None);
            Assert.AreEqual(2, waits);

            bool enabled = true;
            await GameplayRefreshDeferral.WaitAsync(() => enabled, _ =>
            {
                enabled = false;
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        [TestMethod]
        public async Task ShutdownCancelsWaitingWithoutStartingTheRefresh()
        {
            using (var cts = new CancellationTokenSource())
            {
                bool refreshed = false;
                try
                {
                    await GameplayRefreshDeferral.WaitAsync(() => true, token =>
                    {
                        cts.Cancel();
                        return Task.Delay(Timeout.Infinite, token);
                    }, cts.Token);
                    refreshed = true;
                }
                catch (OperationCanceledException) { }
                Assert.IsFalse(refreshed);
            }
        }
    }
}
