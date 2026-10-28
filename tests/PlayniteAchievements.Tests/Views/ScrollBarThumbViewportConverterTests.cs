using System.Globalization;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class ScrollBarThumbViewportConverterTests
    {
        private static readonly ScrollBarThumbViewportConverter Converter = new ScrollBarThumbViewportConverter();

        private static double Convert(double viewport, double maximum, double width, double height, Orientation orientation) =>
            (double)Converter.Convert(
                new object[] { viewport, 0d, maximum, width, height, orientation },
                typeof(double),
                "32",
                CultureInfo.InvariantCulture);

        private static double ThumbLength(double viewport, double maximum, double trackLength) =>
            trackLength * viewport / (maximum + viewport);

        [TestMethod]
        public void LongContent_GrowsTheThumbToTheMinimumInEitherOrientation()
        {
            var vertical = Convert(100, 20000, 17, 300, Orientation.Vertical);
            var horizontal = Convert(100, 20000, 300, 17, Orientation.Horizontal);

            Assert.AreEqual(32d, ThumbLength(vertical, 20000, 300), 0.001d);
            Assert.AreEqual(32d, ThumbLength(horizontal, 20000, 300), 0.001d);
        }

        [TestMethod]
        public void ThumbAlreadyLongEnough_KeepsTheViewport()
        {
            Assert.AreEqual(100d, Convert(100, 200, 17, 300, Orientation.Vertical));
        }

        [TestMethod]
        public void ShortTrack_CapsTheMinimumAtHalfTheTrack()
        {
            var viewport = Convert(10, 20000, 17, 40, Orientation.Vertical);

            Assert.AreEqual(20d, ThumbLength(viewport, 20000, 40), 0.001d);
        }

        [TestMethod]
        public void NothingToScroll_KeepsTheViewport()
        {
            Assert.AreEqual(100d, Convert(100, 0, 17, 300, Orientation.Vertical));
        }
    }
}
