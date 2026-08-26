using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    [DoNotParallelize]
    public class RenderQuietGateTests
    {
        [TestMethod]
        public void Engage_SetsGate_DisposeClearsIt()
        {
            Assert.IsFalse(RenderQuietGate.IsEngaged);
            var scope = RenderQuietGate.Engage();
            try
            {
                Assert.IsTrue(RenderQuietGate.IsEngaged);
            }
            finally
            {
                scope.Dispose();
            }

            Assert.IsFalse(RenderQuietGate.IsEngaged);
        }

        [TestMethod]
        public void NestedScopes_GateClearsOnlyWhenAllDispose()
        {
            var outer = RenderQuietGate.Engage();
            var inner = RenderQuietGate.Engage();
            try
            {
                inner.Dispose();
                Assert.IsTrue(RenderQuietGate.IsEngaged);
            }
            finally
            {
                outer.Dispose();
                inner.Dispose();
            }

            Assert.IsFalse(RenderQuietGate.IsEngaged);
        }

        [TestMethod]
        public void DoubleDispose_DoesNotUnderflowTheCount()
        {
            var scope = RenderQuietGate.Engage();
            scope.Dispose();
            scope.Dispose();
            Assert.IsFalse(RenderQuietGate.IsEngaged);

            var next = RenderQuietGate.Engage();
            try
            {
                Assert.IsTrue(RenderQuietGate.IsEngaged);
            }
            finally
            {
                next.Dispose();
            }
        }

        [TestMethod]
        public void LeakedScope_SelfHealsPastTheHardCap()
        {
            var leaked = RenderQuietGate.Engage();
            try
            {
                Assert.IsTrue(RenderQuietGate.IsEngaged);
                Thread.Sleep(RenderQuietGate.HardCapMs + 200);
                Assert.IsFalse(RenderQuietGate.IsEngaged);
            }
            finally
            {
                leaked.Dispose();
            }
        }

        [TestMethod]
        public async Task WhenClearAsync_ReturnsPromptlyWhenTheGateIsClear()
        {
            Assert.IsFalse(RenderQuietGate.IsEngaged);
            var watch = Stopwatch.StartNew();
            await RenderQuietGate.WhenClearAsync(maxDeferMs: 5000);
            Assert.IsTrue(watch.ElapsedMilliseconds < 500);
        }

        [TestMethod]
        public async Task WhenClearAsync_RespectsTheCallersBound()
        {
            var scope = RenderQuietGate.Engage();
            try
            {
                var watch = Stopwatch.StartNew();
                await RenderQuietGate.WhenClearAsync(maxDeferMs: 150);
                Assert.IsTrue(watch.ElapsedMilliseconds >= 100);
                Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
            }
            finally
            {
                scope.Dispose();
            }
        }

        [TestMethod]
        public async Task WhenClearAsync_ReleasesWhenTheGateClears()
        {
            var scope = RenderQuietGate.Engage();
            var watch = Stopwatch.StartNew();
            var wait = RenderQuietGate.WhenClearAsync(maxDeferMs: 5000);
            await Task.Delay(100);
            scope.Dispose();
            await wait;
            Assert.IsTrue(watch.ElapsedMilliseconds < 2000);
        }
    }
}
