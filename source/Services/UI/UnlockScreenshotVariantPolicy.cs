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
    /// </summary>
    internal static class UnlockScreenshotVariantPolicy
    {
        /// <summary>
        /// The variants this unlock should produce. Null settings resolve to
        /// <see cref="ScreenshotVariants.None"/>.
        /// </summary>
        public static ScreenshotVariants Resolve(
            RarityTier rarity,
            bool isCompletion,
            string providerKey,
            PersistedSettings persisted)
        {
            if (persisted == null)
            {
                return ScreenshotVariants.None;
            }

            var effective = ProviderNotificationPolicy.Resolve(persisted, providerKey);
            var variants = ScreenshotVariants.None;

            if (effective.ScreenshotClean && UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    persisted.UnlockScreenshotCleanRarities,
                    persisted.UnlockScreenshotCleanAlwaysCaptureCompletion))
            {
                variants |= ScreenshotVariants.Clean;
            }

            if (effective.ScreenshotWithToast && UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    persisted.UnlockScreenshotWithToastRarities,
                    persisted.UnlockScreenshotWithToastAlwaysCaptureCompletion))
            {
                variants |= ScreenshotVariants.WithToast;
            }

            if (effective.ScreenshotFramed && UnlockCaptureRarityFilter.ShouldCapture(
                    rarity,
                    isCompletion,
                    persisted.UnlockScreenshotFramedRarities,
                    persisted.UnlockScreenshotFramedAlwaysCaptureCompletion))
            {
                variants |= ScreenshotVariants.Framed;
            }

            return variants;
        }
    }
}
