using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using System;

namespace PlayniteAchievements.Tests.Common
{
    /// <summary>
    /// The probe registry exists so a surface can report its own occupancy to the retention
    /// report. The property that makes it safe is that it holds its targets weakly: a registry
    /// that rooted them would keep alive exactly the objects the report is meant to catch, and
    /// would then be the leak it was added to find.
    /// </summary>
    [TestClass]
    public class RetentionProbesTests
    {
        private sealed class Probe : IRetentionProbe
        {
            private readonly string _detail;

            public Probe(string name, string detail)
            {
                RetentionProbeName = name;
                _detail = detail;
            }

            public string RetentionProbeName { get; }

            public string DescribeRetention()
            {
                return _detail;
            }
        }

        private sealed class ThrowingProbe : IRetentionProbe
        {
            public string RetentionProbeName => "bad";

            public string DescribeRetention()
            {
                throw new InvalidOperationException("probe failed");
            }
        }

        [TestInitialize]
        public void EnableTracking()
        {
            MemoryDiagnostics.TestEnabledOverride = true;
        }

        [TestCleanup]
        public void RestoreTracking()
        {
            MemoryDiagnostics.TestEnabledOverride = null;
        }

        [TestMethod]
        public void Describe_IncludesDetailAndLiveCount()
        {
            var probe = new Probe("overview", "rows=12ach/3games");
            RetentionProbes.Register(probe);
            try
            {
                var description = RetentionProbes.Describe();

                StringAssert.Contains(description, "overviewLive=1");
                StringAssert.Contains(description, "rows=12ach/3games");
            }
            finally
            {
                RetentionProbes.Unregister(probe);
            }
        }

        [TestMethod]
        public void Describe_ReportsMoreThanOneLiveInstanceOfASurface()
        {
            // Two live instances of a surface that should be a singleton is itself the finding,
            // so the count is reported rather than collapsed.
            var first = new Probe("overview", "a=1");
            var second = new Probe("overview", "a=2");
            RetentionProbes.Register(first);
            RetentionProbes.Register(second);
            try
            {
                StringAssert.Contains(RetentionProbes.Describe(), "overviewLive=2");
            }
            finally
            {
                RetentionProbes.Unregister(first);
                RetentionProbes.Unregister(second);
            }
        }

        [TestMethod]
        public void Unregister_DropsTheProbe()
        {
            var probe = new Probe("overview", "rows=1");
            RetentionProbes.Register(probe);
            RetentionProbes.Unregister(probe);

            Assert.IsFalse(RetentionProbes.Describe().Contains("rows=1"));
        }

        [TestMethod]
        public void AThrowingProbe_DoesNotTakeDownTheReport()
        {
            var good = new Probe("overview", "rows=7");
            var bad = new ThrowingProbe();
            RetentionProbes.Register(good);
            RetentionProbes.Register(bad);
            try
            {
                StringAssert.Contains(RetentionProbes.Describe(), "rows=7");
            }
            finally
            {
                RetentionProbes.Unregister(good);
                RetentionProbes.Unregister(bad);
            }
        }

        [TestMethod]
        public void Disabled_RegistersNothing()
        {
            MemoryDiagnostics.TestEnabledOverride = false;
            var probe = new Probe("overview", "rows=99");
            RetentionProbes.Register(probe);
            try
            {
                Assert.AreEqual(string.Empty, RetentionProbes.Describe());
            }
            finally
            {
                RetentionProbes.Unregister(probe);
            }
        }
    }
}
