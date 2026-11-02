using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    /// <summary>
    /// Whether a batch that reached 100% also sends the standalone completion notification.
    /// </summary>
    /// <remarks>
    /// A capstone outside DLC and subset categories (a platinum, a whole-game auto capstone, or any
    /// capstone of a game without groups) that is itself the unlock reaching 100% already announces
    /// the completion, so the standalone notification would repeat it. A DLC category's capstone
    /// finishing the game stands for that DLC only, so the 100% still follows it. While DLC remains
    /// locked a platinum never reaches 100%, which is why no check for groups is needed.
    /// </remarks>
    internal static class CompletionNotificationPolicy
    {
        /// <param name="skipAfterCapstone">The SkipCompletionNotificationAfterCapstone setting.</param>
        /// <param name="completingIsCapstone">Whether the unlock that reached 100% is a capstone.</param>
        /// <param name="completingCategoryKind">That unlock's category kind; empty for a game without groups.</param>
        public static bool SendsStandalone(
            bool skipAfterCapstone,
            bool completingIsCapstone,
            string completingCategoryKind)
        {
            if (!skipAfterCapstone || !completingIsCapstone)
            {
                return true;
            }

            return CategoryKindHelper.IsDlc(completingCategoryKind) ||
                   CategoryKindHelper.IsSubset(completingCategoryKind);
        }
    }
}
