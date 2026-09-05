using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    /// <summary>
    /// Pins the gap arithmetic against the field regression that motivated it: a forced-48kHz
    /// endpoint capture whose devicePosition counter advanced at 4x the frames delivered
    /// (192 kHz native mix), which the old position-delta arithmetic read as 3 s of dropout per
    /// real second. Stamps are the measure now; the position counter is fallback and witness.
    /// </summary>
    [TestClass]
    public class AudioGapTrackerTests
    {
        private const int Rate = 48000;
        private const int MaxGapSeconds = 5;
        private const uint PacketFrames = 480;              // one 10 ms engine period
        private const long PacketTicks = 100_000;           // 10 ms in 100 ns units
        private const long TicksPerSecond = 10_000_000;

        private static AudioGapTracker NewTracker(bool stampsDisabled = false)
        {
            return new AudioGapTracker(Rate, MaxGapSeconds, stampsDisabled: stampsDisabled);
        }

        /// <summary>
        /// Feeds a healthy contiguous stream: stamps advance one packet per packet, the position
        /// counter advances at <paramref name="positionRate"/> per second, the wall clock keeps
        /// pace with the audio. Returns the position and stamp where the stream left off.
        /// </summary>
        private static (long pos, long qpc) FeedHealthy(
            AudioGapTracker tracker, int packets, long positionRate, long startQpc = 1_000_000_000)
        {
            long pos = 0;
            var qpc = startQpc;
            var positionStep = positionRate / 100; // per 10 ms packet
            for (var i = 0; i < packets; i++)
            {
                var elapsedFrames = (long)(i + 1) * PacketFrames;
                tracker.TakeGapBefore(pos, PacketFrames, qpc, stampUsable: true, elapsedFrames);
                pos += positionStep;
                qpc += PacketTicks;
            }

            return (pos, qpc);
        }

        [TestMethod]
        public void ContiguousStampedStreamPadsNothing()
        {
            var tracker = NewTracker();
            FeedHealthy(tracker, 1000, positionRate: Rate);
            Assert.AreEqual(0, tracker.PaddedGapFrames);
            Assert.AreEqual(0, tracker.ImpossibleGapFrames);
        }

        [TestMethod]
        public void FieldRegression_QuadRatePositionCounterPadsNothingWithStamps()
        {
            var tracker = NewTracker();
            FeedHealthy(tracker, 6000, positionRate: 192000); // 60 s at the field machine's ratio
            Assert.AreEqual(0, tracker.PaddedGapFrames);
            Assert.AreEqual(0, tracker.ImpossibleGapFrames);
        }

        [TestMethod]
        public void FieldRegression_RateWitnessNamesTheCounterUnit()
        {
            var tracker = NewTracker();
            FeedHealthy(tracker, 6000, positionRate: 192000);
            Assert.AreEqual(192000, tracker.MeasuredDevicePositionRate, delta: 200);
        }

        [TestMethod]
        public void FieldRegression_LegacyArithmeticIsRefusedByTheWallClock()
        {
            // The pre-stamp arithmetic on the same stream: the wall-clock allowance refuses the
            // 3x-wall-clock padding and books it as impossible instead of flooding the ring.
            var tracker = NewTracker(stampsDisabled: true);
            FeedHealthy(tracker, 6000, positionRate: 192000);
            Assert.IsTrue(
                tracker.ImpossibleGapFrames > 100L * Rate,
                $"expected the bogus padding refused, got {tracker.ImpossibleGapFrames}");
            Assert.IsTrue(tracker.PaddedGapFrames <= 1000);
        }

        [TestMethod]
        public void RealDropoutIsPaddedAtItsTrueSize()
        {
            var tracker = NewTracker();
            var (pos, qpc) = FeedHealthy(tracker, 1000, positionRate: 192000);

            // One second of audio the engine dropped: stamps and positions both jump, and the
            // wall clock saw the second pass.
            var elapsed = (long)(11.01 * Rate);
            var gap = tracker.TakeGapBefore(
                pos + 192000, PacketFrames, qpc + TicksPerSecond, stampUsable: true, elapsed);
            Assert.AreEqual(Rate, gap, delta: Rate / 50);
        }

        [TestMethod]
        public void AgreedGapIsSizedByThePositionCounterNotTheJitteredStamp()
        {
            // A 200 ms dropout whose closing stamp arrived 0.4 ms late (scheduling jitter). The
            // stamp alone would pad 19 extra frames and shift everything after the gap by 0.4 ms,
            // which is the tear that left the tail of a live chime at another lag in the
            // 2026-09-05 clips. The position counter counts the engine's own frames.
            foreach (var positionRate in new[] { (long)Rate, 192000L })
            {
                var tracker = NewTracker();
                var (pos, qpc) = FeedHealthy(tracker, 1000, positionRate);
                var dropoutFrames = Rate / 5;
                var positionJump = dropoutFrames * positionRate / Rate;
                var elapsed = (long)(10.3 * Rate);
                var gap = tracker.TakeGapBefore(
                    pos + positionJump,
                    PacketFrames,
                    qpc + dropoutFrames * TicksPerSecond / Rate + 4_000,
                    stampUsable: true,
                    elapsed);
                Assert.AreEqual(dropoutFrames, gap, $"positionRate={positionRate}");
            }
        }

        [TestMethod]
        public void DisagreeingWitnessesStillTakeTheSmallerClaim()
        {
            var tracker = NewTracker();
            var (pos, qpc) = FeedHealthy(tracker, 1000, positionRate: Rate);

            // The stamp claims a 1 s hole; the counter only advanced 100 ms of frames. Beyond the
            // jitter threshold the witnesses disagree and the smaller claim is padded.
            var elapsed = (long)(11.1 * Rate);
            var gap = tracker.TakeGapBefore(
                pos + Rate / 10, PacketFrames, qpc + TicksPerSecond, stampUsable: true, elapsed);
            Assert.AreEqual(Rate / 10, gap, delta: 2);
        }

        [TestMethod]
        public void AlternatingStampJitterPadsNothing()
        {
            var tracker = NewTracker();
            long pos = 0;
            var qpc = 1_000_000_000L;
            for (var i = 0; i < 2000; i++)
            {
                var jitter = i % 2 == 0 ? 50_000 : -50_000; // ±5 ms around the grid
                var elapsedFrames = (long)(i + 1) * PacketFrames;
                tracker.TakeGapBefore(pos, PacketFrames, qpc + jitter, stampUsable: true, elapsedFrames);
                pos += PacketFrames;
                qpc += PacketTicks;
            }

            Assert.AreEqual(0, tracker.PaddedGapFrames);
        }

        [TestMethod]
        public void WildStampIsBoundedByThePositionWitness()
        {
            var tracker = NewTracker();
            var (pos, qpc) = FeedHealthy(tracker, 1000, positionRate: Rate);

            // A stamp 1.5 s in the future passes the plausibility window, and a slack wall clock
            // (after endpoint idle) would allow the pad — the healthy position delta refuses it.
            var slackElapsed = 15L * Rate;
            var gap = tracker.TakeGapBefore(
                pos, PacketFrames, qpc + 15_000_000, stampUsable: true, slackElapsed);
            Assert.AreEqual(0, gap);

            // And the next on-time stamp must not rebound into a pad either.
            var gapAfter = tracker.TakeGapBefore(
                pos + PacketFrames, PacketFrames, qpc + PacketTicks, stampUsable: true, slackElapsed);
            Assert.AreEqual(0, gapAfter);
        }

        [TestMethod]
        public void StamplessStreamKeepsTheOldPositionArithmetic()
        {
            var tracker = NewTracker();
            long pos = 0;
            for (var i = 0; i < 1000; i++)
            {
                var elapsedFrames = (long)(i + 1) * PacketFrames;
                tracker.TakeGapBefore(pos, PacketFrames, 0, stampUsable: false, elapsedFrames);
                pos += PacketFrames;
            }

            Assert.AreEqual(0, tracker.PaddedGapFrames);

            // A 2400-frame position jump the wall clock corroborates pads exactly that.
            var elapsed = 1000L * PacketFrames + 2400 + PacketFrames;
            var gap = tracker.TakeGapBefore(pos + 2400, PacketFrames, 0, stampUsable: false, elapsed);
            Assert.AreEqual(2400, gap);
        }

        [TestMethod]
        public void StamplessBridgeIsNotMeasuredTwice()
        {
            var tracker = NewTracker();
            var (pos, qpc) = FeedHealthy(tracker, 1000, positionRate: Rate);

            // Stamps drop out across a 50 ms hole; the fallback pads it once.
            var elapsedAfterHole = (long)(10.06 * Rate);
            var gapFallback = tracker.TakeGapBefore(
                pos + 2400, PacketFrames, 0, stampUsable: false, elapsedAfterHole);
            Assert.AreEqual(2400, gapFallback);

            // Stamps return on the real-time grid; the hole must not be padded again.
            var gapStamped = tracker.TakeGapBefore(
                pos + 2400 + PacketFrames, PacketFrames, qpc + 600_000, stampUsable: true,
                (long)(10.07 * Rate));
            Assert.AreEqual(0, gapStamped);
        }

        [TestMethod]
        public void OneGapNeverPadsMoreThanTheCap()
        {
            var tracker = NewTracker();
            var (pos, qpc) = FeedHealthy(tracker, 1000, positionRate: Rate);

            var gap = tracker.TakeGapBefore(
                pos + 10L * Rate, PacketFrames, qpc + 10 * TicksPerSecond, stampUsable: true,
                (long)(20.01 * Rate));
            Assert.AreEqual((long)MaxGapSeconds * Rate, gap);
        }

        [TestMethod]
        public void PaddingNeverExceedsTheWallClock()
        {
            var tracker = NewTracker();
            var (pos, qpc) = FeedHealthy(tracker, 100, positionRate: Rate);

            // Stamp and position both claim 3 s missing, but only 0.5 s of real time passed.
            var elapsed = 100L * PacketFrames + Rate / 2;
            var gap = tracker.TakeGapBefore(
                pos + 3L * Rate, PacketFrames, qpc + 3 * TicksPerSecond, stampUsable: true, elapsed);
            Assert.IsTrue(gap <= Rate / 2, $"gap {gap} exceeds the elapsed half second");
            Assert.IsTrue(tracker.ImpossibleGapFrames > 0);
        }

        [TestMethod]
        public void ResetForgetsEveryChain()
        {
            var tracker = NewTracker();
            FeedHealthy(tracker, 100, positionRate: Rate);
            tracker.Reset();

            // Wildly different position and stamp after the reset read as a fresh stream.
            var gap = tracker.TakeGapBefore(
                999_999, PacketFrames, 9_000_000_000, stampUsable: true, PacketFrames);
            Assert.AreEqual(0, gap);
            Assert.AreEqual(0, tracker.PaddedGapFrames);
        }

        [TestMethod]
        public void RateWitnessSurvivesAPositionCounterReset()
        {
            var tracker = NewTracker();
            var (_, qpc) = FeedHealthy(tracker, 1000, positionRate: 192000);

            // A stream rebuild resets the counter to zero; the witness restarts its baseline
            // and converges on the same rate again instead of reporting a negative one.
            long pos = 0;
            for (var i = 0; i < 1000; i++)
            {
                var elapsedFrames = (long)(1001 + i) * PacketFrames;
                tracker.TakeGapBefore(pos, PacketFrames, qpc, stampUsable: true, elapsedFrames);
                pos += 1920;
                qpc += PacketTicks;
            }

            Assert.AreEqual(192000, tracker.MeasuredDevicePositionRate, delta: 400);
        }

        [TestMethod]
        public void TimelineAnchorConsensus_RejectsOnePlausibleStartupOutlier()
        {
            var consensus = new AudioTimelineAnchorConsensus(Rate);
            var expected = new DateTime(638923104000000000L, DateTimeKind.Utc);
            for (var packet = 0; packet < AudioTimelineAnchorConsensus.RequiredSamples; packet++)
            {
                var framesBefore = (long)packet * PacketFrames;
                var packetUtc = expected.AddTicks(framesBefore * TicksPerSecond / Rate);
                if (packet == 0)
                {
                    packetUtc = packetUtc.AddMilliseconds(215);
                }

                consensus.Observe(packetUtc, framesBefore);
            }

            Assert.IsTrue(consensus.TryGet(false, out var actual, out var samples, out var spread));
            Assert.AreEqual(AudioTimelineAnchorConsensus.RequiredSamples, samples);
            Assert.AreEqual(expected, actual);
            Assert.AreEqual(0, spread, 0.001);
        }

        [TestMethod]
        public void TimelineAnchorConsensus_AccountsForPacketsBufferedBeforeFirstUsableStamp()
        {
            var consensus = new AudioTimelineAnchorConsensus(Rate);
            var expected = new DateTime(638923104000000000L, DateTimeKind.Utc);

            // The first three packets were delivered but their stamps were unusable. The first
            // vote must still subtract those frames; anchoring to this packet itself would move
            // every sample 30 ms late for the rest of the session.
            consensus.Observe(expected.AddMilliseconds(30), 3L * PacketFrames);

            Assert.IsFalse(consensus.TryGet(false, out _, out _, out _));
            Assert.IsTrue(consensus.TryGet(true, out var actual, out var samples, out var spread));
            Assert.AreEqual(1, samples);
            Assert.AreEqual(expected, actual);
            Assert.AreEqual(0, spread, 0.001);
        }
    }
}
