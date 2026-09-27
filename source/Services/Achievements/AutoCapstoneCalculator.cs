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

        private const string UpdateCategoryType = "Update";

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
        /// The category the scope belongs in: the one it all sits in, or when the base game spans
        /// several, the main game's. Null when any of it sits in the default bucket.
        /// </summary>
        private static string ResolveSharedCategory(List<AchievementDetail> scope)
        {
            var labels = DistinctNamedCategories(scope);
            if (labels.Count == 1)
            {
                return labels[0];
            }

            if (labels.Count == 0)
            {
                return null;
            }

            // The base game spread over several categories: SteamHunters files each post-launch
            // update group under its own label, typed Base|Update. The main game is the category
            // holding the base-game rows that are not an update, so the capstone sits with it
            // rather than in a default bucket that nothing else is left in.
            var mainGame = DistinctNamedCategories(scope
                .Where(achievement => HasGroupType(achievement, BaseCategoryType) &&
                                      !HasGroupType(achievement, UpdateCategoryType))
                .ToList());
            if (mainGame.Count == 1)
            {
                return mainGame[0];
            }

            // Still no single answer: the category holding most of what it stands for.
            return scope
                .Select(achievement => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category))
                .Where(label => !IsDefaultCategory(label))
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .FirstOrDefault();
        }

        /// <summary>
        /// The distinct categories <paramref name="achievements"/> sit in, or empty when any of
        /// them sits in the default bucket, which then has as good a claim as a named one.
        /// </summary>
        private static List<string> DistinctNamedCategories(List<AchievementDetail> achievements)
        {
            var labels = achievements
                .Select(achievement => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return labels.Any(IsDefaultCategory) ? new List<string>() : labels;
        }

        private static bool IsDefaultCategory(string label)
        {
            return string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGroupType(AchievementDetail achievement, string groupType)
        {
            return AchievementCategoryTypeHelper
                .GetGroupTypeComponents(achievement.CategoryType)
                .Any(value => string.Equals(value, groupType, StringComparison.OrdinalIgnoreCase));
        }

        /// <param name="category">
        /// The category the capstone stands for, or null for the whole game. Category and category
        /// type are different axes and both apply: the label narrows the capstone to one category,
        /// and the group rules below still drop DLC and subsets from within it. For a game whose
        /// achievements all sit in one category the two give the same answer, which is what keeps a
        /// single-category game behaving exactly as it did.
        /// </param>
        public static AutoCapstoneDerivation Derive(
            IEnumerable<AchievementDetail> achievements,
            string category = null)
        {
            // Filtered achievements are out of the counts completion is read from, so waiting on
            // one would hold the capstone locked on a game the summary already calls finished.
            var candidates = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(achievement => achievement != null &&
                                      !achievement.IsFiltered &&
                                      !achievement.IsFilteredFromSummaries)
                .ToList();

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(category);
            if (!string.IsNullOrWhiteSpace(normalizedCategory))
            {
                candidates = candidates
                    .Where(achievement => string.Equals(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category),
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(normalizedCategory),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

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

        /// <summary>
        /// Derives an existing auto capstone's tracked fields from its game's hydrated achievements.
        /// </summary>
        /// <param name="apiName">The capstone's own ApiName, left out so it does not wait on itself.</param>
        /// <param name="isWholeGame">True when it stands for the whole game rather than a category.</param>
        /// <param name="storedCategory">
        /// The category its definition carries, used only when the capstone's own row is missing
        /// from <paramref name="achievements"/>. The row is what carries the category the user
        /// filed it in; the definition only ever holds the default.
        /// </param>
        public static AutoCapstoneDerivation DeriveForCapstone(
            IEnumerable<AchievementDetail> achievements,
            string apiName,
            bool isWholeGame,
            string storedCategory = null)
        {
            var list = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(achievement => achievement != null)
                .ToList();
            var normalizedApiName = (apiName ?? string.Empty).Trim();
            var own = list.FirstOrDefault(achievement => string.Equals(
                (achievement.ApiName ?? string.Empty).Trim(),
                normalizedApiName,
                StringComparison.OrdinalIgnoreCase));

            var category = isWholeGame ? null : own?.Category ?? storedCategory;
            return Derive(
                list.Where(achievement => !ReferenceEquals(achievement, own) &&
                                          !string.Equals(
                                              (achievement.ApiName ?? string.Empty).Trim(),
                                              normalizedApiName,
                                              StringComparison.OrdinalIgnoreCase)),
                category);
        }
    }
}
