using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class LocalPollingFingerprintGateTests
    {
        [TestMethod]
        public void FirstReadRefreshesThenUnchangedSkipsAndChangedRefreshes()
        {
            var gate = new LocalPollingFingerprintGate();
            Assert.IsTrue(gate.NeedsRefresh(true, "source-a"));
            gate.MarkRefreshed("source-a");
            Assert.IsFalse(gate.NeedsRefresh(true, "source-a"));
            Assert.IsTrue(gate.NeedsRefresh(true, "source-b"));
            // A failed refresh does not advance the baseline.
            Assert.IsTrue(gate.NeedsRefresh(true, "source-b"));
            gate.MarkRefreshed("source-b");
            Assert.IsFalse(gate.NeedsRefresh(true, "source-b"));
        }

        [TestMethod]
        public void UnknownFingerprintAlwaysRefreshesAndDisablingResetsBaseline()
        {
            var gate = new LocalPollingFingerprintGate();
            gate.MarkRefreshed("source-a");
            Assert.IsTrue(gate.NeedsRefresh(true, null));
            Assert.IsTrue(gate.NeedsRefresh(true, ""));
            Assert.IsTrue(gate.NeedsRefresh(false, "source-a"));
            Assert.IsTrue(gate.NeedsRefresh(false, "source-a"));
            Assert.IsTrue(gate.NeedsRefresh(true, "source-a"));
        }

        [TestMethod]
        public void NewSessionHasNoPreviousBaselineAndChangesDuringReadAreNotLost()
        {
            var gate = new LocalPollingFingerprintGate();
            gate.MarkRefreshed("before-read");
            Assert.IsTrue(gate.NeedsRefresh(true, "changed-during-read"));
            Assert.IsTrue(new LocalPollingFingerprintGate().NeedsRefresh(true, "before-read"));
        }
    }
}
