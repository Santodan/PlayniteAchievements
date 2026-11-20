using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ThemeIntegration.Tests;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class LibraryFriendCompareControllerTests
    {
        [TestMethod]
        public async Task PickingAFriend_ReadsTheirOwnershipLinksAsTheyAreThen()
        {
            var gameId = Guid.NewGuid();
            var friend = new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "f1", DisplayName = "Fred" };
            var cache = new ThemeIntegrationBindingsTests.FakeFriendCache(new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem> { friend }
            });
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.EnableFriendsFeatures = true;
            var controller = new LibraryFriendCompareController(cache, settings, null);
            controller.Refresh();
            await controller.LoadTask;
            var item = new AchievementDisplayItem { PlayniteGameId = gameId, ApiName = "a1" };
            controller.SetTargetItems(new[] { item });

            // The friend's game list arrives after the friend list was loaded.
            cache.Data.FriendGameLinks = new List<FriendGameLinkItem>
            {
                new FriendGameLinkItem { ProviderKey = "Steam", ExternalUserId = "f1", PlayniteGameId = gameId, OwnershipConfirmed = true }
            };
            controller.SelectKey(FriendOverviewProjection.GetFriendScopeKey(friend), true);
            await controller.LoadTask;

            Assert.IsTrue(item.HasComparison);
            Assert.IsTrue(item.ComparisonApplicable, "a game the friend owns reads Locked, not N/A");
        }
    }
}
