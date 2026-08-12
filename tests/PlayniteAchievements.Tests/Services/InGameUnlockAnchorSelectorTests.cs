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
            Assert.AreEqual(UnlockVideoAnchorSource.SourceObservation, selected.Source);
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
    }
}
