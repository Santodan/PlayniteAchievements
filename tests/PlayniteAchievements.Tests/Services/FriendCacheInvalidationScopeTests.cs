using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Friends;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class FriendCacheInvalidationScopeTests
    {
        [TestMethod]
        public void Accumulator_DedupesEquivalentChanges_CaseInsensitive()
        {
            var accumulator = new FriendCacheInvalidationScopeAccumulator();
            accumulator.Add(FriendCacheChange.ForFriendGameAchievements("Steam", "alice", 42, "app42"));
            accumulator.Add(FriendCacheChange.ForFriendGameAchievements("steam", "ALICE", 42, "APP42"));
            accumulator.Add(FriendCacheChange.ForFriendGameAchievements("steam", "alice", 43, "app43"));

            var args = accumulator.Drain();

            Assert.IsFalse(args.IsFull);
            Assert.AreEqual(2, args.Changes.Count);
        }

        [TestMethod]
        public void Accumulator_UnscopedChange_CollapsesToFull()
        {
            var accumulator = new FriendCacheInvalidationScopeAccumulator();
            accumulator.Add(FriendCacheChange.ForFriendOwnership("steam", "alice"));
            accumulator.Add(null);
            accumulator.Add(FriendCacheChange.ForFriendOwnership("steam", "bob"));

            var args = accumulator.Drain();

            Assert.IsTrue(args.IsFull);
            Assert.AreEqual(0, args.Changes.Count);
        }

        [TestMethod]
        public void Accumulator_Overflow_CollapsesToFull()
        {
            var accumulator = new FriendCacheInvalidationScopeAccumulator(maxChanges: 3);
            for (var i = 0; i < 4; i++)
            {
                accumulator.Add(FriendCacheChange.ForFriendGameAchievements("steam", "alice", 100 + i, "app" + i));
            }

            Assert.IsTrue(accumulator.Drain().IsFull);
        }

        [TestMethod]
        public void Accumulator_Drain_ResetsScopeForNextWindow()
        {
            var accumulator = new FriendCacheInvalidationScopeAccumulator(maxChanges: 3);
            accumulator.Add(null);
            Assert.IsTrue(accumulator.Drain().IsFull);

            accumulator.Add(FriendCacheChange.ForFriendGameAchievements("steam", "alice", 42, "app42"));
            var args = accumulator.Drain();

            Assert.IsFalse(args.IsFull);
            Assert.AreEqual(FriendCacheChangeKind.FriendGameAchievements, args.Changes.Single().Kind);
        }

        [TestMethod]
        public void EventArgs_ScopedWithEmptySet_IsFull()
        {
            Assert.IsTrue(FriendCacheInvalidatedEventArgs.Scoped(null).IsFull);
            Assert.IsTrue(FriendCacheInvalidatedEventArgs.Scoped(new FriendCacheChange[0]).IsFull);
        }

        [TestMethod]
        public void Change_DifferentKinds_AreDistinct()
        {
            var accumulator = new FriendCacheInvalidationScopeAccumulator();
            accumulator.Add(FriendCacheChange.ForFriendOwnership("steam", "alice"));
            accumulator.Add(FriendCacheChange.ForFriendRemoved("steam", "alice"));

            Assert.AreEqual(2, accumulator.Drain().Changes.Count);
        }
    }
}
