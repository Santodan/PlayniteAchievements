using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Covers how moving an auto capstone changes what it stands for.
    /// </summary>
    [TestClass]
    public class AutoCapstoneScopeTests
    {
        private const string CapstoneApiName = "custom:auto-capstone";

        [TestMethod]
        public void Rescope_MovedFromDefaultToNamed_StandsForThatCategory()
        {
            var result = AutoCapstoneScope.Rescope(
                new[] { Capstone(wholeGame: true) },
                Categories(null),
                Categories("Base Game"));

            Assert.IsNotNull(result);
            Assert.IsFalse(result[0].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Rescope_MovedBackToDefault_StandsForTheWholeGame()
        {
            var result = AutoCapstoneScope.Rescope(
                new[] { Capstone(wholeGame: false) },
                Categories("Base Game"),
                Categories(null));

            Assert.IsNotNull(result);
            Assert.IsTrue(result[0].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Rescope_MovedToTheDefaultLabelSpelledOut_StandsForTheWholeGame()
        {
            var result = AutoCapstoneScope.Rescope(
                new[] { Capstone(wholeGame: false) },
                Categories("Base Game"),
                Categories("Default"));

            Assert.IsNotNull(result);
            Assert.IsTrue(result[0].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Rescope_MovedBetweenNamedCategories_StandsForTheNewOne()
        {
            // Filed with the main game while standing for the whole game, then moved by hand.
            var result = AutoCapstoneScope.Rescope(
                new[] { Capstone(wholeGame: true) },
                Categories("Main"),
                Categories("Base Game"));

            Assert.IsNotNull(result);
            Assert.IsFalse(result[0].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Rescope_CategoryUnchanged_LeavesTheScope()
        {
            Assert.IsNull(AutoCapstoneScope.Rescope(
                new[] { Capstone(wholeGame: true) },
                Categories("Main"),
                Categories("Main")));
        }

        [TestMethod]
        public void Rescope_AlreadyMatchingScope_ReportsNoChange()
        {
            Assert.IsNull(AutoCapstoneScope.Rescope(
                new[] { Capstone(wholeGame: false) },
                Categories("Main"),
                Categories("Base Game")));
        }

        [TestMethod]
        public void Rescope_LeavesAuthoredAchievementsThatAreNotAutoCapstonesAlone()
        {
            var plain = new CustomAchievementDefinition { Id = "auto-capstone", IsAutoCapstone = false };

            Assert.IsNull(AutoCapstoneScope.Rescope(
                new[] { plain },
                Categories(null),
                Categories("Base Game")));
        }

        [TestMethod]
        public void Rescope_DoesNotChangeTheDefinitionsItWasGiven()
        {
            var original = Capstone(wholeGame: true);

            AutoCapstoneScope.Rescope(new[] { original }, Categories(null), Categories("Base Game"));

            Assert.IsTrue(original.IsWholeGameAutoCapstone);
        }

        private static CustomAchievementDefinition Capstone(bool wholeGame)
        {
            return new CustomAchievementDefinition
            {
                Id = "auto-capstone",
                DisplayName = "Capstone",
                IsAutoCapstone = true,
                IsWholeGameAutoCapstone = wholeGame
            };
        }

        private static Func<string, string> Categories(string capstoneCategory)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CapstoneApiName] = capstoneCategory
            };
            return apiName => map.TryGetValue(apiName, out var value) ? value : null;
        }
    }
}
