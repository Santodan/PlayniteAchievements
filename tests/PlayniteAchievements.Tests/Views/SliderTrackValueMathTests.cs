using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class SliderTrackValueMathTests
    {
        [TestMethod]
        public void SamePixel_AlwaysMapsToSameTimestamp()
        {
            // On a 22-second timeline with a 20px thumb, x=100.909... is exactly ten seconds.
            const double x = 10 + (200 * (10.0 / 22));

            var first = SliderTrackValueMath.FromHorizontalPoint(x, 220, 20, 0, 22, false);
            var second = SliderTrackValueMath.FromHorizontalPoint(x, 220, 20, 0, 22, false);

            Assert.AreEqual(10, first, 0.000001);
            Assert.AreEqual(first, second);
        }

        [TestMethod]
        public void ThumbCenters_MapToRangeEndpoints()
        {
            Assert.AreEqual(0, SliderTrackValueMath.FromHorizontalPoint(10, 220, 20, 0, 22, false));
            Assert.AreEqual(22, SliderTrackValueMath.FromHorizontalPoint(210, 220, 20, 0, 22, false));
        }

        [TestMethod]
        public void PointsOutsideThumbTravel_AreClamped()
        {
            Assert.AreEqual(0, SliderTrackValueMath.FromHorizontalPoint(-50, 220, 20, 0, 22, false));
            Assert.AreEqual(22, SliderTrackValueMath.FromHorizontalPoint(500, 220, 20, 0, 22, false));
        }

        [TestMethod]
        public void ReversedDirection_InvertsTheTimeline()
        {
            Assert.AreEqual(22, SliderTrackValueMath.FromHorizontalPoint(10, 220, 20, 0, 22, true));
            Assert.AreEqual(0, SliderTrackValueMath.FromHorizontalPoint(210, 220, 20, 0, 22, true));
        }

        [TestMethod]
        public void OversizedThumb_UsesAStableFallback()
        {
            Assert.AreEqual(11, SliderTrackValueMath.FromHorizontalPoint(110, 220, 500, 0, 22, false));
        }
    }
}
