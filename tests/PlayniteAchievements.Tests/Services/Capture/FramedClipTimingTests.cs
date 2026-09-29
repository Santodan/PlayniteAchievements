using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    /// <summary>
    /// The framed clip's hold, fade and notification rule: full opacity until the last second of
    /// the hold, a smoothstep to nothing at its end, and the notification only once the frame has
    /// gone.
    /// </summary>
    [TestClass]
    public class FramedClipTimingTests
    {
        [TestMethod]
        public void Opacity_HoldsFullUntilTheFadeBegins()
        {
            Assert.AreEqual(1.0, FramedClipTiming.Opacity(0, 5));
            Assert.AreEqual(1.0, FramedClipTiming.Opacity(4.0, 5));
        }

        [TestMethod]
        public void Opacity_FadesSmoothlyOverTheLastSecond()
        {
            Assert.AreEqual(0.5, FramedClipTiming.Opacity(4.5, 5), 1e-9);
            var early = FramedClipTiming.Opacity(4.1, 5);
            var late = FramedClipTiming.Opacity(4.9, 5);
            Assert.IsTrue(early > 0.9 && early < 1.0, $"early={early}");
            Assert.IsTrue(late > 0 && late < 0.1, $"late={late}");
        }

        [TestMethod]
        public void Opacity_IsGoneAtAndAfterTheHold()
        {
            Assert.AreEqual(0.0, FramedClipTiming.Opacity(5, 5));
            Assert.AreEqual(0.0, FramedClipTiming.Opacity(9, 5));
            Assert.AreEqual(0.0, FramedClipTiming.Opacity(-0.1, 5));
        }

        [TestMethod]
        public void Opacity_AHoldShorterThanTheFadeFadesThroughout()
        {
            Assert.AreEqual(1.0, FramedClipTiming.Opacity(0, 0.5), 1e-9);
            Assert.AreEqual(0.5, FramedClipTiming.Opacity(0.25, 0.5), 1e-9);
        }

        [TestMethod]
        public void Opacity_NoHoldStaysFull()
        {
            Assert.AreEqual(1.0, FramedClipTiming.Opacity(0, null));
            Assert.AreEqual(1.0, FramedClipTiming.Opacity(120, null));
        }

        [TestMethod]
        public void IncludesToast_OnlyWhenTheFrameHasGoneBeforeTheCard()
        {
            Assert.IsTrue(FramedClipTiming.IncludesToast(5, 0.2, 10));
            Assert.IsTrue(FramedClipTiming.IncludesToast(5, 0.2, 5.2));
            Assert.IsFalse(FramedClipTiming.IncludesToast(5, 0.2, 5.1));
            Assert.IsFalse(FramedClipTiming.IncludesToast(null, 0.2, 60));
        }

        [TestMethod]
        public void EndSeconds_StopsAtTheHoldOrTheClipEnd()
        {
            Assert.AreEqual(5.2, FramedClipTiming.EndSeconds(5, 0.2, 20), 1e-9);
            Assert.AreEqual(3.0, FramedClipTiming.EndSeconds(5, 0.2, 3), 1e-9);
            Assert.AreEqual(20.0, FramedClipTiming.EndSeconds(null, 0.2, 20), 1e-9);
        }
    }
}
