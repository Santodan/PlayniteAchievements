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
        public void ResolveSelectedGameAchievements_DynamicSortUsesDefaultSourceInsteadOfPrecomputedLists()
        {
            var common = CreateDetail("Common", unlocked: true, globalPercentUnlocked: 80);
            var rare = CreateDetail("Rare", unlocked: true, globalPercentUnlocked: 5);
            var poison = CreateDetail("Precomputed", unlocked: true, globalPercentUnlocked: 1);
            var source = new List<AchievementDetail> { common, rare };
            var state = new SelectedGameRuntimeState(
                Guid.NewGuid(),
                DateTime.UtcNow,
                hasAchievements: true,
                achievementCount: source.Count,
                unlockedCount: source.Count,
                lockedCount: 0,
                progressPercentage: 100,
                isCompleted: true,
                hasCustomAchievementOrder: false,
                achievementDefaultOrder: source,
                allAchievements: source,
                achievementsOldestFirst: new List<AchievementDetail>(),
                achievementsNewestFirst: new List<AchievementDetail>(),
                achievementsRarityAsc: new List<AchievementDetail> { poison },
                achievementsRarityDesc: new List<AchievementDetail> { poison },
                common: new AchievementRarityStats(),
                uncommon: new AchievementRarityStats(),
                rare: new AchievementRarityStats(),
                ultraRare: new AchievementRarityStats(),
                rareAndUltraRare: new AchievementRarityStats());

            var resolved = AchievementSortHelper.ResolveSelectedGameAchievements(
                state,
                DynamicThemeViewKeys.Rarity,
                DynamicThemeViewKeys.Ascending);

            CollectionAssert.AreEqual(
                new[] { "Rare", "Common" },
                resolved.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void ResolveLibraryAchievements_DynamicSortUsesCanonicalSourceInsteadOfPrecomputedLists()
        {
            var common = CreateDetail("Common", unlocked: true, globalPercentUnlocked: 80);
            var rare = CreateDetail("Rare", unlocked: true, globalPercentUnlocked: 5);
            var poison = CreateDetail("Precomputed", unlocked: true, globalPercentUnlocked: 1);
            var state = new LibraryRuntimeState
            {
                TotalTrophies = 2,
                AllAchievements = new List<AchievementDetail> { common, rare },
                AllAchievementsRarityAsc = new List<AchievementDetail> { poison },
                AllAchievementsRarityDesc = new List<AchievementDetail> { poison },
                AllAchievementsUnlockAsc = new List<AchievementDetail> { poison },
                AllAchievementsUnlockDesc = new List<AchievementDetail> { poison }
            };

            var resolved = AchievementSortHelper.ResolveLibraryAchievements(
                state,
                DynamicThemeViewKeys.Rarity,
                DynamicThemeViewKeys.Ascending);

            CollectionAssert.AreEqual(
                new[] { "Rare", "Common" },
                resolved.Select(item => item.DisplayName).ToArray());
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
        public void ResolveGridSortAction_DefaultRarityDescending_CyclesDefaultAscendingNoneDefault()
        {
            var settings = new PersistedSettings
            {
                AchievementDataGridSortMode = CompactListSortMode.Rarity,
                AchievementDataGridSortDescending = true
            };

            var first = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.AchievementDataGrid,
                visibleSortDirection: ListSortDirection.Descending);
            var second = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                first.SortMemberPath,
                first.Direction,
                settings,
                AchievementSortSurface.AchievementDataGrid,
                visibleSortDirection: first.Direction);
            var third = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.AchievementDataGrid,
                visibleSortDirection: null);
            var fourth = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                third.SortMemberPath,
                third.Direction,
                settings,
                AchievementSortSurface.AchievementDataGrid,
                visibleSortDirection: third.Direction);

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, second.Kind);
            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, third.Kind);
            Assert.AreEqual(ListSortDirection.Descending, third.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, fourth.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, fourth.Direction);
        }

        [TestMethod]
        public void ResolveGridSortAction_DefaultRarityAscending_CyclesDefaultDescendingNoneDefault()
        {
            var settings = new PersistedSettings
            {
                AchievementDataGridSortMode = CompactListSortMode.Rarity,
                AchievementDataGridSortDescending = false
            };

            var first = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.AchievementDataGrid,
                visibleSortDirection: ListSortDirection.Ascending);
            var second = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                first.SortMemberPath,
                first.Direction,
                settings,
                AchievementSortSurface.AchievementDataGrid,
                visibleSortDirection: first.Direction);
            var third = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.AchievementDataGrid,
                visibleSortDirection: null);

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Descending, first.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, second.Kind);
            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, third.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, third.Direction);
        }

        [TestMethod]
        public void ResolveGridSortAction_OverviewRecentDefaultOnSameColumn_CyclesAscendingThenReset()
        {
            var first = AchievementSortHelper.ResolveGridSortAction(
                "UnlockTime",
                currentSortPath: null,
                currentSortDirection: null,
                settings: new PersistedSettings(),
                AchievementSortSurface.OverviewRecentAchievements,
                visibleSortDirection: ListSortDirection.Descending);
            var second = AchievementSortHelper.ResolveGridSortAction(
                "UnlockTime",
                first.SortMemberPath,
                first.Direction,
                settings: new PersistedSettings(),
                AchievementSortSurface.OverviewRecentAchievements,
                visibleSortDirection: first.Direction);

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, first.Kind);
            Assert.AreEqual(ListSortDirection.Ascending, first.Direction);
            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, second.Kind);
        }

        [TestMethod]
        public void ResolveSelectedGameAchievements_CompactLockedRarityUsesConfiguredDirection()
        {
            var rare = CreateDetail("Rare", unlocked: false, globalPercentUnlocked: 5);
            var common = CreateDetail("Common", unlocked: false, globalPercentUnlocked: 80);
            var theme = new ModernThemeBindings
            {
                HasAchievements = true,
                AllAchievements = new List<AchievementDetail> { common, rare },
                AchievementDefaultOrder = new List<AchievementDetail> { common, rare },
                AchievementsRarityAsc = new List<AchievementDetail> { rare, common },
                AchievementsRarityDesc = new List<AchievementDetail> { common, rare }
            };

            var settings = new PersistedSettings
            {
                CompactLockedListSortMode = CompactListSortMode.Rarity,
                CompactLockedListSortDescending = true
            };

            var descending = AchievementSortHelper.ResolveSelectedGameAchievements(
                theme,
                settings,
                AchievementSortSurface.CompactLockedList);
            settings.CompactLockedListSortDescending = false;
            var ascending = AchievementSortHelper.ResolveSelectedGameAchievements(
                theme,
                settings,
                AchievementSortSurface.CompactLockedList);

            CollectionAssert.AreEqual(new[] { common, rare }, descending);
            CollectionAssert.AreEqual(new[] { rare, common }, ascending);
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
                    OverviewSelectedGameGridSortMode = CompactListSortMode.None,
                    OverviewSelectedGameGridSortDescending = false
                },
                AchievementSortSurface.OverviewSelectedGame,
                AchievementSortScope.GameAchievements);

            CollectionAssert.AreEqual(
                new[] { "Gamma", "Alpha" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void CollectionScore_SortsByComputedColumnValue()
        {
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Low", DateTime.UtcNow, raritySortValue: 80, trophyType: "bronze", points: 10, collectionScore: 15),
                CreateItem("High", DateTime.UtcNow, raritySortValue: 10, trophyType: "gold", points: 10, collectionScore: 180)
            };

            string sortPath = null;
            ListSortDirection? sortDirection = null;

            var handled = AchievementSortHelper.TrySortItems(
                items,
                "CollectionScore",
                ListSortDirection.Descending,
                AchievementSortScope.GameAchievements,
                ref sortPath,
                ref sortDirection);

            Assert.IsTrue(handled);
            CollectionAssert.AreEqual(
                new[] { "High", "Low" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void PrestigeScore_SortsByComputedColumnValue()
        {
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Mid", DateTime.UtcNow, raritySortValue: 40, trophyType: "silver", points: 10, prestigeScore: 75),
                CreateItem("Low", DateTime.UtcNow, raritySortValue: 80, trophyType: "bronze", points: 10, prestigeScore: 30),
                CreateItem("High", DateTime.UtcNow, raritySortValue: 10, trophyType: "gold", points: 10, prestigeScore: 250)
            };

            string sortPath = null;
            ListSortDirection? sortDirection = null;

            var handled = AchievementSortHelper.TrySortItems(
                items,
                "PrestigeScore",
                ListSortDirection.Ascending,
                AchievementSortScope.GameAchievements,
                ref sortPath,
                ref sortDirection);

            Assert.IsTrue(handled);
            CollectionAssert.AreEqual(
                new[] { "Low", "Mid", "High" },
                items.Select(item => item.DisplayName).ToArray());
        }

        private static AchievementDisplayItem CreateItem(
            string displayName,
            DateTime unlockTimeUtc,
            double raritySortValue,
            string trophyType,
            int points,
            Guid? gameId = null,
            string gameName = "Test Game",
            int collectionScore = 0,
            int prestigeScore = 0)
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
                CollectionScore = collectionScore,
                PrestigeScore = prestigeScore,
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

