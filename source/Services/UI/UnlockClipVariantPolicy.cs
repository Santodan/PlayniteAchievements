using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Resolves which unlock clip variants one unlock qualifies for: the provider's effective
    /// clip flags (see <see cref="ProviderNotificationPolicy"/>) ANDed with each variant's own
    /// rarity threshold. The clip counterpart of <see cref="UnlockScreenshotVariantPolicy"/>,
    /// sharing its <see cref="ScreenshotVariants"/> flags. The recordings master switch (the
    /// game's own when it has capture settings) is ANDed in as well, so a None result means no clip at all.
    /// A retrigger bypasses the rarity thresholds: the user asked for that clip, so only the
    /// switches decide which variants it produces.
    /// </summary>
    internal static class UnlockClipVariantPolicy
    {
        /// <summary>
        /// The clip variants this unlock should produce. Null settings resolve to
        /// <see cref="ScreenshotVariants.None"/>. <paramref name="bypassRarity"/> skips every
        /// variant's rarity threshold while leaving the recordings switch and the per-variant
        /// flags in force.
        /// </summary>
        public static ScreenshotVariants Resolve(
            RarityTier rarity,
            bool isCompletion,
            string providerKey,
            PersistedSettings persisted,
            GameCustomDataFile gameData = null,
            bool bypassRarity = false)
        {
            var effective = ProviderNotificationPolicy.Resolve(persisted, providerKey, gameData);
            if (!effective.RecordingsEnabled)
            {
                return ScreenshotVariants.None;
            }

            var captures = effective.Captures;
            var variants = ScreenshotVariants.None;

            if (effective.RecordingClean && (bypassRarity || UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    captures.UnlockRecordingCleanRarities,
                    captures.UnlockRecordingCleanAlwaysCaptureCompletion)))
            {
                variants |= ScreenshotVariants.Clean;
            }

            // The with-notification variant keeps the rarity settings every clip used before
            // clips had variants.
            if (effective.RecordingWithToast && (bypassRarity || UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    captures.UnlockRecordingRarities,
                    captures.UnlockRecordingAlwaysCaptureCompletion)))
            {
                variants |= ScreenshotVariants.WithToast;
            }

            if (effective.RecordingFramed && (bypassRarity || UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    captures.UnlockRecordingFramedRarities,
                    captures.UnlockRecordingFramedAlwaysCaptureCompletion)))
            {
                variants |= ScreenshotVariants.Framed;
            }

            return variants;
        }

        /// <summary>
        /// Event-args overload: an unparsable rarity counts as Common, and the completing unlock,
        /// the game-complete event and capstones count as completion unlocks, as in
        /// <see cref="UnlockCaptureRarityFilter"/>. A retrigger (<see cref="AchievementUnlockedEventArgs.IsTestFire"/>)
        /// bypasses the rarity thresholds.
        /// </summary>
        public static ScreenshotVariants Resolve(
            AchievementUnlockedEventArgs args,
            PersistedSettings persisted,
            GameCustomDataFile gameData = null)
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
                persisted,
                gameData,
                bypassRarity: args.IsTestFire);
        }
    }
}
