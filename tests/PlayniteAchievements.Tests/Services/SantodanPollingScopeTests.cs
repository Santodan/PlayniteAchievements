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
