using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The retention report is scheduled off every CacheInvalidated, and each run forces two
    /// blocking gen2 collections. It used to queue an independent delayed task per invalidation
    /// with no dedup, so a burst -- one invalidation per custom-data edit -- arrived one delay
    /// later as a storm of full collections. A capture of an editing session held 919 of them.
    ///
    /// PlayniteAchievementsPlugin.StartPage.cs is not linked into this project, so these assert
    /// against the source the way the other wiring definition tests here do.
    /// </summary>
    [TestClass]
    public class RetentionDiagnosticsSchedulerDefinitionTests
    {
        [TestMethod]
        public void TheScheduler_GatesOnTheRetentionSwitchRatherThanTracing()
        {
            var body = Between(
                ReadStartPage(),
                "internal void ScheduleRetentionDiagnostics(string point, int delaySeconds)",
                "private void FlushRetentionDiagnostics(");

            StringAssert.Contains(
                body,
                "MemoryDiagnostics.RetentionReportEnabled",
                "Gating on Enabled instead lets a timing-only build arm the forced collections.");

            Assert.IsFalse(
                body.Contains("MemoryDiagnostics.Enabled"),
                "The broader gate must not appear here: it ORs in PerfScope.PerfTracingEnabled, " +
                "which is committed true, so it would arm the collections in every build.");
        }

        [TestMethod]
        public void TheScheduler_CoalescesInsteadOfQueuingATaskPerInvalidation()
        {
            var body = Between(
                ReadStartPage(),
                "internal void ScheduleRetentionDiagnostics(string point, int delaySeconds)",
                "private void FlushRetentionDiagnostics(");

            // Trailing edge: every call pushes the deadline out, so a burst produces one report.
            StringAssert.Contains(body, "_retentionDiagnosticsTimer.Change(");
            StringAssert.Contains(body, "System.Threading.Timeout.Infinite");

            Assert.IsFalse(
                body.Contains("Task.Run"),
                "A task per call is the regression: N invalidations became N delayed reports, " +
                "each forcing two blocking gen2 collections.");
            Assert.IsFalse(
                body.Contains("Task.Delay"),
                "The delay must come from the coalescing timer, not from a per-call sleep.");
        }

        [TestMethod]
        public void TheCoalescedTimer_IsDisposedWithTheOtherStartPageTimer()
        {
            var source = ReadStartPage();

            StringAssert.Contains(
                source,
                "_retentionDiagnosticsTimer?.Dispose();",
                "An undisposed timer keeps the plugin alive past teardown and can fire a report " +
                "into a half-disposed object graph.");

            // The pending point is what FlushRetentionDiagnostics keys off; leaving it set would
            // let a disposed-then-recreated timer emit a report for a stale point.
            StringAssert.Contains(source, "_pendingRetentionPoint = null;");
        }

        [TestMethod]
        public void TheFlush_NoOpsWhenNothingIsPending()
        {
            var body = Between(
                ReadStartPage(),
                "private void FlushRetentionDiagnostics(",
                "private void LogRetentionDiagnostics(");

            // Read-and-clear under the lock, then bail. Without the null check a spurious timer
            // callback would run the whole census and force a collection for no reason.
            StringAssert.Contains(body, "_pendingRetentionPoint = null;");
            StringAssert.Contains(body, "if (point == null)");
            StringAssert.Contains(
                body,
                "LogRetentionDiagnostics(point)",
                "The flush must report the point that was pending, not a hardcoded one.");
        }

        [TestMethod]
        public void TheDefaultEntryPoint_StillReportsRefreshSettledAfterTheSettleDelay()
        {
            var source = ReadStartPage();

            // The 20s delay is the point of the report: it measures the resting state, not the
            // peak. Coalescing changed how many reports fire, not when one fires.
            StringAssert.Contains(source, "ScheduleRetentionDiagnostics(\"refresh.settled\", 20)");
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadStartPage()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            var parts = new[] { "source", "PlayniteAchievementsPlugin.StartPage.cs" };
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find PlayniteAchievementsPlugin.StartPage.cs.");
            return null;
        }
    }
}
