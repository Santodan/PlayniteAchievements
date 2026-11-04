using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class GameSummariesSortHelperTests
    {
        [TestMethod]
        public void SortByConfiguredDefault_RecentUnlockDescending_PutsNewestUnlockFirst()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Gamma", lastUnlockUtc: Utc(2026, 4, 2), lastPlayed: Utc(2026, 4, 3)),
                CreateItem("Alpha", lastUnlockUtc: Utc(2026, 4, 5), lastPlayed: Utc(2026, 4, 1)),
                CreateItem("Beta", lastUnlockUtc: null, lastPlayed: Utc(2026, 4, 4))
            };

            GameSummariesSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    OverviewGameSummariesGridSortMode = GameSummariesSortMode.RecentUnlock,
                    OverviewGameSummariesGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Alpha", "Gamma", "Beta" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_RecentUnlockAscending_PutsOldestUnlockFirst()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Gamma", lastUnlockUtc: Utc(2026, 4, 2)),
                CreateItem("Alpha", lastUnlockUtc: Utc(2026, 4, 5)),
                CreateItem("Beta", lastUnlockUtc: null)
            };

            GameSummariesSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    OverviewGameSummariesGridSortMode = GameSummariesSortMode.RecentUnlock,
                    OverviewGameSummariesGridSortDescending = false
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_LastPlayed_UsesLastPlayedDate()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", lastPlayed: Utc(2026, 4, 1)),
                CreateItem("Beta", lastPlayed: Utc(2026, 4, 8)),
                CreateItem("Gamma", lastPlayed: Utc(2026, 4, 4))
            };

            GameSummariesSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    OverviewGameSummariesGridSortMode = GameSummariesSortMode.LastPlayed,
                    OverviewGameSummariesGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_TotalAchievements_UsesTotalAchievements()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", totalAchievements: 12, unlockedAchievements: 6),
                CreateItem("Beta", totalAchievements: 40, unlockedAchievements: 10),
                CreateItem("Gamma", totalAchievements: 24, unlockedAchievements: 12)
            };

            GameSummariesSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    OverviewGameSummariesGridSortMode = GameSummariesSortMode.TotalAchievements,
                    OverviewGameSummariesGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_Progress_UsesComputedProgression()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", totalAchievements: 10, unlockedAchievements: 3),
                CreateItem("Beta", totalAchievements: 10, unlockedAchievements: 9),
                CreateItem("Gamma", totalAchievements: 20, unlockedAchievements: 10)
            };

            GameSummariesSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    OverviewGameSummariesGridSortMode = GameSummariesSortMode.Progress,
                    OverviewGameSummariesGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_Alphabetical_UsesSortingName()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Zoo", sortingName: "Bravo"),
                CreateItem("Alpha", sortingName: "Charlie"),
                CreateItem("Beta", sortingName: "Alpha")
            };

            GameSummariesSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    OverviewGameSummariesGridSortMode = GameSummariesSortMode.Alphabetical,
                    OverviewGameSummariesGridSortDescending = false
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Zoo", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultOnDifferentColumn_CyclesAscendingDescendingThenReset()
        {
            var settings = new PersistedSettings
            {
                OverviewGameSummariesGridSortMode = GameSummariesSortMode.RecentUnlock,
                OverviewGameSummariesGridSortDescending = true
            };

            var first = GameSummariesSortHelper.ResolveGridSortAction(
                "SortingName",
                currentSortPath: null,
                currentSortDirection: null,
                settings);
            var second = GameSummariesSortHelper.ResolveGridSortAction(
                "SortingName",
                first.SortMemberPath,
                first.Direction,
                settings);
            var third = GameSummariesSortHelper.ResolveGridSortAction(
                "SortingName",
                second.SortMemberPath,
                second.Direction,
                settings);

            Assert.AreEqual(GameSummariesGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(GameSummariesGridSortActionKind.ApplySort, second.Kind);
            Assert.AreEqual(ListSortDirection.Descending, second.Direction);
            Assert.AreEqual(GameSummariesGridSortActionKind.ResetToDefault, third.Kind);
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultOnSameColumn_SkipsDefaultDirectionAndThenResets()
        {
            var settings = new PersistedSettings
            {
                OverviewGameSummariesGridSortMode = GameSummariesSortMode.LastPlayed,
                OverviewGameSummariesGridSortDescending = true
            };

            var first = GameSummariesSortHelper.ResolveGridSortAction(
                nameof(GameSummaryItem.LastPlayed),
                currentSortPath: null,
                currentSortDirection: null,
                settings);
            var second = GameSummariesSortHelper.ResolveGridSortAction(
                nameof(GameSummaryItem.LastPlayed),
                first.SortMemberPath,
                first.Direction,
                settings);

            Assert.AreEqual(GameSummariesGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(GameSummariesGridSortActionKind.ResetToDefault, second.Kind);
        }

        [TestMethod]
        public void ResolveSourceOrderedGridSortAction_CyclesAscendingDescendingThenResetsToSourceOrder()
        {
            var first = GameSummariesSortHelper.ResolveSourceOrderedGridSortAction(
                "SortingName",
                currentSortPath: null,
                currentSortDirection: null);
            var second = GameSummariesSortHelper.ResolveSourceOrderedGridSortAction(
                "SortingName",
                first.SortMemberPath,
                first.Direction);
            var third = GameSummariesSortHelper.ResolveSourceOrderedGridSortAction(
                "SortingName",
                second.SortMemberPath,
                second.Direction);

            Assert.AreEqual(GameSummariesGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(GameSummariesGridSortActionKind.ApplySort, second.Kind);
            Assert.AreEqual(ListSortDirection.Descending, second.Direction);
            Assert.AreEqual(GameSummariesGridSortActionKind.ResetToDefault, third.Kind);
        }

        [TestMethod]
        public void ResolveSourceOrderedGridSortAction_DifferentColumn_RestartsAtAscending()
        {
            var action = GameSummariesSortHelper.ResolveSourceOrderedGridSortAction(
                nameof(GameSummaryItem.Progression),
                currentSortPath: "SortingName",
                currentSortDirection: ListSortDirection.Descending);

            Assert.AreEqual(GameSummariesGridSortActionKind.ApplySort, action.Kind);
            Assert.AreEqual(nameof(GameSummaryItem.Progression), action.SortMemberPath);
            Assert.AreEqual(ListSortDirection.Ascending, action.Direction);
        }

        [TestMethod]
        public void ResolveSourceOrderedGridSortAction_MissingSortMemberPath_ReturnsNone()
        {
            var action = GameSummariesSortHelper.ResolveSourceOrderedGridSortAction(
                null,
                currentSortPath: "SortingName",
                currentSortDirection: ListSortDirection.Ascending);

            Assert.AreEqual(GameSummariesGridSortActionKind.None, action.Kind);
        }

        [TestMethod]
        public void ApplySortIndicator_RecentUnlockDefault_DoesNotReturnVisibleColumn()
        {
            string indicatorPath = "seed";
            ListSortDirection? indicatorDirection = ListSortDirection.Ascending;

            GameSummariesSortHelper.ApplySortIndicator(
                null,
                null,
                new PersistedSettings
                {
                    OverviewGameSummariesGridSortMode = GameSummariesSortMode.RecentUnlock,
                    OverviewGameSummariesGridSortDescending = true
                },
                (path, direction) =>
                {
                    indicatorPath = path;
                    indicatorDirection = direction;
                });

            Assert.IsNull(indicatorPath);
            Assert.IsNull(indicatorDirection);
        }

        [TestMethod]
        public void TrySortItems_CollectionScore_UsesEarnedScore()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", collectionScore: 120),
                CreateItem("Beta", collectionScore: 420),
                CreateItem("Gamma", collectionScore: 240)
            };
            string currentSortPath = null;
            var currentSortDirection = ListSortDirection.Ascending;

            var sorted = GameSummariesSortHelper.TrySortItems(
                items,
                nameof(GameSummaryItem.CollectionScore),
                ListSortDirection.Descending,
                ref currentSortPath,
                ref currentSortDirection);

            Assert.IsTrue(sorted);
            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void TrySortItems_PrestigeScore_UsesEarnedScore()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", prestigeScore: 900),
                CreateItem("Beta", prestigeScore: 50),
                CreateItem("Gamma", prestigeScore: 300)
            };
            string currentSortPath = null;
            var currentSortDirection = ListSortDirection.Descending;

            var sorted = GameSummariesSortHelper.TrySortItems(
                items,
                nameof(GameSummaryItem.PrestigeScore),
                ListSortDirection.Ascending,
                ref currentSortPath,
                ref currentSortDirection);

            Assert.IsTrue(sorted);
            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void TrySortItems_Points_UsesEarnedPoints()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", points: 120),
                CreateItem("Beta", points: 420),
                CreateItem("Gamma", points: 240)
            };
            string currentSortPath = null;
            var currentSortDirection = ListSortDirection.Ascending;

            var sorted = GameSummariesSortHelper.TrySortItems(
                items,
                nameof(GameSummaryItem.Points),
                ListSortDirection.Descending,
                ref currentSortPath,
                ref currentSortDirection);

            Assert.IsTrue(sorted);
            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void TrySortItems_Owned_UsesPlayniteGamePresence()
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Gamma", owned: false),
                CreateItem("Beta", owned: true),
                CreateItem("Alpha", owned: false)
            };
            string currentSortPath = null;
            var currentSortDirection = ListSortDirection.Ascending;

            var sorted = GameSummariesSortHelper.TrySortItems(
                items,
                nameof(GameSummaryItem.Owned),
                ListSortDirection.Descending,
                ref currentSortPath,
                ref currentSortDirection);

            Assert.IsTrue(sorted);
            CollectionAssert.AreEqual(
                new[] { "Beta", "Alpha", "Gamma" },
                items.Select(item => item.GameName).ToArray());
        }

        [DataTestMethod]
        [DataRow(ListSortDirection.Descending, new[] { "Beta", "Gamma", "Alpha" })]
        [DataRow(ListSortDirection.Ascending, new[] { "Alpha", "Gamma", "Beta" })]
        public void Sort_DateAdded_UsesAddedDateWithMissingDatesAsOldest(
            ListSortDirection direction,
            string[] expected)
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", added: null),
                CreateItem("Beta", added: Utc(2026, 4, 8)),
                CreateItem("Gamma", added: Utc(2026, 4, 4))
            };

            GameSummariesSortHelper.Sort(items, GameSummariesSortMode.DateAdded, direction);

            CollectionAssert.AreEqual(expected, items.Select(item => item.GameName).ToArray());
        }

        [DataTestMethod]
        [DataRow(ListSortDirection.Descending, new[] { "Gamma", "Alpha", "Beta" })]
        [DataRow(ListSortDirection.Ascending, new[] { "Beta", "Alpha", "Gamma" })]
        public void Sort_ReleaseDate_UsesReleaseDateWithMissingDatesAsOldest(
            ListSortDirection direction,
            string[] expected)
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", releaseDate: new DateTime(1998, 11, 21)),
                CreateItem("Beta", releaseDate: null),
                CreateItem("Gamma", releaseDate: new DateTime(2017, 3, 3))
            };

            GameSummariesSortHelper.Sort(items, GameSummariesSortMode.ReleaseDate, direction);

            CollectionAssert.AreEqual(expected, items.Select(item => item.GameName).ToArray());
        }

        [DataTestMethod]
        [DataRow(ListSortDirection.Descending, new[] { "Beta", "Gamma", "Alpha" })]
        [DataRow(ListSortDirection.Ascending, new[] { "Alpha", "Gamma", "Beta" })]
        public void Sort_Playtime_UsesPlaytimeSeconds(ListSortDirection direction, string[] expected)
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", playtimeSeconds: 0),
                CreateItem("Beta", playtimeSeconds: 90000),
                CreateItem("Gamma", playtimeSeconds: 3600)
            };

            GameSummariesSortHelper.Sort(items, GameSummariesSortMode.Playtime, direction);

            CollectionAssert.AreEqual(expected, items.Select(item => item.GameName).ToArray());
        }

        [DataTestMethod]
        [DataRow(ListSortDirection.Ascending, new[] { "Beta", "Gamma", "Alpha" })]
        [DataRow(ListSortDirection.Descending, new[] { "Alpha", "Gamma", "Beta" })]
        public void Sort_Remaining_UsesLockedCount(ListSortDirection direction, string[] expected)
        {
            var items = new List<GameSummaryItem>
            {
                CreateItem("Alpha", totalAchievements: 50, unlockedAchievements: 10),
                CreateItem("Beta", totalAchievements: 20, unlockedAchievements: 20),
                CreateItem("Gamma", totalAchievements: 100, unlockedAchievements: 97)
            };

            GameSummariesSortHelper.Sort(items, GameSummariesSortMode.Remaining, direction);

            CollectionAssert.AreEqual(expected, items.Select(item => item.GameName).ToArray());
        }

        private static GameSummaryItem CreateItem(
            string gameName,
            string sortingName = null,
            DateTime? lastUnlockUtc = null,
            DateTime? lastPlayed = null,
            DateTime? added = null,
            DateTime? releaseDate = null,
            ulong playtimeSeconds = 0,
            int totalAchievements = 10,
            int unlockedAchievements = 5,
            int collectionScore = 0,
            int prestigeScore = 0,
            int points = 0,
            bool owned = false)
        {
            return new GameSummaryItem
            {
                GameName = gameName,
                SortingName = sortingName ?? gameName,
                LastUnlockUtc = lastUnlockUtc,
                LastPlayed = lastPlayed,
                Added = added,
                ReleaseDate = releaseDate,
                PlaytimeSeconds = playtimeSeconds,
                TotalAchievements = totalAchievements,
                UnlockedAchievements = unlockedAchievements,
                CollectionScore = collectionScore,
                PrestigeScore = prestigeScore,
                Points = points,
                PlayniteGameId = owned ? Guid.NewGuid() : (Guid?)null,
                AppId = Math.Abs(gameName.GetHashCode())
            };
        }

        private static DateTime Utc(int year, int month, int day)
        {
            return DateTime.SpecifyKind(new DateTime(year, month, day, 12, 0, 0), DateTimeKind.Utc);
        }
    }
}
