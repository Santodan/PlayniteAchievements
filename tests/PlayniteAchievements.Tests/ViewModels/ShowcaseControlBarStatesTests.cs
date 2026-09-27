using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels;
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
    }
}
