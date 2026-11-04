using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class NotificationGameSummaryCacheTests
    {
        private static CachedSummaryData Data(params CachedGameSummaryData[] games) =>
            new CachedSummaryData { Games = games.ToList() };

        [TestMethod]
        public void ScopedUpdatesReuseOtherGamesAndRemoveMissingGame()
        {
            var cache = new NotificationGameSummaryCache();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var other = new CachedGameSummaryData { PlayniteGameId = second, CollectionScore = 20 };
            var fullReads = 0;
            Func<CachedSummaryData> full = () =>
            {
                fullReads++;
                return Data(new CachedGameSummaryData { PlayniteGameId = first, CollectionScore = 10 }, other);
            };
            var initial = cache.Get(full, id => AssertFailedSlice());
            Assert.AreSame(initial, cache.Get(full, id => AssertFailedSlice()));
            cache.Invalidate(new[] { first });
            var sliceReads = 0;
            var updated = cache.Get(full, id =>
            {
                sliceReads++;
                Assert.AreEqual(first, id);
                return Data(new CachedGameSummaryData { PlayniteGameId = first, CollectionScore = 15 });
            });
            Assert.AreEqual(35, updated.Sum(row => row.CollectionScore));
            Assert.AreSame(other, updated.Single(row => row.PlayniteGameId == second));
            Assert.AreEqual(10, initial.Single(row => row.PlayniteGameId == first).CollectionScore);
            cache.Invalidate(new[] { first });
            Assert.AreSame(other, cache.Get(full, id => Data()).Single());
            Assert.AreEqual(1, fullReads);
            Assert.AreEqual(1, sliceReads);
        }

        [TestMethod]
        public void WriteDeltaAndRefreshCompletionKeepGameScope()
        {
            var cache = new NotificationGameSummaryCache();
            var id = Guid.NewGuid();
            var reads = 0;
            Func<CachedSummaryData> full = () => { reads++; return Data(new CachedGameSummaryData { PlayniteGameId = id, Points = 1 }); };
            cache.Get(full, changed => AssertFailedSlice());
            // The real refresh writes an upsert delta, then reports its saved game ids.
            cache.InvalidateDelta(new global::PlayniteAchievements.Models.CacheDeltaEventArgs(id.ToString(), global::PlayniteAchievements.Models.CacheDeltaOperationType.Upsert, DateTime.UtcNow));
            cache.Invalidate(new[] { id });
            Assert.AreEqual(2, cache.Get(full, changed => Data(new CachedGameSummaryData { PlayniteGameId = changed, Points = 2 })).Single().Points);
            cache.InvalidateDelta(new global::PlayniteAchievements.Models.CacheDeltaEventArgs(id.ToString(), global::PlayniteAchievements.Models.CacheDeltaOperationType.Remove, DateTime.UtcNow));
            Assert.AreEqual(0, cache.Get(full, changed => Data()).Count);
            Assert.AreEqual(1, reads, "Neither a write nor its completion should force another library read.");
        }

        [DataTestMethod]
        [DataRow("", global::PlayniteAchievements.Models.CacheDeltaOperationType.FullReset)]
        [DataRow("unknown", global::PlayniteAchievements.Models.CacheDeltaOperationType.Upsert)]
        public void UnscopedDeltaReloadsLibrary(string key, global::PlayniteAchievements.Models.CacheDeltaOperationType operation)
        {
            var cache = new NotificationGameSummaryCache();
            var reads = 0;
            Func<CachedSummaryData> full = () => { reads++; return Data(); };
            cache.Get(full, changed => AssertFailedSlice());
            cache.InvalidateDelta(new global::PlayniteAchievements.Models.CacheDeltaEventArgs(key, operation, DateTime.UtcNow));
            cache.Get(full, changed => AssertFailedSlice());
            Assert.AreEqual(2, reads);
        }

        [TestMethod]
        public void GlobalInvalidationReloadsLibrary()
        {
            var cache = new NotificationGameSummaryCache();
            var reads = 0;
            Func<CachedSummaryData> full = () => { reads++; return Data(); };
            cache.Get(full, id => AssertFailedSlice());
            cache.Invalidate(null);
            cache.Get(full, id => AssertFailedSlice());
            Assert.AreEqual(2, reads);
        }

        [TestMethod]
        public void InvalidationDuringReadDoesNotInstallStaleRows()
        {
            var cache = new NotificationGameSummaryCache();
            var id = Guid.NewGuid();
            cache.Get(() =>
            {
                cache.Invalidate(new[] { id });
                return Data(new CachedGameSummaryData { PlayniteGameId = id, Points = 1 });
            }, changed => AssertFailedSlice());
            var fresh = cache.Get(() => Data(new CachedGameSummaryData { PlayniteGameId = id, Points = 2 }),
                changed => AssertFailedSlice());
            Assert.AreEqual(2, fresh.Single().Points);
        }

        [TestMethod]
        public void UnavailableScopedReadFallsBackToFullRead()
        {
            var cache = new NotificationGameSummaryCache();
            var id = Guid.NewGuid();
            cache.Get(() => Data(new CachedGameSummaryData { PlayniteGameId = id, Points = 1 }), changed => null);
            cache.Invalidate(new[] { id });
            Assert.AreEqual(2, cache.Get(() => Data(new CachedGameSummaryData { PlayniteGameId = id, Points = 2 }),
                changed => null).Single().Points);
        }

        private static CachedSummaryData AssertFailedSlice()
        {
            Assert.Fail("Unexpected scoped read.");
            return null;
        }
    }
}
