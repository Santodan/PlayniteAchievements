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
