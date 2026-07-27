using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class FriendSummarySortHelperTests
    {
        [TestMethod]
        public void SortByConfiguredDefault_RecentUnlockDescending_PutsNewestUnlockFirst()
        {
            var items = new List<FriendSummaryItem>
            {
                CreateItem("Gamma", lastUnlockUtc: Utc(2026, 4, 2)),
                CreateItem("Alpha", lastUnlockUtc: Utc(2026, 4, 5)),
                CreateItem("Beta", lastUnlockUtc: null)
            };

            FriendSummarySortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    FriendsOverviewFriendSummariesGridSortMode = FriendSummariesSortMode.RecentUnlock,
                    FriendsOverviewFriendSummariesGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Alpha", "Gamma", "Beta" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_SharedGames_UsesSharedGamesCount()
        {
            var items = new List<FriendSummaryItem>
            {
                CreateItem("Alpha", sharedGamesCount: 12),
                CreateItem("Beta", sharedGamesCount: 40),
                CreateItem("Gamma", sharedGamesCount: 24)
            };

            FriendSummarySortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    FriendsOverviewFriendSummariesGridSortMode = FriendSummariesSortMode.SharedGames,
                    FriendsOverviewFriendSummariesGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultOnDifferentColumn_CyclesAscendingDescendingThenReset()
        {
            var settings = new PersistedSettings
            {
                FriendsOverviewFriendSummariesGridSortMode = FriendSummariesSortMode.RecentUnlock,
                FriendsOverviewFriendSummariesGridSortDescending = true
            };

            var first = FriendSummarySortHelper.ResolveGridSortAction(
                nameof(FriendSummaryItem.DisplayName),
                currentSortPath: null,
                currentSortDirection: null,
                settings);
            var second = FriendSummarySortHelper.ResolveGridSortAction(
                nameof(FriendSummaryItem.DisplayName),
                first.SortMemberPath,
                first.Direction,
                settings);
            var third = FriendSummarySortHelper.ResolveGridSortAction(
                nameof(FriendSummaryItem.DisplayName),
                second.SortMemberPath,
                second.Direction,
                settings);

            Assert.AreEqual(FriendSummariesGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(FriendSummariesGridSortActionKind.ApplySort, second.Kind);
            Assert.AreEqual(ListSortDirection.Descending, second.Direction);
            Assert.AreEqual(FriendSummariesGridSortActionKind.ResetToDefault, third.Kind);
        }

        [TestMethod]
        public void TrySortItems_UnlockedAchievementsCount_SortsDescending()
        {
            var items = new List<FriendSummaryItem>
            {
                CreateItem("Alpha", unlockedAchievementsCount: 120),
                CreateItem("Beta", unlockedAchievementsCount: 420),
                CreateItem("Gamma", unlockedAchievementsCount: 240)
            };
            string currentSortPath = null;
            var currentSortDirection = ListSortDirection.Ascending;

            var sorted = FriendSummarySortHelper.TrySortItems(
                items,
                nameof(FriendSummaryItem.UnlockedAchievementsCount),
                ListSortDirection.Descending,
                ref currentSortPath,
                ref currentSortDirection);

            Assert.IsTrue(sorted);
            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void ApplySortIndicator_RecentUnlockDefault_DoesNotReturnVisibleColumn()
        {
            string indicatorPath = "seed";
            ListSortDirection? indicatorDirection = ListSortDirection.Ascending;

            FriendSummarySortHelper.ApplySortIndicator(
                null,
                null,
                new PersistedSettings
                {
                    FriendsOverviewFriendSummariesGridSortMode = FriendSummariesSortMode.RecentUnlock,
                    FriendsOverviewFriendSummariesGridSortDescending = true
                },
                (path, direction) =>
                {
                    indicatorPath = path;
                    indicatorDirection = direction;
                });

            Assert.IsNull(indicatorPath);
            Assert.IsNull(indicatorDirection);
        }

        private static FriendSummaryItem CreateItem(
            string displayName,
            DateTime? lastUnlockUtc = null,
            int sharedGamesCount = 0,
            int unlockedAchievementsCount = 0,
            int prestigeScore = 0,
            int collectionScore = 0)
        {
            return new FriendSummaryItem
            {
                DisplayName = displayName,
                ProviderKey = "steam",
                ExternalUserId = displayName,
                LastUnlockUtc = lastUnlockUtc,
                SharedGamesCount = sharedGamesCount,
                UnlockedAchievementsCount = unlockedAchievementsCount,
                PrestigeScore = prestigeScore,
                CollectionScore = collectionScore
            };
        }

        private static DateTime Utc(int year, int month, int day)
        {
            return DateTime.SpecifyKind(new DateTime(year, month, day, 12, 0, 0), DateTimeKind.Utc);
        }
    }
}
