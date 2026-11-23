using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Tests.TestSupport;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// The feed raised the way <c>CacheManager</c> raises it: current-user writes through
    /// <see cref="AchievementDataFeed.Raise"/>, friend writes through
    /// <see cref="AchievementDataFeed.RaiseFriendChange"/> inside scan batches, and custom layer
    /// writes through <see cref="AchievementDataFeed.Publish"/> once the mirror rows changed.
    /// </summary>
    [TestClass]
    public class AchievementDataFeedTests
    {
        private static readonly Guid GameA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

        private readonly List<CacheInvalidatedEventArgs> _cacheEvents = new List<CacheInvalidatedEventArgs>();
        private readonly List<FriendCacheInvalidatedEventArgs> _friendEvents = new List<FriendCacheInvalidatedEventArgs>();
        private readonly List<AchievementDataChange> _changes = new List<AchievementDataChange>();
        private AchievementDataFeed _feed;

        [TestInitialize]
        public void Setup()
        {
            _feed = new AchievementDataFeed(_cacheEvents.Add, _friendEvents.Add);
            _feed.AchievementDataChanged += (_, change) => _changes.Add(change);
        }

        [TestMethod]
        public void FriendDefinitionChange_TouchesTheCurrentUsersQueriesOfItsLibraryGame()
        {
            var asked = new List<FriendCacheChange>();
            var feed = new AchievementDataFeed(null, null, libraryGamesOf: changes =>
            {
                asked.AddRange(changes);
                return new[] { GameA };
            });
            var changes = new List<AchievementDataChange>();
            feed.AchievementDataChanged += (_, change) => changes.Add(change);

            feed.RaiseFriendChange(FriendCacheChange.ForFriendOwnership("Steam", "f1"));
            Assert.AreEqual(0, asked.Count, "only definition changes are looked up");
            Assert.IsFalse(changes[0].Touches(AchievementQuery.CurrentUser));

            feed.RaiseFriendChange(FriendCacheChange.ForGameDefinition("Steam", 10, null));
            Assert.AreEqual(1, asked.Count);
            CollectionAssert.AreEqual(new[] { GameA }, changes[1].GameIds.ToArray());
            Assert.IsTrue(changes[1].Touches(AchievementQuery.CurrentUser.ForGames(new[] { GameA })));
            Assert.IsFalse(changes[1].Touches(AchievementQuery.CurrentUser.ForGames(new[] { Guid.NewGuid() })));
            Assert.IsTrue(changes[1].Touches(AchievementQuery.AllFriends.ForFriend("Steam", "f2")), "friend queries are still touched");
        }

        [TestMethod]
        public void CurrentUserSave_RaisesCacheInvalidatedAndOneChange()
        {
            _feed.Raise(CacheInvalidatedEventArgs.Scoped(new[] { GameA }), null);

            Assert.AreEqual(1, _cacheEvents.Count);
            Assert.AreEqual(0, _friendEvents.Count);
            Assert.AreEqual(1, _changes.Count);
            CollectionAssert.AreEqual(new[] { GameA }, _changes[0].GameIds.ToArray());
        }

        [TestMethod]
        public void BothEventsAtOnce_RaiseOneChange()
        {
            _feed.Raise(CacheInvalidatedEventArgs.Scoped(new[] { GameA }), FriendCacheInvalidatedEventArgs.FullInvalidation);

            Assert.AreEqual(1, _cacheEvents.Count);
            Assert.AreEqual(1, _friendEvents.Count);
            Assert.AreEqual(1, _changes.Count);
            Assert.IsTrue(_changes[0].AllFriends);
            Assert.AreEqual(1, _changes[0].GameIds.Count);
        }

        [TestMethod]
        public void CustomLayerSave_RaisesOneChangeAndNoCacheEvent()
        {
            _feed.Publish(AchievementDataChange.ForGames(new[] { GameA }));

            Assert.AreEqual(0, _cacheEvents.Count);
            Assert.AreEqual(0, _friendEvents.Count);
            Assert.AreEqual(1, _changes.Count);
            Assert.IsTrue(_changes[0].Touches(AchievementQuery.CurrentUser.ForGames(new[] { GameA })));
        }

        [TestMethod]
        public void FriendWriteOutsideABatch_RaisesAtOnce()
        {
            _feed.RaiseFriendChange(FriendCacheChange.ForFriendOwnership("Steam", "f1"));
            _feed.RaiseFriendChange(null);

            Assert.AreEqual(2, _friendEvents.Count);
            Assert.AreEqual(2, _changes.Count);
            Assert.IsFalse(_changes[0].AllFriends);
            Assert.IsTrue(_changes[1].AllFriends, "an unscoped friend write changes every friend row");
        }

        [TestMethod]
        public void FriendScanBatch_RaisesOnceWhenTheOutermostBatchEnds()
        {
            using (_feed.BeginFriendBatch())
            {
                _feed.RaiseFriendChange(FriendCacheChange.ForFriendGameAchievements("Steam", "f1", 10, "k10"));
                using (_feed.BeginFriendBatch())
                {
                    _feed.RaiseFriendChange(FriendCacheChange.ForFriendGameAchievements("Steam", "f1", 11, "k11"));
                }

                _feed.RaiseFriendChange(FriendCacheChange.ForFriendGameAchievements("Steam", "f1", 10, "k10"));
                Assert.AreEqual(0, _changes.Count, "nothing is raised inside the window");
            }

            Assert.AreEqual(1, _friendEvents.Count);
            Assert.AreEqual(1, _changes.Count);
            Assert.AreEqual(2, _changes[0].FriendChanges.Count, "the same change twice is one");
        }

        [TestMethod]
        public void FriendScanBatch_FlushRaisesWhatCameSoFar_AndTheEndRaisesTheRest()
        {
            using (var scan = _feed.BeginFriendBatch())
            {
                scan.Flush();
                Assert.AreEqual(0, _changes.Count, "an empty flush raises nothing");

                _feed.RaiseFriendChange(FriendCacheChange.ForRoster("Steam"));
                scan.Flush();
                Assert.AreEqual(1, _changes.Count);

                _feed.RaiseFriendChange(null);
            }

            Assert.AreEqual(2, _changes.Count);
            Assert.IsTrue(_changes[1].AllFriends);
            Assert.AreEqual(2, _friendEvents.Count);
        }

        [TestMethod]
        public void Subscribe_RerunsOncePerWindow_ForTouchingChanges()
        {
            var scheduler = new ManualReadScheduler();
            var reruns = 0;
            var subscription = _feed.Subscribe(
                () => AchievementQuery.CurrentUser.ForGames(new[] { GameA }),
                scheduler,
                () => TimeSpan.FromMilliseconds(300),
                () => reruns++);

            _feed.Publish(AchievementDataChange.ForGames(new[] { Guid.NewGuid() }));
            _feed.RaiseFriendChange(FriendCacheChange.ForRoster("Steam"));
            scheduler.Drain();
            Assert.AreEqual(0, scheduler.PendingTimers, "changes that do not touch the query start no window");

            _feed.Publish(AchievementDataChange.ForGames(new[] { GameA }));
            _feed.Publish(AchievementDataChange.Full);
            scheduler.Drain();
            Assert.AreEqual(1, scheduler.PendingTimers, "changes inside a window join it");
            scheduler.Elapse();
            scheduler.Drain();
            Assert.AreEqual(1, reruns);

            _feed.Publish(AchievementDataChange.Full);
            scheduler.Drain();
            subscription.Dispose();
            scheduler.Elapse();
            scheduler.Drain();
            Assert.AreEqual(1, reruns, "disposing drops the pending rerun");
        }

        [TestMethod]
        public void Subscribe_WhileTheGateIsClosed_MarksStale_AndRerunsOnceWhenItOpens()
        {
            var scheduler = new ManualReadScheduler();
            var gate = new FeedGate();
            var reruns = 0;
            var subscription = _feed.Subscribe(
                () => AchievementQuery.CurrentUser.ForGames(new[] { GameA }),
                scheduler,
                () => TimeSpan.FromMilliseconds(300),
                () => reruns++,
                gate);

            gate.IsOpen = false;
            _feed.Publish(AchievementDataChange.ForGames(new[] { GameA }));
            _feed.Publish(AchievementDataChange.Full);
            scheduler.Drain();
            Assert.AreEqual(0, scheduler.PendingTimers, "a closed gate starts no window");
            Assert.AreEqual(0, reruns);

            gate.IsOpen = true;
            Assert.AreEqual(1, reruns, "opening reruns the stale subscription once");
            gate.IsOpen = false;
            gate.IsOpen = true;
            Assert.AreEqual(1, reruns, "with nothing changed meanwhile, opening reruns nothing");

            // A window that ends after the gate closed is held back too.
            _feed.Publish(AchievementDataChange.Full);
            scheduler.Drain();
            gate.IsOpen = false;
            scheduler.Elapse();
            scheduler.Drain();
            Assert.AreEqual(1, reruns);
            gate.IsOpen = true;
            Assert.AreEqual(2, reruns);

            gate.IsOpen = false;
            _feed.Publish(AchievementDataChange.Full);
            scheduler.Drain();
            subscription.Dispose();
            gate.IsOpen = true;
            Assert.AreEqual(2, reruns, "a disposed subscription is not rerun");
        }
    }
}
