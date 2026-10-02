using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Common
{
    /// <summary>
    /// The retention counters are only useful if they report something. A canary that sampled one
    /// row with FirstOrDefault reported nothing at all when the sampled set was empty, and stayed
    /// silent through a session that grew the managed heap from 122 MB to 912 MB. These pin the
    /// reporting contract rather than the collection behaviour: whether a weak reference has been
    /// collected yet is the GC's business and not deterministic enough to assert.
    /// </summary>
    [TestClass]
    public class LeakWatchTests
    {
        private sealed class Row
        {
            public int Id;
        }

        [TestInitialize]
        public void EnableTracking()
        {
            MemoryDiagnostics.TestEnabledOverride = true;
            LeakWatch.ResetForTests();
        }

        [TestCleanup]
        public void RestoreTracking()
        {
            LeakWatch.ResetForTests();
            MemoryDiagnostics.TestEnabledOverride = null;
        }

        [TestMethod]
        public void TrackAll_RecordsEveryInstance_NotJustTheFirst()
        {
            var rows = Enumerable.Range(0, 7).Select(id => new Row { Id = id }).ToList();

            LeakWatch.TrackAll("rows", rows);

            StringAssert.Contains(
                LeakWatch.DescribeLive(),
                "rows:7/7",
                "All seven are still referenced by the local list, so all seven must read live, " +
                "and the created total must be seven rather than one.");
        }

        [TestMethod]
        public void TrackAll_EmptySet_RecordsNothing()
        {
            // The exact case the old canary hit: the overview's summary-only path leaves the
            // achievement list empty, so a sampled row was null and nothing was recorded.
            LeakWatch.TrackAll("rows", new List<Row>());

            Assert.AreEqual("none", LeakWatch.DescribeLive());
        }

        [TestMethod]
        public void TrackAll_SkipsNullEntriesButKeepsTheRest()
        {
            var kept = new Row();

            LeakWatch.TrackAll("rows", new List<Row> { null, kept, null });

            StringAssert.Contains(LeakWatch.DescribeLive(), "rows:1/1");
        }

        [TestMethod]
        public void CreatedTotal_KeepsCountingPastTheTrackingCap()
        {
            // The cap trims the weak-reference list, so its length stops being the number
            // created. A leaking kind would otherwise read "256/256" and hide its growth rate.
            var rows = Enumerable.Range(0, 300).Select(id => new Row { Id = id }).ToList();

            LeakWatch.TrackAll("rows", rows);

            StringAssert.Contains(
                LeakWatch.DescribeLive(),
                "/300",
                "The denominator is a running creation total, not the trimmed list length.");
        }

        [TestMethod]
        public void Disabled_RecordsNothing()
        {
            MemoryDiagnostics.TestEnabledOverride = false;

            LeakWatch.Track("rows", new Row());
            LeakWatch.TrackAll("rows", new List<Row> { new Row() });

            Assert.AreEqual(string.Empty, LeakWatch.DescribeLive());
        }
    }
}
