using System.Globalization;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class ImageFitDimensionConverterTests
    {
        private const double Tolerance = 0.001;

        private static double Convert(string parameter, params object[] values)
        {
            var converter = new ImageFitDimensionConverter();
            return (double)converter.Convert(values, typeof(double), parameter, CultureInfo.InvariantCulture);
        }

        [TestMethod]
        public void ShortRow_WideCell_HeightIsTheConstraint()
        {
            // Custom (small) row height: the badge must fit the row after
            // vertical padding and must not exceed the column width.
            var size = Convert("platform,width,8,12,18", 40d, 200d);

            Assert.AreEqual(40d - 12d, size, Tolerance);
            Assert.IsTrue(size <= 200d - 8d);
        }

        [TestMethod]
        public void TallRow_NarrowCell_WidthIsTheConstraint()
        {
            var size = Convert("platform,width,8,12,18", 200d, 40d);

            Assert.AreEqual(40d - 8d, size, Tolerance);
            Assert.IsTrue(size <= 200d - 12d);
        }

        [TestMethod]
        public void WidthAndHeight_ReturnTheSameSquareSize()
        {
            var width = Convert("platform,width,8,12,18", 60d, 90d);
            var height = Convert("platform,height,8,12,18", 60d, 90d);

            Assert.AreEqual(width, height, Tolerance);
        }

        [TestMethod]
        public void UnsetValues_UseFallbackSize()
        {
            var size = Convert(
                "platform,width,8,12,18",
                DependencyProperty.UnsetValue,
                DependencyProperty.UnsetValue);

            Assert.AreEqual(18d, size, Tolerance);
        }

        [TestMethod]
        public void TinyCell_ClampsToAtLeastOnePixel()
        {
            // Available extents never go below 1 even when padding exceeds the cell.
            var size = Convert("platform,width,8,12,18", 5d, 5d);

            Assert.AreEqual(1d, size, Tolerance);
        }

        [TestMethod]
        public void MissingPaddingTokens_UseDefaults()
        {
            // Defaults: horizontal 8, vertical 8, fallback 12.
            var size = Convert("platform,width", 30d, 200d);

            Assert.AreEqual(30d - 8d, size, Tolerance);
        }
    }
}
