using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Recording;
using System;

namespace PlayniteAchievements.Tests.Services.Recording
{
    /// <summary>
    /// Composes the two stages of unlock-clip anchoring -- the source-policy selector and the
    /// clip-window computation -- and asserts the only invariant the user can see: the moment the
    /// achievement was actually earned is inside the exported clip.
    ///
    /// Each stage was tested in isolation, which is how a remote provider's server-clock stamp
    /// came to displace a whole clip: the selector handed it through as authoritative, and the
    /// window computation's staleness bound (poll interval plus pre-roll) was far wider than the
    /// window's own post-anchor tail, so it accepted it.
    /// </summary>
    [TestClass]
    public class UnlockAnchorCompositionTests
    {
        // A reported case. The provider stamp read 06:16:38.0 while detection happened at
        // 06:17:02.7, on a machine whose clock ran ~20s ahead of the provider's server, so the
        // real unlock was at local-clock ~06:16:58 -- one 5s feed interval before detection.
        private static readonly DateTime Reported =
            new DateTime(2026, 9, 15, 6, 16, 38, 0, DateTimeKind.Utc);
        private static readonly DateTime Observed =
            new DateTime(2026, 9, 15, 6, 17, 2, 700, DateTimeKind.Utc);
        private static readonly DateTime TrueUnlock = Observed.AddSeconds(-5);
        private static readonly DateTime CaptureStart = Reported.AddMinutes(-4);

        private const int PollIntervalSeconds = 15;
        private const int PreRollSeconds = 20;
        private const double ToastSlotSeconds = 5.8;
        private const double TailSeconds = 1.0;

        private static SegmentTimeline.ClipWindow WindowFor(InGameProgressRegistration registration)
        {
            var policy = InGameUnlockAnchorSelector.ResolvePolicy(registration);
            var anchor = InGameUnlockAnchorSelector.Select(
                policy,
                Reported,
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

        [TestMethod]
        public void RemoteSourceWithoutPolicy_ClipContainsTheRealUnlock()
        {
            var window = WindowFor(new InGameProgressRegistration { IsRemote = true });

            Assert.IsTrue(
                TrueUnlock >= window.StartUtc && TrueUnlock <= window.EndUtc,
                $"The real unlock at {TrueUnlock:HH:mm:ss.f} fell outside the clip " +
                $"[{window.StartUtc:HH:mm:ss.f}..{window.EndUtc:HH:mm:ss.f}].");
        }

        [TestMethod]
        public void RemoteSourceWithoutPolicy_NotificationLandsAfterTheRealUnlock()
        {
            var window = WindowFor(new InGameProgressRegistration { IsRemote = true });

            // The card is composited at the anchor, so an anchor before the real unlock shows a
            // notification for an achievement the footage has not earned yet.
            Assert.IsTrue(
                window.ToastAnchorUtc >= TrueUnlock,
                $"The notification was composited at {window.ToastAnchorUtc:HH:mm:ss.f}, before " +
                $"the unlock it announces at {TrueUnlock:HH:mm:ss.f}.");
        }

        [TestMethod]
        public void ForeignStampAnchoring_WouldHaveCutTheRealUnlockOff()
        {
            // Pins the regression itself: trusting the foreign stamp puts the whole window before
            // the real moment, and the staleness bound does not catch it because 24.8s of
            // divergence is well inside the poll interval plus pre-roll.
            var window = WindowFor(new InGameProgressRegistration
            {
                IsRemote = true,
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.ProviderReported,
            });

            Assert.AreEqual(Reported, window.ToastAnchorUtc);
            Assert.IsTrue(
                TrueUnlock > window.EndUtc,
                "Expected the foreign-stamp anchor to end the clip before the real unlock.");
        }

        [TestMethod]
        public void LocalSourceWithoutPolicy_KeepsItsReportedAnchorAndFullPreRoll()
        {
            // A local source's stamp shares this machine's clock, so a genuine propagation lag
            // (GOG's Galaxy database write lag is the case this protects) must not cost pre-roll.
            var window = WindowFor(new InGameProgressRegistration { IsRemote = false });

            Assert.AreEqual(Reported, window.ToastAnchorUtc);
            Assert.AreEqual(Reported.AddSeconds(-PreRollSeconds), window.StartUtc);
        }
    }
}
