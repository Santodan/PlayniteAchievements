using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Applies a user's per-achievement overrides onto a row, for every surface that shows one.
    /// </summary>
    /// <remarks>
    /// One copy on purpose. Each field is applied only when the override carries a value, so
    /// clearing one falls back to the provider's own value rather than to blank.
    ///
    /// Deliberately absent: unlock status and rarity. Unlock status stays provider-owned so an edit
    /// cannot change unlocked counts, completion, or look like a real unlock to the in-game
    /// monitor; the unlock timestamp is therefore only corrected on a row that is already unlocked.
    /// Rarity stays provider-owned because the stored-rarity guard cannot tell a deliberate Common
    /// from "never filled in".
    /// </remarks>
    public static class AchievementOverrideApplier
    {
        /// <param name="hasManualLink">
        /// True when the game's achievements come from a manual link, which records unlock state
        /// and time itself.
        /// </param>
        public static void Apply(
            IAchievementOverrideTarget target,
            AchievementOverride userOverride,
            bool hasManualLink)
        {
            if (target == null || userOverride == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(userOverride.DisplayName))
            {
                target.DisplayName = userOverride.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(userOverride.Description))
            {
                target.Description = userOverride.Description;
            }

            if (userOverride.Points.HasValue)
            {
                target.Points = userOverride.Points;
            }

            if (!string.IsNullOrWhiteSpace(userOverride.TrophyType))
            {
                target.TrophyType = userOverride.TrophyType;
            }

            // Either value is a customization, so the caller stores null to mean "back to whatever
            // the provider says" rather than "not hidden".
            if (userOverride.Hidden.HasValue)
            {
                target.Hidden = userOverride.Hidden.Value;
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
