using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using PlayniteAchievements.Common;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Common
{
    /// <summary>
    /// The retention report forces two blocking gen2 collections per call and is scheduled off
    /// every cache invalidation. While it answered to the same switch as timing tracing, a
    /// session spent editing custom data -- which invalidates per edit -- turned into a forced
    /// full collection every few seconds: one capture took ~1838 of them on a ~350MB managed
    /// heap, each suspending every thread including the UI.
    ///
    /// These pin the split: the cheap counters follow tracing, the collections do not.
    /// </summary>
    [TestClass]
    public class RetentionReportGateTests
    {
        private sealed class CapturingLogger : ILogger
        {
            public List<string> DebugMessages { get; } = new List<string>();

            public void Debug(string message) => DebugMessages.Add(message);
            public void Debug(Exception exception, string message) => DebugMessages.Add(message);
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
        }

        [TestCleanup]
        public void ClearOverride()
        {
            MemoryDiagnostics.TestEnabledOverride = null;
        }

        [TestMethod]
        public void WithNoOverride_TheRetentionReportFollowsMemoryTracingAlone()
        {
            MemoryDiagnostics.TestEnabledOverride = null;

            // Deliberately asserted as a relationship rather than against a literal, so this
            // still means something after PerfTracingEnabled is flipped for a release build.
            // The whole point of the split is that PerfTracingEnabled cannot reach this gate.
            Assert.AreEqual(
                MemoryDiagnostics.MemoryTracingEnabled,
                MemoryDiagnostics.RetentionReportEnabled,
                "The retention report must answer to MemoryTracingEnabled alone. If it ever ORs " +
                "in PerfScope.PerfTracingEnabled again, a timing build forces a full blocking " +
                "collection on every cache invalidation.");
        }

        [TestMethod]
        public void TracingOnlyBuild_LeavesTheCheapCountersArmedAndTheCollectionsDisarmed()
        {
            MemoryDiagnostics.TestEnabledOverride = null;

            if (MemoryDiagnostics.MemoryTracingEnabled)
            {
                Assert.Inconclusive(
                    "MemoryTracingEnabled is on, so this build is not the tracing-only case.");
            }

            Assert.IsFalse(
                MemoryDiagnostics.RetentionReportEnabled,
                "Memory tracing is off, so the forced collections must be off.");

            Assert.AreEqual(
                PerfScope.PerfTracingEnabled,
                MemoryDiagnostics.Enabled,
                "The cheap [MemPerf] counters and LeakWatch still ride along with timing tracing; " +
                "only the retention report was split off.");
        }

        [TestMethod]
        public void LogRetained_WhenDisabled_WritesNothingAndForcesNoCollection()
        {
            MemoryDiagnostics.TestEnabledOverride = false;
            var logger = new CapturingLogger();

            // Settle first: an unrelated gen2 from earlier test work would otherwise be counted
            // against this call.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.CollectionCount(2);

            MemoryDiagnostics.LogRetained(logger, "test.disabled", "detail=x");

            Assert.AreEqual(
                before,
                GC.CollectionCount(2),
                "A disabled retention report must not force a gen2 collection. This is the " +
                "regression that made custom-data editing stall.");
            Assert.AreEqual(0, logger.DebugMessages.Count, "A disabled report must log nothing.");
        }

        [TestMethod]
        public void LogRetained_WhenEnabled_StillReportsWithTheDetailItWasGiven()
        {
            MemoryDiagnostics.TestEnabledOverride = true;
            var logger = new CapturingLogger();

            MemoryDiagnostics.LogRetained(logger, "test.enabled", "images=1/2MB");

            Assert.AreEqual(
                1,
                logger.DebugMessages.Count,
                "Arming the switch must still produce the report; the split removed no capability.");
            StringAssert.Contains(logger.DebugMessages[0], "point=test.enabled");
            StringAssert.Contains(logger.DebugMessages[0], "images=1/2MB");
        }

        [TestMethod]
        public void TheCheapLogPath_IsNotGatedOnTheRetentionSwitch()
        {
            // Enabled and RetentionReportEnabled are separate gates, but TestEnabledOverride
            // drives both, so this pins the one thing that still distinguishes them under the
            // override: Log does not collect, LogRetained does.
            MemoryDiagnostics.TestEnabledOverride = true;
            var logger = new CapturingLogger();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.CollectionCount(2);

            MemoryDiagnostics.Log(logger, "test.cheap");

            Assert.AreEqual(
                before,
                GC.CollectionCount(2),
                "The plain counter line must never force a collection -- it is emitted from " +
                "refresh start/end and the sampler.");
            Assert.AreEqual(1, logger.DebugMessages.Count);
        }
    }
}
