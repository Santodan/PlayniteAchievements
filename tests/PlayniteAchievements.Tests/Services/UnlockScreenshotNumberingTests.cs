using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers the ordering basis for unlock-screenshot numbering: an achievement's number is its
    /// 1-based position in the game's provider/custom sort order, produced by
    /// <see cref="AchievementOrderHelper.ApplyOrder"/> (custom order first, source/provider order
    /// as fallback).
    /// </summary>
    [TestClass]
    public class UnlockScreenshotNumberingTests
    {
        [TestMethod]
        public void ApplyOrder_NoCustomOrder_KeepsProviderOrder()
        {
            var source = new List<string> { "a", "b", "c", "d" };

            var ordered = AchievementOrderHelper.ApplyOrder(source, s => s, new List<string>());

            CollectionAssert.AreEqual(new List<string> { "a", "b", "c", "d" }, ordered);
        }

        [TestMethod]
        public void ApplyOrder_CustomOrder_MovesListedFirstThenProviderOrder()
        {
            var source = new List<string> { "a", "b", "c", "d" };

            var ordered = AchievementOrderHelper.ApplyOrder(source, s => s, new List<string> { "c", "a" });

            // Custom-ordered entries first (in custom order), remaining in source order.
            CollectionAssert.AreEqual(new List<string> { "c", "a", "b", "d" }, ordered);

            // The resulting 1-based index map is what drives the NNN prefix.
            var numberByKey = new Dictionary<string, int>();
            for (var i = 0; i < ordered.Count; i++)
            {
                numberByKey[ordered[i]] = i + 1;
            }

            Assert.AreEqual(1, numberByKey["c"]);
            Assert.AreEqual(2, numberByKey["a"]);
            Assert.AreEqual(3, numberByKey["b"]);
            Assert.AreEqual(4, numberByKey["d"]);
        }

        [TestMethod]
        public void BuildRelativePath_UsesGameNameFolderWithoutProviderSuffix()
        {
            var relative = UnlockScreenshotService.BuildRelativePath(
                providerKey: "Steam",
                gameName: "Game: Name",
                achievementName: "First Win",
                number: 7,
                total: 123);

            Assert.AreEqual("Game_ Name", relative.Folder);
            Assert.AreEqual("007_First Win.png", relative.FileName);
            Assert.IsFalse(relative.Folder.Contains("["));
            Assert.IsFalse(relative.Folder.Contains("Steam"));
        }

        [TestMethod]
        public void BuildRelativePath_AppendsVariantSuffixBeforeExtension()
        {
            var relative = UnlockScreenshotService.BuildRelativePath(
                providerKey: "Steam",
                gameName: "Game",
                achievementName: "First Win",
                number: 7,
                total: 123,
                variantSuffix: "framed");

            Assert.AreEqual("007_First Win_framed.png", relative.FileName);
        }

        [TestMethod]
        public void BuildRelativePath_HonorsCustomExtension()
        {
            var relative = UnlockScreenshotService.BuildRelativePath(
                providerKey: "Steam",
                gameName: "Game",
                achievementName: "First Win",
                number: 7,
                total: 123,
                variantSuffix: null,
                extension: ".mp4");

            Assert.AreEqual("007_First Win.mp4", relative.FileName);
        }

        [TestMethod]
        public void VariantSuffix_MapsSingleFlagsOnly()
        {
            Assert.AreEqual("clean", UnlockScreenshotService.VariantSuffix(ScreenshotVariants.Clean));
            Assert.AreEqual("toast", UnlockScreenshotService.VariantSuffix(ScreenshotVariants.WithToast));
            Assert.AreEqual("framed", UnlockScreenshotService.VariantSuffix(ScreenshotVariants.Framed));
            Assert.IsNull(UnlockScreenshotService.VariantSuffix(ScreenshotVariants.None));
            Assert.IsNull(UnlockScreenshotService.VariantSuffix(ScreenshotVariants.Clean | ScreenshotVariants.Framed));
        }
    }
}
