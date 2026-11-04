using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Covers how the settings' auto capstone tags are read: only global, non-derived keys count,
    /// and the applied signature tells a save or startup whether a sweep is owed.
    /// </summary>
    [TestClass]
    public class AutoCapstoneTagRuleTests
    {
        private IReadOnlyList<AchievementTagDefinition> _previousGlobal;

        [TestInitialize]
        public void Setup()
        {
            _previousGlobal = AchievementTagRegistry.Global;
            AchievementTagRegistry.SetGlobal(new[]
            {
                new AchievementTagDefinition { Key = "Completion" },
                new AchievementTagDefinition { Key = "u-11111111", Name = "Mine" },
                new AchievementTagDefinition { Key = "Softcore" }
            });
        }

        [TestCleanup]
        public void Cleanup()
        {
            AchievementTagRegistry.SetGlobal(_previousGlobal);
        }

        [TestMethod]
        public void Resolve_KeepsOnlyGlobalNonDerivedKeysPerKind()
        {
            var settings = new PersistedSettings
            {
                AutoCapstoneGameTags = "Completion|u-11111111|u-deadbeef|Softcore",
                AutoCapstoneCategoryTags = "Missable"
            };

            CollectionAssert.AreEquivalent(
                new[] { "Completion", "u-11111111" },
                new List<string>(AchievementTagHelper.GetComponents(AutoCapstoneTagRule.Resolve(settings, wholeGame: true))));
            Assert.IsNull(AutoCapstoneTagRule.Resolve(settings, wholeGame: false));
        }

        [TestMethod]
        public void Resolve_NothingConfigured_IsNull()
        {
            Assert.IsNull(AutoCapstoneTagRule.Resolve(new PersistedSettings(), wholeGame: true));
            Assert.IsNull(AutoCapstoneTagRule.Resolve(null, wholeGame: false));
        }

        [TestMethod]
        public void Signature_SameForNullAndEmptyLists()
        {
            var untouched = AutoCapstoneTagRule.Signature(new PersistedSettings());
            var emptied = AutoCapstoneTagRule.Signature(new PersistedSettings
            {
                AutoCapstoneGameTags = string.Empty,
                AutoCapstoneCategoryTags = "u-deadbeef"
            });

            Assert.AreEqual(AutoCapstoneTagRule.EmptySignature, untouched);
            Assert.AreEqual(untouched, emptied);
        }

        [TestMethod]
        public void NeedsApply_FollowsTheRecordedSignature()
        {
            var settings = new PersistedSettings();
            Assert.IsFalse(AutoCapstoneTagRule.NeedsApply(settings), "a fresh install has nothing to apply");

            settings.AutoCapstoneGameTags = "Completion";
            Assert.IsTrue(AutoCapstoneTagRule.NeedsApply(settings));

            settings.AutoCapstoneAppliedTags = AutoCapstoneTagRule.Signature(settings);
            Assert.IsFalse(AutoCapstoneTagRule.NeedsApply(settings));

            settings.AutoCapstoneGameTags = null;
            Assert.IsTrue(AutoCapstoneTagRule.NeedsApply(settings), "clearing the list is a change to apply");
        }
    }
}
