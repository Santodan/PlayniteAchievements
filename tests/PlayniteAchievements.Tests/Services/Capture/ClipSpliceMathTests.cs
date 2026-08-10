using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class ClipSpliceMathTests
    {
        private const long Second = 10_000_000L;

        // A keyframe every second, as the capture encoder writes them (MaxKeyframeSpacing = fps).
        private static List<long> Keyframes(int count)
        {
            return Enumerable.Range(0, count).Select(i => i * Second).ToList();
        }

        private static string Describe(IEnumerable<ClipSpliceMath.Span> spans)
        {
            return string.Join(" ", spans.Select(s => s.ToString()));
        }

        [TestMethod]
        public void Plan_CoversTheClipWithoutGapsOrOverlaps()
        {
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: Second / 2, clipEnd: 20 * Second,
                toastStart: 15 * Second, toastEnd: 18 * Second);

            Assert.IsTrue(spans.Count > 0, Describe(spans));
            Assert.AreEqual(Second / 2, spans[0].Start, Describe(spans));
            Assert.AreEqual(20 * Second, spans[spans.Count - 1].End, Describe(spans));
            for (var i = 1; i < spans.Count; i++)
            {
                Assert.AreEqual(spans[i - 1].End, spans[i].Start, "gap or overlap: " + Describe(spans));
            }
        }

        [TestMethod]
        public void Plan_ReencodesTheLeadingFragmentUpToTheNextKeyframe()
        {
            // The clip starts half a second into a group, so those frames cannot be copied.
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: Second / 2, clipEnd: 20 * Second,
                toastStart: 15 * Second, toastEnd: 18 * Second);

            Assert.IsTrue(spans[0].Reencode, Describe(spans));
            Assert.AreEqual(Second, spans[0].End, "should stop at the next keyframe: " + Describe(spans));
            Assert.IsFalse(spans[1].Reencode, Describe(spans));
        }

        [TestMethod]
        public void Plan_StartingOnAKeyframeNeedsNoLeadingReencode()
        {
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: 2 * Second, clipEnd: 20 * Second,
                toastStart: 15 * Second, toastEnd: 18 * Second);

            Assert.IsFalse(spans[0].Reencode, Describe(spans));
            Assert.AreEqual(2 * Second, spans[0].Start, Describe(spans));
        }

        [TestMethod]
        public void Plan_ReencodesFromTheCardOnsetToTheKeyframeAfterIt()
        {
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: 0, clipEnd: 20 * Second,
                toastStart: 15 * Second + Second / 4, toastEnd: 18 * Second + Second / 4);

            var reencoded = spans.Where(s => s.Reencode).ToList();
            Assert.AreEqual(1, reencoded.Count, Describe(spans));

            // Begins exactly at the card, since the encoder makes its own keyframe there.
            Assert.AreEqual(15 * Second + Second / 4, reencoded[0].Start, Describe(spans));

            // Ends on the first keyframe after the card, so the copy that follows has one to join at.
            Assert.AreEqual(19 * Second, reencoded[0].End, Describe(spans));
        }

        [TestMethod]
        public void Plan_TailIsCopiedAndSimplyTruncated()
        {
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: 0, clipEnd: 20 * Second + Second / 3,
                toastStart: 5 * Second, toastEnd: 6 * Second);

            var last = spans[spans.Count - 1];
            Assert.IsFalse(last.Reencode, "the tail never needs the encoder: " + Describe(spans));
            Assert.AreEqual(20 * Second + Second / 3, last.End, Describe(spans));
        }

        [TestMethod]
        public void Plan_MergesTheLeadingFragmentIntoTheCardSpanWhenTheyMeet()
        {
            // Card starts inside the very first group, so there is one run, not two.
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: Second / 2, clipEnd: 20 * Second,
                toastStart: Second / 2, toastEnd: 3 * Second);

            var reencoded = spans.Where(s => s.Reencode).ToList();
            Assert.AreEqual(1, reencoded.Count, Describe(spans));
            Assert.AreEqual(Second / 2, reencoded[0].Start, Describe(spans));
            Assert.AreEqual(4 * Second, reencoded[0].End, Describe(spans));
        }

        [TestMethod]
        public void Plan_MostOfATypicalClipIsCopied()
        {
            // The shape of a real clip: 15s of pre-roll, a card for 5s, a short tail.
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: Second / 2, clipEnd: 28 * Second,
                toastStart: 15 * Second, toastEnd: 20 * Second);

            var copied = ClipSpliceMath.CopiedFraction(spans);
            Assert.IsTrue(copied > 0.75, "expected most of the clip copied, got " + copied.ToString("0.000"));
        }

        [TestMethod]
        public void Plan_CardCoveringEverythingFallsBackToOneReencode()
        {
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: 0, clipEnd: 10 * Second,
                toastStart: 0, toastEnd: 10 * Second);

            Assert.AreEqual(1, spans.Count, Describe(spans));
            Assert.IsTrue(spans[0].Reencode, Describe(spans));
            Assert.AreEqual(0d, ClipSpliceMath.CopiedFraction(spans));
        }

        [TestMethod]
        public void Plan_NoCardStillCopiesEverythingAfterTheLeadingFragment()
        {
            // toastEnd before toastStart means there is nothing to draw.
            var spans = ClipSpliceMath.Plan(
                Keyframes(30), clipStart: 2 * Second, clipEnd: 12 * Second,
                toastStart: 1, toastEnd: 0);

            Assert.AreEqual(1, spans.Count, Describe(spans));
            Assert.IsFalse(spans[0].Reencode, Describe(spans));
        }

        [TestMethod]
        public void Plan_EmptyOrInvertedWindowPlansNothing()
        {
            Assert.AreEqual(0, ClipSpliceMath.Plan(Keyframes(5), 0, 0, 0, 0).Count);
            Assert.AreEqual(0, ClipSpliceMath.Plan(Keyframes(5), 5 * Second, Second, 0, 0).Count);
            Assert.AreEqual(0, ClipSpliceMath.Plan(null, 0, Second, 0, 0).Count);
        }

        [TestMethod]
        public void Plan_CardRunningPastTheLastKeyframeReencodesToTheEnd()
        {
            var spans = ClipSpliceMath.Plan(
                Keyframes(10), clipStart: 0, clipEnd: 12 * Second,
                toastStart: 9 * Second + Second / 2, toastEnd: 11 * Second);

            var last = spans[spans.Count - 1];
            Assert.IsTrue(last.Reencode, Describe(spans));
            Assert.AreEqual(12 * Second, last.End, Describe(spans));
        }
    }
}
