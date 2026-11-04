using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics.Tracing;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass, DoNotParallelize]
    public class PollingDebugLogTests
    {
        [TestMethod]
        public async Task EnabledLogCorrelatesAsyncStagesAndStopsWhenDisabled()
        {
            var folder = Path.Combine(Path.GetTempPath(), "poll-debug-test-" + Guid.NewGuid());
            try
            {
                PollingDebugLog.Configure(folder, true);
                using (PollingDebugLog.BeginPoll("game-test"))
                {
                    await Task.Run(() =>
                    {
                        using (PollingDebugLog.Stage("test-stage")) PollingDebugLog.Write("async-marker");
                    });
                }
                PollingDebugLog.Write("outside-poll-marker");
                PollingDebugLog.Configure(null, false);
                var path = Path.Combine(folder, "PollingDebug.log");
                var content = File.ReadAllText(path);
                StringAssert.Contains(content, "poll.begin games=game-test");
                StringAssert.Contains(content, "async-marker");
                StringAssert.Contains(content, "stage=test-stage elapsedMs=");
                StringAssert.Contains(content, "stage=poll.total elapsedMs=");
                StringAssert.Contains(content, "managedTid=");
                StringAssert.Contains(content, "osTid=");
                StringAssert.Contains(content, "parentStageId=");
                StringAssert.Contains(content, "processWindowCpuMs=");
                StringAssert.Contains(content, "threadWindowCpuMs=");
                StringAssert.Contains(content, "threadWindowAllocatedBytes=");
                StringAssert.Contains(content, "managedHeapDeltaBytes=");
                StringAssert.Contains(content, "gc0=");
                Assert.IsFalse(content.Contains("outside-poll-marker"));
                Assert.IsNull(PollingDebugLog.BeginPoll("disabled"));
                Assert.AreEqual(content, File.ReadAllText(path));
            }
            finally
            {
                PollingDebugLog.Configure(null, false);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void ThreadSwitchDoesNotReportMisleadingThreadCpuAndEtwSchemaIsValid()
        {
            var folder = Path.Combine(Path.GetTempPath(), "poll-debug-test-" + Guid.NewGuid());
            try
            {
                using (var listener = new PollEventListener())
                {
                    PollingDebugLog.Configure(folder, true);
                    using (PollingDebugLog.BeginPoll("thread-test"))
                    {
                        var stage = PollingDebugLog.Stage("cross-thread-stage");
                        var thread = new Thread(() => stage.Dispose());
                        thread.Start();
                        thread.Join();
                    }
                    PollingDebugLog.Configure(null, false);
                    var content = File.ReadAllText(Path.Combine(folder, "PollingDebug.log"));
                    StringAssert.Contains(content, "threadSwitched=True threadWindowCpuMs=unavailable");
                    StringAssert.Contains(content, "threadWindowAllocatedBytes=unavailable");
                    Assert.IsTrue(listener.Ids.Contains(1));
                    Assert.IsTrue(listener.Ids.Contains(2));
                    Assert.IsFalse(listener.Ids.Contains(0), "EventSource reported an event schema error.");
                }
            }
            finally
            {
                PollingDebugLog.Configure(null, false);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private sealed class PollEventListener : EventListener
        {
            public readonly List<int> Ids = new List<int>();
            protected override void OnEventSourceCreated(EventSource source)
            {
                if (source.Name == "PlayniteAchievements-Polling") EnableEvents(source, EventLevel.Informational);
            }
            protected override void OnEventWritten(EventWrittenEventArgs args)
            {
                lock (Ids) Ids.Add(args.EventId);
            }
        }

        [TestMethod]
        public void DisabledLoggingDoesNotCreateDirectory()
        {
            var folder = Path.Combine(Path.GetTempPath(), "poll-debug-test-" + Guid.NewGuid());
            PollingDebugLog.Configure(folder, false);
            Assert.IsNull(PollingDebugLog.BeginPoll("disabled"));
            Assert.IsNull(PollingDebugLog.Stage("disabled"));
            Assert.IsFalse(Directory.Exists(folder));
        }
    }
}
