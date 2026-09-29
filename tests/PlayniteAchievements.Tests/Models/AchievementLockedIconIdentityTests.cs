using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using System.IO;

namespace PlayniteAchievements.Models.Achievements.Tests
{
    /// <summary>
    /// Covers whether an achievement has a locked icon of its own, which decides between showing
    /// that icon and graying the unlocked one.
    ///
    /// The answer comes from the cache's file naming rather than from comparing the two paths.
    /// Comparing them gets both of the cases here wrong: one image chosen for both slots is one
    /// image in two files and reads as no locked icon, and a game with separate locked icons off
    /// stores the unlocked path in both slots, so a custom unlocked icon leaves a copy behind
    /// pointing at the art it replaced.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class AchievementLockedIconIdentityTests
    {
        private string _tempDir;
        private string _providerUnlocked;
        private string _providerLocked;
        private string _customUnlocked;
        private string _customLocked;

        [TestInitialize]
        public void Setup()
        {
            // Under an icon_cache segment, so these read as the plugin's own files.
            _tempDir = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                Path.GetRandomFileName(),
                "icon_cache",
                "11111111-1111-1111-1111-111111111111");

            _providerUnlocked = WriteFile(Path.Combine(_tempDir, "original", "ACH.jpg"));
            _providerLocked = WriteFile(Path.Combine(_tempDir, "original", "ACH.locked.jpg"));
            _customUnlocked = WriteFile(Path.Combine(_tempDir, "custom", "ACH.jpg"));
            _customLocked = WriteFile(Path.Combine(_tempDir, "custom", "ACH.locked.jpg"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                var root = Directory.GetParent(Directory.GetParent(_tempDir).FullName).FullName;
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }

        [TestMethod]
        public void TheMirroredProviderPathIsNotALockedIconOfItsOwn()
        {
            // How a game with separate locked icons off is stored: one icon, named once, in both
            // slots. Nothing was chosen for the locked state, so it is grayed.
            Assert.IsFalse(
                AchievementIconResolver.HasExplicitLockedIcon(_providerUnlocked, _providerUnlocked));
        }

        [TestMethod]
        public void ACustomUnlockedIconLeavesNoLockedIconBehindIt()
        {
            // The reported bug. The locked slot still holds the provider path the custom icon
            // replaced, which compares as different art and showed the original back in colour.
            Assert.IsFalse(
                AchievementIconResolver.HasExplicitLockedIcon(_providerUnlocked, _customUnlocked),
                "The mirrored provider path is not locked art, so the custom icon is what gets " +
                "grayed for the locked state.");

            var locked = AchievementIconResolver.GetLockedDisplayIcon(_customUnlocked, _providerUnlocked);
            StringAssert.Contains(locked, "gray:");
            StringAssert.Contains(locked, Path.GetFileName(_customUnlocked));
        }

        [TestMethod]
        public void TheSameImageChosenForBothSlotsStillShowsInColour()
        {
            // Each slot is materialized into its own managed file, so the two are one image under
            // two names. A locked icon somebody picked shows as they picked it.
            Assert.IsTrue(
                AchievementIconResolver.HasExplicitLockedIcon(_customLocked, _customUnlocked));

            var locked = AchievementIconResolver.GetLockedDisplayIcon(_customUnlocked, _customLocked);
            Assert.IsFalse(locked.Contains("gray:"), "A chosen locked icon is not grayed.");
            StringAssert.Contains(locked, Path.GetFileName(_customLocked));
        }

        [TestMethod]
        public void ProviderSuppliedLockedArtIsStillItsOwn()
        {
            Assert.IsTrue(
                AchievementIconResolver.HasExplicitLockedIcon(_providerLocked, _providerUnlocked));
            Assert.IsTrue(
                AchievementIconResolver.HasExplicitLockedIcon(_providerLocked, _customUnlocked),
                "Separate locked art the provider gave is not derivable from the unlocked icon, " +
                "so a custom unlocked icon does not displace it.");
        }

        [TestMethod]
        public void APathFromOutsideTheCacheKeepsTheOlderComparison()
        {
            // Nothing of ours named it, so there is no name to read: a hand-typed locked path
            // counts while it differs from the unlocked one, as it always has.
            var outside = WriteFile(Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                Path.GetRandomFileName() + ".jpg"));

            try
            {
                Assert.IsTrue(AchievementIconResolver.HasExplicitLockedIcon(outside, _providerUnlocked));
                Assert.IsFalse(AchievementIconResolver.HasExplicitLockedIcon(outside, outside));
            }
            finally
            {
                try
                {
                    File.Delete(outside);
                }
                catch
                {
                }
            }
        }

        private static string WriteFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }
    }
}
