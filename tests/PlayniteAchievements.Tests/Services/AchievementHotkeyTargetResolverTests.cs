using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class AchievementHotkeyTargetResolverTests
    {
        [TestMethod]
        public void Resolve_PrefersRunningGameOverSelectedGame()
        {
            var runningId = Guid.NewGuid();
            var selectedId = Guid.NewGuid();

            var result = AchievementHotkeyTargetResolver.Resolve(
                new[] { Game(runningId) },
                new[] { Game(selectedId) },
                Array.Empty<Guid>());

            Assert.IsTrue(result.HasTarget);
            Assert.AreEqual(runningId, result.GameId);
        }

        [TestMethod]
        public void Resolve_MultipleRunningGamesUsesMostRecentlyStarted()
        {
            var firstId = Guid.NewGuid();
            var latestId = Guid.NewGuid();

            var result = AchievementHotkeyTargetResolver.Resolve(
                new[] { Game(firstId), Game(latestId) },
                Array.Empty<Game>(),
                new[] { latestId, firstId });

            Assert.IsTrue(result.HasTarget);
            Assert.AreEqual(latestId, result.GameId);
        }

        [TestMethod]
        public void ResolveRunningGame_ReturnsSingleRunningGame()
        {
            var runningId = Guid.NewGuid();

            var result = AchievementHotkeyTargetResolver.ResolveRunningGame(
                new[] { Game(runningId) },
                Array.Empty<Guid>());

            Assert.IsTrue(result.HasTarget);
            Assert.AreEqual(runningId, result.GameId);
        }

        [TestMethod]
        public void ResolveRunningGame_MultipleRunningGamesUsesPriority()
        {
            var firstId = Guid.NewGuid();
            var latestId = Guid.NewGuid();

            var result = AchievementHotkeyTargetResolver.ResolveRunningGame(
                new[] { Game(firstId), Game(latestId) },
                new[] { latestId, firstId });

            Assert.IsTrue(result.HasTarget);
            Assert.AreEqual(latestId, result.GameId);
        }

        [TestMethod]
        public void ResolveRunningGame_NoRunningGameReturnsNoTarget()
        {
            var result = AchievementHotkeyTargetResolver.ResolveRunningGame(
                Array.Empty<Game>(),
                new[] { Guid.NewGuid() });

            Assert.IsFalse(result.HasTarget);
            Assert.AreEqual(Guid.Empty, result.GameId);
        }

        [TestMethod]
        public void Resolve_FallsBackToSingleSelectedGame()
        {
            var selectedId = Guid.NewGuid();

            var result = AchievementHotkeyTargetResolver.Resolve(
                Array.Empty<Game>(),
                new[] { Game(selectedId) },
                Array.Empty<Guid>());

            Assert.IsTrue(result.HasTarget);
            Assert.AreEqual(selectedId, result.GameId);
        }

        [TestMethod]
        public void Resolve_NoTargetWhenNoRunningGameAndSelectionIsNotSingle()
        {
            var result = AchievementHotkeyTargetResolver.Resolve(
                Array.Empty<Game>(),
                new List<Game> { Game(Guid.NewGuid()), Game(Guid.NewGuid()) },
                Array.Empty<Guid>());

            Assert.IsFalse(result.HasTarget);
            Assert.AreEqual(Guid.Empty, result.GameId);
        }

        private static Game Game(Guid id)
        {
            return new Game
            {
                Id = id,
                Name = id.ToString()
            };
        }
    }
}
