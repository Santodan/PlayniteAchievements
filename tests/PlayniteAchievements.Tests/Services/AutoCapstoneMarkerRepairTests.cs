using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Covers restoring the auto capstone mark an editor save cleared.
    /// </summary>
    [TestClass]
    public class AutoCapstoneMarkerRepairTests
    {
        [TestMethod]
        public void Restore_UnmarkedInMainCategory_RestoredAsWholeGame()
        {
            var result = AutoCapstoneMarkerRepair.Restore(
                new[] { Definition("auto-capstone") },
                new[] { "custom:auto-capstone" },
                Filed("custom:auto-capstone", "MONOMOON"),
                "MONOMOON");

            Assert.IsNotNull(result);
            Assert.IsTrue(result[0].IsAutoCapstone);
            Assert.IsTrue(result[0].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Restore_UnmarkedInDefault_RestoredAsWholeGame()
        {
            var result = AutoCapstoneMarkerRepair.Restore(
                new[] { Definition("auto-capstone") },
                new[] { "custom:auto-capstone" },
                Filed("custom:auto-capstone", null),
                "Base Game");

            Assert.IsNotNull(result);
            Assert.IsTrue(result[0].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Restore_UnmarkedInOtherCategory_RestoredAsThatCategory()
        {
            var result = AutoCapstoneMarkerRepair.Restore(
                new[] { Definition("auto-capstone-2") },
                new[] { "custom:auto-capstone-2" },
                Filed("custom:auto-capstone-2", "DLC"),
                "Base Game");

            Assert.IsNotNull(result);
            Assert.IsTrue(result[0].IsAutoCapstone);
            Assert.IsFalse(result[0].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Restore_MainCategoryWithWholeGameAlreadyMarked_RestoredAsCategory()
        {
            var wholeGame = Definition("auto-capstone");
            wholeGame.IsAutoCapstone = true;
            wholeGame.IsWholeGameAutoCapstone = true;

            var result = AutoCapstoneMarkerRepair.Restore(
                new[] { wholeGame, Definition("auto-capstone-2") },
                new[] { "custom:auto-capstone", "custom:auto-capstone-2" },
                Filed("custom:auto-capstone-2", "Base Game"),
                "Base Game");

            Assert.IsNotNull(result);
            Assert.IsTrue(result[1].IsAutoCapstone);
            Assert.IsFalse(result[1].IsWholeGameAutoCapstone);
        }

        [TestMethod]
        public void Restore_UserAchievementNamedLikeIt_LeftAlone()
        {
            // Not nominated, and not platinum: an achievement the user authored and named.
            var own = Definition("auto-capstone");
            own.TrophyType = null;

            Assert.IsNull(AutoCapstoneMarkerRepair.Restore(
                new[] { own },
                new[] { "custom:auto-capstone" },
                Filed("custom:auto-capstone", null),
                null));
            Assert.IsNull(AutoCapstoneMarkerRepair.Restore(
                new[] { Definition("auto-capstone") },
                Array.Empty<string>(),
                Filed("custom:auto-capstone", null),
                null));
        }

        [TestMethod]
        public void Restore_OtherId_LeftAlone()
        {
            Assert.IsNull(AutoCapstoneMarkerRepair.Restore(
                new[] { Definition("auto-capstone-extra") },
                new[] { "custom:auto-capstone-extra" },
                Filed("custom:auto-capstone-extra", null),
                null));
        }

        [TestMethod]
        public void Restore_AlreadyMarked_ReturnsNull()
        {
            var marked = Definition("auto-capstone");
            marked.IsAutoCapstone = true;

            Assert.IsNull(AutoCapstoneMarkerRepair.Restore(
                new[] { marked },
                new[] { "custom:auto-capstone" },
                Filed("custom:auto-capstone", null),
                null));
        }

        private static CustomAchievementDefinition Definition(string id)
        {
            return new CustomAchievementDefinition
            {
                Id = id,
                DisplayName = "Game",
                TrophyType = "platinum",
                Category = "Default"
            };
        }

        private static Func<string, string> Filed(string apiName, string category)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [apiName] = category };
            return key => map.TryGetValue(key, out var value) ? value : null;
        }
    }
}
