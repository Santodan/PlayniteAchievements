using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers returning individual achievements to the provider's order from within a stored
    /// custom order, which is what the editor's Revert does to an achievement's position.
    /// </summary>
    [TestClass]
    public class AchievementOrderRestoreTests
    {
        private static List<KeyValuePair<string, int>> Current(params string[] apiNames)
        {
            // Provider index is derived from the name suffix so each case reads explicitly.
            var providerOrder = new[] { "alpha", "beta", "gamma", "delta" };
            return apiNames
                .Select(name => new KeyValuePair<string, int>(name, System.Array.IndexOf(providerOrder, name)))
                .ToList();
        }

        [TestMethod]
        public void RestoreDefaultPositions_NoStoredOrder_ReturnsNull()
        {
            Assert.IsNull(AchievementOrderHelper.RestoreDefaultPositions(null, new[] { "alpha" }));
            Assert.IsNull(AchievementOrderHelper.RestoreDefaultPositions(
                new List<KeyValuePair<string, int>>(),
                new[] { "alpha" }));
        }

        [TestMethod]
        public void RestoreDefaultPositions_ReseatsTheRevertedRowByProviderIndex()
        {
            // The user dragged delta to the front; reverting it puts it back after gamma.
            var restored = AchievementOrderHelper.RestoreDefaultPositions(
                Current("delta", "alpha", "beta", "gamma"),
                new[] { "delta" });

            Assert.IsNull(restored, "The result is the provider's own order, so the override is dropped.");
        }

        [TestMethod]
        public void RestoreDefaultPositions_KeepsTheRestOfTheCustomOrder()
        {
            // beta and alpha stay swapped; only delta is reverted.
            var restored = AchievementOrderHelper.RestoreDefaultPositions(
                Current("delta", "beta", "alpha", "gamma"),
                new[] { "delta" });

            CollectionAssert.AreEqual(
                new[] { "beta", "alpha", "gamma", "delta" },
                restored.ToArray());
        }

        [TestMethod]
        public void RestoreDefaultPositions_SeveralRevertedRowsLandInProviderOrder()
        {
            var restored = AchievementOrderHelper.RestoreDefaultPositions(
                Current("delta", "gamma", "beta", "alpha"),
                new[] { "alpha", "gamma" });

            CollectionAssert.AreEqual(
                new[] { "alpha", "gamma", "delta", "beta" },
                restored.ToArray());
        }

        [TestMethod]
        public void RestoreDefaultPositions_MatchesApiNamesCaseInsensitively()
        {
            var restored = AchievementOrderHelper.RestoreDefaultPositions(
                Current("delta", "beta", "alpha", "gamma"),
                new[] { "DELTA" });

            CollectionAssert.AreEqual(
                new[] { "beta", "alpha", "gamma", "delta" },
                restored.ToArray());
        }

        [TestMethod]
        public void RestoreDefaultPositions_RevertingEveryRow_DropsTheOverride()
        {
            var restored = AchievementOrderHelper.RestoreDefaultPositions(
                Current("delta", "gamma", "beta", "alpha"),
                new[] { "alpha", "beta", "gamma", "delta" });

            Assert.IsNull(restored);
        }

        [TestMethod]
        public void RestoreDefaultPositions_RevertingNothing_LeavesTheOrderAlone()
        {
            var restored = AchievementOrderHelper.RestoreDefaultPositions(
                Current("delta", "beta", "alpha", "gamma"),
                new string[0]);

            CollectionAssert.AreEqual(
                new[] { "delta", "beta", "alpha", "gamma" },
                restored.ToArray());
        }
    }
}
