using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class OverlaySplicePlanTests
    {
        private const long Second = 10_000_000L;

        // 50 fps keeps every frame time an exact tick count; one keyframe per second, like capture.
        private const int Fps = 50;
        private const long FrameTicks = Second / Fps;

        private static List<OverlaySplicePlan.SampleInfo> Clip(double seconds, long firstTime = 0)
        {
            var samples = new List<OverlaySplicePlan.SampleInfo>();
            var frames = (int)(seconds * Fps);
            for (var i = 0; i < frames; i++)
            {
                samples.Add(new OverlaySplicePlan.SampleInfo
                {
                    Time = firstTime + (i * FrameTicks),
                    Duration = FrameTicks,
                    IsKeyframe = i % Fps == 0,
                });
            }

            return samples;
        }

        private static long Ticks(double seconds)
        {
            return (long)(seconds * Second);
        }

        private static OverlaySplicePlan.Interval Card(double startSeconds, double endSeconds)
        {
            return new OverlaySplicePlan.Interval(Ticks(startSeconds), Ticks(endSeconds));
        }

        private static string Describe(OverlaySplicePlan plan)
        {
            return string.Join(" ", plan.Runs.Select(r =>
                (r.Kind == OverlaySplicePlan.RunKind.Copy ? "copy" : "recode") +
                "[" + (r.Start / (double)Second).ToString("0.##") + "," + (r.End / (double)Second).ToString("0.##") + ")"));
        }

        private static void AssertAlternates(OverlaySplicePlan plan)
        {
            for (var i = 1; i < plan.Runs.Count; i++)
            {
                Assert.AreNotEqual(plan.Runs[i - 1].Kind, plan.Runs[i].Kind, "adjacent runs of one kind: " + Describe(plan));
                Assert.AreEqual(plan.Runs[i - 1].End, plan.Runs[i].Start, "runs must be contiguous: " + Describe(plan));
            }

            Assert.AreEqual(plan.Start, plan.Runs[0].Start);
            Assert.AreEqual(plan.End, plan.Runs[plan.Runs.Count - 1].End);
        }

        [TestMethod]
        public void Plan_ReencodesTheHeadCopiesWholeGopsAndReencodesAroundTheCard()
        {
            // 10 s clip whose window starts half a second in; the card shows from 5.5 s to 9.5 s.
            var plan = OverlaySplicePlan.TryPlan(
                Clip(10), Ticks(0.5), new[] { Card(5.5, 9.5) }, Ticks(10), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual("recode[0.5,1) copy[1,5) recode[5,10)", Describe(plan));
            AssertAlternates(plan);
            Assert.AreEqual(25, plan.Runs[0].Frames);
            Assert.AreEqual(200, plan.Runs[1].Frames);
            Assert.AreEqual(250, plan.Runs[2].Frames, "the tail past the card is under a GOP and folds in");
            Assert.AreEqual(200, plan.CopyFrames);
            Assert.AreEqual(275, plan.ReencodeFrames);
            Assert.AreEqual(2, plan.ReencodeRuns);
            Assert.AreEqual(Ticks(4.0), plan.CopyTicks);
        }

        [TestMethod]
        public void Plan_CopiesAgainAfterACardWhenEnoughFootageFollows()
        {
            // The card ends at 4.2 s; the next keyframe is 5 s and 15 s of clip follow it. The one
            // second between the head and the card is too short to copy, so the head, that GOP and
            // the card's GOPs become a single re-encoded run.
            var plan = OverlaySplicePlan.TryPlan(
                Clip(20), Ticks(0.5), new[] { Card(2.5, 4.2) }, Ticks(20), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual("recode[0.5,5) copy[5,20)", Describe(plan));
            AssertAlternates(plan);
            Assert.AreEqual(750, plan.CopyFrames);
        }

        [TestMethod]
        public void Plan_InterleavesSeveralOverlays()
        {
            // Two cards far apart with a copyable stretch between, and a third overlapping the second.
            var plan = OverlaySplicePlan.TryPlan(
                Clip(30), Ticks(0.5),
                new[] { Card(20.5, 24.5), Card(6.2, 8.1), Card(23.0, 25.5) },
                Ticks(30), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual("recode[0.5,1) copy[1,6) recode[6,9) copy[9,20) recode[20,26) copy[26,30)", Describe(plan));
            AssertAlternates(plan);
            Assert.AreEqual(3, plan.ReencodeRuns);
            Assert.AreEqual(1475, plan.CopyFrames + plan.ReencodeFrames, "every frame from 0.5 s on accounted for once");
        }

        [TestMethod]
        public void Plan_FoldsAShortCopyBetweenTwoOverlaysIntoOneReencode()
        {
            // Cards at 5.5 s and 8.5 s leave only [7 s, 8 s) to copy between them, under the minimum,
            // so their GOPs merge into one re-encoded run; the ten seconds after are copied again.
            var plan = OverlaySplicePlan.TryPlan(
                Clip(20), Ticks(0.5), new[] { Card(5.5, 6.5), Card(8.5, 9.5) }, Ticks(20), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual("recode[0.5,1) copy[1,5) recode[5,10) copy[10,20)", Describe(plan));
            AssertAlternates(plan);
            Assert.AreEqual(2, plan.ReencodeRuns);
        }

        [TestMethod]
        public void Plan_AccountsForEveryKeptFrameExactlyOnce()
        {
            var samples = Clip(12.5);
            var trimLead = Ticks(0.3);
            var end = Ticks(9.7);
            var plan = OverlaySplicePlan.TryPlan(samples, trimLead, new[] { Card(7.25, 8.0) }, end, Ticks(1));

            Assert.IsNotNull(plan);
            var kept = samples.Count(s => s.Time >= trimLead && s.Time <= end);
            Assert.AreEqual(kept, plan.Runs.Sum(r => r.Frames));
            Assert.AreEqual(kept, plan.CopyFrames + plan.ReencodeFrames);
        }

        [TestMethod]
        public void Plan_HasNoHeadWhenTheWindowStartsOnAKeyframe()
        {
            var plan = OverlaySplicePlan.TryPlan(Clip(10), Ticks(1.0), new[] { Card(6.0, 9.5) }, Ticks(10), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual("copy[1,6) recode[6,10)", Describe(plan));
            AssertAlternates(plan);
        }

        [TestMethod]
        public void Plan_HasNoHeadWhenThereIsNoLead()
        {
            var plan = OverlaySplicePlan.TryPlan(Clip(10), 0, new[] { Card(6.0, 9.5) }, Ticks(10), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual("copy[0,6) recode[6,10)", Describe(plan));
        }

        [TestMethod]
        public void Plan_IsNullWhenTheCardStartsInTheFirstGopAfterTheLead()
        {
            Assert.IsNull(OverlaySplicePlan.TryPlan(Clip(10), Ticks(0.5), new[] { Card(1.5, 9.5) }, Ticks(10), 0));
            Assert.IsNull(OverlaySplicePlan.TryPlan(Clip(10), Ticks(0.5), new[] { Card(0.7, 9.5) }, Ticks(10), 0));
        }

        [TestMethod]
        public void Plan_IsNullWhenTheOnlyCopyIsShorterThanTheMinimum()
        {
            // Copy would be [1 s, 3 s): two seconds.
            Assert.IsNull(OverlaySplicePlan.TryPlan(Clip(10), Ticks(0.5), new[] { Card(3.5, 9.5) }, Ticks(10), Ticks(3)));
            Assert.IsNotNull(OverlaySplicePlan.TryPlan(Clip(10), Ticks(0.5), new[] { Card(3.5, 9.5) }, Ticks(10), Ticks(2)));
        }

        [TestMethod]
        public void Plan_CopiesEverythingWhenNoOverlayFallsInsideTheClip()
        {
            // The card would show at 12 s but the clip is cut at 9.5 s: nothing to re-encode but the head.
            var plan = OverlaySplicePlan.TryPlan(Clip(15), Ticks(0.5), new[] { Card(12, 16) }, Ticks(9.5), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual("recode[0.5,1) copy[1,9.5)", Describe(plan));
            Assert.AreEqual(451, plan.Runs.Sum(r => r.Frames), "0.50 s through 9.50 s inclusive at 50 fps");

            var noOverlays = OverlaySplicePlan.TryPlan(Clip(15), Ticks(1.0), new OverlaySplicePlan.Interval[0], Ticks(9.5), Ticks(3));
            Assert.IsNotNull(noOverlays);
            Assert.AreEqual("copy[1,9.5)", Describe(noOverlays));
        }

        [TestMethod]
        public void Plan_IsNullWithoutSamplesOrWithAnEmptyWindow()
        {
            var card = new[] { Card(5, 6) };
            Assert.IsNull(OverlaySplicePlan.TryPlan(null, 0, card, Ticks(10), 0));
            Assert.IsNull(OverlaySplicePlan.TryPlan(new List<OverlaySplicePlan.SampleInfo>(), 0, card, Ticks(10), 0));
            Assert.IsNull(OverlaySplicePlan.TryPlan(Clip(10), Ticks(5), card, Ticks(4), 0), "end before the lead");
        }

        [TestMethod]
        public void Plan_IsNullWhenSamplesAreOutOfOrder()
        {
            var samples = Clip(10);
            var swapped = samples[300];
            samples[300] = samples[301];
            samples[301] = swapped;

            Assert.IsNull(OverlaySplicePlan.TryPlan(samples, Ticks(0.5), new[] { Card(7, 9) }, Ticks(10), Ticks(1)));
        }

        [TestMethod]
        public void Plan_IsNullWhenNoKeyframeFollowsTheLead()
        {
            var samples = Clip(1.0);
            for (var i = 1; i < samples.Count; i++)
            {
                var sample = samples[i];
                sample.IsKeyframe = false;
                samples[i] = sample;
            }

            Assert.IsNull(OverlaySplicePlan.TryPlan(samples, Ticks(0.5), new[] { Card(0.9, 1.0) }, Ticks(1.0), 0));
        }

        [TestMethod]
        public void Plan_IgnoresTheAbsoluteOriginOfSampleTimes()
        {
            var offset = Ticks(123.4);
            var plan = OverlaySplicePlan.TryPlan(
                Clip(10, offset), offset + Ticks(0.5),
                new[] { new OverlaySplicePlan.Interval(offset + Ticks(5.5), offset + Ticks(9.5)) },
                offset + Ticks(10), Ticks(3));

            Assert.IsNotNull(plan);
            Assert.AreEqual(offset + Ticks(1.0), plan.Runs[1].Start);
            Assert.AreEqual(offset + Ticks(5.0), plan.Runs[1].End);
            Assert.AreEqual(200, plan.CopyFrames);
        }
    }
}
