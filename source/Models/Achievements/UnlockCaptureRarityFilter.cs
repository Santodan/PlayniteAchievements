namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Shared decision for whether an unlock's rarity qualifies it for capture media
    /// (screenshots and recording clips). Both capture services route through this so the
    /// minimum-rarity threshold and the completion bypass behave identically.
    /// </summary>
    public static class UnlockCaptureRarityFilter
    {
        /// <summary>
        /// Whether an unlock at <paramref name="rarity"/> should be captured under a
        /// minimum-rarity threshold. Completion unlocks bypass the threshold when
        /// <paramref name="alwaysCaptureCompletion"/> is set; otherwise they filter like any
        /// other unlock.
        /// </summary>
        public static bool ShouldCapture(
            RarityTier rarity,
            bool isCompletionUnlock,
            RarityTier minimumRarity,
            bool alwaysCaptureCompletion)
        {
            if (isCompletionUnlock && alwaysCaptureCompletion)
            {
                return true;
            }

            return rarity >= minimumRarity;
        }

        /// <summary>
        /// Event-args overload: parses <see cref="AchievementUnlockedEventArgs.RarityTier"/>
        /// (null or unparsable counts as Common — the standalone game-complete event carries no
        /// rarity) and treats the completing unlock, the game-complete event, and capstones as
        /// completion unlocks.
        /// </summary>
        public static bool ShouldCapture(
            AchievementUnlockedEventArgs args,
            RarityTier minimumRarity,
            bool alwaysCaptureCompletion)
        {
            if (args == null)
            {
                return false;
            }

            if (!System.Enum.TryParse(args.RarityTier, true, out RarityTier rarity))
            {
                rarity = RarityTier.Common;
            }

            var isCompletionUnlock = args.IsGameCompleted || args.IsCompletionAchievement || args.IsCapstone;
            return ShouldCapture(rarity, isCompletionUnlock, minimumRarity, alwaysCaptureCompletion);
        }
    }
}
