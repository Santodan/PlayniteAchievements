using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Services.Achievements;
using System;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class AutoCapstoneTemplateArtTests
    {
        [TestCleanup]
        public void TearDown()
        {
            AutoCapstoneTemplate.MetadataArtAccessor = null;
        }

        [TestMethod]
        public void MetadataArt_ComesBeforeTheGamesOwnArt()
        {
            var game = new Game("Test") { Id = Guid.NewGuid(), Icon = "icon.png" };
            AutoCapstoneTemplate.MetadataArtAccessor = id => id == game.Id ? "https://psn/platinum.png" : null;

            Assert.AreEqual("https://psn/platinum.png", AutoCapstoneTemplate.ResolveIconSource(game, null));
        }

        [TestMethod]
        public void MissingMetadataArt_FallsThroughToTheGamesArt()
        {
            var game = new Game("Test") { Id = Guid.NewGuid() };
            AutoCapstoneTemplate.MetadataArtAccessor = _ => null;

            Assert.AreNotEqual("https://psn/platinum.png", AutoCapstoneTemplate.ResolveIconSource(game, null));
        }
    }
}
