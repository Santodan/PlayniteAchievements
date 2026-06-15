using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class ColumnWidthNormalizationTests
    {
        [TestMethod]
        public void TryBuildNormalizedWidths_DefaultSeedsLargerThanContainerUseEqualFractions()
        {
            var keys = new[] { "A", "B", "C", "D" };
            var seedWidths = new[] { 240d, 240d, 240d, 240d };
            var floorWidths = new[] { 30d, 30d, 30d, 30d };

            var result = ColumnWidthNormalization.TryBuildNormalizedWidths(
                keys,
                seedWidths,
                floorWidths,
                protectedKey: null,
                rescaleAll: true,
                targetWidth: 400d,
                out var normalized);

            Assert.IsTrue(result);
            Assert.AreEqual(100d, normalized["A"]);
            Assert.AreEqual(100d, normalized["B"]);
            Assert.AreEqual(100d, normalized["C"]);
            Assert.AreEqual(100d, normalized["D"]);
            Assert.AreEqual(400d, normalized.Values.Sum());
        }

        [TestMethod]
        public void TryBuildNormalizedWidths_ProtectedResizedColumnStaysStable()
        {
            var keys = new[] { "A", "B", "C" };
            var seedWidths = new[] { 300d, 420d, 300d };
            var floorWidths = new[] { 80d, 80d, 80d };

            var result = ColumnWidthNormalization.TryBuildNormalizedWidths(
                keys,
                seedWidths,
                floorWidths,
                protectedKey: "B",
                rescaleAll: false,
                targetWidth: 900d,
                out var normalized);

            Assert.IsTrue(result);
            Assert.AreEqual(300d, normalized["A"]);
            Assert.AreEqual(420d, normalized["B"]);
            Assert.AreEqual(180d, normalized["C"]);
            Assert.AreEqual(900d, normalized.Values.Sum());
        }

        [TestMethod]
        public void TryBuildNormalizedWidths_ProtectedColumnUsesImmediateRightNeighborByDefault()
        {
            var keys = new[] { "A", "B", "C", "D" };
            var seedWidths = new[] { 200d, 350d, 200d, 200d };
            var floorWidths = new[] { 80d, 80d, 80d, 80d };

            var result = ColumnWidthNormalization.TryBuildNormalizedWidths(
                keys,
                seedWidths,
                floorWidths,
                protectedKey: "B",
                rescaleAll: false,
                targetWidth: 900d,
                out var normalized);

            Assert.IsTrue(result);
            Assert.AreEqual(200d, normalized["A"]);
            Assert.AreEqual(350d, normalized["B"]);
            Assert.AreEqual(150d, normalized["C"]);
            Assert.AreEqual(200d, normalized["D"]);
            Assert.AreEqual(900d, normalized.Values.Sum());
        }

        [TestMethod]
        public void TryBuildNormalizedWidths_PreferredLeftNeighborAbsorbsDelta()
        {
            var keys = new[] { "A", "B", "C" };
            var seedWidths = new[] { 300d, 420d, 300d };
            var floorWidths = new[] { 80d, 80d, 80d };

            var result = ColumnWidthNormalization.TryBuildNormalizedWidths(
                keys,
                seedWidths,
                floorWidths,
                protectedKey: "B",
                preferredAbsorberKey: "A",
                rescaleAll: false,
                targetWidth: 900d,
                out var normalized);

            Assert.IsTrue(result);
            Assert.AreEqual(180d, normalized["A"]);
            Assert.AreEqual(420d, normalized["B"]);
            Assert.AreEqual(300d, normalized["C"]);
            Assert.AreEqual(900d, normalized.Values.Sum());
        }

        [TestMethod]
        public void TryBuildNormalizedWidths_RescaleAllHonorsMinimumWidths()
        {
            var keys = new[] { "A", "B", "C" };
            var seedWidths = new[] { 500d, 50d, 50d };
            var floorWidths = new[] { 120d, 120d, 120d };

            var result = ColumnWidthNormalization.TryBuildNormalizedWidths(
                keys,
                seedWidths,
                floorWidths,
                protectedKey: null,
                rescaleAll: true,
                targetWidth: 600d,
                out var normalized);

            Assert.IsTrue(result);
            Assert.IsTrue(normalized.Values.All(width => width >= 120d));
            Assert.AreEqual(360d, normalized["A"]);
            Assert.AreEqual(120d, normalized["B"]);
            Assert.AreEqual(120d, normalized["C"]);
            Assert.AreEqual(600d, normalized.Values.Sum());
        }

        [TestMethod]
        public void TryBuildNormalizedWidths_FreshEmptyKeysDoNotProducePersistableWidths()
        {
            var result = ColumnWidthNormalization.TryBuildNormalizedWidths(
                new List<string>(),
                new List<double>(),
                new List<double>(),
                protectedKey: null,
                rescaleAll: true,
                targetWidth: 600d,
                out var normalized);

            Assert.IsFalse(result);
            Assert.IsNull(normalized);
        }
    }
}
