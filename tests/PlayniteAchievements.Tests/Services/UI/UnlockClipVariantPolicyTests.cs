using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class UnlockClipVariantPolicyTests
    {
        private static PersistedSettings MakeSettings(bool clean = true, bool withToast = true, bool framed = true)
        {
            return new PersistedSettings
            {
                EnableUnlockRecordings = true,
                UnlockRecordingClean = clean,
                UnlockRecordingWithToast = withToast,
                UnlockRecordingFramed = framed,
            };
        }

        private static ScreenshotVariants Resolve(
            PersistedSettings settings, RarityTier rarity = RarityTier.Common, bool isCompletion = false) =>
            UnlockClipVariantPolicy.Resolve(rarity, isCompletion, "Steam", settings);

        [TestMethod]
        public void Defaults_ProduceOnlyTheNotificationClip()
        {
            var settings = new PersistedSettings { EnableUnlockRecordings = true };
            Assert.AreEqual(ScreenshotVariants.WithToast, Resolve(settings));
            Assert.AreEqual(5, settings.UnlockRecordingFramedSeconds);
        }

        [TestMethod]
        public void RecordingsOff_ProducesNothing()
        {
            var settings = MakeSettings();
            settings.EnableUnlockRecordings = false;
            Assert.AreEqual(ScreenshotVariants.None, Resolve(settings));
        }

        [TestMethod]
        public void EachVariant_UsesItsOwnRarityThreshold()
        {
            var settings = MakeSettings();
            settings.UnlockRecordingCleanRarities = RaritySelection.Rare;
            settings.UnlockRecordingRarities = RaritySelection.All;
            settings.UnlockRecordingFramedRarities = RaritySelection.Rare;
            settings.UnlockRecordingFramedAlwaysCaptureCompletion = false;
            settings.UnlockRecordingCleanAlwaysCaptureCompletion = false;

            Assert.AreEqual(ScreenshotVariants.WithToast, Resolve(settings, RarityTier.Common));
            Assert.AreEqual(ScreenshotVariants.Clean | ScreenshotVariants.WithToast | ScreenshotVariants.Framed,
                Resolve(settings, RarityTier.Rare));
        }

        [TestMethod]
        public void CompletionBypass_IsPerVariant()
        {
            var settings = MakeSettings();
            settings.UnlockRecordingRarities = RaritySelection.None;
            settings.UnlockRecordingAlwaysCaptureCompletion = false;
            settings.UnlockRecordingCleanRarities = RaritySelection.None;
            settings.UnlockRecordingCleanAlwaysCaptureCompletion = false;
            settings.UnlockRecordingFramedRarities = RaritySelection.None;
            settings.UnlockRecordingFramedAlwaysCaptureCompletion = true;

            Assert.AreEqual(ScreenshotVariants.Framed, Resolve(settings, isCompletion: true));
        }

        [TestMethod]
        public void PlatformOverride_TurnsOneVariantOff()
        {
            var settings = MakeSettings();
            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride { RecordingFramed = false });
            Assert.AreEqual(ScreenshotVariants.Clean | ScreenshotVariants.WithToast, Resolve(settings));
        }

        [TestMethod]
        public void LegacyAllClipsOff_TurnsOffVariantsWithoutTheirOwnValue()
        {
            var settings = MakeSettings();
            settings.SetProviderNotificationOverride(
                "Steam", new ProviderNotificationOverride { Recordings = false, RecordingClean = true });
            Assert.AreEqual(ScreenshotVariants.Clean, Resolve(settings));
        }

        [TestMethod]
        public void EventArgs_UnparsableRarityCountsAsCommon()
        {
            var settings = MakeSettings(clean: false, framed: false);
            settings.UnlockRecordingRarities = RaritySelection.Common;
            var args = new AchievementUnlockedEventArgs { ProviderKey = "Steam", RarityTier = "nonsense" };
            Assert.AreEqual(ScreenshotVariants.WithToast, UnlockClipVariantPolicy.Resolve(args, settings));
        }

        [TestMethod]
        public void FramedSeconds_NonPositiveMeansWholeClip()
        {
            var settings = new PersistedSettings { UnlockRecordingFramedSeconds = 0 };
            Assert.IsNull(settings.UnlockRecordingFramedSeconds);
            settings.UnlockRecordingFramedSeconds = 8;
            Assert.AreEqual(8, settings.UnlockRecordingFramedSeconds);
        }
    }
}
