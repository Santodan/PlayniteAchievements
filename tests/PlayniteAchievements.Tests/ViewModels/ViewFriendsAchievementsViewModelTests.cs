using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Tests.TestSupport;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class ViewFriendsAchievementsViewModelTests
    {
        private static readonly Guid GameOne = Guid.Parse("11111111-1111-1111-1111-111111111111");

        [TestMethod]
        public void ReadsTheGamesFriendsAndRows_AndRereadsThemOnATouchingChange()
        {
            var reads = new InMemoryFriendsOverviewReads(CreateData());
            var scheduler = new ManualReadScheduler();
            var runtime = new RefreshRuntime();
            using var vm = new ViewFriendsAchievementsViewModel(
                GameOne,
                new RefreshRuntime.TestRuntimeCache { FriendReads = reads },
                runtime,
                new RefreshEntryPoint(runtime, logger: null),
                null,
                null,
                new PlayniteAchievementsSettings(),
                scheduler);
            scheduler.Drain();

            CollectionAssert.AreEqual(new Guid?[] { GameOne }, reads.ListGames, "the lists of the window's game");
            CollectionAssert.AreEqual(new[] { GameOne }, reads.RowReads.Single().GameIds.ToArray(), "every friend's rows of the game");
            CollectionAssert.AreEquivalent(new[] { "Alice", "Bob" }, vm.Friends.Select(friend => friend.DisplayName).ToArray());
            CollectionAssert.AreEquivalent(new[] { "Alice One", "Bob Two" }, vm.Achievements.Select(row => row.DisplayName).ToArray(), "unlocks of every friend");

            vm.SelectedFriend = vm.Friends.Single(friend => friend.DisplayName == "Alice");
            CollectionAssert.AreEquivalent(new[] { "Alice One", "Alice Two" }, vm.Achievements.Select(row => row.DisplayName).ToArray(), "the friend's locked rows too");
            StringAssert.Contains(vm.AchievementCountText, "1/2");

            reads.Feed.Raise(CacheInvalidatedEventArgs.Scoped(new[] { GameOne }), null);
            scheduler.Drain();
            scheduler.Elapse();
            scheduler.Drain();
            Assert.AreEqual(2, reads.ListLoads);
            Assert.AreEqual("Alice", vm.SelectedFriend?.DisplayName, "the selection stays");
        }

        private static FriendsOverviewData CreateData()
        {
            FriendAchievementDisplayItem Row(string friend, string name, bool unlocked) => new FriendAchievementDisplayItem
            {
                ProviderKey = "Steam",
                FriendExternalUserId = friend.ToLowerInvariant(),
                FriendName = friend,
                AppId = 10,
                PlayniteGameId = GameOne,
                GameName = "Game One",
                ApiName = name,
                DisplayName = name,
                Unlocked = unlocked,
                UnlockTimeUtc = unlocked ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : (DateTime?)null
            };

            FriendGameLinkItem Link(string friend) => new FriendGameLinkItem
            {
                ProviderKey = "Steam",
                ExternalUserId = friend,
                AppId = 10,
                PlayniteGameId = GameOne,
                OwnershipConfirmed = true
            };

            return new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem>
                {
                    new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "alice", DisplayName = "Alice" },
                    new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "bob", DisplayName = "Bob" }
                },
                Games = new List<FriendGameSummaryItem>
                {
                    new FriendGameSummaryItem { ProviderKey = "Steam", AppId = 10, PlayniteGameId = GameOne, GameName = "Game One", TotalAchievements = 2 }
                },
                FriendGameLinks = new List<FriendGameLinkItem> { Link("alice"), Link("bob") },
                AllAchievements = new List<FriendAchievementDisplayItem>
                {
                    Row("Alice", "Alice One", true),
                    Row("Alice", "Alice Two", false),
                    Row("Bob", "Bob Two", true)
                }
            };
        }
    }
}
