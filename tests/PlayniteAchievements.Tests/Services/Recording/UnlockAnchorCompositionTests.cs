using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Recording;
using System;

namespace PlayniteAchievements.Tests.Services.Recording
{
    /// <summary>
    /// Composes the two stages of unlock-clip anchoring -- the source-policy selector and the
    /// clip-window computation -- and asserts the only invariant the user can see: the notification
    /// composited into the clip lands on the moment the achievement was actually earned.
    ///
    /// Each stage was tested in isolation, which is how a remote provider's server-clock stamp came
    /// to displace a whole clip: the selector handed it through as authoritative, and the window
    /// computation's staleness bound (poll interval plus pre-roll) was far wider than the window's
    /// own post-anchor tail, so it accepted it.
    /// </summary>
    [TestClass]
    public class UnlockAnchorCompositionTests
    {
        // A reported case. RetroAchievements stamped the unlock 06:16:38 on its own clock while
        // detection happened at 06:17:02.7, on a machine running ~20s ahead of that server. The
        // real unlock was therefore at 06:16:58 on this machine's timeline.
        private static readonly DateTime ReportedOnServerClock =
            new DateTime(2026, 9, 15, 6, 16, 38, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Skew = TimeSpan.FromSeconds(20);
        private static readonly DateTime TrueUnlock = ReportedOnServerClock.Add(Skew);
        private static readonly DateTime Observed =
            new DateTime(2026, 9, 15, 6, 17, 2, 700, DateTimeKind.Utc);
        private static readonly DateTime CaptureStart = ReportedOnServerClock.AddMinutes(-4);

        private const int PollIntervalSeconds = 15;
        private const int PreRollSeconds = 20;
        private const double ToastSlotSeconds = 5.8;
        private const double TailSeconds = 1.0;

        private static SegmentTimeline.ClipWindow WindowFor(
            InGameProgressRegistration registration,
            DateTime reportedUtc)
        {
            var policy = InGameUnlockAnchorSelector.ResolvePolicy(registration);
            var anchor = InGameUnlockAnchorSelector.Select(
                policy,
                reportedUtc,
                Observed,
                registration?.UnlockAnchorBias ?? TimeSpan.Zero);

            return SegmentTimeline.ComputeClipWindow(
                anchor.Utc,
                Observed,
                CaptureStart,
                oldestSegmentStartUtc: null,
                pollIntervalSeconds: PollIntervalSeconds,
                preRollSeconds: PreRollSeconds,
                toastSlotSeconds: ToastSlotSeconds,
                tailSeconds: TailSeconds);
        }

        private static ServerClockOffset CorrelatedClock()
        {
            // One exchange: sent, 100ms round trip, and a server Date 20s behind this machine.
            var sent = new DateTime(2026, 9, 15, 6, 17, 2, 600, DateTimeKind.Utc);
            var clock = new ServerClockOffset();
            var adopted = clock.Observe(
                sent,
                sent.AddMilliseconds(100),
                new DateTimeOffset(sent.Add(-Skew).AddMilliseconds(50), TimeSpan.Zero));

            Assert.IsTrue(adopted, "Expected the sample to be adopted.");
            return clock;
        }

        [TestMethod]
        public void CorrelatedForeignClock_NotificationLandsOnTheRealUnlock()
        {
            var clock = CorrelatedClock();
            var registration = new InGameProgressRegistration
            {
                IsRemote = true,
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.ProviderReported,
                ReportedClock = clock,
            };

            // The mapper converts before emission, aiming at the middle of the whole second the
            // feed reports, so this is the stamp that reaches the selector.
            var converted = clock.ToCaptureTimeline(ReportedOnServerClock.AddMilliseconds(500));
            Assert.IsTrue(converted.HasValue);

            var window = WindowFor(registration, converted.Value);

            // Within a second of the true moment: the residual is the feed's whole-second
            // granularity plus the round trip, not the clock difference.
            Assert.AreEqual(
                0,
                (window.ToastAnchorUtc - TrueUnlock).TotalSeconds,
                1.0,
                $"The notification was composited at {window.ToastAnchorUtc:HH:mm:ss.fff}, " +
                $"but the unlock happened at {TrueUnlock:HH:mm:ss.fff}.");
            Assert.IsTrue(TrueUnlock >= window.StartUtc && TrueUnlock <= window.EndUtc);
        }

        [TestMethod]
        public void UncorrelatedForeignClock_FallsBackToObservation()
        {
            // No sample yet, so the stamp cannot be placed on this timeline. Anchoring on the
            // observation costs a poll interval; anchoring on an unconverted stamp would put the
            // notification wherever the two clocks happen to differ.
            var registration = new InGameProgressRegistration
            {
                IsRemote = true,
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.ProviderReported,
                ReportedClock = new ServerClockOffset(),
            };

            Assert.AreEqual(
                InGameUnlockAnchorPolicy.SourceObservation,
                InGameUnlockAnchorSelector.ResolvePolicy(registration));

            var window = WindowFor(registration, ReportedOnServerClock);

            Assert.AreEqual(Observed, window.ToastAnchorUtc);
            Assert.IsTrue(TrueUnlock >= window.StartUtc && TrueUnlock <= window.EndUtc);
        }

        [TestMethod]
        public void RemoteSourceWithoutPolicy_AnchorsOnObservation()
        {
            var window = WindowFor(
                new InGameProgressRegistration { IsRemote = true }, ReportedOnServerClock);

            Assert.AreEqual(Observed, window.ToastAnchorUtc);
        }

        [TestMethod]
        public void UnconvertedForeignStamp_WouldHaveCutTheRealUnlockOff()
        {
            // Pins the regression itself: trusting an unconverted foreign stamp puts the whole
            // window before the real moment, and the staleness bound does not catch it, because
            // 24.8s of divergence sits well inside the poll interval plus pre-roll.
            var window = WindowFor(
                new InGameProgressRegistration
                {
                    IsRemote = true,
                    UnlockAnchorPolicy = InGameUnlockAnchorPolicy.ProviderReported,
                },
                ReportedOnServerClock);

            Assert.AreEqual(ReportedOnServerClock, window.ToastAnchorUtc);
            Assert.IsTrue(
                TrueUnlock > window.EndUtc,
                "Expected the unconverted anchor to end the clip before the real unlock.");
        }

        [TestMethod]
        public void LocalSourceWithoutPolicy_KeepsItsReportedAnchorAndFullPreRoll()
        {
            // A local source's stamp shares this machine's clock, so a genuine propagation lag
            // (GOG's Galaxy database write lag is the case this protects) must not cost pre-roll.
            var window = WindowFor(
                new InGameProgressRegistration { IsRemote = false }, ReportedOnServerClock);

            Assert.AreEqual(ReportedOnServerClock, window.ToastAnchorUtc);
            Assert.AreEqual(
                ReportedOnServerClock.AddSeconds(-PreRollSeconds), window.StartUtc);
        }
    }
}
