using PlayniteAchievements.Models.Achievements;
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
        public static bool CanEditRarity(bool isCustomRow, bool isAutoCapstone = false) =>
            isCustomRow && !isAutoCapstone;

        /// <summary>
        /// An authored achievement always has a rarity: blank reads as Common, on a new row, on a
        /// stored definition that carries none, and when the user clears the box. The bulk proxy is
        /// the one exception, where blank stands for a selection that disagrees.
        /// </summary>
        public static string NormalizeAuthoredRarity(string rarity, bool isBulkRow) =>
            string.IsNullOrWhiteSpace(rarity)
                ? (isBulkRow ? null : nameof(RarityTier.Common))
                : rarity.Trim();

        /// <summary>
        /// An unlock timestamp is only a correction to an achievement that is already unlocked.
        /// Unlock status itself is never editable: it would move unlocked counts and completion,
        /// and look like a real unlock to the in-game monitor.
        /// </summary>
        public static bool CanEditUnlockTime(bool unlocked) => unlocked;

        /// <summary>
        /// Whether a culture writes the time of day on a 24-hour clock, used to pick the unlock-time
        /// editor's default mode so a user whose language has no AM/PM is not handed one.
        /// </summary>
        /// <remarks>
        /// Read from the culture's own short time pattern rather than a language list: "H" is the
        /// 24-hour hour specifier and "h" the 12-hour one, and quoted literals in the pattern are
        /// skipped so a separator such as 'h' in the French pattern is not mistaken for a specifier.
        /// The editor's mode dropdown still lets the user pick the other one.
        /// </remarks>
        public static bool PrefersTwentyFourHourClock(CultureInfo culture)
        {
            var pattern = culture?.DateTimeFormat?.ShortTimePattern;
            if (string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            var quote = '\0';
            foreach (var character in pattern)
            {
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (character == '\'' || character == '"')
                {
                    quote = character;
                    continue;
                }

                if (character == 'h')
                {
                    return false;
                }

                if (character == 'H')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the user may change whether an achievement is unlocked.
        /// </summary>
        /// <remarks>
        /// Provider-owned by default: editing it would move unlocked counts and completion, and read
        /// as a real unlock to the in-game monitor. Two cases are not provider-owned. An authored
        /// achievement has no provider behind it at all. And on a manually tracked game the user is
        /// the source of unlock state by definition -- that is the whole feature -- so the rule does
        /// not apply to its rows either.
        /// </remarks>
        public static bool CanEditUnlockStatus(
            bool isCustomRow,
            bool isManuallyTrackedGame,
            bool isAutoCapstone = false) =>
            (isCustomRow || isManuallyTrackedGame) && !isAutoCapstone;

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
