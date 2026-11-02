using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class ShowcaseControlBarStatesTests
    {
        [TestMethod]
        public void Get_SameInstanceAndType_ReturnsTheSameAdapter()
        {
            var id = Guid.NewGuid().ToString();

            Assert.AreSame(
                ShowcaseControlBarStates.Get<CrossGameAchievementControlBarAdapter>(id),
                ShowcaseControlBarStates.Get<CrossGameAchievementControlBarAdapter>(id));
        }

        [TestMethod]
        public void Get_WithoutInstanceId_ReturnsUnsharedAdapters()
        {
            Assert.AreNotSame(
                ShowcaseControlBarStates.Get<CrossGameAchievementControlBarAdapter>(null),
                ShowcaseControlBarStates.Get<CrossGameAchievementControlBarAdapter>(null));
        }

        [TestMethod]
        public void RemoveExcept_DropsStaleInstancesOnly()
        {
            var live = Guid.NewGuid().ToString();
            var stale = Guid.NewGuid().ToString();
            var liveAdapter = ShowcaseControlBarStates.Get<GameSummaryGridControlBarAdapter>(live);
            var staleAdapter = ShowcaseControlBarStates.Get<GameSummaryGridControlBarAdapter>(stale);

            ShowcaseControlBarStates.RemoveExcept(new HashSet<string> { live });

            Assert.AreSame(liveAdapter, ShowcaseControlBarStates.Get<GameSummaryGridControlBarAdapter>(live));
            Assert.AreNotSame(staleAdapter, ShowcaseControlBarStates.Get<GameSummaryGridControlBarAdapter>(stale));
        }

        private sealed class FakeStore : IShowcaseControlBarStateStore
        {
            public readonly Dictionary<string, string> Saved = new Dictionary<string, string>();

            public string Load(string instanceId, string key) =>
                Saved.TryGetValue(instanceId + "|" + key, out var state) ? state : null;

            public void Save(string instanceId, string key, string state) =>
                Saved[instanceId + "|" + key] = state;
        }

        [TestMethod]
        public void Store_SavesFilterChangesAndRestoresThemAfterTheRegistryForgets()
        {
            var store = new FakeStore();
            var id = Guid.NewGuid().ToString();
            ShowcaseControlBarStates.Store = store;
            try
            {
                var adapter = ShowcaseControlBarStates.Get<GameSummaryGridControlBarAdapter>(id);
                adapter.SearchText = "halo";
                adapter.SetProgressFilterSelected(adapter.ProgressFilterOptions[1], true);
                Assert.AreEqual(1, store.Saved.Count);

                // A restart: the registry starts empty and reloads from the store.
                ShowcaseControlBarStates.RemoveExcept(new HashSet<string>());
                var restored = ShowcaseControlBarStates.Get<GameSummaryGridControlBarAdapter>(id);

                Assert.AreNotSame(adapter, restored);
                Assert.AreEqual("halo", restored.SearchText);
                Assert.IsTrue(restored.IsProgressFilterSelected(restored.ProgressFilterOptions[1]));
                Assert.IsFalse(restored.IsProgressFilterSelected(restored.ProgressFilterOptions[0]));
            }
            finally
            {
                ShowcaseControlBarStates.Store = null;
            }
        }

        [TestMethod]
        public void Store_SavesRarityAndTrophyPicksAndRestoresThemAfterARestart()
        {
            var store = new FakeStore();
            var id = Guid.NewGuid().ToString();
            ShowcaseControlBarStates.Store = store;
            try
            {
                var adapter = ShowcaseControlBarStates.Get<CrossGameAchievementControlBarAdapter>(id);
                adapter.UpdateGames(new[] { new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Bloodborne", TrophyGoldTotal = 1 } });
                var rarity = (GridMultiSelectFilter)adapter.ControlBar.Items.Single(item => item.Id == ShowcaseControlBarItems.Rarity);
                var trophy = (GridMultiSelectFilter)adapter.ControlBar.Items.Single(item => item.Id == ShowcaseControlBarItems.Trophy);
                var rare = rarity.Options.ElementAt(2);
                var gold = trophy.Options.ElementAt(1);
                rarity.SetSelected(rare, true);
                trophy.SetSelected(gold, true);
                var saved = store.Saved.Values.Single();
                StringAssert.Contains(saved, "Rarities");
                StringAssert.Contains(saved, "Trophies");

                ShowcaseControlBarStates.RemoveExcept(new HashSet<string>());
                var restored = ShowcaseControlBarStates.Get<CrossGameAchievementControlBarAdapter>(id);
                restored.UpdateGames(new[] { new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Bloodborne", TrophyGoldTotal = 1 } });
                var restoredRarity = (GridMultiSelectFilter)restored.ControlBar.Items.Single(item => item.Id == ShowcaseControlBarItems.Rarity);
                var restoredTrophy = (GridMultiSelectFilter)restored.ControlBar.Items.Single(item => item.Id == ShowcaseControlBarItems.Trophy);

                Assert.IsTrue(restoredRarity.IsSelected(rare), saved);
                Assert.IsTrue(restoredTrophy.IsSelected(gold), saved);
            }
            finally
            {
                ShowcaseControlBarStates.Store = null;
            }
        }

        [TestMethod]
        public void Slots_ForOneInstance_ShareStateAndEachHearFilterChanges()
        {
            var id = Guid.NewGuid().ToString();
            var firstCalls = 0;
            var secondCalls = 0;
            var first = new ShowcaseControlBarSlot<CrossGameAchievementControlBarAdapter>(() => firstCalls++);
            var second = new ShowcaseControlBarSlot<CrossGameAchievementControlBarAdapter>(() => secondCalls++);

            Assert.IsTrue(first.Bind(id));
            first.Adapter.SearchText = "halo";
            Assert.IsTrue(second.Bind(id));
            Assert.IsFalse(second.Bind(id));

            Assert.AreEqual("halo", second.Adapter.SearchText);
            second.Adapter.SearchText = "portal";
            Assert.AreEqual(2, firstCalls);
            Assert.AreEqual(1, secondCalls);
            GC.KeepAlive(first);
            GC.KeepAlive(second);
        }

        [TestMethod]
        public void Entries_AreTheSearchThenTheItemsInBarOrderWithUniqueIds()
        {
            var achievements = new CrossGameAchievementControlBarAdapter();
            var games = new GameSummaryGridControlBarAdapter();

            CollectionAssert.AreEqual(
                new[]
                {
                    ShowcaseControlBarItems.Search,
                    ShowcaseControlBarItems.Platform,
                    ShowcaseControlBarItems.Game,
                    ShowcaseControlBarItems.Category,
                    ShowcaseControlBarItems.Tag,
                    ShowcaseControlBarItems.UnlockDate,
                    ShowcaseControlBarItems.Rarity,
                    ShowcaseControlBarItems.Trophy
                },
                achievements.Entries.Select(entry => entry.Id).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    ShowcaseControlBarItems.Search,
                    ShowcaseControlBarItems.Platform,
                    ShowcaseControlBarItems.Game,
                    ShowcaseControlBarItems.Progress,
                    ShowcaseControlBarItems.Activity
                },
                games.Entries.Select(entry => entry.Id).ToArray());
            Assert.AreSame(achievements.ControlBar.Search, achievements.Entries[0]);
            Assert.AreSame(achievements.ControlBar.Items[0], achievements.Entries[1]);
        }

        [TestMethod]
        public void UpdateShownBar_ShowsTheChosenItemsAsTheAdaptersOwnObjects()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };
            var slot = new ShowcaseControlBarSlot<CrossGameAchievementControlBarAdapter>(() => { });
            slot.Bind(instance.InstanceId);

            Assert.IsTrue(slot.UpdateShownBar(instance));
            Assert.IsNull(slot.ShownBar);

            ShowcaseWidgetOptions.ShowOnlyControlBarItems(
                instance,
                slot.Adapter.Entries.Select(entry => entry.Id),
                ShowcaseControlBarItems.Rarity);
            Assert.IsTrue(slot.UpdateShownBar(instance));

            Assert.IsNull(slot.ShownBar.Search);
            Assert.AreEqual(1, slot.ShownBar.Items.Count);
            Assert.AreSame(slot.Adapter.ControlBar.Items[5], slot.ShownBar.Items[0]);
        }

        [TestMethod]
        public void CategoryFilter_OffersOneGamesCategoriesAndNarrowsItsRows()
        {
            var adapter = new CrossGameAchievementControlBarAdapter();
            var gameA = Guid.NewGuid();
            var gameB = Guid.NewGuid();
            var story = new AchievementDisplayItem { PlayniteGameId = gameA, ApiName = "a1", CategoryLabel = "Story" };
            var dlc = new AchievementDisplayItem { PlayniteGameId = gameA, ApiName = "a2", CategoryLabel = "DLC" };
            var other = new AchievementDisplayItem { PlayniteGameId = gameB, ApiName = "b1", CategoryLabel = "Story" };
            var category = (GridMultiSelectFilter)adapter.ControlBar.Items
                .First(item => (item as GridMultiSelectFilter)?.Id == ShowcaseControlBarItems.Category);

            // Two games: no categories to offer, so no category narrows the rows.
            adapter.Apply(new[] { story, dlc, other });
            Assert.AreEqual(0, category.Options.Count());

            adapter.Apply(new[] { story, dlc });
            CollectionAssert.AreEquivalent(new[] { "Story", "DLC" }, category.Options.ToArray());

            category.SetSelected("DLC", true);
            CollectionAssert.AreEqual(new[] { dlc }, adapter.Apply(new[] { story, dlc }).ToArray());
            Assert.AreEqual(3, adapter.Apply(new[] { story, dlc, other }).Count, "a selection made for one game drops away with several");
        }

        [TestMethod]
        public void UpdateShownBar_KeepsTheBarWhileTheShownItemsAreUnchanged()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            var slot = new ShowcaseControlBarSlot<GameSummaryGridControlBarAdapter>(() => { });
            slot.Bind(instance.InstanceId);
            ShowcaseWidgetOptions.ShowOnlyControlBarItems(
                instance,
                slot.Adapter.Entries.Select(entry => entry.Id),
                ShowcaseControlBarItems.Search);
            slot.UpdateShownBar(instance);
            var bar = slot.ShownBar;

            Assert.IsFalse(slot.UpdateShownBar(instance));
            Assert.AreSame(bar, slot.ShownBar);
            Assert.AreSame(slot.Adapter.ControlBar.Search, bar.Search);
            Assert.AreEqual(0, bar.Items.Count);
        }

        [TestMethod]
        public void UpdateShownBar_FollowsTheFormerWholeBarOptionUntilItemsAreChosen()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            var slot = new ShowcaseControlBarSlot<GameSummaryGridControlBarAdapter>(() => { });
            slot.Bind(instance.InstanceId);

            slot.UpdateShownBar(instance, legacyVisible: true);

            Assert.IsNotNull(slot.ShownBar.Search);
            Assert.AreEqual(4, slot.ShownBar.Items.Count);
        }

        [TestMethod]
        public void GameFilter_ListsGamesByNameWithinTheScopeAndFiltersByGame()
        {
            var zelda = new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Zelda" };
            var halo = new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Halo" };
            var portal = new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Portal" };
            var adapter = new GameSummaryGridControlBarAdapter();
            adapter.UpdateOptions(new[] { zelda, halo, portal });

            var item = adapter.GameFilter.Item;
            CollectionAssert.AreEqual(
                new[] { "Halo", "Portal", "Zelda" },
                item.Options.Select(item.GetDisplayLabel).ToArray());

            adapter.GameFilter.SetScope(new[] { zelda.PlayniteGameId.Value, portal.PlayniteGameId.Value });
            CollectionAssert.AreEqual(
                new[] { "Portal", "Zelda" },
                item.Options.Select(item.GetDisplayLabel).ToArray());

            item.SetSelected(zelda.PlayniteGameId.Value.ToString("D"), true);
            Assert.AreEqual("Zelda", item.DisplayText);
            CollectionAssert.AreEqual(new[] { zelda }, adapter.Apply(new[] { zelda, halo, portal }).ToArray());
        }

        [TestMethod]
        public void GameFilter_SelectionRoundTripsThroughSavedState()
        {
            var gameId = Guid.NewGuid();
            var game = new GameSummaryItem { PlayniteGameId = gameId, GameName = "Celeste" };
            var row = new AchievementDisplayItem { PlayniteGameId = gameId, ApiName = "a", Unlocked = true };
            var other = new AchievementDisplayItem { PlayniteGameId = Guid.NewGuid(), ApiName = "b", Unlocked = true };
            var source = new CrossGameAchievementControlBarAdapter();
            source.UpdateGames(new[] { game });
            source.GameFilter.Item.SetSelected(gameId.ToString("D"), true);

            var restored = new CrossGameAchievementControlBarAdapter();
            restored.RestoreState(source.CaptureState());

            CollectionAssert.AreEqual(new[] { gameId.ToString("D") }, source.CaptureState().Games);
            CollectionAssert.AreEqual(new[] { row }, restored.Apply(new[] { row, other }).ToArray());
        }

        private static ControlBarFilterState XboxPick() => new ControlBarFilterState
        {
            Platforms = new Dictionary<string, List<string>> { ["Xbox"] = new List<string> { "Xbox" } }
        };

        private static GameSummaryItem[] SteamAndXbox() => new[]
        {
            new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Portal", ProviderKey = "Steam", Provider = "Steam", Platforms = new[] { "PC" } },
            new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Halo", ProviderKey = "Xbox", Provider = "Xbox", Platforms = new[] { "Xbox" } }
        };

        private static bool IsXboxSelected(IEnumerable<ProviderFilterGroup> groups) =>
            groups.Single(group => group.ProviderKey == "Xbox").SelectedPlatformNames.Contains("Xbox");

        [TestMethod]
        public void AchievementAdapter_RestoredPlatformSurvivesAnEmptyFirstGameList()
        {
            var adapter = new CrossGameAchievementControlBarAdapter();
            adapter.RestoreState(XboxPick());

            adapter.UpdateGames(Array.Empty<GameSummaryItem>());
            CollectionAssert.AreEqual(new[] { "Xbox" }, adapter.CaptureState().Platforms["Xbox"]);

            adapter.UpdateGames(SteamAndXbox());
            Assert.IsTrue(IsXboxSelected(adapter.ProviderFilterGroups));
        }

        [TestMethod]
        public void GameAdapter_RestoredPlatformSurvivesAnEmptyFirstGameList()
        {
            var adapter = new GameSummaryGridControlBarAdapter();
            adapter.RestoreState(XboxPick());

            adapter.UpdateOptions(Array.Empty<GameSummaryItem>());
            CollectionAssert.AreEqual(new[] { "Xbox" }, adapter.CaptureState().Platforms["Xbox"]);

            adapter.UpdateOptions(SteamAndXbox());
            Assert.IsTrue(IsXboxSelected(adapter.ProviderFilterGroups));
        }

        [TestMethod]
        public void GameAdapter_PendingPickOnAnAbsentProviderKeepsTheGroups()
        {
            var adapter = new GameSummaryGridControlBarAdapter();
            adapter.RestoreState(new ControlBarFilterState
            {
                Platforms = new Dictionary<string, List<string>> { ["PSN"] = new List<string> { "PS5" } }
            });
            adapter.UpdateOptions(SteamAndXbox());
            var groups = adapter.ProviderFilterGroups;

            adapter.UpdateOptions(SteamAndXbox());

            Assert.AreSame(groups, adapter.ProviderFilterGroups);
            CollectionAssert.AreEqual(new[] { "PS5" }, adapter.CaptureState().Platforms["PSN"]);
        }

        [TestMethod]
        public void TagFilter_OffersGlobalTagsThenThePickedGamesSharedTags()
        {
            var first = new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "First" };
            var second = new GameSummaryItem { PlayniteGameId = Guid.NewGuid(), GameName = "Second" };
            var firstRow = new AchievementDisplayItem
            {
                PlayniteGameId = first.PlayniteGameId,
                ApiName = "a",
                Unlocked = true,
                Tags = "first-only|shared-source"
            };
            var secondRow = new AchievementDisplayItem
            {
                PlayniteGameId = second.PlayniteGameId,
                ApiName = "b",
                Unlocked = true,
                Tags = "shared-source"
            };
            var adapter = new CrossGameAchievementControlBarAdapter();
            adapter.UpdateGames(new[] { first, second });
            adapter.Apply(new[] { firstRow, secondRow });

            CollectionAssert.AreEqual(
                AchievementTagRegistry.GetAssignableKeys(null).ToArray(),
                adapter.TagOptions.ToArray());

            adapter.GameFilter.Item.SetSelected(first.PlayniteGameId.Value.ToString("D"), true);
            CollectionAssert.Contains(adapter.TagOptions.ToArray(), "first-only");
            CollectionAssert.Contains(adapter.TagOptions.ToArray(), "shared-source");

            adapter.GameFilter.Item.SetSelected(second.PlayniteGameId.Value.ToString("D"), true);
            CollectionAssert.DoesNotContain(adapter.TagOptions.ToArray(), "first-only");
            CollectionAssert.Contains(adapter.TagOptions.ToArray(), "shared-source");
        }

        [TestMethod]
        public void TagFilter_KeepsRowsCarryingAPickedTagAndRoundTrips()
        {
            var gameId = Guid.NewGuid();
            var tagged = new AchievementDisplayItem
            {
                PlayniteGameId = gameId,
                ApiName = "a",
                Unlocked = true,
                Tags = AchievementTagHelper.Missable
            };
            var untagged = new AchievementDisplayItem { PlayniteGameId = gameId, ApiName = "b", Unlocked = true };
            var adapter = new CrossGameAchievementControlBarAdapter();
            var tagItem = adapter.ControlBar.Items.Single(item => item.Id == ShowcaseControlBarItems.Tag);

            ((GridMultiSelectFilter)tagItem).SetSelected(AchievementTagHelper.Missable, true);
            var restored = new CrossGameAchievementControlBarAdapter();
            restored.RestoreState(adapter.CaptureState());

            CollectionAssert.AreEqual(new[] { tagged }, adapter.Apply(new[] { tagged, untagged }).ToArray());
            CollectionAssert.AreEqual(new[] { tagged }, restored.Apply(new[] { tagged, untagged }).ToArray());
        }
    }
}
