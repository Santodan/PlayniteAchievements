using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// An explicit set of rarity tiers selected for a capture section. Unlike a
    /// <see cref="RarityTier"/> floor, membership is exact: a tier is captured only when its bit is
    /// set. Serialized as an integer bitfield. The bit for a tier is <c>1 &lt;&lt; (int)tier</c>.
    /// Colocated with <see cref="UnlockCaptureRarityFilter"/> because it is that filter's input type.
    /// </summary>
    [Flags]
    public enum RaritySelection
    {
        None = 0,
        Common = 1 << (int)RarityTier.Common,       // 1
        Uncommon = 1 << (int)RarityTier.Uncommon,   // 2
        Rare = 1 << (int)RarityTier.Rare,           // 4
        UltraRare = 1 << (int)RarityTier.UltraRare, // 8
        All = Common | Uncommon | Rare | UltraRare
    }

    public static class RaritySelectionExtensions
    {
        /// <summary>The single-tier flag for a <see cref="RarityTier"/>.</summary>
        public static RaritySelection ToFlag(this RarityTier tier)
        {
            return (RaritySelection)(1 << (int)tier);
        }

        /// <summary>Whether <paramref name="selection"/> includes <paramref name="tier"/>.</summary>
        public static bool Contains(this RaritySelection selection, RarityTier tier)
        {
            return (selection & tier.ToFlag()) != RaritySelection.None;
        }
    }

    /// <summary>
    /// Shared decision for whether an unlock's rarity qualifies it for capture media
    /// (screenshots and recording clips). Both capture services route through this so the
    /// selected-rarities set and the completion bypass behave identically.
    /// </summary>
    public static class UnlockCaptureRarityFilter
    {
        /// <summary>
        /// Whether an unlock at <paramref name="rarity"/> should be captured given the set of
        /// <paramref name="selectedRarities"/>. Completion unlocks bypass the set when
        /// <paramref name="alwaysCaptureCompletion"/> is set; otherwise they filter like any
        /// other unlock.
        /// </summary>
        public static bool ShouldCapture(
            RarityTier rarity,
            bool isCompletionUnlock,
            RaritySelection selectedRarities,
            bool alwaysCaptureCompletion)
        {
            if (isCompletionUnlock && alwaysCaptureCompletion)
            {
                return true;
            }

            return selectedRarities.Contains(rarity);
        }

        /// <summary>
        /// Event-args overload: parses <see cref="AchievementUnlockedEventArgs.RarityTier"/>
        /// (null or unparsable counts as Common — the standalone game-complete event carries no
        /// rarity) and treats the completing unlock, the game-complete event, and capstones as
        /// completion unlocks.
        /// </summary>
        public static bool ShouldCapture(
            AchievementUnlockedEventArgs args,
            RaritySelection selectedRarities,
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
            return ShouldCapture(rarity, isCompletionUnlock, selectedRarities, alwaysCaptureCompletion);
        }
    }
}
