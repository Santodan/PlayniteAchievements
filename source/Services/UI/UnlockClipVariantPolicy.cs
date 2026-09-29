using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Resolves which unlock clip variants one unlock qualifies for: the provider's effective
    /// clip flags (see <see cref="ProviderNotificationPolicy"/>) ANDed with each variant's own
    /// rarity threshold. The clip counterpart of <see cref="UnlockScreenshotVariantPolicy"/>,
    /// sharing its <see cref="ScreenshotVariants"/> flags. The EnableUnlockRecordings master
    /// switch is ANDed in as well, so a None result means no clip at all.
    /// </summary>
    internal static class UnlockClipVariantPolicy
    {
        /// <summary>
        /// The clip variants this unlock should produce. Null settings resolve to
        /// <see cref="ScreenshotVariants.None"/>.
        /// </summary>
        public static ScreenshotVariants Resolve(
            RarityTier rarity,
            bool isCompletion,
            string providerKey,
            PersistedSettings persisted)
        {
            if (persisted?.EnableUnlockRecordings != true)
            {
                return ScreenshotVariants.None;
            }

            var effective = ProviderNotificationPolicy.Resolve(persisted, providerKey);
            var variants = ScreenshotVariants.None;

            if (effective.RecordingClean && UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    persisted.UnlockRecordingCleanRarities,
                    persisted.UnlockRecordingCleanAlwaysCaptureCompletion))
            {
                variants |= ScreenshotVariants.Clean;
            }

            // The with-notification variant keeps the rarity settings every clip used before
            // clips had variants.
            if (effective.RecordingWithToast && UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    persisted.UnlockRecordingRarities,
                    persisted.UnlockRecordingAlwaysCaptureCompletion))
            {
                variants |= ScreenshotVariants.WithToast;
            }

            if (effective.RecordingFramed && UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    persisted.UnlockRecordingFramedRarities,
                    persisted.UnlockRecordingFramedAlwaysCaptureCompletion))
            {
                variants |= ScreenshotVariants.Framed;
            }

            return variants;
        }

        /// <summary>
        /// Event-args overload: an unparsable rarity counts as Common, and the completing unlock,
        /// the game-complete event and capstones count as completion unlocks, as in
        /// <see cref="UnlockCaptureRarityFilter"/>.
        /// </summary>
        public static ScreenshotVariants Resolve(AchievementUnlockedEventArgs args, PersistedSettings persisted)
        {
            if (args == null)
            {
                return ScreenshotVariants.None;
            }

            if (!System.Enum.TryParse(args.RarityTier, true, out RarityTier rarity))
            {
                rarity = RarityTier.Common;
            }

            return Resolve(
                rarity,
                args.IsGameCompleted || args.IsCompletionAchievement || args.IsCapstone,
                args.ProviderKey,
                persisted);
        }
    }
}
