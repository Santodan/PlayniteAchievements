using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class MemoryDiagnosticsTests
    {
        private const long Mb = 1024 * 1024;

        private static MemorySnapshot Snapshot(long workingSetMb, long privateMb, long managedMb, int gen0, int gen1, int gen2)
        {
            return new MemorySnapshot
            {
                IsValid = true,
                WorkingSetBytes = workingSetMb * Mb,
                PrivateBytes = privateMb * Mb,
                ManagedBytes = managedMb * Mb,
                Gen0 = gen0,
                Gen1 = gen1,
                Gen2 = gen2
            };
        }

        [TestMethod]
        public void Capture_ReturnsValidNonZeroCounters()
        {
            var snapshot = MemoryDiagnostics.Capture();

            Assert.IsTrue(snapshot.IsValid);
            Assert.IsTrue(snapshot.WorkingSetBytes > 0);
            Assert.IsTrue(snapshot.PrivateBytes > 0);
            Assert.IsTrue(snapshot.ManagedBytes > 0);
        }

        [TestMethod]
        public void Format_WithoutBaseline_OmitsDeltas()
        {
            var line = MemoryDiagnostics.Format(
                "refresh.start", Snapshot(1500, 1600, 400, 10, 5, 2), default(MemorySnapshot), "mode=Full");

            Assert.AreEqual(
                "[MemPerf] point=refresh.start workingSetMb=1500.0 privateMb=1600.0 managedMb=400.0 gen0=10 gen1=5 gen2=2 mode=Full",
                line);
        }

        [TestMethod]
        public void Format_WithBaseline_AppendsSignedDeltas()
        {
            var baseline = Snapshot(1000, 1100, 500, 10, 5, 2);
            var current = Snapshot(1500, 1600, 400, 12, 6, 5);

            var line = MemoryDiagnostics.Format("refresh.end", current, baseline, null);

            StringAssert.Contains(line, "deltaWorkingSetMb=+500.0");
            StringAssert.Contains(line, "deltaManagedMb=-100.0");
            StringAssert.Contains(line, "deltaGen2=+3");
        }

        [TestMethod]
        public void Format_BlankPointAndDetail_UsesUnknownAndOmitsDetail()
        {
            var line = MemoryDiagnostics.Format("  ", Snapshot(1, 1, 1, 0, 0, 0), default(MemorySnapshot), "   ");

            StringAssert.StartsWith(line, "[MemPerf] point=unknown ");
            Assert.IsFalse(line.EndsWith(" "));
        }
    }
}
