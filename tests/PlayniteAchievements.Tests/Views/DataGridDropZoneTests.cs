using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class DataGridDropZoneTests
    {
        private const double RowHeight = 88;

        [TestMethod]
        public void Resolve_SplitsRowIntoThreeBandsWhenNestAllowed()
        {
            Assert.AreEqual(DataGridDropZoneKind.InsertBefore, DataGridDropZone.Resolve(10, RowHeight, nestAllowed: true));
            Assert.AreEqual(DataGridDropZoneKind.NestOnTarget, DataGridDropZone.Resolve(44, RowHeight, nestAllowed: true));
            Assert.AreEqual(DataGridDropZoneKind.InsertAfter, DataGridDropZone.Resolve(80, RowHeight, nestAllowed: true));
        }

        [TestMethod]
        public void Resolve_BandBoundariesForAn88PixelRow()
        {
            // Bands are 22px: below 22 inserts before, above 66 inserts after, between nests.
            Assert.AreEqual(DataGridDropZoneKind.InsertBefore, DataGridDropZone.Resolve(21.9, RowHeight, nestAllowed: true));
            Assert.AreEqual(DataGridDropZoneKind.NestOnTarget, DataGridDropZone.Resolve(22.0, RowHeight, nestAllowed: true));
            Assert.AreEqual(DataGridDropZoneKind.NestOnTarget, DataGridDropZone.Resolve(66.0, RowHeight, nestAllowed: true));
            Assert.AreEqual(DataGridDropZoneKind.InsertAfter, DataGridDropZone.Resolve(66.1, RowHeight, nestAllowed: true));
        }

        [TestMethod]
        public void Resolve_FallsBackToMidpointSplitWhenNestNotAllowed()
        {
            Assert.AreEqual(DataGridDropZoneKind.InsertBefore, DataGridDropZone.Resolve(44, RowHeight, nestAllowed: false));
            Assert.AreEqual(DataGridDropZoneKind.InsertAfter, DataGridDropZone.Resolve(44.1, RowHeight, nestAllowed: false));
            Assert.AreEqual(DataGridDropZoneKind.InsertBefore, DataGridDropZone.Resolve(0, RowHeight, nestAllowed: false));
            Assert.AreEqual(DataGridDropZoneKind.InsertAfter, DataGridDropZone.Resolve(RowHeight, RowHeight, nestAllowed: false));
        }

        [TestMethod]
        public void Resolve_DegenerateRowHeightNeverNests()
        {
            Assert.AreNotEqual(DataGridDropZoneKind.NestOnTarget, DataGridDropZone.Resolve(0, 0, nestAllowed: true));
            Assert.AreNotEqual(DataGridDropZoneKind.NestOnTarget, DataGridDropZone.Resolve(5, 0, nestAllowed: true));
            Assert.AreNotEqual(DataGridDropZoneKind.NestOnTarget, DataGridDropZone.Resolve(5, -1, nestAllowed: true));
        }
    }
}
