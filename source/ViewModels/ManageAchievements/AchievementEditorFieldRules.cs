using System;
using System.Globalization;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Which achievement fields a user may edit, and how their text is parsed. Kept free of any
    /// view-model base class so the rules can be tested directly rather than through the grid row.
    /// </summary>
    internal static class AchievementEditorFieldRules
    {
        /// <summary>
        /// Rarity is user input only for achievements the user authored. A provider achievement's
        /// rarity is derived from the unlock percentages the provider supplies, and the
        /// stored-rarity guard cannot distinguish a deliberate Common from "never filled in".
        /// </summary>
        public static bool CanEditRarity(bool isCustomRow) => isCustomRow;

        /// <summary>
        /// An unlock timestamp is only a correction to an achievement that is already unlocked.
        /// Unlock status itself is never editable: it would move unlocked counts and completion,
        /// and look like a real unlock to the in-game monitor.
        /// </summary>
        public static bool CanEditUnlockTime(bool unlocked) => unlocked;

        /// <summary>
        /// Unlock status is provider-owned in every case. Present as an explicit rule so the
        /// intent is stated in one place rather than implied by the absence of a setter.
        /// </summary>
        public static bool CanEditUnlockStatus() => false;

        /// <summary>
        /// Parses a points override. Blank clears the override and yields null. Returns false when
        /// the text is present but not a non-negative integer, so the caller refuses to persist it.
        /// </summary>
        public static bool TryParsePoints(string text, out int? points)
        {
            points = null;
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return true;
            }

            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) &&
                !int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            if (parsed < 0)
            {
                return false;
            }

            points = parsed;
            return true;
        }

        /// <summary>
        /// Parses a corrected unlock timestamp into UTC. Blank clears the override and yields null.
        /// Text without an explicit offset is read as local time, matching how it is displayed.
        /// </summary>
        public static bool TryParseUnlockTimeUtc(string text, out DateTime? unlockTimeUtc)
        {
            unlockTimeUtc = null;
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return true;
            }

            if (!DateTime.TryParse(
                    trimmed,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal,
                    out var parsed) &&
                !DateTime.TryParse(
                    trimmed,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal,
                    out parsed))
            {
                return false;
            }

            unlockTimeUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        /// <summary>
        /// Formats a stored UTC timestamp for editing, in local time so the user edits what they
        /// see elsewhere in the plugin.
        /// </summary>
        public static string FormatUnlockTimeForEditing(DateTime? unlockTimeUtc)
        {
            if (!unlockTimeUtc.HasValue)
            {
                return null;
            }

            return unlockTimeUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }
    }
}
