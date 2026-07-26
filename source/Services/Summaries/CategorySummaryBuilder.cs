using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Summaries
{
    /// <summary>
    /// Builds one <see cref="GameSummaryItem"/> per distinct category label from a set of
    /// achievement display items. Mirrors <see cref="GameSummaryItemBuilder"/>'s stats
    /// projection (<see cref="AchievementStatsAccumulator.FromDisplayItems"/> ->
    /// <see cref="AchievementGameStats.ApplyTo"/>) but keys on the free-form category label
    /// rather than the game, so the achievement grid can present a per-category rollup.
    /// </summary>
    internal static class CategorySummaryBuilder
    {
        public static List<GameSummaryItem> Build(IEnumerable<AchievementDisplayItem> achievements)
        {
            var result = new List<GameSummaryItem>();
            var source = achievements as IReadOnlyList<AchievementDisplayItem>
                ?? achievements?.ToList();
            if (source == null || source.Count == 0)
            {
                return result;
            }

            var groups = new Dictionary<string, List<AchievementDisplayItem>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
            {
                if (item == null)
                {
                    continue;
                }

                var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel);
                if (!groups.TryGetValue(label, out var bucket))
                {
                    bucket = new List<AchievementDisplayItem>();
                    groups[label] = bucket;
                }

                bucket.Add(item);
            }

            var preferredOrder = source
                .Where(item => item != null && item.CategoryOrderIndex < int.MaxValue)
                .OrderBy(item => item.CategoryOrderIndex)
                .Select(item => item.CategoryLabel)
                .ToList();

            foreach (var label in AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(source, i => i.CategoryLabel, preferredOrder))
            {
                if (!groups.TryGetValue(label, out var bucket))
                {
                    continue;
                }

                var display = AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(label);
                // Category art fills both image slots so the grid's icon/cover toggle only
                // selects the game-asset fallback for categories without art.
                var sharedArt = ResolveSharedImage(bucket, item => item.CategoryArtPath);
                var item = new CategorySummaryItem
                {
                    CategoryLabel = label,
                    PlayniteGameId = ResolveSharedGameId(bucket),
                    GameName = display,
                    SortingName = display,
                    GameLogo = sharedArt ?? ResolveSharedImage(bucket, item => item.GameIconPath),
                    GameCoverPath = sharedArt ?? ResolveSharedImage(bucket, item => item.GameCoverPath)
                };

                AchievementStatsAccumulator
                    .FromDisplayItems(bucket)
                    .ApplyTo(item);

                item.IsCompleted = ComputeIsCompleted(bucket);
                item.CategoryType = ResolveCategoryType(bucket);

                result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// Mirrors <see cref="PlayniteAchievements.Models.Achievements.GameAchievementData.IsCompleted"/>:
        /// every achievement unlocked, or the category contains the game's unlocked capstone achievement.
        /// </summary>
        private static bool ComputeIsCompleted(IReadOnlyList<AchievementDisplayItem> bucket)
        {
            var hasAny = false;
            var allUnlocked = true;
            foreach (var achievement in bucket)
            {
                if (achievement == null)
                {
                    continue;
                }

                hasAny = true;
                if (achievement.IsCapstone && achievement.Unlocked)
                {
                    return true;
                }

                if (!achievement.Unlocked)
                {
                    allUnlocked = false;
                }
            }

            return hasAny && allUnlocked;
        }

        /// <summary>
        /// Resolves the bucket's group-based category type token (Base/DLC/Update/Subset) for theme
        /// binding, falling back to <see cref="AchievementCategoryTypeHelper.DefaultCategoryType"/> when
        /// the bucket has no group membership. A category label maps 1:1 to a provider set in practice,
        /// so the group component is uniform; the canonical-first pick is a defensive tiebreak.
        /// </summary>
        private static string ResolveCategoryType(IReadOnlyList<AchievementDisplayItem> bucket)
        {
            var combined = AchievementCategoryTypeHelper.Combine(
                bucket.Where(item => item != null).Select(item => item.CategoryType));
            var groupComponents = AchievementCategoryTypeHelper.GetGroupTypeComponents(combined);
            return groupComponents.Count > 0
                ? groupComponents[0]
                : AchievementCategoryTypeHelper.DefaultCategoryType;
        }

        private static System.Guid? ResolveSharedGameId(IReadOnlyList<AchievementDisplayItem> bucket)
        {
            System.Guid? gameId = null;
            foreach (var item in bucket)
            {
                var candidate = item?.PlayniteGameId;
                if (!candidate.HasValue || candidate.Value == System.Guid.Empty)
                {
                    continue;
                }

                if (!gameId.HasValue)
                {
                    gameId = candidate.Value;
                }
                else if (gameId.Value != candidate.Value)
                {
                    return null;
                }
            }

            return gameId;
        }

        private static string ResolveSharedImage(
            IReadOnlyList<AchievementDisplayItem> bucket,
            System.Func<AchievementDisplayItem, string> selector)
        {
            if (selector == null)
            {
                return null;
            }

            string image = null;
            foreach (var item in bucket)
            {
                var candidate = selector(item);
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                if (image == null)
                {
                    image = candidate;
                }
                else if (!string.Equals(image, candidate, System.StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return image;
        }
    }
}
