using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using System;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class InGameUnlockAnchorSelectorTests
    {
        private static readonly DateTime Reported =
            new DateTime(2026, 8, 11, 18, 45, 52, DateTimeKind.Utc);
        private static readonly DateTime Observed =
            new DateTime(2026, 8, 11, 18, 45, 59, 966, DateTimeKind.Utc);

        [TestMethod]
        public void SourceObservation_ReconcilesProviderTimestampToCaptureClock()
        {
            var selected = InGameUnlockAnchorSelector.Select(
                InGameUnlockAnchorPolicy.SourceObservation,
                Reported,
                Observed);

            Assert.AreEqual(Observed, selected.Utc);
            Assert.AreEqual(
                UnlockVideoAnchorSource.SourceObservationForeignStamp, selected.Source);
        }

        [TestMethod]
        public void ProviderReported_PreservesAuthoritativeHistoricalTime()
        {
            var selected = InGameUnlockAnchorSelector.Select(
                InGameUnlockAnchorPolicy.ProviderReported,
                Reported,
                Observed);

            Assert.AreEqual(Reported, selected.Utc);
            Assert.AreEqual(UnlockVideoAnchorSource.ProviderReported, selected.Source);
        }

        [TestMethod]
        public void MissingProviderTime_FallsBackToObservation()
        {
            var selected = InGameUnlockAnchorSelector.Select(
                InGameUnlockAnchorPolicy.ProviderReported,
                null,
                Observed);

            Assert.AreEqual(Observed, selected.Utc);
            Assert.AreEqual(UnlockVideoAnchorSource.SourceObservation, selected.Source);
        }

        [TestMethod]
        public void ReportedBias_ShiftsProviderAnchorLater()
        {
            var selected = InGameUnlockAnchorSelector.Select(
                InGameUnlockAnchorPolicy.ProviderReported,
                Reported,
                Observed,
                TimeSpan.FromSeconds(2.5));

            Assert.AreEqual(Reported.AddSeconds(2.5), selected.Utc);
            Assert.AreEqual(UnlockVideoAnchorSource.ProviderReported, selected.Source);
        }

        [TestMethod]
        public void ReportedBias_NeverPushesAnchorPastObservation()
        {
            var selected = InGameUnlockAnchorSelector.Select(
                InGameUnlockAnchorPolicy.ProviderReported,
                Observed.AddSeconds(-1),
                Observed,
                TimeSpan.FromSeconds(2.5));

            Assert.AreEqual(Observed, selected.Utc);
            Assert.AreEqual(UnlockVideoAnchorSource.ProviderReported, selected.Source);
        }

        [TestMethod]
        public void ReportedBias_IgnoredUnderSourceObservation()
        {
            var selected = InGameUnlockAnchorSelector.Select(
                InGameUnlockAnchorPolicy.SourceObservation,
                Reported,
                Observed,
                TimeSpan.FromSeconds(2.5));

            Assert.AreEqual(Observed, selected.Utc);
            Assert.AreEqual(
                UnlockVideoAnchorSource.SourceObservationForeignStamp, selected.Source);
        }

        [TestMethod]
        public void ResolvePolicy_RemoteRegistrationWithoutPolicy_AnchorsOnObservation()
        {
            // A remote source's stamp is produced by the provider's server clock. Anchoring a
            // clip on it seeks the locally-stamped capture buffer with a foreign clock's reading,
            // which lands the composited notification wherever the two clocks differ. The
            // RetroAchievements feed shipped without a declared policy and inherited the
            // provider-reported default, putting a notification ~20s before the gameplay that
            // earned it and cutting the real moment past the end of the clip.
            var registration = new InGameProgressRegistration { IsRemote = true };

            Assert.AreEqual(
                InGameUnlockAnchorPolicy.SourceObservation,
                InGameUnlockAnchorSelector.ResolvePolicy(registration));
        }

        [TestMethod]
        public void ResolvePolicy_LocalRegistrationWithoutPolicy_KeepsProviderReported()
        {
            // A local source reads a file or database written on this machine's clock, so its
            // stamp shares the capture timeline's correlation point and stays authoritative.
            var registration = new InGameProgressRegistration { IsRemote = false };

            Assert.AreEqual(
                InGameUnlockAnchorPolicy.ProviderReported,
                InGameUnlockAnchorSelector.ResolvePolicy(registration));
        }

        [TestMethod]
        public void ResolvePolicy_ExplicitPolicyOnRemoteSource_IsHonored()
        {
            var registration = new InGameProgressRegistration
            {
                IsRemote = true,
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.ProviderReported,
            };

            Assert.AreEqual(
                InGameUnlockAnchorPolicy.ProviderReported,
                InGameUnlockAnchorSelector.ResolvePolicy(registration));
        }

        [TestMethod]
        public void ResolvePolicy_NullRegistration_KeepsProviderReported()
        {
            // The refresh prong runs for games with no fast in-game source at all.
            Assert.AreEqual(
                InGameUnlockAnchorPolicy.ProviderReported,
                InGameUnlockAnchorSelector.ResolvePolicy(null));
        }

        [TestMethod]
        public void Select_ObservationPolicyWithoutReportedStamp_IsNotAForeignStamp()
        {
            var selected = InGameUnlockAnchorSelector.Select(
                InGameUnlockAnchorPolicy.SourceObservation,
                null,
                Observed);

            Assert.AreEqual(Observed, selected.Utc);
            Assert.AreEqual(UnlockVideoAnchorSource.SourceObservation, selected.Source);
        }
    }
}
