using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Summaries
{
    /// <summary>
    /// Builds <see cref="GameSummaryItem"/> rollup rows from a set of achievement display items,
    /// keyed on the category label rather than the game. Mirrors
    /// <see cref="GameSummaryItemBuilder"/>'s stats projection
    /// (<see cref="AchievementStatsAccumulator.FromDisplayItems"/> ->
    /// <see cref="AchievementGameStats.ApplyTo"/>), so every rarity, points and goal figure comes
    /// out of the same accumulator whether a row covers one category or a whole subtree.
    ///
    /// Three shapes over one core:
    /// <see cref="Build"/> emits one row per distinct label, which is what the theme surface
    /// publishes; <see cref="BuildLevel"/> emits the immediate children of one node, which is what
    /// a drilled grid shows; <see cref="BuildTree"/> walks every node pre-order.
    /// </summary>
    internal static class CategorySummaryBuilder
    {
        /// <summary>
        /// Builds the per-category rollup rows in configured category order.
        /// </summary>
        /// <param name="achievements">The achievement display items to group.</param>
        /// <param name="badgeMode">
        /// Which rows may render the completion badge. Stamped onto
        /// <see cref="CategorySummaryItem.AllowCompletionBadge"/> in the configured category order
        /// this method emits, so <see cref="CategoryCompletionBadgeMode.First"/> resolves to the
        /// first configured category rather than whichever row a later grid sort floats to the top.
        /// </param>
        public static List<GameSummaryItem> Build(
            IEnumerable<AchievementDisplayItem> achievements,
            CategoryCompletionBadgeMode badgeMode = CategoryCompletionBadgeMode.All)
        {
            var source = Materialize(achievements);
            if (source == null)
            {
                return new List<GameSummaryItem>();
            }

            var groups = GroupByLabel(source);
            var order = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                source,
                item => item.CategoryLabel,
                ResolvePreferredOrder(source));

            return BuildRows(groups, order, aggregateSubtree: false, useLeafNames: false, badgeMode);
        }

        /// <summary>
        /// Builds one row per immediate child of <paramref name="parentPath"/>, each aggregating
        /// its whole subtree. A null or blank parent gives the root level.
        ///
        /// Rows at one level are disjoint by construction - every label has exactly one ancestor
        /// that is an immediate child of the parent - so summing them cannot double-count.
        /// Achievements sitting directly on the parent belong to no row here; the grid renders
        /// those alongside, which is what makes a node with both children and its own achievements
        /// display correctly.
        /// </summary>
        /// <param name="useLeafNames">
        /// True to title rows with the last path segment, for a drilled view whose breadcrumb
        /// already shows the ancestry. False to use the full display path.
        /// </param>
        public static List<GameSummaryItem> BuildLevel(
            IEnumerable<AchievementDisplayItem> achievements,
            string parentPath,
            CategoryCompletionBadgeMode badgeMode = CategoryCompletionBadgeMode.All,
            bool useLeafNames = true)
        {
            var source = Materialize(achievements);
            if (source == null)
            {
                return new List<GameSummaryItem>();
            }

            var groups = GroupByLabel(source);
            var order = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLevel(
                groups.Keys,
                parentPath,
                ResolvePreferredOrder(source));

            return BuildRows(groups, order, aggregateSubtree: true, useLeafNames, badgeMode);
        }

        /// <summary>
        /// Builds a row for every node, pre-order: each node immediately followed by its own
        /// subtree. Rows overlap by design - a parent covers its descendants - so a consumer must
        /// treat this as a tree rather than a flat list to sum.
        /// </summary>
        /// <param name="useLeafNames">
        /// True to title rows with the last path segment, for a surface that conveys ancestry
        /// structurally - an indented list. False to use the full display path.
        /// </param>
        public static List<GameSummaryItem> BuildTree(
            IEnumerable<AchievementDisplayItem> achievements,
            CategoryCompletionBadgeMode badgeMode = CategoryCompletionBadgeMode.All,
            bool useLeafNames = false)
        {
            var source = Materialize(achievements);
            if (source == null)
            {
                return new List<GameSummaryItem>();
            }

            var groups = GroupByLabel(source);
            var order = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(
                groups.Keys,
                ResolvePreferredOrder(source));

            return BuildRows(groups, order, aggregateSubtree: true, useLeafNames, badgeMode);
        }

        private static IReadOnlyList<AchievementDisplayItem> Materialize(
            IEnumerable<AchievementDisplayItem> achievements)
        {
            var source = achievements as IReadOnlyList<AchievementDisplayItem> ?? achievements?.ToList();
            return source == null || source.Count == 0 ? null : source;
        }

        private static Dictionary<string, List<AchievementDisplayItem>> GroupByLabel(
            IReadOnlyList<AchievementDisplayItem> source)
        {
            var groups = new Dictionary<string, List<AchievementDisplayItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
            {
                if (item == null)
                {
                    continue;
                }

                var label = CategoryPathHelper.NormalizePath(item.CategoryLabel);
                if (!groups.TryGetValue(label, out var bucket))
                {
                    bucket = new List<AchievementDisplayItem>();
                    groups[label] = bucket;
                }

                bucket.Add(item);
            }

            return groups;
        }

        private static List<string> ResolvePreferredOrder(IReadOnlyList<AchievementDisplayItem> source)
        {
            return source
                .Where(item => item != null && item.CategoryOrderIndex < int.MaxValue)
                .OrderBy(item => item.CategoryOrderIndex)
                .Select(item => item.CategoryLabel)
                .ToList();
        }

        private static List<GameSummaryItem> BuildRows(
            Dictionary<string, List<AchievementDisplayItem>> groups,
            IReadOnlyList<string> orderedNodes,
            bool aggregateSubtree,
            bool useLeafNames,
            CategoryCompletionBadgeMode badgeMode)
        {
            var result = new List<GameSummaryItem>();

            foreach (var node in orderedNodes)
            {
                groups.TryGetValue(node, out var directMembers);
                var members = aggregateSubtree ? CollectSubtree(groups, node) : directMembers;
                if (members == null || members.Count == 0)
                {
                    continue;
                }

                var depth = CategoryPathHelper.GetDepth(node);
                var display = useLeafNames && depth > 1
                    ? CategoryPathHelper.GetLeafName(node)
                    : AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(node);

                var item = new CategorySummaryItem
                {
                    CategoryLabel = node,
                    CategoryPath = node,
                    CategoryLeafName = CategoryPathHelper.GetLeafName(node),
                    CategoryDepth = depth,
                    ChildCategoryCount = CategoryPathHelper.GetChildPaths(groups.Keys, node).Count,
                    DirectAchievementCount = directMembers?.Count ?? 0,
                    PlayniteGameId = ResolveSharedGameId(members),
                    GameName = display,
                    SortingName = display,
                    // Sorting orders by the name shown, so a nested row needs its path on hover:
                    // once a column sort breaks the tree order, two leaves that happen to share a
                    // name have nothing else to tell them apart.
                    NameToolTip = depth > 1 ? AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(node) : null
                };

                // Category art fills both image slots so the grid's icon/cover toggle only
                // selects the game-asset fallback for categories without art.
                var art = ResolveNodeArt(members, depth, aggregateSubtree);
                item.GameLogo = art ?? ResolveSharedImage(members, member => member.GameIconPath);
                item.GameCoverPath = art ?? ResolveSharedImage(members, member => member.GameCoverPath);

                AchievementStatsAccumulator
                    .FromDisplayItems(members)
                    .ApplyTo(item);

                item.IsCompleted = ComputeIsCompleted(members);
                item.CategoryType = ResolveCategoryType(directMembers, members);
                item.AllowCompletionBadge = AllowsCompletionBadge(badgeMode, result.Count);

                result.Add(item);
            }

            return result;
        }

        private static List<AchievementDisplayItem> CollectSubtree(
            Dictionary<string, List<AchievementDisplayItem>> groups,
            string node)
        {
            var members = new List<AchievementDisplayItem>();
            foreach (var pair in groups)
            {
                if (CategoryPathHelper.IsSelfOrDescendantOf(pair.Key, node))
                {
                    members.AddRange(pair.Value);
                }
            }

            return members;
        }

        /// <summary>
        /// Art for the node at its own depth. A row covering a subtree cannot use
        /// <see cref="ResolveSharedImage"/> over CategoryArtPath, because members that inherited a
        /// descendant's art disagree and that returns null - the normal case for a parent. The
        /// per-level art each member already carries answers it without another disk probe.
        /// </summary>
        private static string ResolveNodeArt(
            IReadOnlyList<AchievementDisplayItem> members,
            int depth,
            bool aggregateSubtree)
        {
            if (aggregateSubtree)
            {
                foreach (var member in members)
                {
                    var perLevel = member?.CategoryAncestorArtPaths;
                    if (perLevel != null && perLevel.Count >= depth && !string.IsNullOrWhiteSpace(perLevel[depth - 1]))
                    {
                        return perLevel[depth - 1];
                    }
                }
            }

            return ResolveSharedImage(members, member => member.CategoryArtPath);
        }

        /// <summary>
        /// Whether the row at <paramref name="emittedCount"/> (its zero-based position in the
        /// configured category order) may render the completion badge. Under
        /// <see cref="CategoryCompletionBadgeMode.First"/> only the first category is permitted, so a
        /// first category that is not completed leaves the whole list badge-free. Each level is its
        /// own call, so First means the first row of the level being shown.
        /// </summary>
        private static bool AllowsCompletionBadge(CategoryCompletionBadgeMode mode, int emittedCount)
        {
            switch (mode)
            {
                case CategoryCompletionBadgeMode.None:
                    return false;
                case CategoryCompletionBadgeMode.First:
                    return emittedCount == 0;
                default:
                    return true;
            }
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
        /// The row's group-based category type token (Base/DLC/Update/Subset), falling back to
        /// <see cref="AchievementCategoryTypeHelper.DefaultCategoryType"/> when there is no group
        /// membership.
        ///
        /// A node's own achievements describe it best, so they win when it has any; otherwise the
        /// subtree decides. Either way the pick is the most common signature, which is what stops
        /// one stray Base member making a DLC parent report Base.
        /// </summary>
        private static string ResolveCategoryType(
            IReadOnlyList<AchievementDisplayItem> directMembers,
            IReadOnlyList<AchievementDisplayItem> allMembers)
        {
            var source = directMembers != null && directMembers.Count > 0 ? directMembers : allMembers;
            var group = AchievementCategoryTypeHelper.ResolveDominantGroupType(
                source.Where(item => item != null).Select(item => item.CategoryType));

            return group.Count > 0 ? group[0] : AchievementCategoryTypeHelper.DefaultCategoryType;
        }

        private static Guid? ResolveSharedGameId(IReadOnlyList<AchievementDisplayItem> bucket)
        {
            Guid? gameId = null;
            foreach (var item in bucket)
            {
                var candidate = item?.PlayniteGameId;
                if (!candidate.HasValue || candidate.Value == Guid.Empty)
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
            Func<AchievementDisplayItem, string> selector)
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
                else if (!string.Equals(image, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return image;
        }
    }
}
