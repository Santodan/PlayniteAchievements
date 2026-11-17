using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Resolves which unlock-screenshot variants one unlock qualifies for: the provider's
    /// effective feature flags (see <see cref="ProviderNotificationPolicy"/>) ANDed with each
    /// variant's own rarity threshold.
    /// <para>
    /// All three variants gate identically. The with-notification variant does not depend on an
    /// on-screen notification: the card it composites is rendered from a real toast window
    /// whether or not that window is ever revealed, so the saved file always shows one.
    /// </para>
    /// <para>
    /// A retrigger bypasses the rarity thresholds: the user asked for that capture, so only the
    /// feature flags decide which variants it produces.
    /// </para>
    /// </summary>
    internal static class UnlockScreenshotVariantPolicy
    {
        /// <summary>
        /// The variants this unlock should produce. Null settings resolve to
        /// <see cref="ScreenshotVariants.None"/>. <paramref name="bypassRarity"/> skips every
        /// variant's rarity threshold while leaving its feature flag in force.
        /// </summary>
        public static ScreenshotVariants Resolve(
            RarityTier rarity,
            bool isCompletion,
            string providerKey,
            PersistedSettings persisted,
            GameCustomDataFile gameData = null,
            bool bypassRarity = false)
        {
            if (persisted == null)
            {
                return ScreenshotVariants.None;
            }

            var effective = ProviderNotificationPolicy.Resolve(persisted, providerKey, gameData);
            var captures = effective.Captures;
            var variants = ScreenshotVariants.None;

            if (effective.ScreenshotClean && (bypassRarity || UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    captures.UnlockScreenshotCleanRarities,
                    captures.UnlockScreenshotCleanAlwaysCaptureCompletion)))
            {
                variants |= ScreenshotVariants.Clean;
            }

            if (effective.ScreenshotWithToast && (bypassRarity || UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    captures.UnlockScreenshotWithToastRarities,
                    captures.UnlockScreenshotWithToastAlwaysCaptureCompletion)))
            {
                variants |= ScreenshotVariants.WithToast;
            }

            if (effective.ScreenshotFramed && (bypassRarity || UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    captures.UnlockScreenshotFramedRarities,
                    captures.UnlockScreenshotFramedAlwaysCaptureCompletion)))
            {
                variants |= ScreenshotVariants.Framed;
            }

            return variants;
        }
    }
}
