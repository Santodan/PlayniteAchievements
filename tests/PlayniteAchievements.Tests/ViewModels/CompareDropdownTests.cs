using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class CompareDropdownTests
    {
        private static FriendSummaryItem MakeFriend(string externalUserId, bool isFavorite = false)
        {
            return new FriendSummaryItem
            {
                ProviderKey = "Steam",
                ExternalUserId = externalUserId,
                DisplayName = externalUserId,
                IsFavorite = isFavorite
            };
        }

        [TestMethod]
        public void CompareFriendOptions_ListFavoritesFirst_ThenFriendsWithTheGame()
        {
            var plain = MakeFriend("plain");
            var owner = MakeFriend("owner");
            var favorite = MakeFriend("favorite", isFavorite: true);
            var favoriteOwner = MakeFriend("favoriteOwner", isFavorite: true);
            var selected = MakeFriend("selected", isFavorite: true);
            var owners = new HashSet<FriendSummaryItem> { owner, favoriteOwner };

            var options = FriendSurfaceControlBars.GetCompareFriendOptions(
                new[] { plain, owner, selected, favorite, favoriteOwner },
                selected,
                owners.Contains);

            CollectionAssert.AreEqual(
                new[] { favoriteOwner, favorite, owner, plain },
                options.ToList());
        }

        [TestMethod]
        public void IsKeyMuted_HoldsForACandidateWithoutTheGame_AndNeverForSelf()
        {
            var selected = MakeFriend("selected");
            var owner = MakeFriend("owner");
            var other = MakeFriend("other");
            var controller = new FriendVsFriendCompareController(
                () => selected,
                () => new List<FriendSummaryItem> { owner, other },
                hasPairData: friend => ReferenceEquals(friend, owner));

            Assert.IsFalse(controller.IsKeyMuted(FriendOverviewProjection.GetFriendScopeKey(owner)));
            Assert.IsTrue(controller.IsKeyMuted(FriendOverviewProjection.GetFriendScopeKey(other)));
            Assert.IsFalse(controller.IsKeyMuted(FriendVsFriendCompareController.SelfOptionKey));
            Assert.IsFalse(controller.IsKeyMuted("steam|unlisted"));
        }

        [TestMethod]
        public void IsKeyMuted_IsFalseForEveryCandidate_WithoutAGameTest()
        {
            var other = MakeFriend("other");
            var controller = new FriendVsFriendCompareController(
                () => null,
                () => new List<FriendSummaryItem> { other });

            Assert.IsFalse(controller.IsKeyMuted(FriendOverviewProjection.GetFriendScopeKey(other)));
        }

        [TestMethod]
        public void AdoptSelection_TakesAnotherGridsPick_WithoutSavingItBack()
        {
            var selected = MakeFriend("selected");
            var first = MakeFriend("first");
            var second = MakeFriend("second");
            var firstKey = FriendOverviewProjection.GetFriendScopeKey(first);
            var secondKey = FriendOverviewProjection.GetFriendScopeKey(second);
            var saves = new List<string>();
            var controller = new FriendVsFriendCompareController(
                () => selected,
                () => new List<FriendSummaryItem> { first, second });
            var row = new FriendAchievementDisplayItem
            {
                ApiName = "ACH_1",
                ProviderKey = "Steam",
                FriendExternalUserId = "selected"
            };
            var rows = new List<FriendAchievementDisplayItem> { row };
            controller.PersistSelection(firstKey + "\tfirst", saves.Add);
            controller.UpdateRows(rows, rows);
            Assert.IsTrue(controller.IsKeySelected(firstKey));

            controller.AdoptSelection(secondKey + "\tsecond");
            Assert.IsTrue(controller.IsKeySelected(secondKey));
            Assert.IsFalse(controller.IsKeySelected(firstKey));
            Assert.AreEqual("second", row.ComparisonFriendName);

            controller.AdoptSelection(null);
            Assert.IsFalse(controller.IsKeySelected(secondKey));
            Assert.IsFalse(row.HasComparison);
            Assert.AreEqual(0, saves.Count);
        }

        [TestMethod]
        public void CompareGroup_SharesOnePickPerWindow_AndKeepsOtherSurfacesApart()
        {
            Assert.AreEqual(
                GridOptionKeys.Achievement.OverviewRecent,
                GridOptionKeys.Achievement.CompareGroup(GridOptionKeys.Achievement.OverviewSelectedGame));
            Assert.AreEqual(
                GridOptionKeys.Achievement.ViewFriendsAchievements,
                GridOptionKeys.Achievement.CompareGroup(
                    GridOptionKeys.Achievement.ViewFriendsAchievementsSelectedFriend));
            Assert.AreEqual(
                GridOptionKeys.Achievement.SingleGame,
                GridOptionKeys.Achievement.CompareGroup(GridOptionKeys.Achievement.SingleGame));
            Assert.AreEqual(
                GridOptionKeys.Achievement.DesktopTheme,
                GridOptionKeys.Achievement.CompareGroup(GridOptionKeys.Achievement.DesktopTheme));

            var friendsOverview = GridOptionKeys.Achievement.CompareGroupMembers(
                GridOptionKeys.Achievement.FriendsOverviewRecent);
            Assert.AreEqual(4, friendsOverview.Count);
            foreach (var member in friendsOverview)
            {
                Assert.AreEqual(
                    GridOptionKeys.Achievement.FriendsOverviewRecent,
                    GridOptionKeys.Achievement.CompareGroup(member));
            }
        }
    }
}
