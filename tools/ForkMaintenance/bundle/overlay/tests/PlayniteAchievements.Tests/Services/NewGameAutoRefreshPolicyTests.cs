using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Services.Refresh;
using System;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class NewGameAutoRefreshPolicyTests
    {
        [TestMethod]
        public void ShouldDefer_TemporaryManualGame_ReturnsTrue()
        {
            var game = new Game
            {
                Name = "New Game",
                PluginId = Guid.Empty
            };

            Assert.IsTrue(NewGameAutoRefreshPolicy.ShouldDefer(game));
        }

        [TestMethod]
        public void ShouldDefer_NamedManualGame_ReturnsFalse()
        {
            var game = new Game
            {
                Name = "Tiny Tina's Wonderlands",
                PluginId = Guid.Empty
            };

            Assert.IsFalse(NewGameAutoRefreshPolicy.ShouldDefer(game));
        }

        [TestMethod]
        public void ShouldDefer_ImportedGameNamedNewGame_ReturnsFalse()
        {
            var game = new Game
            {
                Name = "New Game",
                PluginId = Guid.NewGuid()
            };

            Assert.IsFalse(NewGameAutoRefreshPolicy.ShouldDefer(game));
        }
    }
}
