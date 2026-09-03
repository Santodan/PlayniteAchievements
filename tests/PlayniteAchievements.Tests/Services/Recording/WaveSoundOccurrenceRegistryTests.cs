using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Services.Recording
{
    [TestClass]
    public class WaveSoundOccurrenceRegistryTests
    {
        private static readonly DateTime T0 =
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        [TestMethod]
        public void OneWaveWithSeveralUnlocksCreatesOneOwnedOccurrence()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var wave = Guid.NewGuid();
            var owners = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

            var occurrence = registry.Register(
                session, wave, T0, 6.5, 0.3, owners, "diamond.wav", 0.4, 450);

            Assert.AreEqual(wave, occurrence.OccurrenceId);
            Assert.AreSame(occurrence, registry.Find(session, wave));
            CollectionAssert.AreEquivalent(owners, occurrence.OwnerCorrelationIds.ToArray());
            foreach (var owner in owners)
            {
                Assert.AreSame(occurrence, registry.FindOwned(session, owner));
            }
            Assert.AreEqual(1, registry.GetOverlapping(session, T0, T0.AddSeconds(7)).Count);
        }

        [TestMethod]
        public void SameTimestampAndSameFileRemainDistinctWaves()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var firstOwner = Guid.NewGuid();
            var secondOwner = Guid.NewGuid();
            var first = registry.Register(
                session, Guid.NewGuid(), T0, 4, 0.3, new[] { firstOwner }, "same.wav", 1, 0);
            var second = registry.Register(
                session, Guid.NewGuid(), T0, 4, 0.3, new[] { secondOwner }, "same.wav", 1, 0);

            Assert.AreNotEqual(first.OccurrenceId, second.OccurrenceId);
            Assert.AreNotEqual(first.Sequence, second.Sequence);
            Assert.AreEqual(T0, first.EndUtc);
            registry.SetNaturalPlaybackSeconds(session, first.OccurrenceId, 9);
            Assert.AreEqual(
                T0,
                first.EndUtc,
                "a late duration read must not resurrect a same-stamp player UPS replaced");
            Assert.AreSame(first, registry.FindOwned(session, firstOwner));
            Assert.AreSame(second, registry.FindOwned(session, secondOwner));
        }

        [TestMethod]
        public void LaterUpsLaunchStopsThePreviousOccurrenceAndFormsOneOverlapCluster()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var first = registry.Register(
                session, Guid.NewGuid(), T0, 8, 0.3, new[] { Guid.NewGuid() }, "one.wav", 1, 0);
            var secondLaunch = T0.AddSeconds(2);
            var second = registry.Register(
                session, Guid.NewGuid(), secondLaunch, 4, 0.3,
                new[] { Guid.NewGuid() }, "two.wav", 1, 0);

            Assert.AreEqual(secondLaunch, first.EndUtc);
            var clusters = registry.GetOverlappingClusters(
                session, T0.AddSeconds(-1), T0.AddSeconds(10));
            Assert.AreEqual(1, clusters.Count);
            CollectionAssert.AreEqual(
                new[] { first.OccurrenceId, second.OccurrenceId },
                clusters[0].Occurrences.Select(o => o.OccurrenceId).ToArray());
        }

        [TestMethod]
        public void QueryIncludesAChimeThatStartsBeforeTheClipButRingsIntoIt()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var occurrence = registry.Register(
                session, Guid.NewGuid(), T0, 5, 0.3,
                new[] { Guid.NewGuid() }, "long.wav", 1, 0);

            var found = registry.GetOverlapping(
                session, T0.AddSeconds(4.5), T0.AddSeconds(8));
            Assert.AreEqual(1, found.Count);
            Assert.AreSame(occurrence, found[0]);
        }

        [TestMethod]
        public void SessionsDoNotCrossContaminateAndRetentionIsTimeBased()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var keepSession = Guid.NewGuid();
            var pruneSession = Guid.NewGuid();
            registry.Register(
                keepSession, Guid.NewGuid(), T0, 1, 0.3,
                new[] { Guid.NewGuid() }, "keep.wav", 1, 0);
            registry.Register(
                pruneSession, Guid.NewGuid(), T0, 1, 0.3,
                new[] { Guid.NewGuid() }, "prune.wav", 1, 0);

            registry.PruneSessionBefore(pruneSession, T0.AddSeconds(2));

            Assert.AreEqual(1, registry.GetOverlapping(
                keepSession, T0.AddSeconds(-1), T0.AddSeconds(2)).Count);
            Assert.AreEqual(0, registry.GetOverlapping(
                pruneSession, T0.AddSeconds(-1), T0.AddSeconds(2)).Count);
        }

        [TestMethod]
        public void OutOfOrderDeliveryStillStopsTheOlderPlayerAtTheNewerLaunch()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var newer = registry.Register(
                session, Guid.NewGuid(), T0.AddSeconds(3), 5, 0.3,
                new[] { Guid.NewGuid() }, "new.wav", 1, 0);
            var older = registry.Register(
                session, Guid.NewGuid(), T0, 8, 0.3,
                new[] { Guid.NewGuid() }, "old.wav", 1, 0);

            Assert.AreEqual(newer.LaunchUtc, older.EndUtc);
            Assert.AreEqual(T0.AddSeconds(8), newer.EndUtc);
        }

        [TestMethod]
        public void TimelineSearchPaddingExpandsCleanupButNotReplacementPlayback()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var occurrence = registry.Register(
                session, Guid.NewGuid(), T0, 1, 0.3,
                new[] { Guid.NewGuid() }, "shifted.wav", 1, 0,
                timelinePaddingSeconds: 0.75);

            Assert.AreEqual(T0.AddSeconds(1), occurrence.EndUtc);
            Assert.AreEqual(T0.AddSeconds(-1.05), occurrence.RemovalStartUtc);
            Assert.AreEqual(T0.AddSeconds(1.75), occurrence.RemovalEndUtc);
            var cluster = registry.GetOverlappingClusters(
                session, T0.AddSeconds(-2), T0.AddSeconds(3)).Single();
            Assert.AreEqual(occurrence.RemovalStartUtc, cluster.StartUtc);
            Assert.AreEqual(occurrence.RemovalEndUtc, cluster.EndUtc);
        }

        [TestMethod]
        public void ResolvedNaturalDurationReplacesEstimateAndNextLaunchStillWins()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var first = registry.Register(
                session, Guid.NewGuid(), T0, 2, 0.3,
                new[] { Guid.NewGuid() }, "long.wav", 1, 0);

            registry.SetNaturalPlaybackSeconds(session, first.OccurrenceId, 8);
            Assert.AreEqual(T0.AddSeconds(8), first.EndUtc);

            var second = registry.Register(
                session, Guid.NewGuid(), T0.AddSeconds(5), 2, 0.3,
                new[] { Guid.NewGuid() }, "next.wav", 1, 0);
            Assert.AreEqual(second.LaunchUtc, first.EndUtc);

            // A duration result arriving after the next launch must not resurrect the disposed
            // first player beyond that launch.
            registry.SetNaturalPlaybackSeconds(session, first.OccurrenceId, 12);
            Assert.AreEqual(second.LaunchUtc, first.EndUtc);

            registry.SetNaturalPlaybackSeconds(session, second.OccurrenceId, 0.75);
            Assert.AreEqual(second.LaunchUtc.AddSeconds(0.75), second.EndUtc);
        }
    }
}
