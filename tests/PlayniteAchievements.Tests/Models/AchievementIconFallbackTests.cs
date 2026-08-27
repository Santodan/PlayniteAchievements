using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Models.Achievements.Tests
{
    /// <summary>
    /// Covers how the user's custom locked and hidden images take effect in
    /// <see cref="AchievementIconResolver"/>. They are cover art for the masked state only: the
    /// real-artwork paths never consult them, so revealing a cover shows the achievement's own icon.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class AchievementIconFallbackTests
    {
        private const string BuiltInPlaceholder =
            "pack://application:,,,/PlayniteAchievements;component/Resources/HiddenAchIcon.png";

        private string _tempDir;
        private string _customLocked;
        private string _customHidden;
        private string _providerUnlocked;
        private string _providerLocked;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(
                Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            _customLocked = WritePng("custom-locked.png", Colors.Red);
            _customHidden = WritePng("custom-hidden.png", Colors.Blue);
            _providerUnlocked = WritePng("provider-unlocked.png", Colors.Green);
            _providerLocked = WritePng("provider-locked.png", Colors.Gray);

            AchievementIconResolver.LockedFallbackPathAccessor = null;
            AchievementIconResolver.HiddenFallbackPathAccessor = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = null;
            AchievementIconResolver.HiddenFallbackPathAccessor = null;

            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        // --- Unset: today's behaviour must be untouched ---

        [TestMethod]
        public void WithNoFallbackConfigured_MaskedStatesUseTheBuiltInPlaceholder()
        {
            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetLockedFallbackIcon());
            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetHiddenFallbackIcon());
        }

        [TestMethod]
        public void WithNoFallbackConfigured_LockedIconStillGraysTheUnlockedIcon()
        {
            var resolved = AchievementIconResolver.GetLockedDisplayIcon(_providerUnlocked, null);

            StringAssert.Contains(resolved, "gray:");
            StringAssert.Contains(resolved, "provider-unlocked.png");
        }

        [TestMethod]
        public void WithNoFallbackConfigured_NoIconAtAllUsesTheBuiltInPlaceholder()
        {
            Assert.AreEqual(
                BuiltInPlaceholder,
                AchievementIconResolver.GetLockedDisplayIcon(null, null));
        }

        // --- Configured: the cover image stays out of the real-artwork path ---

        [TestMethod]
        public void ConfiguredLockedCover_LeavesTheGrayscaleUnlockedIconAlone()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;

            var resolved = AchievementIconResolver.GetLockedDisplayIcon(_providerUnlocked, null);

            StringAssert.Contains(resolved, "gray:");
            StringAssert.Contains(resolved, "provider-unlocked.png");
            Assert.IsFalse(
                resolved.Contains("custom-locked.png"),
                "The cover image belongs to the masked state, not the revealed artwork.");
        }

        [TestMethod]
        public void ConfiguredLockedCover_LeavesTheNoIconAtAllPlaceholderAlone()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;

            var resolved = AchievementIconResolver.GetLockedDisplayIcon(null, null);

            Assert.AreEqual(BuiltInPlaceholder, resolved);
        }

        [TestMethod]
        public void ConfiguredLockedFallback_DoesNotOverrideAProviderSuppliedLockedIcon()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;

            var resolved = AchievementIconResolver.GetLockedDisplayIcon(
                _providerUnlocked, _providerLocked);

            StringAssert.Contains(resolved, "provider-locked.png");
            Assert.IsFalse(resolved.Contains("custom-locked.png"));
        }

        [TestMethod]
        public void ConfiguredFallbacks_ResolveToTheirOwnSlots()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;
            AchievementIconResolver.HiddenFallbackPathAccessor = () => _customHidden;

            StringAssert.Contains(AchievementIconResolver.GetLockedFallbackIcon(), "custom-locked.png");
            StringAssert.Contains(AchievementIconResolver.GetHiddenFallbackIcon(), "custom-hidden.png");
        }

        [TestMethod]
        public void ConfiguredFallback_CarriesACacheBustTokenSoOverwritesReRead()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;

            var before = AchievementIconResolver.GetLockedFallbackIcon();
            StringAssert.StartsWith(before, "cachebust|");

            // The managed slot filename is fixed, so replacing the image overwrites the same
            // path; the token must change or the previously decoded bitmap keeps being served.
            File.SetLastWriteTimeUtc(_customLocked, DateTime.UtcNow.AddMinutes(5));
            var after = AchievementIconResolver.GetLockedFallbackIcon();

            Assert.AreNotEqual(before, after);
        }

        // --- ResolveRowDisplayIcon: the shared masked-vs-real decision ---

        [TestMethod]
        public void RowIcon_HiddenMaskedUsesTheHiddenCover()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;
            AchievementIconResolver.HiddenFallbackPathAccessor = () => _customHidden;

            // Both masks active: hidden is the more spoiler-sensitive state and must win.
            var resolved = AchievementIconResolver.ResolveRowDisplayIcon(
                isIconHidden: true,
                isLockedIconHidden: true,
                unlocked: false,
                unlockedIconPath: _providerUnlocked,
                lockedIconPath: _providerLocked);

            StringAssert.Contains(resolved, "custom-hidden.png");
        }

        [TestMethod]
        public void RowIcon_LockedMaskedUsesTheLockedCover()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;
            AchievementIconResolver.HiddenFallbackPathAccessor = () => _customHidden;

            var resolved = AchievementIconResolver.ResolveRowDisplayIcon(
                isIconHidden: false,
                isLockedIconHidden: true,
                unlocked: false,
                unlockedIconPath: _providerUnlocked,
                lockedIconPath: null);

            StringAssert.Contains(resolved, "custom-locked.png");
        }

        [TestMethod]
        public void RowIcon_RevealedLockedRowShowsItsOwnArtwork()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;
            AchievementIconResolver.HiddenFallbackPathAccessor = () => _customHidden;

            var resolved = AchievementIconResolver.ResolveRowDisplayIcon(
                isIconHidden: false,
                isLockedIconHidden: false,
                unlocked: false,
                unlockedIconPath: _providerUnlocked,
                lockedIconPath: null);

            StringAssert.Contains(resolved, "gray:");
            StringAssert.Contains(resolved, "provider-unlocked.png");
            Assert.IsFalse(resolved.Contains("custom-locked.png"));
        }

        [TestMethod]
        public void RowIcon_UnlockedRowIgnoresBothCovers()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;
            AchievementIconResolver.HiddenFallbackPathAccessor = () => _customHidden;

            var resolved = AchievementIconResolver.ResolveRowDisplayIcon(
                isIconHidden: false,
                isLockedIconHidden: false,
                unlocked: true,
                unlockedIconPath: _providerUnlocked,
                lockedIconPath: _providerLocked);

            StringAssert.Contains(resolved, "provider-unlocked.png");
            Assert.IsFalse(resolved.Contains("gray:"));
        }

        [TestMethod]
        public void RowIcon_UnmaskedRowWithNoArtworkUsesTheBuiltInPlaceholder()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;

            var resolved = AchievementIconResolver.ResolveRowDisplayIcon(
                isIconHidden: false,
                isLockedIconHidden: false,
                unlocked: false,
                unlockedIconPath: null,
                lockedIconPath: null);

            Assert.AreEqual(BuiltInPlaceholder, resolved);
        }

        // --- Robustness ---

        [TestMethod]
        public void MissingConfiguredFile_FallsBackToTheBuiltInPlaceholder()
        {
            AchievementIconResolver.LockedFallbackPathAccessor =
                () => Path.Combine(_tempDir, "deleted-by-the-user.png");

            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetLockedFallbackIcon());
        }

        [TestMethod]
        public void BlankConfiguredPath_FallsBackToTheBuiltInPlaceholder()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => "   ";
            AchievementIconResolver.HiddenFallbackPathAccessor = () => null;

            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetLockedFallbackIcon());
            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetHiddenFallbackIcon());
        }

        [TestMethod]
        public void ThrowingAccessor_FallsBackToTheBuiltInPlaceholder()
        {
            AchievementIconResolver.LockedFallbackPathAccessor =
                () => throw new InvalidOperationException("settings not ready");

            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetLockedFallbackIcon());
        }

        // --- Unlocked and editor paths must be unaffected ---

        [TestMethod]
        public void ConfiguredFallbacks_DoNotAffectTheUnlockedOrEditorPaths()
        {
            AchievementIconResolver.LockedFallbackPathAccessor = () => _customLocked;
            AchievementIconResolver.HiddenFallbackPathAccessor = () => _customHidden;

            // GetDefaultIcon is the "nothing selected" thumbnail in the icon editors.
            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetDefaultIcon());
            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetUnlockedDisplayIcon(null));
            Assert.AreEqual(BuiltInPlaceholder, AchievementIconResolver.GetLegacyCompatibleIcon(null));

            var unlocked = AchievementIconResolver.GetUnlockedDisplayIcon(_providerUnlocked);
            StringAssert.Contains(unlocked, "provider-unlocked.png");
            Assert.IsFalse(unlocked.Contains("custom-locked.png"));
        }

        private string WritePng(string fileName, Color color)
        {
            var path = Path.Combine(_tempDir, fileName);
            var pixels = new byte[]
            {
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A
            };
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                encoder.Save(stream);
            }

            return path;
        }
    }
}
