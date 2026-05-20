using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementSortHelperTests
    {
        [TestMethod]
        public void UnlockTime_TieBreaksByRarityBeforeTrophyType()
        {
            var unlockTime = DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc);
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Gold Common", unlockTime, raritySortValue: 80, trophyType: "gold", points: 10),
                CreateItem("Bronze Rare", unlockTime, raritySortValue: 8, trophyType: "bronze", points: 10)
            };

            string sortPath = null;
            ListSortDirection? sortDirection = null;

            var handled = AchievementSortHelper.TrySortItems(
                items,
                "UnlockTime",
                ListSortDirection.Descending,
                AchievementSortScope.GameAchievements,
                ref sortPath,
                ref sortDirection);

            Assert.IsTrue(handled);
            CollectionAssert.AreEqual(
                new[] { "Bronze Rare", "Gold Common" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void TrophyType_TieBreaksByRarity()
        {
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Gold Common", DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 80, trophyType: "gold", points: 10),
                CreateItem("Gold Rare", DateTime.SpecifyKind(new DateTime(2026, 3, 2, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 8, trophyType: "gold", points: 10),
                CreateItem("Silver Common", DateTime.SpecifyKind(new DateTime(2026, 3, 3, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 90, trophyType: "silver", points: 10)
            };

            string sortPath = null;
            ListSortDirection? sortDirection = null;

            var handled = AchievementSortHelper.TrySortItems(
                items,
                "TrophyType",
                ListSortDirection.Descending,
                AchievementSortScope.GameAchievements,
                ref sortPath,
                ref sortDirection);

            Assert.IsTrue(handled);
            CollectionAssert.AreEqual(
                new[] { "Gold Rare", "Gold Common", "Silver Common" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void DefaultDetailSort_PrioritizesUnlockedThenLatestUnlockTime()
        {
            var sorted = AchievementSortHelper.CreateDefaultSortedDetailList(new List<AchievementDetail>
            {
                CreateDetail("Locked", unlocked: false),
                CreateDetail("Unlocked Older", unlocked: true, unlockTimeUtc: DateTime.SpecifyKind(new DateTime(2026, 3, 1, 10, 0, 0), DateTimeKind.Utc)),
                CreateDetail("Unlocked Newer", unlocked: true, unlockTimeUtc: DateTime.SpecifyKind(new DateTime(2026, 3, 2, 10, 0, 0), DateTimeKind.Utc)),
                CreateDetail("Unlocked No Time", unlocked: true)
            });

            CollectionAssert.AreEqual(
                new[] { "Unlocked Newer", "Unlocked Older", "Unlocked No Time", "Locked" },
                sorted.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void ResolveSelectedGameAchievements_DefaultFallsBackToAllAchievements_WhenDefaultOrderIsEmpty()
        {
            var first = CreateDetail("First", unlocked: true);
            var second = CreateDetail("Second", unlocked: false);
            var theme = new ModernThemeBindings
            {
                HasAchievements = true,
                AllAchievements = new List<AchievementDetail> { first, second },
                AchievementDefaultOrder = new List<AchievementDetail>()
            };

            var settings = new PersistedSettings
            {
                CompactListSortMode = CompactListSortMode.None,
                CompactListSortDescending = false
            };

            var resolved = AchievementSortHelper.ResolveSelectedGameAchievements(
                theme,
                settings,
                AchievementSortSurface.CompactList);

            CollectionAssert.AreEqual(new[] { first, second }, resolved);
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultNone_CyclesAscendingDescendingThenReset()
        {
            var settings = new PersistedSettings
            {
                SingleGameGridSortMode = CompactListSortMode.None,
                SingleGameGridSortDescending = false
            };

            var first = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.SingleGame);
            var second = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                first.SortMemberPath,
                first.Direction,
                settings,
                AchievementSortSurface.SingleGame);
            var third = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                second.SortMemberPath,
                second.Direction,
                settings,
                AchievementSortSurface.SingleGame);

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, second.Kind);
            Assert.AreEqual(ListSortDirection.Descending, second.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, third.Kind);
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultOnDifferentColumn_CyclesAscendingDescendingThenReset()
        {
            var settings = new PersistedSettings
            {
                SingleGameGridSortMode = CompactListSortMode.UnlockTime,
                SingleGameGridSortDescending = true
            };

            var first = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.SingleGame);
            var second = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                first.SortMemberPath,
                first.Direction,
                settings,
                AchievementSortSurface.SingleGame);
            var third = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                second.SortMemberPath,
                second.Direction,
                settings,
                AchievementSortSurface.SingleGame);

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, second.Kind);
            Assert.AreEqual(ListSortDirection.Descending, second.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, third.Kind);
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultOnSameColumn_SkipsDefaultDirectionAndThenResets()
        {
            var settings = new PersistedSettings
            {
                SingleGameGridSortMode = CompactListSortMode.UnlockTime,
                SingleGameGridSortDescending = true
            };

            var first = AchievementSortHelper.ResolveGridSortAction(
                "UnlockTime",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.SingleGame);
            var second = AchievementSortHelper.ResolveGridSortAction(
                "UnlockTime",
                first.SortMemberPath,
                first.Direction,
                settings,
                AchievementSortSurface.SingleGame);

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, second.Kind);
        }

        [TestMethod]
        public void ResolveGridSortAction_SidebarRecentDefaultOnSameColumn_SkipsDefaultDirectionAndThenResets()
        {
            var first = AchievementSortHelper.ResolveGridSortAction(
                "UnlockTime",
                currentSortPath: null,
                currentSortDirection: null,
                settings: new PersistedSettings(),
                AchievementSortSurface.SidebarRecentAchievements);
            var second = AchievementSortHelper.ResolveGridSortAction(
                "UnlockTime",
                first.SortMemberPath,
                first.Direction,
                settings: new PersistedSettings(),
                AchievementSortSurface.SidebarRecentAchievements);

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, second.Kind);
        }

        [TestMethod]
        public void ApplyConfiguredDefaultSort_NonePreservesExplicitCustomOrder()
        {
            var gameId = Guid.NewGuid();
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Alpha", DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 80, trophyType: "gold", points: 10, gameId: gameId),
                CreateItem("Beta", DateTime.SpecifyKind(new DateTime(2026, 3, 2, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 50, trophyType: "silver", points: 10, gameId: gameId),
                CreateItem("Gamma", DateTime.SpecifyKind(new DateTime(2026, 3, 3, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 10, trophyType: "bronze", points: 10, gameId: gameId)
            };

            var explicitOrder = new List<string>
            {
                AchievementDisplayItem.MakeRevealKey(gameId, "Gamma", "Test Game"),
                AchievementDisplayItem.MakeRevealKey(gameId, "Alpha", "Test Game"),
                AchievementDisplayItem.MakeRevealKey(gameId, "Beta", "Test Game")
            };

            AchievementSortHelper.ApplyConfiguredDefaultSort(
                items,
                new PersistedSettings
                {
                    SingleGameGridSortMode = CompactListSortMode.None,
                    SingleGameGridSortDescending = false
                },
                AchievementSortSurface.SingleGame,
                AchievementSortScope.GameAchievements,
                explicitOrder: explicitOrder);

            CollectionAssert.AreEqual(
                new[] { "Gamma", "Alpha", "Beta" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void ApplyConfiguredDefaultSort_NoneKeepsFilteredSourceOrder()
        {
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Gamma", DateTime.SpecifyKind(new DateTime(2026, 3, 3, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 10, trophyType: "bronze", points: 10),
                CreateItem("Alpha", DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 80, trophyType: "gold", points: 10)
            };

            AchievementSortHelper.ApplyConfiguredDefaultSort(
                items,
                new PersistedSettings
                {
                    SidebarSelectedGameGridSortMode = CompactListSortMode.None,
                    SidebarSelectedGameGridSortDescending = false
                },
                AchievementSortSurface.SidebarSelectedGame,
                AchievementSortScope.GameAchievements);

            CollectionAssert.AreEqual(
                new[] { "Gamma", "Alpha" },
                items.Select(item => item.DisplayName).ToArray());
        }

        private static AchievementDisplayItem CreateItem(
            string displayName,
            DateTime unlockTimeUtc,
            double raritySortValue,
            string trophyType,
            int points,
            Guid? gameId = null,
            string gameName = "Test Game")
        {
            return new AchievementDisplayItem
            {
                DisplayName = displayName,
                ApiName = displayName,
                GameName = gameName,
                PlayniteGameId = gameId,
                UnlockTimeUtc = unlockTimeUtc,
                RaritySortValue = raritySortValue,
                TrophyType = trophyType,
                PointsValue = points,
                Unlocked = true
            };
        }

        private static AchievementDetail CreateDetail(
            string displayName,
            bool unlocked,
            DateTime? unlockTimeUtc = null,
            double? globalPercentUnlocked = 50)
        {
            return new AchievementDetail
            {
                ApiName = displayName,
                DisplayName = displayName,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTimeUtc,
                GlobalPercentUnlocked = globalPercentUnlocked
            };
        }
    }
}

