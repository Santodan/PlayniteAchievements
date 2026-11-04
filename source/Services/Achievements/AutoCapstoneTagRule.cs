using PlayniteAchievements.Models.Settings;
using System;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The tags the settings give every auto capstone, besides the tags of the achievements it
    /// stands for: one list for whole-game capstones and one for category capstones.
    /// </summary>
    /// <remarks>
    /// Read against the global tag list each time, so a tag deleted from that list drops out of
    /// the rule without the stored keys having to be rewritten.
    /// </remarks>
    public static class AutoCapstoneTagRule
    {
        private const string SignatureSeparator = "\u001f";

        /// <summary>
        /// The configured keys for a capstone of the given kind, normalized and limited to the
        /// tags the global list defines. Null when none apply.
        /// </summary>
        public static string Resolve(PersistedSettings settings, bool wholeGame)
        {
            var stored = wholeGame ? settings?.AutoCapstoneGameTags : settings?.AutoCapstoneCategoryTags;
            return AchievementTagHelper.Combine(AchievementTagHelper.GetComponents(stored)
                .Where(key => !AchievementTagHelper.IsDerivedTag(key) && AchievementTagRegistry.IsDefined(key)));
        }

        /// <summary>
        /// Both resolved lists as one string, to tell whether what is in effect differs from what
        /// was last applied. The same for lists that are null and lists that are empty.
        /// </summary>
        public static string Signature(PersistedSettings settings) =>
            (Resolve(settings, wholeGame: true) ?? string.Empty) +
            SignatureSeparator +
            (Resolve(settings, wholeGame: false) ?? string.Empty);

        /// <summary>True when the configured tags in effect are not the ones last applied.</summary>
        public static bool NeedsApply(PersistedSettings settings) =>
            settings != null &&
            !string.Equals(Signature(settings), settings.AutoCapstoneAppliedTags ?? EmptySignature, StringComparison.Ordinal);

        /// <summary>The signature of no configured tags, which a fresh install counts as applied.</summary>
        public static string EmptySignature => SignatureSeparator;
    }
}
