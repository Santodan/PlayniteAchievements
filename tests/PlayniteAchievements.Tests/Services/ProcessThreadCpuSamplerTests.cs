using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class ProcessThreadCpuSamplerTests
    {
        [TestMethod]
        public void AttributesAProcessSpikeToOsThreadsUsingTheActualWindow()
        {
            var snapshots = new Queue<ProcessThreadCpuSampler.Snapshot>();
            snapshots.Enqueue(new ProcessThreadCpuSampler.Snapshot
            {
                Qpc = 100, ProcessCpuMs = 1000,
                Threads = new Dictionary<int, double> { [101] = 300, [202] = 200 }
            });
            snapshots.Enqueue(new ProcessThreadCpuSampler.Snapshot
            {
                Qpc = 100 + Stopwatch.Frequency, ProcessCpuMs = 1093.75,
                Threads = new Dictionary<int, double> { [101] = 362.5, [202] = 231.25 }
            });
            var logs = new List<string>();
            using (var sampler = new ProcessThreadCpuSampler(() => snapshots.Dequeue(), logs.Add, false))
                sampler.SampleNow();
            Assert.AreEqual(1, logs.Count);
            StringAssert.Contains(logs[0], "elapsedMs=1000.00");
            StringAssert.Contains(logs[0], "processCpuMs=93.75");
            StringAssert.Contains(logs[0], "processCpuPercent=9.38");
            StringAssert.Contains(logs[0], "topThreads=101:62.50,202:31.25");
        }

        [TestMethod]
        public void HandlesReusedAndExitedThreadsAndDoesNotSampleAfterDisposal()
        {
            var snapshots = new Queue<ProcessThreadCpuSampler.Snapshot>();
            snapshots.Enqueue(new ProcessThreadCpuSampler.Snapshot
            {
                Qpc = 100, ProcessCpuMs = 1000,
                Threads = new Dictionary<int, double> { [101] = 300, [202] = 200 }
            });
            snapshots.Enqueue(new ProcessThreadCpuSampler.Snapshot
            {
                Qpc = 100 + Stopwatch.Frequency, ProcessCpuMs = 1020,
                Threads = new Dictionary<int, double> { [101] = 5, [303] = 10 }, UnavailableThreads = 1
            });
            var logs = new List<string>();
            var sampler = new ProcessThreadCpuSampler(() => snapshots.Dequeue(), logs.Add, false);
            sampler.SampleNow();
            sampler.Dispose();
            sampler.SampleNow();
            Assert.AreEqual(1, logs.Count);
            StringAssert.Contains(logs[0], "topThreads=303:10.00,101:5.00");
            StringAssert.Contains(logs[0], "unavailableThreads=1");
        }
    }
}
