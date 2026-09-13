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
            string rarity,
            string category)
        {
            Unlocked = unlocked;
            UnlockTimeUtc = unlockTimeUtc;
            GlobalPercentUnlocked = globalPercentUnlocked;
            Rarity = rarity;
            Category = category;
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

        /// <summary>
        /// The category everything the capstone stands for sits in, when they all sit in one, so a
        /// capstone can be filed alongside them rather than landing in the default bucket beside a
        /// game whose achievements are all sorted. Null when they disagree, or when the one they
        /// share is the default bucket and there is nothing to inherit.
        /// </summary>
        /// <remarks>
        /// Unlike the rarity and the unlock this is only read when the capstone is authored: it is
        /// a sensible starting place rather than something kept in step, so filing the capstone
        /// somewhere of your own choosing sticks.
        /// </remarks>
        public string Category { get; }
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
        /// The group types that say an achievement belongs to something other than the base game.
        /// Update is not among them: a base-game update is still the base game, and the providers
        /// that emit it pair it with its owner.
        /// </summary>
        private static readonly string[] NonBaseGroupTypes = { "DLC", "Subset" };

        /// <summary>
        /// Derives the tracked fields, or null when there is nothing to stand for.
        /// </summary>
        /// <remarks>
        /// Only the base game's achievements count: a platinum is not withheld for DLC, so neither
        /// is this. Three providers say which group an achievement is in -- PSN by trophy group,
        /// RetroAchievements by set, and Steam through SteamHunters -- and a user's own type
        /// assignment reaches this the same way, hydration having already applied it over the
        /// provider's. Where nothing is marked as the base game, anything marked as <em>not</em>
        /// the base game is still dropped, which is what makes hand-marking the DLC on a game no
        /// provider groups do something. Rarity takes the rarest of whatever is left, finishing a
        /// game being at least as hard as its hardest single step.
        /// </remarks>
        /// <summary>
        /// The achievements the capstone stands for: the ones marked as the base game, or failing
        /// that everything not marked as belonging elsewhere.
        /// </summary>
        private static List<AchievementDetail> ResolveScope(List<AchievementDetail> candidates)
        {
            var baseGame = candidates
                .Where(achievement => HasGroupType(achievement, BaseCategoryType))
                .ToList();
            if (baseGame.Count > 0)
            {
                return baseGame;
            }

            var withoutOtherGroups = candidates
                .Where(achievement => !NonBaseGroupTypes.Any(type => HasGroupType(achievement, type)))
                .ToList();

            // Everything is marked as belonging elsewhere, which leaves nothing to stand for; the
            // whole list is a better answer than none of it.
            return withoutOtherGroups.Count > 0 ? withoutOtherGroups : candidates;
        }

        /// <summary>
        /// The one category the whole scope sits in, or null when they are spread across several or
        /// all sit in the default bucket.
        /// </summary>
        private static string ResolveSharedCategory(List<AchievementDetail> scope)
        {
            var labels = scope
                .Select(achievement => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (labels.Count != 1)
            {
                return null;
            }

            var shared = labels[0];
            return string.Equals(shared, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? null
                : shared;
        }

        private static bool HasGroupType(AchievementDetail achievement, string groupType)
        {
            return AchievementCategoryTypeHelper
                .GetGroupTypeComponents(achievement.CategoryType)
                .Any(value => string.Equals(value, groupType, StringComparison.OrdinalIgnoreCase));
        }

        public static AutoCapstoneDerivation Derive(IEnumerable<AchievementDetail> achievements)
        {
            var candidates = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(achievement => achievement != null)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var scope = ResolveScope(candidates);

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
                rarest.HasValue ? PercentRarityHelper.GetRarityTier(rarest.Value).ToString() : null,
                ResolveSharedCategory(scope));
        }
    }
}
