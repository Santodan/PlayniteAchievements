using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class GamesOverviewSortHelperTests
    {
        [TestMethod]
        public void SortByConfiguredDefault_RecentUnlockDescending_PutsNewestUnlockFirst()
        {
            var items = new List<GameOverviewItem>
            {
                CreateItem("Gamma", lastUnlockUtc: Utc(2026, 4, 2), lastPlayed: Utc(2026, 4, 3)),
                CreateItem("Alpha", lastUnlockUtc: Utc(2026, 4, 5), lastPlayed: Utc(2026, 4, 1)),
                CreateItem("Beta", lastUnlockUtc: null, lastPlayed: Utc(2026, 4, 4))
            };

            GamesOverviewSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    GamesOverviewGridSortMode = GamesOverviewSortMode.RecentUnlock,
                    GamesOverviewGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Alpha", "Gamma", "Beta" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_RecentUnlockAscending_PutsOldestUnlockFirst()
        {
            var items = new List<GameOverviewItem>
            {
                CreateItem("Gamma", lastUnlockUtc: Utc(2026, 4, 2)),
                CreateItem("Alpha", lastUnlockUtc: Utc(2026, 4, 5)),
                CreateItem("Beta", lastUnlockUtc: null)
            };

            GamesOverviewSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    GamesOverviewGridSortMode = GamesOverviewSortMode.RecentUnlock,
                    GamesOverviewGridSortDescending = false
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_LastPlayed_UsesLastPlayedDate()
        {
            var items = new List<GameOverviewItem>
            {
                CreateItem("Alpha", lastPlayed: Utc(2026, 4, 1)),
                CreateItem("Beta", lastPlayed: Utc(2026, 4, 8)),
                CreateItem("Gamma", lastPlayed: Utc(2026, 4, 4))
            };

            GamesOverviewSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    GamesOverviewGridSortMode = GamesOverviewSortMode.LastPlayed,
                    GamesOverviewGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_TotalAchievements_UsesTotalAchievements()
        {
            var items = new List<GameOverviewItem>
            {
                CreateItem("Alpha", totalAchievements: 12, unlockedAchievements: 6),
                CreateItem("Beta", totalAchievements: 40, unlockedAchievements: 10),
                CreateItem("Gamma", totalAchievements: 24, unlockedAchievements: 12)
            };

            GamesOverviewSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    GamesOverviewGridSortMode = GamesOverviewSortMode.TotalAchievements,
                    GamesOverviewGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_Progress_UsesComputedProgression()
        {
            var items = new List<GameOverviewItem>
            {
                CreateItem("Alpha", totalAchievements: 10, unlockedAchievements: 3),
                CreateItem("Beta", totalAchievements: 10, unlockedAchievements: 9),
                CreateItem("Gamma", totalAchievements: 20, unlockedAchievements: 10)
            };

            GamesOverviewSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    GamesOverviewGridSortMode = GamesOverviewSortMode.Progress,
                    GamesOverviewGridSortDescending = true
                });

            CollectionAssert.AreEqual(
                new[] { "Beta", "Gamma", "Alpha" },
                items.Select(item => item.GameName).ToArray());
        }

        [TestMethod]
        public void SortByConfiguredDefault_Alphabetical_UsesSortingName()
        {
            var items = new List<GameOverviewItem>
            {
                CreateItem("Zoo", sortingName: "Bravo"),
                CreateItem("Alpha", sortingName: "Charlie"),
                CreateItem("Beta", sortingName: "Alpha")
            };

            GamesOverviewSortHelper.SortByConfiguredDefault(
                items,
                new PersistedSettings
                {
                    GamesOverviewGridSortMode = GamesOverviewSortMode.Alphabetical,
                    GamesOverviewGridSortDescending = false
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
                GamesOverviewGridSortMode = GamesOverviewSortMode.RecentUnlock,
                GamesOverviewGridSortDescending = true
            };

            var first = GamesOverviewSortHelper.ResolveGridSortAction(
                "SortingName",
                currentSortPath: null,
                currentSortDirection: null,
                settings);
            var second = GamesOverviewSortHelper.ResolveGridSortAction(
                "SortingName",
                first.SortMemberPath,
                first.Direction,
                settings);
            var third = GamesOverviewSortHelper.ResolveGridSortAction(
                "SortingName",
                second.SortMemberPath,
                second.Direction,
                settings);

            Assert.AreEqual(GamesOverviewGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(GamesOverviewGridSortActionKind.ApplySort, second.Kind);
            Assert.AreEqual(ListSortDirection.Descending, second.Direction);
            Assert.AreEqual(GamesOverviewGridSortActionKind.ResetToDefault, third.Kind);
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultOnSameColumn_SkipsDefaultDirectionAndThenResets()
        {
            var settings = new PersistedSettings
            {
                GamesOverviewGridSortMode = GamesOverviewSortMode.LastPlayed,
                GamesOverviewGridSortDescending = true
            };

            var first = GamesOverviewSortHelper.ResolveGridSortAction(
                nameof(GameOverviewItem.LastPlayed),
                currentSortPath: null,
                currentSortDirection: null,
                settings);
            var second = GamesOverviewSortHelper.ResolveGridSortAction(
                nameof(GameOverviewItem.LastPlayed),
                first.SortMemberPath,
                first.Direction,
                settings);

            Assert.AreEqual(GamesOverviewGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(GamesOverviewGridSortActionKind.ResetToDefault, second.Kind);
        }

        [TestMethod]
        public void ApplySortIndicator_RecentUnlockDefault_DoesNotReturnVisibleColumn()
        {
            string indicatorPath = "seed";
            ListSortDirection? indicatorDirection = ListSortDirection.Ascending;

            GamesOverviewSortHelper.ApplySortIndicator(
                null,
                null,
                new PersistedSettings
                {
                    GamesOverviewGridSortMode = GamesOverviewSortMode.RecentUnlock,
                    GamesOverviewGridSortDescending = true
                },
                (path, direction) =>
                {
                    indicatorPath = path;
                    indicatorDirection = direction;
                });

            Assert.IsNull(indicatorPath);
            Assert.IsNull(indicatorDirection);
        }

        private static GameOverviewItem CreateItem(
            string gameName,
            string sortingName = null,
            DateTime? lastUnlockUtc = null,
            DateTime? lastPlayed = null,
            int totalAchievements = 10,
            int unlockedAchievements = 5)
        {
            return new GameOverviewItem
            {
                GameName = gameName,
                SortingName = sortingName ?? gameName,
                LastUnlockUtc = lastUnlockUtc,
                LastPlayed = lastPlayed,
                TotalAchievements = totalAchievements,
                UnlockedAchievements = unlockedAchievements,
                AppId = Math.Abs(gameName.GetHashCode())
            };
        }

        private static DateTime Utc(int year, int month, int day)
        {
            return DateTime.SpecifyKind(new DateTime(year, month, day, 12, 0, 0), DateTimeKind.Utc);
        }
    }
}
