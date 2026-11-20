using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Tests.TestSupport;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class FriendsOverviewViewModelTests
    {
        [TestMethod]
        public void LoadAsync_ShowsTheSummariesAndEveryFriendsRecentUnlocksNewestFirst()
        {
            using var h = new Harness(CreateData()).Load();

            Assert.AreEqual(3, h.Vm.FilteredFriends.Count);
            Assert.AreEqual(3, h.Vm.FilteredGames.Count);
            CollectionAssert.AreEqual(
                new[] { "Recent Only", "Alice Game Two", "Bob Game One", "Cora GOG" },
                h.Rows);

            var query = h.Query;
            Assert.IsTrue(query.Friends);
            Assert.IsNull(query.FriendAccounts, "every friend");
            Assert.AreEqual(true, query.Unlocked);
            Assert.IsTrue(query.RequireUnlockTime, "the recent feed lists unlocks that have a time");
            Assert.AreEqual(AchievementSort.UnlockTime, query.Sort);
            Assert.IsTrue(query.Descending);
            Assert.AreEqual(1, h.Reads.ListLoads);
        }

        [TestMethod]
        public void LoadAsync_ShowsSkeletonRowsUntilTheListsLand()
        {
            using var h = new Harness(CreateData());
            h.Vm.LoadAsync();

            Assert.AreEqual(PlaceholderRows.Count, h.Vm.FilteredFriends.Count);
            Assert.IsTrue(h.Vm.FilteredFriends.All(item => item.IsPlaceholder));
            Assert.IsTrue(h.Vm.FilteredGames.All(item => item.IsPlaceholder));
            Assert.AreEqual(PlaceholderRows.Count, h.Vm.AchievementRows.Count);
            Assert.IsFalse(h.Vm.HasData);

            // A placeholder is never selected, and a search before the lists land keeps them.
            h.Vm.SelectedFriend = h.Vm.FilteredFriends[0];
            Assert.IsNull(h.Vm.SelectedFriend);
            h.Vm.FriendSearchText = "alice";
            Assert.IsTrue(h.Vm.FilteredFriends.All(item => item.IsPlaceholder));

            h.Settle();
            Assert.IsFalse(h.Vm.FilteredFriends.Any(item => item.IsPlaceholder));
            Assert.IsFalse(h.Vm.FilteredGames.Any(item => item.IsPlaceholder));
            Assert.AreSame(h.Vm.DisplayedAchievements, h.Vm.AchievementRows);
            Assert.IsTrue(h.Vm.HasData);
        }

        [TestMethod]
        public void LoadAsync_ReadsNoAchievementsUntilTheListsLand()
        {
            using var h = new Harness(CreateData());

            h.Vm.LoadAsync();
            Assert.IsNull(h.Vm.DisplayedAchievements, "the query names the friends the lists bring");

            h.Settle();
            Assert.IsNotNull(h.Vm.DisplayedAchievements);
        }

        [TestMethod]
        public void LoadAsync_AppliesConfiguredRowLimitsToAllThreeGrids()
        {
            using var h = new Harness(CreateData(), settings =>
            {
                settings.FriendsOverviewFriendSummariesGridMaxRows = 2;
                settings.FriendsOverviewGameSummariesGridMaxRows = 2;
                settings.FriendsOverviewAchievementsGridMaxRows = 2;
            }).Load();

            CollectionAssert.AreEqual(new[] { "Alice", "Bob" }, h.Vm.FilteredFriends.Select(item => item.DisplayName).ToArray());
            CollectionAssert.AreEqual(new[] { "Game One", "Game Two" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
            Assert.AreEqual(2, h.Vm.DisplayedAchievements.Count, "the cap limits the row count");
            CollectionAssert.AreEqual(new[] { "Recent Only", "Alice Game Two" }, h.Rows);
        }

        [TestMethod]
        public void SelectedFriend_ListsTheirGamesAndUnlocks_ThroughTheirAccounts()
        {
            var data = CreateData();
            using var h = new Harness(data).Load();

            h.Select(friend: h.Friend("Alice"));

            CollectionAssert.AreEquivalent(new[] { "Game One", "Game Two" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
            CollectionAssert.AreEquivalent(new[] { "Recent Only", "Alice Game Two" }, h.Rows);
            var query = h.Query;
            Assert.AreEqual("steam|alice", query.FriendAccounts.Single().Key);
            Assert.AreEqual(true, query.Unlocked);
            Assert.IsFalse(query.RequireUnlockTime, "a friend's unlocks include those without a time");
            Assert.IsNull(query.GameProviderKey);
        }

        [TestMethod]
        public void SelectedFriendGames_ComeFromTheirTotalsAndLinks()
        {
            using var h = new Harness(CreateData()).Load();

            h.Select(friend: h.Friend("Alice"));

            var gameOne = h.Vm.FilteredGames.Single(item => item.AppId == 10);
            Assert.AreEqual(600UL * 60UL, gameOne.PlaytimeSeconds);
            Assert.AreEqual(new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), gameOne.LastPlayed);
            Assert.AreEqual(1, gameOne.UnlockedAchievements);
            Assert.AreEqual(1, gameOne.UniqueFriendUnlockedAchievementsCount);
            Assert.AreEqual(4, gameOne.TotalAchievements);
            Assert.AreEqual(25, gameOne.Progression);
            Assert.AreEqual(new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), gameOne.LastUnlockUtc);
            Assert.AreEqual(1, gameOne.FriendsWithUnlocksCount);
            Assert.AreEqual(600, gameOne.TotalFriendPlaytimeMinutes);

            h.Select(friend: h.Friend("Bob"));
            var bobGameOne = h.Vm.FilteredGames.Single(item => item.AppId == 10);
            Assert.AreNotSame(gameOne, bobGameOne);
            Assert.AreEqual(300UL * 60UL, bobGameOne.PlaytimeSeconds);
            Assert.AreEqual(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), bobGameOne.LastUnlockUtc);

            // The lists read once: a friend's games need no read of their own.
            Assert.AreEqual(1, h.Reads.ListLoads);
        }

        [TestMethod]
        public void SelectedFriendGameSearch_FiltersTheirGames()
        {
            using var h = new Harness(CreateData()).Load();

            h.Select(friend: h.Friend("Alice"));
            h.Vm.GameSearchText = "two";
            h.Settle();

            CollectionAssert.AreEqual(new[] { "Game Two" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SelectedGame_ListsItsFriendsAndUnlocks_NarrowedToTheProviderGame()
        {
            var data = CreateData();
            using var h = new Harness(data).Load();

            h.Select(game: data.Games[0]);

            CollectionAssert.AreEquivalent(new[] { "Alice", "Bob" }, h.Vm.FilteredFriends.Select(item => item.DisplayName).ToArray());
            CollectionAssert.AreEquivalent(new[] { "Recent Only", "Bob Game One" }, h.Rows);
            var query = h.Query;
            Assert.AreEqual("Steam", query.GameProviderKey);
            Assert.AreEqual(10, query.AppId);
            CollectionAssert.AreEqual(new[] { data.Games[0].PlayniteGameId.Value }, query.GameIds.ToList());
            Assert.IsNull(query.FriendAccounts);
            Assert.AreEqual(true, query.Unlocked);
        }

        [TestMethod]
        public void SelectedFriendAndGame_ListsTheirEveryAchievement_AndFeedsTheCategoryRollups()
        {
            var data = CreateData();
            var locked = Locked(data, "AAA Locked");
            data.AllAchievements = new[] { locked }.Concat(data.AllAchievements).ToList();
            using var h = new Harness(data).Load();

            Assert.AreEqual(0, h.Vm.SelectedFriendGameAllAchievements.Count, "no pair, no rollup source");

            h.Select(friend: h.Friend("Alice"), game: data.Games[0]);

            Assert.IsNull(h.Query.Unlocked, "locked rows show for one friend and one game");
            CollectionAssert.AreEqual(new[] { "Recent Only", "AAA Locked" }, h.Rows, "the configured sort, newest unlock first");
            CollectionAssert.AreEqual(
                new[] { "AAA Locked", "Recent Only" },
                h.Vm.SelectedFriendGameAllAchievements.Select(item => item.DisplayName).ToArray(),
                "the rollup source keeps definition order");

            h.Vm.ClearGameSelection();
            h.Settle();
            Assert.AreEqual(0, h.Vm.SelectedFriendGameAllAchievements.Count);
        }

        [TestMethod]
        public void UnlockStateToggles_FilterThePairByTheFriendsUnlockState()
        {
            var data = CreateData();
            var locked = Locked(data, "Alice Locked");
            var hiddenLocked = Locked(data, "Alice Hidden Locked");
            hiddenLocked.Hidden = true;
            data.AllAchievements = data.AllAchievements.Concat(new[] { locked, hiddenLocked }).ToList();
            using var h = new Harness(data).Load();

            var toggles = h.Vm.AchievementsControlBar.Items.OfType<GridToggleFilter>().ToList();
            Assert.AreEqual(3, toggles.Count);
            Assert.IsFalse(toggles.Any(toggle => toggle.EffectiveIsVisible), "unlocked rows only: nothing to toggle");

            h.Select(friend: h.Friend("Alice"), game: data.Games[0]);
            Assert.IsTrue(toggles.All(toggle => toggle.EffectiveIsVisible), "the pair's totals have unlocked, locked and hidden locked rows");
            CollectionAssert.AreEquivalent(new[] { "Recent Only", "Alice Locked", "Alice Hidden Locked" }, h.Rows);

            Toggle(h, GridToggleFilterIcon.Locked).IsChecked = false;
            h.Settle();
            Assert.AreEqual(true, h.Query.Unlocked);
            CollectionAssert.AreEquivalent(new[] { "Recent Only" }, h.Rows);

            Toggle(h, GridToggleFilterIcon.Locked).IsChecked = true;
            Toggle(h, GridToggleFilterIcon.Unlocked).IsChecked = false;
            h.Settle();
            CollectionAssert.AreEquivalent(new[] { "Alice Locked", "Alice Hidden Locked" }, h.Rows);

            Toggle(h, GridToggleFilterIcon.Locked).IsChecked = false;
            h.Settle();
            Assert.IsTrue(h.Query.MatchesNothing, "both off lists nothing");

            Toggle(h, GridToggleFilterIcon.Locked).IsChecked = true;
            Toggle(h, GridToggleFilterIcon.Unlocked).IsChecked = true;
            Toggle(h, GridToggleFilterIcon.Hidden).IsChecked = false;
            h.Settle();
            Assert.IsTrue(h.Query.HideHiddenLocked);
            CollectionAssert.AreEquivalent(new[] { "Recent Only", "Alice Locked" }, h.Rows);
        }

        [TestMethod]
        public void UnlockStateTogglesSurviveLeavingThePair_AndFilterOnlyWhereOffered()
        {
            var data = CreateData();
            data.AllAchievements = data.AllAchievements.Concat(new[] { Locked(data, "Alice Locked") }).ToList();
            using var h = new Harness(data);
            var saved = new List<string>();
            h.Vm.PersistAchievementUnlockToggles(null, saved.Add);
            h.Load();

            h.Select(friend: h.Friend("Alice"), game: data.Games[0]);
            Toggle(h, GridToggleFilterIcon.Unlocked).IsChecked = false;
            Toggle(h, GridToggleFilterIcon.Hidden).IsChecked = false;
            h.Settle();
            CollectionAssert.AreEquivalent(new[] { "Alice Locked" }, h.Rows);

            // The friend-only view offers no toggles, so they do not filter it.
            h.Vm.ClearGameSelection();
            h.Settle();
            Assert.IsFalse(Toggle(h, GridToggleFilterIcon.Unlocked).IsChecked);
            Assert.IsFalse(Toggle(h, GridToggleFilterIcon.Hidden).IsChecked);
            CollectionAssert.AreEquivalent(new[] { "Recent Only", "Alice Game Two" }, h.Rows);
            Assert.AreEqual(2, saved.Count);

            using var restored = new Harness(data);
            restored.Vm.PersistAchievementUnlockToggles(saved[1], null);
            Assert.IsFalse(Toggle(restored, GridToggleFilterIcon.Unlocked).IsChecked);
            Assert.IsTrue(Toggle(restored, GridToggleFilterIcon.Locked).IsChecked);
        }

        [TestMethod]
        public void TypeAndCategoryOptions_ForASelectedGame_ComeFromItsTotals()
        {
            var data = CreateData();
            var locked = Locked(data, "Alice Locked", "Base|Missable", "Locked Only");
            data.AllAchievements = data.AllAchievements.Concat(new[] { locked }).ToList();
            using var h = new Harness(data).Load();
            var typeFilter = GetTypeFilter(h.Vm);
            var categoryFilter = GetCategoryFilter(h.Vm);

            Assert.AreEqual(0, typeFilter.Options.Count(), "no game, no game vocabulary");
            Assert.AreEqual(0, categoryFilter.Options.Count());

            h.Select(friend: h.Friend("Alice"), game: data.Games[0]);
            var optionReads = h.Reads.TotalsReads.Where(read => read.Value == AchievementGroup.Category || read.Value == AchievementGroup.Tag).ToList();
            Assert.AreEqual(2, optionReads.Count, "one category and one tag totals read");
            Assert.IsTrue(optionReads.All(read => read.Key.Unlocked == null && read.Key.FriendAccounts.Single().Key == "steam|alice"));
            CollectionAssert.AreEqual(new[] { "Missable" }, typeFilter.Options.ToArray());
            CollectionAssert.AreEqual(new[] { "Side", "Locked Only" }, categoryFilter.Options.ToArray(), "definition order, not by name");

            typeFilter.SetSelected("Missable", true);
            h.Settle();
            CollectionAssert.AreEqual(new[] { "missable" }, h.Query.ClassificationKeys.Select(key => key.ToLowerInvariant()).ToArray());
            CollectionAssert.AreEqual(new[] { "Alice Locked" }, h.Rows);

            h.Vm.ClearGameSelection();
            h.Settle();
            Assert.AreEqual(0, typeFilter.Options.Count());
            Assert.IsFalse(typeFilter.IsSelected("Missable"));
        }

        [TestMethod]
        public void ProviderSearchTypeAndCategoryFiltersCompose()
        {
            var data = CreateData();
            using var h = new Harness(data).Load();

            h.Vm.SetFriendProviderFilterSelected("Steam", true);
            h.Vm.FriendSearchText = "ali";
            h.Select(game: data.Games[0]);
            GetTypeFilter(h.Vm).SetSelected("Base", true);
            GetCategoryFilter(h.Vm).SetSelected("Main", true);
            h.Settle();

            CollectionAssert.AreEqual(new[] { "Main" }, h.Query.CategoryLabels.ToArray());
            CollectionAssert.AreEqual(new[] { "Bob Game One" }, h.Rows);
            CollectionAssert.AreEqual(new[] { "Alice" }, h.Vm.FilteredFriends.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void AchievementSearch_RunsInTheQuery()
        {
            using var h = new Harness(CreateData()).Load();

            h.Vm.AchievementSearchText = "b";
            h.Vm.AchievementSearchText = "bob";
            h.Settle();
            Assert.IsNull(h.Query.SearchText, "typing waits for a pause");

            var keyReads = h.Reads.KeyReads.Count;
            h.Scheduler.Elapse();
            h.Settle();

            Assert.AreEqual("bob", h.Query.SearchText);
            Assert.AreEqual(keyReads + 1, h.Reads.KeyReads.Count, "one read for the last text");
            CollectionAssert.AreEqual(new[] { "Bob Game One" }, h.Rows);

            h.Vm.ClearAchievementSearch();
            h.Scheduler.Elapse();
            h.Settle();
            Assert.IsNull(h.Query.SearchText);
            Assert.AreEqual(4, h.Rows.Length);
        }

        [TestMethod]
        public void FriendSortAndSearch_FollowTheShownNickname()
        {
            using var h = new Harness(CreateData(), settings =>
            {
                settings.FriendNameDisplayMode = FriendNameDisplayMode.Nickname;
                settings.AddOrUpdateFriend(new FriendIdentity
                {
                    ProviderKey = "Steam",
                    ExternalUserId = "bob",
                    DisplayName = "Bob",
                    ProviderNickname = "Zed"
                });
            }).Load();

            h.Vm.SortAchievements("FriendName", ListSortDirection.Ascending);
            h.Settle();
            Assert.AreEqual("Zed", h.Query.FriendNames.Single(name => name.Key.ExternalUserId == "bob").Value);
            Assert.AreEqual("Bob Game One", h.Rows.Last(), "Zed sorts after Cora");
            Assert.AreEqual("Zed", h.Vm.DisplayedAchievements.Last().FriendName);

            h.Vm.AchievementSearchText = "zed";
            h.Scheduler.Elapse();
            h.Settle();
            CollectionAssert.AreEqual(new[] { "Bob Game One" }, h.Rows);
        }

        [TestMethod]
        public void DuringAFriendRefresh_FeedChangesRereadAfterALongerWindow()
        {
            using var h = new Harness(CreateData()).Load();
            var change = FriendCacheChange.ForFriendGameAchievements("Steam", "alice", 20, null);

            h.Reads.Feed.RaiseFriendChange(change);
            h.Settle();
            Assert.IsTrue(h.Scheduler.Delays.Count > 0 && h.Scheduler.Delays.All(delay => delay < TimeSpan.FromSeconds(1)));
            h.Scheduler.Elapse();
            h.Settle();
            h.Scheduler.Delays.Clear();

            h.Vm.IsRefreshing = true;
            h.Reads.Feed.RaiseFriendChange(change);
            h.Settle();
            Assert.IsTrue(h.Scheduler.Delays.Count > 0 && h.Scheduler.Delays.All(delay => delay == TimeSpan.FromSeconds(3)));
        }

        [TestMethod]
        public void WhileInactive_FeedChangesReadNothing_UntilShownAgain()
        {
            using var h = new Harness(CreateData()).Load();
            var change = FriendCacheChange.ForFriendGameAchievements("Steam", "alice", 20, null);

            h.Vm.SetActive(false);
            h.Reads.Feed.RaiseFriendChange(change);
            h.Settle();
            Assert.AreEqual(0, h.Scheduler.PendingTimers);
            Assert.AreEqual(0, h.Scheduler.PendingReads);

            h.Vm.SetActive(true);
            Assert.IsTrue(h.Scheduler.PendingReads > 0, "showing it again reads what changed");
            h.Settle();
        }

        [TestMethod]
        public void ColumnSorts_SortThroughTheQuery()
        {
            using var h = new Harness(CreateData()).Load();

            h.Vm.SortAchievements("FriendName", ListSortDirection.Descending);
            h.Settle();
            Assert.AreEqual(AchievementSort.Friend, h.Query.Sort);
            Assert.IsTrue(h.Query.Descending);
            CollectionAssert.AreEqual(new[] { "Cora GOG", "Bob Game One" }, h.Rows.Take(2).ToArray());
            Assert.AreEqual("FriendName", h.Vm.AchievementSortPath);

            h.Vm.SortAchievements("RaritySortValue", ListSortDirection.Ascending);
            h.Settle();
            Assert.AreEqual(AchievementSort.Rarity, h.Query.Sort);
            Assert.IsFalse(h.Query.Descending);

            // A column with no query order leaves the sort alone.
            h.Vm.SortAchievements("NotAColumn", ListSortDirection.Ascending);
            h.Settle();
            Assert.AreEqual(AchievementSort.Rarity, h.Query.Sort);

            h.Vm.ApplyDefaultAchievementSort();
            h.Settle();
            Assert.AreEqual(AchievementSort.UnlockTime, h.Query.Sort);
            Assert.IsTrue(h.Query.Descending);
        }

        [TestMethod]
        public void EverySortableAchievementColumn_HasAQueryOrder()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "Controls", "AchievementDataGridControl.xaml"));
            var paths = Regex.Matches(xaml, "SortMemberPath=\"([^\"]+)\"").Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .ToList();

            Assert.IsTrue(paths.Count >= 10);
            foreach (var path in paths)
            {
                Assert.IsNotNull(AchievementQuery.SortOfColumn(path), path);
            }
        }

        [TestMethod]
        public void SelectedFriendGameHeader_UsesTheFriendsUnlockFraction_NotTheVisibleRows()
        {
            var data = CreateData();
            using var h = new Harness(data).Load();

            h.Select(friend: h.Friend("Alice"), game: data.Games[1]);
            h.Vm.AchievementSearchText = "does not match";
            h.Scheduler.Elapse();
            h.Settle();

            Assert.AreEqual(0, h.Vm.DisplayedAchievements.Count);
            StringAssert.Contains(h.Vm.AchievementCountText, "1/2");
        }

        [TestMethod]
        public void OwnedGamesShowWithoutUnlocks_GamesWithoutPairDataClearFriendLists()
        {
            var data = CreateData();
            var ownedOnlyGameId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            data.Games.Add(new FriendGameSummaryItem
            {
                ProviderKey = "Steam",
                AppId = 40,
                PlayniteGameId = ownedOnlyGameId,
                GameName = "Owned Only",
                FriendCount = 1,
                TotalAchievements = 12
            });
            data.FriendGameLinks.Add(new FriendGameLinkItem
            {
                ProviderKey = "Steam",
                ExternalUserId = "alice",
                AppId = 40,
                PlayniteGameId = ownedOnlyGameId,
                PlaytimeForeverMinutes = 45
            });
            var noPairDataGame = new FriendGameSummaryItem { ProviderKey = "Steam", AppId = 50, GameName = "No Pair Data", TotalAchievements = 3 };
            data.Games.Add(noPairDataGame);
            using var h = new Harness(data).Load();

            Assert.IsTrue(h.Vm.FilteredGames.Any(item => item.GameName == "Owned Only"));

            h.Select(friend: h.Friend("Alice"));
            var ownedRow = h.Vm.FilteredGames.Single(item => item.GameName == "Owned Only");
            Assert.AreEqual(0, ownedRow.UnlockedAchievements);
            Assert.AreEqual(12, ownedRow.TotalAchievements);
            Assert.AreEqual(45, ownedRow.TotalFriendPlaytimeMinutes);
            Assert.IsFalse(h.Vm.FilteredGames.Any(item => item.GameName == "No Pair Data"));

            h.Select(game: ownedRow);
            Assert.IsNotNull(h.Vm.SelectedGame);
            Assert.AreEqual(0, h.Vm.DisplayedAchievements.Count);

            h.Vm.ClearFriendSelection();
            h.Select(game: noPairDataGame);
            Assert.AreEqual(0, h.Vm.FilteredFriends.Count);
            Assert.AreEqual(0, h.Vm.DisplayedAchievements.Count);
        }

        [TestMethod]
        public void FavoritesOnly_ScopesFriendsGamesAndTheRecentFeedToFavorites()
        {
            using var h = new Harness(CreateData(), settings =>
            {
                settings.AddOrUpdateFriend("Steam", "bob", "Bob", null, null, FriendSettingsSource.Manual);
                settings.SetFriendFavorite("Steam", "bob", true);
            }).Load();

            var items = h.Vm.FriendSummariesControlBar.Items;
            var toggle = items.OfType<GridToggleFilter>().Single(item => item.Icon == GridToggleFilterIcon.Favorite);
            var platformFilter = items.OfType<GridMultiSelectFilter>().Single();
            Assert.IsFalse(toggle.IsChecked, "favorites only starts off");
            Assert.IsTrue(items.IndexOf(toggle) < items.IndexOf(platformFilter), "the star sits left of the platform dropdown");

            toggle.IsChecked = true;
            h.Settle();

            CollectionAssert.AreEqual(new[] { "Bob" }, h.Vm.FilteredFriends.Select(friend => friend.DisplayName).ToArray());
            Assert.AreEqual("steam|bob", h.Query.FriendAccounts.Single().Key);
            Assert.IsTrue(h.Query.RequireUnlockTime, "still the recent feed");
            CollectionAssert.AreEqual(new[] { "Bob Game One" }, h.Rows);

            // Bob has data for Game One only, its columns counted over him alone from a read of
            // the favorites' unique unlocks.
            var gamesRead = h.Reads.TotalsReads.Last(read => read.Value == AchievementGroup.ProviderGame).Key;
            Assert.IsTrue(gamesRead.DistinctAchievements);
            var game = h.Vm.FilteredGames.Single();
            Assert.AreEqual("Game One", game.GameName);
            Assert.AreEqual(1, game.FriendCount);
            Assert.AreEqual(1, game.FriendUnlockedAchievementsCount);
            Assert.AreEqual(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), game.LastFriendUnlockUtc);
            Assert.AreEqual(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), game.LastFriendPlayedUtc);
            Assert.AreEqual(300, game.TotalFriendPlaytimeMinutes);

            toggle.IsChecked = false;
            h.Settle();
            Assert.AreEqual(3, h.Vm.FilteredFriends.Count);
            Assert.AreEqual(3, h.Vm.FilteredGames.Count);
            Assert.AreEqual(4, h.Rows.Length);
        }

        [TestMethod]
        public void FavoritesOnly_DropsASelectedFriendWhoIsNotAFavorite()
        {
            using var h = new Harness(CreateData(), settings =>
            {
                settings.AddOrUpdateFriend("Steam", "bob", "Bob", null, null, FriendSettingsSource.Manual);
                settings.SetFriendFavorite("Steam", "bob", true);
            }).Load();
            h.Select(friend: h.Friend("Alice"));
            Assert.AreEqual(2, h.Vm.FilteredGames.Count, "Alice's own games");

            h.Vm.FavoritesOnly = true;
            h.Settle();

            Assert.IsNull(h.Vm.SelectedFriend);
            CollectionAssert.AreEqual(new[] { "Bob" }, h.Vm.FilteredFriends.Select(friend => friend.DisplayName).ToArray());
            CollectionAssert.AreEqual(new[] { "Game One" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void FriendProviderFilterMatchesMergedMembership()
        {
            var data = CreateData();
            data.Friends.Add(new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "dana-steam", DisplayName = "Dana" });
            data.Friends.Add(new FriendSummaryItem { ProviderKey = "RetroAchievements", ExternalUserId = "dana-ra", DisplayName = "Dana" });
            using var h = new Harness(data, settings => settings.AddOrUpdateFriendMergeGroup(
                new[] { FriendAccountRef.From("Steam", "dana-steam"), FriendAccountRef.From("RetroAchievements", "dana-ra") },
                nickname: "Dana")).Load();

            CollectionAssert.AreEquivalent(new[] { "GOG", "RetroAchievements", "Steam" }, h.Vm.FriendProviderFilterOptions.ToArray());

            h.Vm.SetFriendProviderFilterSelected("RetroAchievements", true);
            CollectionAssert.AreEqual(new[] { "Dana" }, h.Vm.FilteredFriends.Select(item => item.DisplayName).ToArray());

            h.Vm.SetFriendProviderFilterSelected("GOG", true);
            CollectionAssert.AreEquivalent(new[] { "Cora", "Dana" }, h.Vm.FilteredFriends.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void MergedFriendPair_ReadsEveryMemberAccount_AndShowsTheMergedName()
        {
            var data = CreateData();
            var gameOneId = data.Games[0].PlayniteGameId.Value;
            data.Friends.Add(new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "dana-steam", DisplayName = "Dana Steam" });
            data.Friends.Add(new FriendSummaryItem { ProviderKey = "RetroAchievements", ExternalUserId = "dana-ra", DisplayName = "Dana RA" });
            data.FriendGameLinks.Add(new FriendGameLinkItem { ProviderKey = "Steam", ExternalUserId = "dana-steam", AppId = 10, PlayniteGameId = gameOneId });
            var unlocked = CreateAchievement("Steam", "dana-steam", "Dana Steam", 10, gameOneId, "Game One", "Dana Unlocked", "Story", "Main", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
            var locked = CreateAchievement("Steam", "dana-steam", "Dana Steam", 10, gameOneId, "Game One", "Dana Locked", "Story", "Main", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
            locked.Unlocked = false;
            locked.UnlockTimeUtc = null;
            data.AllAchievements = data.AllAchievements.Concat(new[] { unlocked, locked }).ToList();
            using var h = new Harness(data, settings => settings.AddOrUpdateFriendMergeGroup(
                new[] { FriendAccountRef.From("Steam", "dana-steam"), FriendAccountRef.From("RetroAchievements", "dana-ra") },
                nickname: "Dana")).Load();

            h.Select(friend: h.Friend("Dana"), game: data.Games[0]);

            CollectionAssert.AreEquivalent(new[] { "steam|dana-steam", "retroachievements|dana-ra" }, h.Query.FriendAccounts.Select(account => account.Key).ToArray());
            CollectionAssert.AreEquivalent(new[] { "Dana Unlocked", "Dana Locked" }, h.Rows);
            Assert.IsTrue(h.Vm.DisplayedAchievements.All(item => item.FriendName == "Dana"), "rows carry the merged name");
            Assert.AreEqual(2, h.Vm.SelectedFriendGameAllAchievements.Count);
        }

        [TestMethod]
        public void OwnershipFilterOffersOptionsOnlyWhenOwnedAndUnownedMix()
        {
            using var allOwned = new Harness(CreateData()).Load();
            Assert.AreEqual(0, allOwned.Vm.OwnershipFilterOptions.Count);

            var mixedData = CreateData();
            mixedData.Games.Add(new FriendGameSummaryItem { ProviderKey = "Steam", AppId = 999, GameName = "Provider Only" });
            using var mixed = new Harness(mixedData).Load();
            Assert.AreEqual(2, mixed.Vm.OwnershipFilterOptions.Count);
        }

        [TestMethod]
        public void OwnershipFilterSelectsOwnedOrUnownedGames()
        {
            var data = CreateData();
            data.Games.Add(new FriendGameSummaryItem { ProviderKey = "Steam", AppId = 999, GameName = "Provider Only" });
            using var h = new Harness(data).Load();

            var unownedLabel = ResourceProvider.GetString("LOCPlayAch_Filter_Unowned");
            h.Vm.SetOwnershipFilterSelected(unownedLabel, true);
            CollectionAssert.AreEqual(new[] { "Provider Only" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());

            h.Vm.SetOwnershipFilterSelected(unownedLabel, false);
            h.Vm.SetOwnershipFilterSelected(ResourceProvider.GetString("LOCPlayAch_Column_Owned"), true);
            CollectionAssert.AreEquivalent(new[] { "Game One", "Game Two", "GOG Game" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void GameFilterOptionsRescopeToSelectedFriend()
        {
            var data = CreateData();
            data.Games.Add(new FriendGameSummaryItem { ProviderKey = "Steam", AppId = 999, GameName = "Provider Only" });
            using var h = new Harness(data).Load();
            Assert.AreEqual(2, h.Vm.OwnershipFilterOptions.Count);

            h.Select(friend: h.Friend("Alice"));
            Assert.AreEqual(0, h.Vm.OwnershipFilterOptions.Count);
            Assert.IsTrue(h.Vm.GamePlatformFilterGroups.All(group => group.ProviderKey == "Steam"));

            h.Vm.SelectedFriend = null;
            h.Settle();
            Assert.AreEqual(2, h.Vm.OwnershipFilterOptions.Count);
        }

        [TestMethod]
        public void GamePlatformFilterComposesFullAndPartialSelections()
        {
            var data = CreateData();
            data.Games[0].Platforms = new[] { "PC" };
            data.Games[1].Platforms = new[] { "Steam Deck" };
            data.Games.Add(new FriendGameSummaryItem { ProviderKey = "Steam", AppId = 999, GameName = "Provider Only" });
            using var h = new Harness(data).Load();

            var gogGroup = h.Vm.GamePlatformFilterGroups.Single(group => group.ProviderKey == "GOG");
            gogGroup.SetAll(true);
            CollectionAssert.AreEqual(new[] { "GOG Game" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
            gogGroup.SetAll(false);

            var steamGroup = h.Vm.GamePlatformFilterGroups.Single(group => group.ProviderKey == "Steam");
            steamGroup.Platforms.Single(option => option.PlatformName == "PC").IsSelected = true;
            CollectionAssert.AreEqual(new[] { "Game One" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());

            steamGroup.SetAll(true);
            CollectionAssert.AreEquivalent(new[] { "Game One", "Game Two", "Provider Only" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void GamePlatformFilterGroupsByDisplayProvider()
        {
            var data = CreateData();
            data.Games.Add(new FriendGameSummaryItem { ProviderKey = "Exophase", DisplayProviderKey = "EA", Provider = "EA app", AppId = 555, GameName = "EA Game" });
            using var h = new Harness(data).Load();

            var eaGroup = h.Vm.GamePlatformFilterGroups.Single(group => group.ProviderKey == "EA");
            Assert.AreEqual("EA app", eaGroup.DisplayName);
            Assert.IsNull(h.Vm.GamePlatformFilterGroups.FirstOrDefault(group => group.ProviderKey == "Exophase"));

            eaGroup.SetAll(true);
            CollectionAssert.AreEqual(new[] { "EA Game" }, h.Vm.FilteredGames.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void LoadAsync_ScoresFriendsFromTheirUnlockedTotals()
        {
            using var h = new Harness(CreateData()).Load();

            var alice = h.Vm.FilteredFriends.Single(item => item.ExternalUserId == "alice");
            StringAssert.Contains(alice.ProviderDisplayName, "Steam");
            Assert.AreEqual("https://cdn.example/alice.png", alice.AvatarPath);
            Assert.AreEqual(2, alice.GamesWithUnlocksCount);
            Assert.AreEqual(new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), alice.LastUnlockUtc);
            Assert.AreEqual(900, alice.TotalPlaytimeMinutes);

            // Two Common unlocks, as the friend and game totals read them.
            var common = PlayniteAchievements.Models.Achievements.Scoring.AchievementScoreCalculator.GetCollectionValue(
                PlayniteAchievements.Models.Achievements.RarityTier.Common);
            Assert.AreEqual(2 * common, alice.CollectionScore);
            Assert.AreEqual(2, alice.CommonCount);
            var totalsRead = h.Reads.TotalsReads.Single(read => read.Value == AchievementGroup.FriendGame).Key;
            Assert.AreEqual(true, totalsRead.Unlocked);
            Assert.IsNull(totalsRead.FriendAccounts, "one read for every friend");

            var gameOne = h.Vm.FilteredGames.Single(item => item.AppId == 10);
            Assert.AreEqual(2, gameOne.FriendCount);
            Assert.AreEqual(2, gameOne.UniqueFriendUnlockedAchievementsCount);
            Assert.AreEqual(1200, gameOne.TotalFriendPlaytimeMinutes);
            Assert.AreEqual("OK", gameOne.LastFriendScrapeStatusDisplay);
        }

        [TestMethod]
        public void LoadAsync_ExposesFriendAchievementIdentityFields()
        {
            using var h = new Harness(CreateData()).Load();

            var recent = h.Vm.DisplayedAchievements.First();
            Assert.AreEqual("Alice", recent.FriendName);
            Assert.AreEqual("https://cdn.example/alice.png", recent.FriendAvatarPath);
            Assert.AreEqual(new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), recent.FriendUnlockTimeUtc);
            Assert.IsTrue(recent.ShowUnlockDate);
            Assert.IsFalse(recent.IsPlaceholder);
        }

        [TestMethod]
        public void ClearingSelectedRowsRestoresBroaderState()
        {
            var data = CreateData();
            using var h = new Harness(data).Load();
            h.Select(friend: h.Friend("Alice"), game: data.Games[1]);

            h.Vm.ClearGameSelection();
            h.Vm.ClearFriendSelection();
            h.Settle();

            Assert.AreEqual(3, h.Vm.FilteredFriends.Count);
            Assert.AreEqual(3, h.Vm.FilteredGames.Count);
            Assert.AreSame(data.Games[0], h.Vm.FilteredGames.Single(item => item.AppId == 10));
            Assert.AreEqual(4, h.Rows.Length);
            Assert.IsTrue(h.Query.RequireUnlockTime);
        }

        [TestMethod]
        public void CompareWithAFriend_ReadsTheirStateInTheQuery_AndNamesIt()
        {
            var data = CreateData();
            data.FriendGameLinks[2].OwnershipConfirmed = true;
            using var h = new Harness(data).Load();
            h.Select(game: data.Games[0]);

            CompareFilter(h.Vm).SetSelected("steam|bob", true);
            h.Settle();

            Assert.AreEqual("steam|bob", h.Query.CompareAccounts.Single().Key);
            var alice = h.Vm.DisplayedAchievements.Single(item => item.FriendExternalUserId == "alice");
            Assert.IsTrue(alice.HasComparison);
            Assert.AreEqual("Bob", alice.ComparisonFriendName);
            Assert.IsTrue(alice.ComparisonApplicable, "Bob has the game");
            Assert.IsFalse(alice.ComparisonUnlocked, "but not this achievement");
            Assert.AreEqual("Alice", alice.ComparisonOwnerName);
            var bob = h.Vm.DisplayedAchievements.Single(item => item.FriendExternalUserId == "bob");
            Assert.IsFalse(bob.HasComparison, "a friend is not compared with themself");

            CompareFilter(h.Vm).SetSelected("steam|bob", false);
            h.Settle();
            Assert.IsNull(h.Query.CompareAccounts);
            Assert.IsTrue(h.Vm.DisplayedAchievements.All(item => !item.HasComparison));
        }

        [TestMethod]
        public void CompareWithSelf_UsesTheRowsOwnState()
        {
            var data = CreateData();
            data.AllAchievements[0].UnlockedBySelf = true;
            data.AllAchievements[0].SelfHasGameData = true;
            using var h = new Harness(data).Load();
            h.Select(game: data.Games[0]);

            CompareFilter(h.Vm).SetSelected(FriendVsFriendCompareController.SelfOptionKey, true);
            h.Settle();

            Assert.IsNull(h.Query.CompareAccounts, "the rows carry the user's own state");
            var alice = h.Vm.DisplayedAchievements.Single(item => item.FriendExternalUserId == "alice");
            Assert.IsTrue(alice.HasComparison);
            Assert.IsTrue(alice.ComparisonUnlocked);
            var bob = h.Vm.DisplayedAchievements.Single(item => item.FriendExternalUserId == "bob");
            Assert.IsFalse(bob.ComparisonApplicable, "no progress of the user's own for it");
        }

        [TestMethod]
        public void SpoilerAndNameDisplaySettings_RereadTheRows()
        {
            using var h = new Harness(CreateData()).Load();
            var rowReads = h.Reads.RowReads.Count;
            var listLoads = h.Reads.ListLoads;

            h.Settings.Persisted.ShowFriendSpoilers = !h.Settings.Persisted.ShowFriendSpoilers;
            h.Settle();

            Assert.IsTrue(h.Reads.RowReads.Count > rowReads);
            Assert.AreEqual(listLoads + 1, h.Reads.ListLoads);
        }

        [TestMethod]
        public void SettingsSaves_RereadOnlyForTheSettingsWindowOrChangedFriendEntries()
        {
            using var h = new Harness(CreateData()).Load();
            var listLoads = h.Reads.ListLoads;

            // View state: a Showcase page, a window position.
            PlayniteAchievementsPlugin.NotifySettingsSaved();
            h.Settle();
            Assert.AreEqual(listLoads, h.Reads.ListLoads, "a view-state save re-reads nothing");

            h.Settings.Persisted.AddOrUpdateFriend("Steam", "alice", null, null, null, FriendSettingsSource.AutoDiscovered).IsFavorite = true;
            PlayniteAchievementsPlugin.NotifySettingsSaved();
            h.Settle();
            Assert.AreEqual(listLoads + 1, h.Reads.ListLoads, "a changed friend entry re-reads the lists");

            PlayniteAchievementsPlugin.NotifySettingsSaved();
            h.Settle();
            Assert.AreEqual(listLoads + 1, h.Reads.ListLoads, "the same entries do not re-read again");

            PlayniteAchievementsPlugin.NotifySettingsSaved(settingsWindow: true);
            h.Settle();
            Assert.AreEqual(listLoads + 2, h.Reads.ListLoads, "a settings window save re-reads");
        }

        [TestMethod]
        public void FeedChanges_RereadTheQueriesTheyTouch_AtTheEndOfTheWindow()
        {
            var data = CreateData();
            using var h = new Harness(data).Load();
            h.Select(friend: h.Friend("Bob"));
            var rowReads = h.Reads.RowReads.Count;
            var listLoads = h.Reads.ListLoads;

            // Alice's game: not Bob's rows, though the lists span every friend.
            h.Reads.Feed.RaiseFriendChange(FriendCacheChange.ForFriendGameAchievements("Steam", "alice", 20, null));
            h.Settle();
            h.Scheduler.Elapse();
            h.Settle();
            Assert.AreEqual(rowReads, h.Reads.RowReads.Count, "Bob's rows are untouched");
            Assert.AreEqual(listLoads + 1, h.Reads.ListLoads);

            data.AllAchievements.Add(CreateAchievement("Steam", "bob", "Bob", 10, data.Games[0].PlayniteGameId.Value, "Game One", "Bob Later", "Base", "Main", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            h.Reads.Feed.RaiseFriendChange(FriendCacheChange.ForFriendGameAchievements("Steam", "bob", 10, null));
            h.Settle();
            Assert.AreEqual(rowReads, h.Reads.RowReads.Count, "nothing reruns before the window ends");
            h.Scheduler.Elapse();
            h.Settle();
            CollectionAssert.Contains(h.Rows, "Bob Later");
        }

        [TestMethod]
        public void SelectedFriendRefreshMode_UsesSelectedShortLabel()
        {
            Assert.AreEqual(
                "LOCPlayAch_RefreshModeShort_Selected",
                RefreshModeType.FriendsSelectedGame.GetShortResourceKey());
        }

        [TestMethod]
        public void SelectedRefreshRequest_GameOnlyUsesSelectedGameMode()
        {
            var data = CreateData();

            var success = FriendsOverviewViewModel.TryBuildSelectedFriendRefreshRequest(
                data.Games[0],
                selectedFriend: null,
                selectedGame: null,
                out var request);

            Assert.IsTrue(success);
            Assert.AreEqual(RefreshModeType.FriendsSelectedGame, request.Mode);
            Assert.AreEqual(data.Games[0].PlayniteGameId, request.SingleGameId);
        }

        [TestMethod]
        public void SelectedRefreshRequest_FriendOnlyUsesCustomFullFriendScope()
        {
            var data = CreateData();

            var success = FriendsOverviewViewModel.TryBuildSelectedFriendRefreshRequest(
                data.Friends[0],
                selectedFriend: null,
                selectedGame: data.Games[0],
                out var request);

            Assert.IsTrue(success);
            Assert.AreEqual(RefreshModeType.FriendsCustom, request.Mode);
            Assert.AreEqual(RefreshSubjects.Friends, request.Options.Subjects);
            Assert.AreEqual(RefreshGameScope.All, request.Options.Scope);
            CollectionAssert.AreEqual(
                new[] { "Steam" },
                request.Options.ProviderKeys.ToList());
            CollectionAssert.AreEqual(
                new[] { "alice" },
                request.Options.FriendExternalUserIds.ToList());
            Assert.AreEqual(1, request.Options.FriendAccounts.Count);
            Assert.IsTrue(request.Options.FriendAccounts.Single().Matches("Steam", "alice"));
            Assert.IsNull(request.Options.PlayniteGameIds);
            Assert.IsNull(request.Options.ProviderAppIds);
        }

        [TestMethod]
        public void SelectedRefreshRequest_MergedFriendUsesAllMemberAccounts()
        {
            var mergedFriend = new FriendSummaryItem
            {
                ProviderKey = FriendOverviewProjection.MergedProviderKey,
                ExternalUserId = "merged-alice",
                MergedFriendId = "merged-alice",
                DisplayName = "Alice Unified",
                MemberAccounts = new List<FriendAccountRef>
                {
                    FriendAccountRef.From("Steam", "alice"),
                    FriendAccountRef.From("Exophase", "exo-alice")
                },
                MemberProviderKeys = new List<string> { "Steam", "Exophase" }
            };

            var success = FriendsOverviewViewModel.TryBuildSelectedFriendRefreshRequest(
                mergedFriend,
                selectedFriend: null,
                selectedGame: null,
                out var request);

            Assert.IsTrue(success);
            Assert.AreEqual(RefreshModeType.FriendsCustom, request.Mode);
            CollectionAssert.AreEquivalent(
                new[] { "Steam", "Exophase" },
                request.Options.ProviderKeys.ToList());
            CollectionAssert.AreEquivalent(
                new[] { "alice", "exo-alice" },
                request.Options.FriendExternalUserIds.ToList());
            var accounts = request.Options.FriendAccounts.ToList();
            Assert.AreEqual(2, accounts.Count);
            Assert.IsTrue(accounts.Any(account => account.Matches("Steam", "alice")));
            Assert.IsTrue(accounts.Any(account => account.Matches("Exophase", "exo-alice")));
        }

        [TestMethod]
        public void SelectedRefreshRequest_FriendAndGameUsesCustomPairScope()
        {
            var data = CreateData();

            var success = FriendsOverviewViewModel.TryBuildSelectedFriendRefreshRequest(
                parameter: null,
                selectedFriend: data.Friends[0],
                selectedGame: data.Games[1],
                out var request);

            Assert.IsTrue(success);
            Assert.AreEqual(RefreshModeType.FriendsCustom, request.Mode);
            Assert.AreEqual(RefreshSubjects.Friends, request.Options.Subjects);
            Assert.AreEqual(RefreshGameScope.SelectedGame, request.Options.Scope);
            CollectionAssert.AreEqual(
                new[] { "Steam" },
                request.Options.ProviderKeys.ToList());
            CollectionAssert.AreEqual(
                new[] { "alice" },
                request.Options.FriendExternalUserIds.ToList());
            Assert.AreEqual(1, request.Options.FriendAccounts.Count);
            Assert.IsTrue(request.Options.FriendAccounts.Single().Matches("Steam", "alice"));
            CollectionAssert.AreEqual(
                new[] { data.Games[1].PlayniteGameId.Value },
                request.Options.PlayniteGameIds.ToList());
        }

        [TestMethod]
        public void SelectedRefreshRequest_FriendAndProviderOnlyGameUsesProviderAppScope()
        {
            var data = CreateData();
            var providerOnlyGame = new FriendGameSummaryItem
            {
                ProviderKey = "Steam",
                AppId = 999,
                GameName = "Provider Only"
            };

            var success = FriendsOverviewViewModel.TryBuildSelectedFriendRefreshRequest(
                parameter: null,
                selectedFriend: data.Friends[0],
                selectedGame: providerOnlyGame,
                out var request);

            Assert.IsTrue(success);
            Assert.AreEqual(RefreshModeType.FriendsCustom, request.Mode);
            Assert.AreEqual(RefreshSubjects.Friends, request.Options.Subjects);
            Assert.AreEqual(RefreshGameScope.SelectedGame, request.Options.Scope);
            CollectionAssert.AreEqual(
                new[] { "alice" },
                request.Options.FriendExternalUserIds.ToList());
            Assert.AreEqual(1, request.Options.FriendAccounts.Count);
            Assert.IsTrue(request.Options.FriendAccounts.Single().Matches("Steam", "alice"));
            CollectionAssert.AreEqual(
                new[] { 999 },
                request.Options.ProviderAppIds.ToList());
        }

        /// <summary>A view model over in-memory reads, run on a scheduler the test drains.</summary>
        private sealed class Harness : IDisposable
        {
            public Harness(FriendsOverviewData data, Action<PersistedSettings> configure = null)
            {
                Settings = new PlayniteAchievementsSettings();
                configure?.Invoke(Settings.Persisted);
                Reads = new InMemoryFriendsOverviewReads(data);
                Scheduler = new ManualReadScheduler();
                Vm = new FriendsOverviewViewModel(null, Reads, null, null, Settings, null, scheduler: Scheduler);
            }

            public PlayniteAchievementsSettings Settings { get; }

            public InMemoryFriendsOverviewReads Reads { get; }

            public ManualReadScheduler Scheduler { get; }

            public FriendsOverviewViewModel Vm { get; }

            /// <summary>The achievements grid's query as the view model set it.</summary>
            public AchievementQuery Query => Vm.DisplayedAchievements.Query;

            public string[] Rows => Vm.DisplayedAchievements.Select(item => item.DisplayName).ToArray();

            public Harness Load()
            {
                Vm.LoadAsync();
                Settle();
                return this;
            }

            /// <summary>Runs every queued read and apply; feed windows stay open.</summary>
            public void Settle() => Scheduler.Drain();

            public FriendSummaryItem Friend(string name) =>
                Vm.FilteredFriends.Single(friend => friend.DisplayName == name);

            public void Select(FriendSummaryItem friend = null, FriendGameSummaryItem game = null)
            {
                if (friend != null)
                {
                    Vm.SelectedFriend = friend;
                    Settle();
                }

                if (game != null)
                {
                    Vm.SelectedGame = game;
                    Settle();
                }
            }

            public void Dispose() => Vm.Dispose();
        }

        private static FriendAchievementDisplayItem Locked(
            FriendsOverviewData data,
            string name,
            string categoryType = "Story",
            string categoryLabel = "Main")
        {
            var locked = CreateAchievement(
                "Steam",
                "alice",
                "Alice",
                "https://cdn.example/alice.png",
                10,
                data.Games[0].PlayniteGameId.Value,
                "Game One",
                name,
                categoryType,
                categoryLabel,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            locked.Unlocked = false;
            locked.UnlockTimeUtc = null;
            return locked;
        }

        private static GridToggleFilter Toggle(Harness harness, GridToggleFilterIcon icon) =>
            harness.Vm.AchievementsControlBar.Items.OfType<GridToggleFilter>().Single(toggle => toggle.Icon == icon);

        private static GridMultiSelectFilter GetTypeFilter(FriendsOverviewViewModel viewModel) =>
            viewModel.AchievementsControlBar.Items.OfType<GridMultiSelectFilter>()
                .Single(filter => filter.IsCategoryFilter && !filter.RendersCategoryTree);

        private static GridMultiSelectFilter GetCategoryFilter(FriendsOverviewViewModel viewModel) =>
            viewModel.AchievementsControlBar.Items.OfType<GridMultiSelectFilter>()
                .Single(filter => filter.IsCategoryFilter && filter.RendersCategoryTree);

        private static GridMultiSelectFilter CompareFilter(FriendsOverviewViewModel viewModel) =>
            viewModel.AchievementsControlBar.Items.OfType<GridMultiSelectFilter>()
                .Single(filter => filter.Id == ShowcaseControlBarItems.Compare);

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Repository file not found: " + Path.Combine(parts));
            return null;
        }

        private static FriendsOverviewData CreateData()
        {
            var gameOneId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var gameTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var gogGameId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            var friends = new List<FriendSummaryItem>
            {
                new FriendSummaryItem
                {
                    ProviderKey = "Steam",
                    ExternalUserId = "alice",
                    DisplayName = "Alice",
                    AvatarPath = "https://cdn.example/alice.png",
                    SharedGamesCount = 2,
                    GamesWithUnlocksCount = 2,
                    UnlockedAchievementsCount = 2,
                    CollectionScore = 375,
                    PrestigeScore = 425,
                    RecentUnlockCount = 1,
                    LastUnlockUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                    LastRefreshedUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                    TotalPlaytimeMinutes = 900
                },
                new FriendSummaryItem
                {
                    ProviderKey = "Steam",
                    ExternalUserId = "bob",
                    DisplayName = "Bob",
                    AvatarPath = "https://cdn.example/bob.png",
                    SharedGamesCount = 1,
                    GamesWithUnlocksCount = 1,
                    UnlockedAchievementsCount = 1,
                    CollectionScore = 90,
                    PrestigeScore = 140,
                    RecentUnlockCount = 0,
                    LastUnlockUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    TotalPlaytimeMinutes = 300
                },
                new FriendSummaryItem
                {
                    ProviderKey = "GOG",
                    ExternalUserId = "cora",
                    DisplayName = "Cora",
                    SharedGamesCount = 1,
                    GamesWithUnlocksCount = 1,
                    UnlockedAchievementsCount = 1,
                    CollectionScore = 30,
                    PrestigeScore = 45,
                    RecentUnlockCount = 0,
                    LastUnlockUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    TotalPlaytimeMinutes = 120
                }
            };

            var games = new List<FriendGameSummaryItem>
            {
                new FriendGameSummaryItem
                {
                    ProviderKey = "Steam",
                    AppId = 10,
                    PlayniteGameId = gameOneId,
                    GameName = "Game One",
                    FriendCount = 2,
                    FriendsWithUnlocksCount = 2,
                    FriendUnlockedAchievementsCount = 2,
                    UniqueFriendUnlockedAchievementsCount = 2,
                    TotalAchievements = 4,
                    LastFriendUnlockUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                    TotalFriendPlaytimeMinutes = 1200,
                    AverageFriendPlaytimeMinutes = 600,
                    LastFriendPlayedUtc = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc),
                    LastFriendScrapeStatus = "OK"
                },
                new FriendGameSummaryItem
                {
                    ProviderKey = "Steam",
                    AppId = 20,
                    PlayniteGameId = gameTwoId,
                    GameName = "Game Two",
                    FriendCount = 1,
                    FriendsWithUnlocksCount = 1,
                    FriendUnlockedAchievementsCount = 1,
                    UniqueFriendUnlockedAchievementsCount = 1,
                    TotalAchievements = 2,
                    LastFriendUnlockUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)
                },
                new FriendGameSummaryItem
                {
                    ProviderKey = "GOG",
                    AppId = 30,
                    PlayniteGameId = gogGameId,
                    GameName = "GOG Game",
                    FriendCount = 1,
                    FriendsWithUnlocksCount = 1,
                    FriendUnlockedAchievementsCount = 1,
                    UniqueFriendUnlockedAchievementsCount = 1,
                    TotalAchievements = 1,
                    LastFriendUnlockUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            };

            var allUnlocked = new List<FriendAchievementDisplayItem>
            {
                // Category types use the canonical vocabulary (real rows are canonicalized at the
                // store boundary, and non-canonical tokens are dropped by ParseValues).
                CreateAchievement("Steam", "alice", "Alice", "https://cdn.example/alice.png", 10, gameOneId, "Game One", "Recent Only", "DLC", "Side", new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)),
                CreateAchievement("Steam", "alice", "Alice", "https://cdn.example/alice.png", 20, gameTwoId, "Game Two", "Alice Game Two", "Base", "Main", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
                CreateAchievement("Steam", "bob", "Bob", "https://cdn.example/bob.png", 10, gameOneId, "Game One", "Bob Game One", "Base", "Main", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateAchievement("GOG", "cora", "Cora", 30, gogGameId, "GOG Game", "Cora GOG", "Base", "Main", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            };

            return new FriendsOverviewData
            {
                Friends = friends,
                Games = games,
                AllAchievements = allUnlocked,
                FriendGameLinks = new List<FriendGameLinkItem>
                {
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 10,
                        PlayniteGameId = gameOneId,
                        PlaytimeForeverMinutes = 600,
                        LastPlayedUtc = new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 20,
                        PlayniteGameId = gameTwoId,
                        PlaytimeForeverMinutes = 300,
                        LastPlayedUtc = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "bob",
                        AppId = 10,
                        PlayniteGameId = gameOneId,
                        PlaytimeForeverMinutes = 300,
                        LastPlayedUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "GOG",
                        ExternalUserId = "cora",
                        AppId = 30,
                        PlayniteGameId = gogGameId,
                        PlaytimeForeverMinutes = 120,
                        LastPlayedUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
                    }
                }
            };
        }

        private static FriendAchievementDisplayItem CreateAchievement(
            string provider,
            string externalUserId,
            string friendName,
            string friendAvatarPath,
            int appId,
            Guid playniteGameId,
            string gameName,
            string achievementName,
            string categoryType,
            string categoryLabel,
            DateTime unlockTimeUtc)
        {
            return new FriendAchievementDisplayItem
            {
                ProviderKey = provider,
                FriendExternalUserId = externalUserId,
                FriendName = friendName,
                FriendAvatarPath = friendAvatarPath,
                AppId = appId,
                PlayniteGameId = playniteGameId,
                GameName = gameName,
                SortingName = gameName,
                DisplayName = achievementName,
                Description = achievementName + " description",
                CategoryKind = PlayniteAchievements.Services.Achievements.LegacyCategoryTypes.SplitKind(categoryType),
                Tags = PlayniteAchievements.Services.Achievements.AchievementTagHelper.Normalize(categoryType),
                CategoryLabel = categoryLabel,
                UnlockTimeUtc = unlockTimeUtc,
                Unlocked = true
            };
        }

        private static FriendAchievementDisplayItem CreateAchievement(
            string provider,
            string externalUserId,
            string friendName,
            int appId,
            Guid playniteGameId,
            string gameName,
            string achievementName,
            string categoryType,
            string categoryLabel,
            DateTime unlockTimeUtc) =>
            CreateAchievement(
                provider,
                externalUserId,
                friendName,
                null,
                appId,
                playniteGameId,
                gameName,
                achievementName,
                categoryType,
                categoryLabel,
                unlockTimeUtc);
    }
}
