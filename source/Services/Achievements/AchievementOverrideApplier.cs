using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Applies a user's unlock-time override, the one override field kept outside the layers.
    /// Unlock status itself stays provider-owned, so the timestamp is only corrected on a row that
    /// is already unlocked.
    /// </summary>
    public static class AchievementOverrideApplier
    {
        /// <summary>
        /// The override's unlock time on an unlocked row. Kept apart from the layers because a
        /// live unlock changes a loaded row's time.
        /// </summary>
        public static void ApplyUnlockTime(
            IAchievementOverrideTarget target,
            AchievementOverride userOverride,
            bool hasManualLink)
        {
            if (target == null || userOverride == null)
            {
                return;
            }

            // A manually tracked game records unlock state and time in its link, which reaches the
            // row through the cache. Layering an override on top would show one timestamp in the
            // editor while every count, summary and theme surface kept the link's, with nothing on
            // screen to explain the disagreement.
            if (target.Unlocked && !hasManualLink)
            {
                // A stored timestamp and a cleared state are mutually exclusive, and normalization
                // resolves the pair the same way: the timestamp wins.
                if (userOverride.UnlockTimeUtc.HasValue)
                {
                    target.UnlockTimeUtc = userOverride.UnlockTimeUtc;
                }
                else if (userOverride.ClearUnlockTime)
                {
                    target.UnlockTimeUtc = null;
                }
            }
        }
    }
}
