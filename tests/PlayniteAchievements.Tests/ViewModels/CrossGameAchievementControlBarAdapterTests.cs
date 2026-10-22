using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class CrossGameAchievementControlBarAdapterTests
    {
        private static readonly Guid SteamPcId = Guid.NewGuid();
        private static readonly Guid SteamDeckId = Guid.NewGuid();
        private static readonly Guid XboxId = Guid.NewGuid();

        private static GameSummaryItem[] Games() => new[]
        {
            new GameSummaryItem { GameName = "Portal", PlayniteGameId = SteamPcId, ProviderKey = "Steam", Platforms = new[] { "PC" } },
            new GameSummaryItem { GameName = "Celeste", PlayniteGameId = SteamDeckId, ProviderKey = "Steam", Platforms = new[] { "Steam Deck" } },
            new GameSummaryItem { GameName = "Halo", PlayniteGameId = XboxId, ProviderKey = "Xbox", Platforms = new[] { "Xbox" } }
        };

        private static AchievementDisplayItem[] Rows() => new[]
        {
            new AchievementDisplayItem { GameName = "Portal", DisplayName = "Lab Rat", PlayniteGameId = SteamPcId },
            new AchievementDisplayItem { GameName = "Celeste", DisplayName = "Summit", PlayniteGameId = SteamDeckId },
            new AchievementDisplayItem { GameName = "Halo", DisplayName = "Finish the Fight", PlayniteGameId = XboxId },
            new AchievementDisplayItem { GameName = "Orphan", DisplayName = "No Game", PlayniteGameId = null }
        };

        private static CrossGameAchievementControlBarAdapter CreateAdapter(AchievementDisplayItem[] rows)
        {
            var adapter = new CrossGameAchievementControlBarAdapter();
            adapter.UpdateGames(Games());
            return adapter;
        }

        private static string[] Names(System.Collections.Generic.IEnumerable<AchievementDisplayItem> rows) =>
            rows.Select(row => row.DisplayName).ToArray();

        [TestMethod]
        public void NoSelection_KeepsEveryRow()
        {
            var rows = Rows();
            var adapter = CreateAdapter(rows);

            CollectionAssert.AreEqual(Names(rows), Names(adapter.Apply(rows)));
        }

        [TestMethod]
        public void FullySelectedProvider_KeepsThatProvidersRowsAndDropsRowsWithoutAGame()
        {
            var rows = Rows();
            var adapter = CreateAdapter(rows);

            adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam").SetAll(true);

            CollectionAssert.AreEqual(new[] { "Lab Rat", "Summit" }, Names(adapter.Apply(rows)));
        }

        [TestMethod]
        public void SinglePlatformOfProvider_KeepsOnlyThatPlatformsRows()
        {
            var rows = Rows();
            var adapter = CreateAdapter(rows);

            adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam")
                .Platforms.Single(platform => platform.PlatformName == "Steam Deck")
                .IsSelected = true;

            CollectionAssert.AreEqual(new[] { "Summit" }, Names(adapter.Apply(rows)));
        }

        [TestMethod]
        public void SearchAndPlatform_Combine()
        {
            var rows = Rows();
            var adapter = CreateAdapter(rows);

            adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam").SetAll(true);
            adapter.SearchText = "portal";

            CollectionAssert.AreEqual(new[] { "Lab Rat" }, Names(adapter.Apply(rows)));
        }

        [TestMethod]
        public void Dropdown_ListsEveryLibraryPlatform_NotOnlyThoseInTheRows()
        {
            var adapter = CreateAdapter(Rows());

            CollectionAssert.AreEquivalent(
                new[] { "Steam", "Xbox" },
                adapter.ProviderFilterGroups.Select(group => group.ProviderKey).ToArray());
        }

        [TestMethod]
        public void UpdateGames_WithSameGames_KeepsGroupInstances()
        {
            var games = Games();
            var adapter = new CrossGameAchievementControlBarAdapter();
            adapter.UpdateGames(games);
            var groups = adapter.ProviderFilterGroups;

            adapter.UpdateGames(games);

            Assert.AreSame(groups, adapter.ProviderFilterGroups);
        }

        [TestMethod]
        public void UpdateGames_WithDifferentGames_KeepsSelections()
        {
            var rows = Rows();
            var games = Games();
            var adapter = new CrossGameAchievementControlBarAdapter();
            adapter.UpdateGames(games);
            adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Xbox").SetAll(true);

            adapter.UpdateGames(games.Skip(1).ToArray());

            Assert.IsTrue(adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Xbox").IsFullySelected);
            CollectionAssert.AreEqual(new[] { "Finish the Fight" }, Names(adapter.Apply(rows)));
        }

        [TestMethod]
        public void PickedPlatforms_LimitTheGameListAndUnpickOtherGames()
        {
            var adapter = CreateAdapter(Rows());
            adapter.GameFilter.Item.SetSelected(SteamDeckId.ToString("D"), true);
            adapter.GameFilter.Item.SetSelected(XboxId.ToString("D"), true);

            adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam").SetAll(true);

            CollectionAssert.AreEquivalent(
                new[] { SteamPcId.ToString("D"), SteamDeckId.ToString("D") },
                adapter.GameFilter.Item.Options.ToArray());
            CollectionAssert.AreEquivalent(new[] { SteamDeckId }, adapter.GameFilter.SelectedGameIds.ToArray());

            adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam").SetAll(false);
            Assert.AreEqual(3, adapter.GameFilter.Item.Options.Count(), "no platform picks list every game");
        }

        [TestMethod]
        public void Collection_UnpicksGamesOutsideIt()
        {
            var adapter = CreateAdapter(Rows());
            adapter.RestoreState(new ControlBarFilterState
            {
                Games = new[] { SteamPcId.ToString("D"), XboxId.ToString("D") }.ToList()
            });

            adapter.GameFilter.SetScope(null);
            Assert.AreEqual(2, adapter.GameFilter.SelectedGameIds.Count, "no collection keeps every pick");

            adapter.GameFilter.SetScope(new[] { SteamPcId, SteamDeckId });
            CollectionAssert.AreEquivalent(new[] { SteamPcId }, adapter.GameFilter.SelectedGameIds.ToArray());
            CollectionAssert.AreEqual(new[] { SteamPcId.ToString("D") }, adapter.CaptureState().Games);
        }

        [TestMethod]
        public void ClearSelection_ResetsEachEntryOnItsOwn()
        {
            var rows = Rows();
            var adapter = CreateAdapter(rows);
            adapter.RestoreState(new ControlBarFilterState
            {
                SearchText = "summit",
                Games = new[] { SteamPcId.ToString("D"), Guid.NewGuid().ToString("D") }.ToList(),
                UnlockedFrom = new DateTime(2026, 1, 1),
                Rarities = new[] { 2 }.ToList()
            });
            adapter.UpdateGames(Games());
            var entries = adapter.Entries.ToDictionary(entry => entry.Id);

            entries[PlayniteAchievements.Models.ShowcaseControlBarItems.Search].ClearSelection();
            Assert.AreEqual(string.Empty, adapter.SearchText);
            Assert.IsTrue(adapter.GameFilter.HasSelection, "only the search box clears");

            entries[PlayniteAchievements.Models.ShowcaseControlBarItems.Game].ClearSelection();
            Assert.IsFalse(adapter.GameFilter.HasSelection, "unlisted games clear too");

            entries[PlayniteAchievements.Models.ShowcaseControlBarItems.UnlockDate].ClearSelection();
            Assert.IsNull(adapter.UnlockedFrom);

            entries[PlayniteAchievements.Models.ShowcaseControlBarItems.Rarity].ClearSelection();
            Assert.IsNull(adapter.CaptureState().Rarities);

            adapter.ProviderFilterGroups.Single(group => group.ProviderKey == "Xbox").SetAll(true);
            entries[PlayniteAchievements.Models.ShowcaseControlBarItems.Platform].ClearSelection();
            Assert.IsFalse(adapter.ProviderFilterGroups.Any(group => group.HasAnySelected));
        }

        [TestMethod]
        public void LocksRows_LeavesLockedRowsToTheUnlockRangeAndHidesItsItem()
        {
            var adapter = new CrossGameAchievementControlBarAdapter();
            adapter.RestoreState(new ControlBarFilterState { UnlockedFrom = new DateTime(2026, 1, 1) });
            var locked = new[] { new AchievementDisplayItem { DisplayName = "Next", Unlocked = false } };
            var dateItem = adapter.ControlBar.Items.Single(item => item.Id == PlayniteAchievements.Models.ShowcaseControlBarItems.UnlockDate);

            Assert.AreEqual(0, adapter.Apply(locked).Count, "the range keeps unlocked rows only");

            adapter.LocksRows = true;
            Assert.AreEqual(1, adapter.Apply(locked).Count);
            Assert.IsFalse(dateItem.IsVisible);
            Assert.AreEqual(new DateTime(2026, 1, 1), adapter.UnlockedFrom, "the range is kept");

            adapter.LocksRows = false;
            Assert.IsTrue(dateItem.IsVisible);
        }

        [TestMethod]
        public void CapturedState_RestoresIntoAFreshAdapter()
        {
            var rows = Rows();
            var original = CreateAdapter(rows);
            original.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam")
                .Platforms.Single(platform => platform.PlatformName == "Steam Deck")
                .IsSelected = true;
            original.SearchText = "summit";

            var restored = new CrossGameAchievementControlBarAdapter();
            restored.RestoreState(original.CaptureState());
            restored.UpdateGames(Games());

            Assert.AreEqual("summit", restored.SearchText);
            CollectionAssert.AreEqual(new[] { "Steam Deck" },
                restored.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam").SelectedPlatformNames.ToArray());
            CollectionAssert.AreEqual(new[] { "Summit" }, Names(restored.Apply(rows)));
        }
    }
}
