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
