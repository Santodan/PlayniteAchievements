using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;
using System.Windows;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class RoundedClipTests
    {
        [TestMethod]
        public void ComputeRadius_NegativeOrZero_IsSquare()
        {
            Assert.AreEqual(0d, RoundedClip.ComputeRadius(-4, new Size(32, 32)));
            Assert.AreEqual(0d, RoundedClip.ComputeRadius(0, new Size(32, 32)));
        }

        [TestMethod]
        public void ComputeRadius_WithinHalfShortSide_IsUnchanged()
        {
            Assert.AreEqual(12d, RoundedClip.ComputeRadius(12, new Size(32, 32)));
        }

        [TestMethod]
        public void ComputeRadius_LargeValue_CapsAtHalfShortSide()
        {
            Assert.AreEqual(16d, RoundedClip.ComputeRadius(100, new Size(32, 48)));
        }

        [TestMethod]
        public void ComputeRadius_EmptySize_IsSquare()
        {
            Assert.AreEqual(0d, RoundedClip.ComputeRadius(6, new Size(0, 0)));
        }
    }
}
