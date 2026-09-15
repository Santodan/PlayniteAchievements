using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using System;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class ServerClockOffsetTests
    {
        private static readonly DateTime Sent =
            new DateTime(2026, 9, 15, 6, 17, 2, 600, DateTimeKind.Utc);

        private static DateTimeOffset ServerDate(DateTime localInstant, TimeSpan skew)
        {
            // The server's clock reads local minus skew, and an HTTP Date truncates to the second.
            var serverReading = localInstant.Add(-skew);
            return new DateTimeOffset(
                new DateTime(
                    serverReading.Year, serverReading.Month, serverReading.Day,
                    serverReading.Hour, serverReading.Minute, serverReading.Second,
                    DateTimeKind.Utc),
                TimeSpan.Zero);
        }

        [TestMethod]
        public void NoSample_HasNoOffsetAndConvertsNothing()
        {
            var clock = new ServerClockOffset();

            Assert.IsNull(clock.Offset);
            Assert.IsNull(clock.ToCaptureTimeline(Sent));
        }

        [TestMethod]
        public void SingleSample_RecoversTheOffsetWithinTheHeaderGranularity()
        {
            var skew = TimeSpan.FromSeconds(20);
            var clock = new ServerClockOffset();

            var adopted = clock.Observe(
                Sent, Sent.AddMilliseconds(100), ServerDate(Sent.AddMilliseconds(50), skew));

            Assert.IsTrue(adopted);
            Assert.AreEqual(
                skew.TotalSeconds,
                clock.Offset.Value.TotalSeconds,
                1.0,
                "The estimate should land within the Date header's one-second granularity.");
        }

        [TestMethod]
        public void ToCaptureTimeline_ShiftsAServerInstantOntoTheLocalClock()
        {
            var skew = TimeSpan.FromSeconds(20);
            var clock = new ServerClockOffset();
            clock.Observe(
                Sent, Sent.AddMilliseconds(100), ServerDate(Sent.AddMilliseconds(50), skew));

            var serverUnlock = new DateTime(2026, 9, 15, 6, 16, 38, DateTimeKind.Utc);
            var local = clock.ToCaptureTimeline(serverUnlock);

            Assert.IsTrue(local.HasValue);
            Assert.AreEqual(DateTimeKind.Utc, local.Value.Kind);
            Assert.AreEqual(
                0,
                (local.Value - serverUnlock.Add(skew)).TotalSeconds,
                1.0);
        }

        [TestMethod]
        public void SlowerExchange_DoesNotReplaceATighterSample()
        {
            var clock = new ServerClockOffset();
            clock.Observe(
                Sent, Sent.AddMilliseconds(50), ServerDate(Sent, TimeSpan.FromSeconds(20)));
            var tight = clock.Offset;

            // A three-second round trip constrains the server reading far more loosely, so it must
            // not displace the estimate even though it is newer. Same underlying clock, so this is
            // not a step.
            var adopted = clock.Observe(
                Sent.AddMinutes(1),
                Sent.AddMinutes(1).AddSeconds(3),
                ServerDate(Sent.AddMinutes(1).AddSeconds(1), TimeSpan.FromSeconds(20)));

            Assert.IsFalse(adopted);
            Assert.AreEqual(tight, clock.Offset);
            Assert.AreEqual(50, clock.SampleRoundTrip.Value.TotalMilliseconds, 1.0);
        }

        [TestMethod]
        public void SteppedLocalClock_ReplacesTheRetainedSample()
        {
            var clock = new ServerClockOffset();
            clock.Observe(
                Sent, Sent.AddMilliseconds(50), ServerDate(Sent, TimeSpan.FromSeconds(20)));

            // The user corrects a clock that was 20s fast. A slower exchange now reports an offset
            // no round trip could explain, so the stale estimate must be abandoned rather than
            // held until restart.
            var later = Sent.AddMinutes(5);
            var adopted = clock.Observe(
                later, later.AddMilliseconds(400), ServerDate(later, TimeSpan.Zero));

            Assert.IsTrue(adopted);
            Assert.AreEqual(0, clock.Offset.Value.TotalSeconds, 1.0);
        }

        [TestMethod]
        public void MissingDateHeader_IsIgnored()
        {
            var clock = new ServerClockOffset();

            Assert.IsFalse(clock.Observe(Sent, Sent.AddMilliseconds(100), null));
            Assert.IsNull(clock.Offset);
        }

        [TestMethod]
        public void UnorderedOrImplausibleTimings_AreIgnored()
        {
            var clock = new ServerClockOffset();

            Assert.IsFalse(clock.Observe(
                Sent, Sent.AddMilliseconds(-1), ServerDate(Sent, TimeSpan.Zero)),
                "A negative round trip means the clock moved mid-request.");
            Assert.IsFalse(clock.Observe(
                Sent, Sent.AddMinutes(2), ServerDate(Sent, TimeSpan.Zero)),
                "A two-minute round trip constrains nothing.");
            Assert.IsNull(clock.Offset);
        }
    }
}
