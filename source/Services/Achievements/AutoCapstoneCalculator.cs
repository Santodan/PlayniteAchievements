using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// What the auto capstone's tracked fields work out to for a given set of achievements.
    /// </summary>
    public sealed class AutoCapstoneDerivation
    {
        public AutoCapstoneDerivation(
            bool unlocked,
            DateTime? unlockTimeUtc,
            double? globalPercentUnlocked,
            string rarity)
        {
            Unlocked = unlocked;
            UnlockTimeUtc = unlockTimeUtc;
            GlobalPercentUnlocked = globalPercentUnlocked;
            Rarity = rarity;
        }

        /// <summary>True once every achievement the capstone stands for is unlocked.</summary>
        public bool Unlocked { get; }

        /// <summary>
        /// When the last of them was unlocked, which is the moment the game was finished. Null
        /// while the game is unfinished, or when none of them carries a timestamp.
        /// </summary>
        public DateTime? UnlockTimeUtc { get; }

        public double? GlobalPercentUnlocked { get; }

        public string Rarity { get; }
    }

    /// <summary>
    /// Works out the auto capstone's rarity and unlock state from the achievements it stands for.
    /// </summary>
    /// <remarks>
    /// Kept apart from the service that stores the result so the rules can be read and tested
    /// without a cache, a store or a game behind them.
    /// </remarks>
    public static class AutoCapstoneCalculator
    {
        private const string BaseCategoryType = "Base";

        /// <summary>
        /// Derives the tracked fields, or null when there is nothing to stand for.
        /// </summary>
        /// <remarks>
        /// Only the base game's achievements count where a provider groups them: a platinum is not
        /// withheld for DLC, so neither is this. Rarity takes the rarest of them, finishing a game
        /// being at least as hard as its hardest single step.
        /// </remarks>
        public static AutoCapstoneDerivation Derive(IEnumerable<AchievementDetail> achievements)
        {
            var candidates = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(achievement => achievement != null)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var baseGame = candidates
                .Where(achievement => AchievementCategoryTypeHelper
                    .ParseValues(achievement.CategoryType)
                    .Any(value => string.Equals(value, BaseCategoryType, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var scope = baseGame.Count > 0 ? baseGame : candidates;

            var unlocked = scope.All(achievement => achievement.Unlocked);
            var unlockTimeUtc = unlocked
                ? scope
                    .Where(achievement => achievement.UnlockTimeUtc.HasValue)
                    .Select(achievement => achievement.UnlockTimeUtc.Value)
                    .DefaultIfEmpty()
                    .Max()
                : default(DateTime);

            var percents = scope
                .Where(achievement => achievement.GlobalPercentUnlocked.HasValue &&
                                      achievement.GlobalPercentUnlocked.Value > 0)
                .Select(achievement => achievement.GlobalPercentUnlocked.Value)
                .ToList();
            var rarest = percents.Count > 0 ? percents.Min() : (double?)null;

            return new AutoCapstoneDerivation(
                unlocked,
                unlockTimeUtc == default(DateTime) ? (DateTime?)null : unlockTimeUtc,
                rarest,
                rarest.HasValue ? PercentRarityHelper.GetRarityTier(rarest.Value).ToString() : null);
        }
    }
}
