using System;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Achievements
{
    // Recover text saved while the English dictionary could not load. Only exact
    // markers for the capstone defaults qualify; authored descriptions stay intact.
    internal static class SantodanAutoCapstoneTextRecovery
    {
        internal static bool IsBrokenDefault(string value)
        {
            value = value?.Trim();
            return string.Equals(value, "<!" + AutoCapstoneTemplate.DescriptionKey + "!>", StringComparison.Ordinal) ||
                string.Equals(value, "<!" + AutoCapstoneText.CategoryDescriptionKey + "!>", StringComparison.Ordinal) ||
                string.Equals(value, "<!" + AutoCapstoneText.FallbackGameNameKey + "!>", StringComparison.Ordinal);
        }

        internal static string DefaultText(string key, string fallback)
        {
            var text = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(text) || IsBrokenDefault(text) ? fallback : text;
        }

        // Run the existing capstone text reconciliation once even if an earlier
        // startup already recorded healthy templates without repairing saved markers.
        internal static string AppliedSignature(string signature) => signature + "\u001fSantodanCapstoneTextRecovery1";
    }
}
