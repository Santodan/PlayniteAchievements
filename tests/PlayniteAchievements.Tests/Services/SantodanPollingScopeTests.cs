using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class SantodanPollingScopeTests
    {
        [TestMethod]
        public async Task DiscoveryResultsAreReusedAcrossAwaitButNeverAcrossPolls()
        {
            var owner = new object();
            int calls = 0;
            using (SantodanPollingScope.Enter(true))
            {
                Assert.AreEqual(1, SantodanPollingScope.GetOrDiscover(owner, "game", () => ++calls));
                Assert.AreEqual(1, await Task.Run(() =>
                    SantodanPollingScope.GetOrDiscover(owner, "game", () => ++calls)));
                Assert.AreEqual(2, SantodanPollingScope.GetOrDiscover(owner, "changed-settings", () => ++calls));
                Assert.AreEqual(3, SantodanPollingScope.GetOrDiscover(new object(), "game", () => ++calls));
                using (SantodanPollingScope.Enter(false))
                {
                    Assert.AreEqual(4, SantodanPollingScope.GetOrDiscover(owner, "game", () => ++calls));
                    Assert.AreEqual(5, SantodanPollingScope.GetOrDiscover(owner, "game", () => ++calls));
                }
                Assert.AreEqual(1, SantodanPollingScope.GetOrDiscover(owner, "game", () => ++calls));
            }
            using (SantodanPollingScope.Enter(true))
                Assert.AreEqual(6, SantodanPollingScope.GetOrDiscover(owner, "game", () => ++calls));
        }

        [TestMethod]
        public void FailedDiscoveryIsRetriedAndNegativeResultsExpireAtNextPoll()
        {
            var owner = new object();
            bool fileExists = false;
            using (SantodanPollingScope.Enter(true))
            {
                Assert.ThrowsException<InvalidOperationException>(() =>
                    SantodanPollingScope.GetOrDiscover<bool>(owner, "game", () => throw new InvalidOperationException()));
                Assert.IsFalse(SantodanPollingScope.GetOrDiscover(owner, "game", () => fileExists));
                fileExists = true;
                Assert.IsFalse(SantodanPollingScope.GetOrDiscover(owner, "game", () => fileExists));
            }
            using (SantodanPollingScope.Enter(true))
                Assert.IsTrue(SantodanPollingScope.GetOrDiscover(owner, "game", () => fileExists));
        }

        [TestMethod]
        public async Task CustomContextFlowsAcrossAwaitAndNestedUpstreamWorkRestoresIt()
        {
            Assert.IsFalse(SantodanPollingScope.IsActive);
            using (SantodanPollingScope.Enter(true))
            {
                Assert.IsTrue(await Task.Run(() => SantodanPollingScope.IsActive));
                using (SantodanPollingScope.Enter(false))
                    Assert.IsFalse(await Task.Run(() => SantodanPollingScope.IsActive));
                Assert.IsTrue(SantodanPollingScope.IsActive);
            }
            Assert.IsFalse(SantodanPollingScope.IsActive);
        }

        [TestMethod]
        public async Task CustomWorkDoesNotLeakIntoConcurrentUpstreamWorkOrAfterFailure()
        {
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            var custom = Task.Run(async () =>
            {
                try
                {
                    using (SantodanPollingScope.Enter(true))
                    {
                        entered.SetResult(true);
                        await release.Task;
                        Assert.IsTrue(SantodanPollingScope.IsActive);
                        throw new InvalidOperationException();
                    }
                }
                catch (InvalidOperationException) { }
                Assert.IsFalse(SantodanPollingScope.IsActive);
            });
            await entered.Task;
            try { Assert.IsFalse(SantodanPollingScope.IsActive); }
            finally { release.SetResult(true); }
            await custom;
        }
    }
}
