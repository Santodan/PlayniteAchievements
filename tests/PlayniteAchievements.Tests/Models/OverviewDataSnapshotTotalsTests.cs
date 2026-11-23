using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class OverviewDataSnapshotTotalsTests
    {
        [TestMethod]
        public void FromGameSummaries_TotalsOnlyTheGivenGames()
        {
            var games = new List<GameSummaryItem>
            {
                new GameSummaryItem { ProviderKey = "Steam", TotalAchievements = 10, UnlockedAchievements = 8 },
                new GameSummaryItem { ProviderKey = "steam", TotalAchievements = 5, UnlockedAchievements = 1 },
                new GameSummaryItem { ProviderKey = "Epic", TotalAchievements = 20, UnlockedAchievements = 13 }
            };

            var snapshot = OverviewDataSnapshot.FromGameSummaries(games);

            Assert.AreEqual(3, snapshot.TotalGames);
            Assert.AreEqual(35, snapshot.TotalAchievements);
            Assert.AreEqual(22, snapshot.TotalUnlocked);
            Assert.AreEqual(13, snapshot.TotalLocked);
            Assert.AreEqual(9, snapshot.UnlockedByProvider["STEAM"]);
            Assert.AreEqual(15, snapshot.TotalByProvider["Steam"]);
            Assert.AreEqual(13, snapshot.UnlockedByProvider["Epic"]);
            Assert.AreEqual(3, snapshot.GameSummaries.Count);
        }

        [TestMethod]
        public void FromGameSummaries_EmptyOrNullListHasZeroTotals()
        {
            foreach (var games in new[] { new List<GameSummaryItem>(), null })
            {
                var snapshot = OverviewDataSnapshot.FromGameSummaries(games);

                Assert.AreEqual(0, snapshot.TotalGames);
                Assert.AreEqual(0, snapshot.TotalAchievements);
                Assert.AreEqual(0, snapshot.TotalLocked);
                Assert.AreEqual(0, snapshot.TotalByProvider.Count);
                Assert.AreEqual(0, snapshot.GameSummaries.Count);
            }
        }

        [TestMethod]
        public void RequestUnlockNextPool_ReadsOncePerDepthAndNotifiesEveryWaiter()
        {
            var reads = new List<int>();
            var candidate = new AchievementDisplayItem();
            using var gate = new System.Threading.ManualResetEventSlim(false);
            var snapshot = new OverviewDataSnapshot
            {
                UnlockNextReader = slice =>
                {
                    lock (reads)
                    {
                        reads.Add(slice);
                    }

                    gate.Wait();
                    return new List<AchievementDisplayItem> { candidate };
                }
            };
            Assert.IsFalse(snapshot.NeedsUnlockNextRead(0), "no widget asks");
            Assert.IsTrue(snapshot.NeedsUnlockNextRead(3));

            using var landed = new System.Threading.CountdownEvent(2);
            snapshot.RequestUnlockNextPool(3, () => landed.Signal());
            snapshot.RequestUnlockNextPool(5, () => landed.Signal());
            gate.Set();
            Assert.IsTrue(landed.Wait(System.TimeSpan.FromSeconds(5)));

            CollectionAssert.AreEqual(new[] { UnlockNextCandidateSelector.SliceFor(5) }, reads, "the same depth is read once");
            CollectionAssert.AreEqual(new[] { candidate }, snapshot.UnlockNextCandidates);
            Assert.IsFalse(snapshot.NeedsUnlockNextRead(5));
            Assert.IsTrue(snapshot.NeedsUnlockNextRead(UnlockNextCandidateSelector.SliceFor(5) + 1), "a deeper ask reads again");
        }

        [TestMethod]
        public void NeedsUnlockNextRead_IsFalseForADerivedViewWithNoReader()
        {
            Assert.IsFalse(OverviewDataSnapshot.FromGameSummaries(new List<GameSummaryItem>()).NeedsUnlockNextRead(5));
        }
    }
}
