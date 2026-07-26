using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class FriendsOverviewPatchTests
    {
        private static FriendAchievementDisplayItem Item(
            string provider,
            string friend,
            int appId,
            string gameKey,
            string gameName,
            string name,
            bool unlocked,
            DateTime? unlockTimeUtc = null)
        {
            return new FriendAchievementDisplayItem
            {
                ProviderKey = provider,
                FriendExternalUserId = friend,
                FriendName = friend,
                AppId = appId,
                ProviderGameKey = gameKey,
                GameName = gameName,
                SortingName = gameName,
                DisplayName = name,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTimeUtc
            };
        }

        private static List<FriendAchievementDisplayItem> BaseWorld()
        {
            return new List<FriendAchievementDisplayItem>
            {
                Item("Steam", "alice", 10, "app10", "Game Ten", "A1", unlocked: false),
                Item("Steam", "alice", 10, "app10", "Game Ten", "A2", unlocked: true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Item("Steam", "bob", 10, "app10", "Game Ten", "B1", unlocked: true, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                Item("Steam", "alice", 20, "app20", "Game Twenty", "C1", unlocked: false)
            };
        }

        private static FriendsOverviewData Patch(params FriendAchievementDisplayItem[] achievements)
        {
            return new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem>
                {
                    new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "alice", DisplayName = "alice" },
                    new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "bob", DisplayName = "bob" }
                },
                Games = new List<FriendGameSummaryItem>(),
                FriendGameLinks = new List<FriendGameLinkItem>(),
                AllAchievements = achievements.ToList()
            };
        }

        [TestMethod]
        public void BuildPatchedData_ReplacesChangedFriendGameRows_AndRederives()
        {
            var baseAchievements = BaseWorld();
            var freshA1 = Item("Steam", "alice", 10, "app10", "Game Ten", "A1", unlocked: true, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
            var freshA2 = Item("Steam", "alice", 10, "app10", "Game Ten", "A2", unlocked: true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var changes = new[] { FriendCacheChange.ForFriendGameAchievements("Steam", "alice", 10, "app10") };

            var data = FriendsOverviewDataCoordinator.BuildPatchedData(
                baseAchievements, Patch(freshA1, freshA2), changes);

            Assert.AreEqual(4, data.AllAchievements.Count);
            Assert.IsTrue(data.AllAchievements.Contains(freshA1), "Fresh rows must replace the changed pair's rows.");
            Assert.IsFalse(data.AllAchievements.Contains(baseAchievements[0]), "Old rows of the changed pair must be gone.");
            Assert.IsTrue(data.AllAchievements.Contains(baseAchievements[2]), "Other friends' rows are reused by reference.");
            Assert.IsTrue(data.AllAchievements.Contains(baseAchievements[3]), "Other games' rows are reused by reference.");

            // Derivations recomputed over the patched set.
            Assert.AreEqual(3, data.AllUnlockedAchievements.Count);
            CollectionAssert.AreEqual(
                new[] { "A1", "B1", "A2" },
                data.RecentUnlocks.Select(item => item.DisplayName).ToArray(),
                "Recent unlocks must be re-sorted by unlock time descending.");
        }

        [TestMethod]
        public void BuildPatchedData_GameDefinitionChange_ReplacesAllFriendsRowsOfThatGame()
        {
            var baseAchievements = BaseWorld();
            var fresh = Item("Steam", "alice", 10, "app10", "Game Ten", "A1", unlocked: false);
            var changes = new[] { FriendCacheChange.ForGameDefinition("Steam", 10, "app10") };

            var data = FriendsOverviewDataCoordinator.BuildPatchedData(
                baseAchievements, Patch(fresh), changes);

            // All three game-10 rows (alice x2, bob x1) removed; one fresh row + untouched game-20 row.
            Assert.AreEqual(2, data.AllAchievements.Count);
            Assert.IsTrue(data.AllAchievements.Contains(fresh));
            Assert.IsTrue(data.AllAchievements.Contains(baseAchievements[3]));
        }

        [TestMethod]
        public void BuildPatchedData_FriendRemoved_DropsRowsWithoutReplacement()
        {
            var baseAchievements = BaseWorld();
            var changes = new[] { FriendCacheChange.ForFriendRemoved("Steam", "alice") };

            var data = FriendsOverviewDataCoordinator.BuildPatchedData(
                baseAchievements, Patch(), changes);

            Assert.AreEqual(1, data.AllAchievements.Count);
            Assert.AreEqual("B1", data.AllAchievements[0].DisplayName);
        }

        [TestMethod]
        public void BuildPatchedData_OwnershipChange_KeepsAchievementRowsAndTakesFreshLists()
        {
            var baseAchievements = BaseWorld();
            var patch = Patch();
            var changes = new[] { FriendCacheChange.ForFriendOwnership("Steam", "alice") };

            var data = FriendsOverviewDataCoordinator.BuildPatchedData(baseAchievements, patch, changes);

            Assert.AreEqual(baseAchievements.Count, data.AllAchievements.Count);
            Assert.AreSame(patch.Friends, data.Friends, "The cheap lists come from the fresh patch load.");
        }

        [TestMethod]
        public void MatchesRemovalScope_MatchesByAppIdWhenProviderGameKeyMissing()
        {
            var item = Item("Steam", "alice", 10, null, "Game Ten", "A1", unlocked: false);
            var change = FriendCacheChange.ForFriendGameAchievements("steam", "ALICE", 10, null);

            Assert.IsTrue(FriendsOverviewDataCoordinator.MatchesRemovalScope(item, change));
        }

        [TestMethod]
        public void CanPatch_RosterOrFullScope_IsNotPatchable()
        {
            Assert.IsFalse(FriendsOverviewDataCoordinator.CanPatch(FriendCacheInvalidatedEventArgs.FullInvalidation));
            Assert.IsFalse(FriendsOverviewDataCoordinator.CanPatch(
                FriendCacheInvalidatedEventArgs.Scoped(new[] { FriendCacheChange.ForRoster("Steam") })));
            Assert.IsTrue(FriendsOverviewDataCoordinator.CanPatch(
                FriendCacheInvalidatedEventArgs.Scoped(new[]
                {
                    FriendCacheChange.ForFriendGameAchievements("Steam", "alice", 10, "app10"),
                    FriendCacheChange.ForFriendOwnership("Steam", "alice")
                })));
        }

        [TestMethod]
        public void SelectAchievementReloadScopes_KeepsOnlyRowChangingKinds()
        {
            var scopes = FriendsOverviewDataCoordinator.SelectAchievementReloadScopes(new[]
            {
                FriendCacheChange.ForFriendGameAchievements("Steam", "alice", 10, "app10"),
                FriendCacheChange.ForGameDefinition("Steam", 20, "app20"),
                FriendCacheChange.ForFriendOwnership("Steam", "alice"),
                FriendCacheChange.ForFriendRemoved("Steam", "bob")
            });

            CollectionAssert.AreEquivalent(
                new[] { FriendCacheChangeKind.FriendGameAchievements, FriendCacheChangeKind.GameDefinition },
                scopes.Select(scope => scope.Kind).ToArray());
        }
    }
}
