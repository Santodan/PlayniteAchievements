using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class ProviderFilterGroupTests
    {
        [TestMethod]
        public void MultiPlatformGroup_NoneSelected_ReportsCollapsedState()
        {
            var group = CreateGroup("RetroAchievements", "Game Boy", "NES", "PlayStation 2");

            Assert.IsTrue(group.HasMultiplePlatforms);
            Assert.IsFalse(group.HasAnySelected);
            Assert.IsFalse(group.IsFullySelected);
            Assert.AreEqual(false, group.SelectionState);
        }

        [TestMethod]
        public void SinglePlatformGroup_HasNoExpander()
        {
            var group = CreateGroup("Steam", "PC");

            Assert.IsFalse(group.HasMultiplePlatforms);
        }

        [TestMethod]
        public void SelectingSomePlatforms_YieldsIndeterminateState()
        {
            var group = CreateGroup("RetroAchievements", "Game Boy", "NES", "PlayStation 2");

            group.Platforms.First(p => p.PlatformName == "NES").IsSelected = true;

            Assert.IsTrue(group.HasAnySelected);
            Assert.IsFalse(group.IsFullySelected);
            Assert.IsNull(group.SelectionState);
            CollectionAssert.AreEqual(new[] { "NES" }, group.SelectedPlatformNames.ToList());
        }

        [TestMethod]
        public void SelectingAllPlatforms_YieldsFullySelectedState()
        {
            var group = CreateGroup("RetroAchievements", "Game Boy", "NES");

            foreach (var option in group.Platforms)
            {
                option.IsSelected = true;
            }

            Assert.IsTrue(group.IsFullySelected);
            Assert.AreEqual(true, group.SelectionState);
        }

        [TestMethod]
        public void ToggleAll_SelectsAllWhenNotAll_ThenClears()
        {
            var group = CreateGroup("RetroAchievements", "Game Boy", "NES", "PlayStation 2");

            group.ToggleAll();
            Assert.IsTrue(group.IsFullySelected);

            group.ToggleAll();
            Assert.IsFalse(group.HasAnySelected);
        }

        [TestMethod]
        public void ToggleAll_FromPartial_SelectsAll()
        {
            var group = CreateGroup("RetroAchievements", "Game Boy", "NES", "PlayStation 2");
            group.Platforms.First().IsSelected = true;

            group.ToggleAll();

            Assert.IsTrue(group.IsFullySelected);
        }

        [TestMethod]
        public void SetAll_FiresSelectionChangedExactlyOnce()
        {
            var changeCount = 0;
            var group = new ProviderFilterGroup(
                "RetroAchievements",
                "RetroAchievements",
                new[] { "Game Boy", "NES", "PlayStation 2" },
                _ => false,
                () => changeCount++);

            group.SetAll(true);

            Assert.AreEqual(1, changeCount);
            Assert.IsTrue(group.IsFullySelected);
        }

        [TestMethod]
        public void IndividualSelection_FiresSelectionChanged()
        {
            var changeCount = 0;
            var group = new ProviderFilterGroup(
                "RetroAchievements",
                "RetroAchievements",
                new[] { "Game Boy", "NES" },
                _ => false,
                () => changeCount++);

            group.Platforms.First().IsSelected = true;

            Assert.AreEqual(1, changeCount);
        }

        [TestMethod]
        public void InitialSelection_HonorsPredicate()
        {
            var group = new ProviderFilterGroup(
                "RetroAchievements",
                "RetroAchievements",
                new[] { "Game Boy", "NES", "PlayStation 2" },
                name => name == "NES" || name == "Game Boy",
                () => { });

            Assert.IsTrue(group.HasAnySelected);
            Assert.IsFalse(group.IsFullySelected);
            CollectionAssert.AreEqual(
                new[] { "Game Boy", "NES" },
                group.SelectedPlatformNames.ToList());
        }

        [TestMethod]
        public void HasSameFilterOptions_NewInstancesOfTheSameGames_IsTrue()
        {
            Assert.IsTrue(ProviderFilterGroupBuilder.HasSameFilterOptions(Library(), Library()));
        }

        [TestMethod]
        public void HasSameFilterOptions_ChangedPlatformProviderOrOrder_IsFalse()
        {
            var changedPlatform = Library();
            changedPlatform[1].Platforms = new[] { "PC" };
            var changedProvider = Library();
            changedProvider[2].ProviderKey = "PSN";
            var reordered = Enumerable.Reverse(Library()).ToArray();
            var shorter = Library().Take(2).ToArray();

            Assert.IsFalse(ProviderFilterGroupBuilder.HasSameFilterOptions(Library(), changedPlatform));
            Assert.IsFalse(ProviderFilterGroupBuilder.HasSameFilterOptions(Library(), changedProvider));
            Assert.IsFalse(ProviderFilterGroupBuilder.HasSameFilterOptions(Library(), reordered));
            Assert.IsFalse(ProviderFilterGroupBuilder.HasSameFilterOptions(Library(), shorter));
            Assert.IsFalse(ProviderFilterGroupBuilder.HasSameFilterOptions(null, Library()));
        }

        [TestMethod]
        public void GameSummaryAdapter_NewSnapshotOfTheSameLibrary_KeepsTheGroups()
        {
            var adapter = new GameSummaryGridControlBarAdapter();
            adapter.UpdateOptions(Library());
            var groups = adapter.ProviderFilterGroups;

            adapter.UpdateOptions(Library());

            Assert.AreSame(groups, adapter.ProviderFilterGroups);
        }

        [TestMethod]
        public void GameSummaryAdapter_NewPlatformInTheLibrary_RebuildsTheGroups()
        {
            var adapter = new GameSummaryGridControlBarAdapter();
            adapter.UpdateOptions(Library());
            var groups = adapter.ProviderFilterGroups;

            var changed = Library();
            changed[0].Platforms = new[] { "PC", "Linux" };
            adapter.UpdateOptions(changed);

            Assert.AreNotSame(groups, adapter.ProviderFilterGroups);
            CollectionAssert.Contains(
                adapter.ProviderFilterGroups.SelectMany(group => group.Platforms.Select(p => p.PlatformName)).ToList(),
                "Linux");
        }

        private static GameSummaryItem[] Library() => new[]
        {
            new GameSummaryItem { GameName = "Portal", ProviderKey = "Steam", Provider = "Steam", Platforms = new[] { "PC" } },
            new GameSummaryItem { GameName = "Celeste", ProviderKey = "Steam", Provider = "Steam", Platforms = new[] { "Steam Deck" } },
            new GameSummaryItem { GameName = "Halo", ProviderKey = "Xbox", Provider = "Xbox", Platforms = new[] { "Xbox" } }
        };

        private static ProviderFilterGroup CreateGroup(string providerKey, params string[] platforms)
        {
            return new ProviderFilterGroup(
                providerKey,
                providerKey,
                platforms,
                _ => false,
                () => { });
        }
    }
}
