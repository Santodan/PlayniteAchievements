using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The category edits a game supports - rename, re-parent, merge, duplicate, delete, create -
    /// each landing as one store write over an explicit <see cref="CategoryEditSnapshot"/>. Owns
    /// no rows and raises no events: a caller hands in what it renders and folds the returned
    /// <see cref="CategoryEditOutcome"/> into its own state, which is what lets the Manage tab and
    /// a grid's context menu run the same operations.
    /// </summary>
    /// <remarks>
    /// Membership (which achievement sits in which category) is keyed by ApiName; the order, art
    /// and summary selection are keyed by label. A move touches both, and writing them separately
    /// cost two whole-library recomputes per rename, so every operation here lands both halves in
    /// a single update.
    /// </remarks>
    internal sealed class CategoryEditor
    {
        private readonly ICategoryMetadataWriter _writer;
        private readonly Func<PersistedSettings> _settings;
        private readonly GameCustomDataStore _store;
        private readonly ILogger _logger;

        /// <param name="settings">
        /// Read per call rather than captured: the persisted settings instance is replaced when
        /// the settings window is cancelled.
        /// </param>
        /// <param name="store">The store the reads go to; null resolves to the plugin's.</param>
        public CategoryEditor(
            ICategoryMetadataWriter writer,
            Func<PersistedSettings> settings,
            ILogger logger = null,
            GameCustomDataStore store = null)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _settings = settings ?? (() => null);
            _logger = logger;
            _store = store;
        }

        /// <summary>
        /// The labels a game renders, in tree order with the Default bucket inserted: the
        /// achievements' categories in definition order, plus every label that carries user state
        /// (art, a stored order slot, the summary selection) even when no achievement sits in it.
        /// The order list seeds rows too, so a user-created category survives on the strength of
        /// the persisted order alone; a stale entry surfaces as an empty row the user can delete.
        /// </summary>
        public static List<string> ResolveRenderedLabels(
            IEnumerable<string> definitionOrderedCategories,
            IReadOnlyList<string> categoryOrder,
            IEnumerable<string> imageLabels,
            string summaryLabel)
        {
            var sourceLabels = (definitionOrderedCategories ?? Enumerable.Empty<string>()).ToList();
            sourceLabels.AddRange(imageLabels ?? Enumerable.Empty<string>());
            sourceLabels.AddRange(categoryOrder ?? (IReadOnlyList<string>)Array.Empty<string>());
            if (summaryLabel != null)
            {
                sourceLabels.Add(summaryLabel);
            }

            var orderedLabels = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(
                sourceLabels,
                categoryOrder);
            EnsureDefaultCategoryLabel(orderedLabels, categoryOrder);
            return orderedLabels;
        }

        // Inserts the Default label when no achievement currently falls into it, at its
        // persisted custom-order position when one exists, otherwise at the end.
        private static void EnsureDefaultCategoryLabel(List<string> orderedLabels, IReadOnlyList<string> categoryOrder)
        {
            if (orderedLabels == null ||
                orderedLabels.Contains(AchievementCategoryHelper.DefaultCategoryLabel, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            var insertIndex = orderedLabels.Count;
            var defaultOrderIndex = AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndex(
                AchievementCategoryHelper.DefaultCategoryLabel,
                categoryOrder);
            if (defaultOrderIndex != int.MaxValue)
            {
                // Ordered labels precede unordered ones (index int.MaxValue), so the first
                // label ranked after Default marks the insertion point.
                for (var i = 0; i < orderedLabels.Count; i++)
                {
                    if (AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndex(orderedLabels[i], categoryOrder) > defaultOrderIndex)
                    {
                        insertIndex = i;
                        break;
                    }
                }
            }

            // Land on a root boundary. The ranked position can fall between a parent and its
            // children, and Default is a root - dropped there it would split a subtree, which reads
            // as an unrelated row wearing that subtree's connectors.
            while (insertIndex < orderedLabels.Count &&
                   CategoryPathHelper.GetDepth(orderedLabels[insertIndex]) > 1)
            {
                insertIndex++;
            }

            orderedLabels.Insert(insertIndex, AchievementCategoryHelper.DefaultCategoryLabel);
        }

        /// <summary>
        /// Validates one rename and yields the move it becomes, so a run of renames can be applied
        /// as a single batch instead of one store write each.
        /// </summary>
        public static bool TryPlanCategoryRename(
            string sourceCategoryLabel,
            string targetCategoryLabel,
            out KeyValuePair<string, string> move)
        {
            move = default(KeyValuePair<string, string>);
            var normalizedSourceCategory = AchievementCategoryHelper.NormalizeCategory(sourceCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedSourceCategory) ||
                CategoryEditSnapshot.IsDefaultLabel(normalizedSourceCategory))
            {
                // The Default bucket holds every achievement without an explicit category;
                // renaming it away would leave no fallback bucket.
                return false;
            }

            normalizedSourceCategory = CategoryPathHelper.NormalizePath(normalizedSourceCategory);
            var normalizedTargetCategory = CategoryPathHelper.NormalizePath(targetCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedTargetCategory) ||
                CategoryPathHelper.IsSame(normalizedSourceCategory, normalizedTargetCategory))
            {
                return false;
            }

            // A node cannot become its own descendant, and nothing may nest under Default: it has
            // no provider identity and refuses rename and merge, so a child there is unreachable.
            if (CategoryPathHelper.IsDescendantOf(normalizedTargetCategory, normalizedSourceCategory) ||
                CategoryEditSnapshot.IsDefaultLabel(CategoryPathHelper.Split(normalizedTargetCategory)[0]))
            {
                return false;
            }

            move = new KeyValuePair<string, string>(normalizedSourceCategory, normalizedTargetCategory);
            return true;
        }

        /// <summary>The moves that make each label a subcategory of the target (null = top level).</summary>
        public static List<KeyValuePair<string, string>> PlanNestMoves(
            CategoryEditSnapshot snapshot,
            IReadOnlyList<string> labels,
            string targetParentLabel)
        {
            return CategoryNestPlanner.PlanNestMoves(
                snapshot?.MovableLabels ?? (IReadOnlyList<string>)Array.Empty<string>(),
                labels,
                targetParentLabel);
        }

        /// <summary>Whether <see cref="NestCategoryRowsUnder"/> would move anything.</summary>
        public static bool CanNestCategoryRowsUnder(
            CategoryEditSnapshot snapshot,
            IReadOnlyList<string> labels,
            string targetParentLabel)
        {
            return PlanNestMoves(snapshot, labels, targetParentLabel).Count > 0;
        }

        /// <summary>
        /// How many achievements are filed under any of the labels or their descendants - what a
        /// delete would send back to the Default bucket, for the confirmation prompt.
        /// </summary>
        public static int CountAchievementsInCategories(CategoryEditSnapshot snapshot, IReadOnlyList<string> labels)
        {
            var normalized = (labels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(CategoryPathHelper.LabelComparer)
                .ToList();
            if (normalized.Count == 0 || snapshot == null)
            {
                return 0;
            }

            return snapshot.Members.Count(member => member != null && normalized.Any(label =>
                CategoryPathHelper.IsSelfOrDescendantOf(member.Category, label)));
        }

        internal static string ResolveStoredCategoryKind(IReadOnlyDictionary<string, string> storedKinds, string category)
        {
            var key = CategoryPathHelper.NormalizePath(category);
            return key != null && storedKinds != null && storedKinds.TryGetValue(key, out var kind) ? kind : null;
        }

        /// <summary>
        /// The single funnel for moving a category, whether the user renamed it, indented it, or
        /// dragged it onto another. Descendants follow, so one cycle guard covers every gesture.
        /// </summary>
        public CategoryEditOutcome RenameCategoryLabel(
            CategoryEditSnapshot snapshot,
            string sourceCategoryLabel,
            string targetCategoryLabel)
        {
            return TryPlanCategoryRename(sourceCategoryLabel, targetCategoryLabel, out var move)
                ? ApplyCategoryMoves(snapshot, new[] { move })
                : CategoryEditOutcome.None;
        }

        /// <summary>
        /// Makes each label a subcategory of <paramref name="targetParentLabel"/> (null = top level)
        /// as one batch: one snapshot, one store write.
        /// </summary>
        public CategoryEditOutcome NestCategoryRowsUnder(
            CategoryEditSnapshot snapshot,
            IReadOnlyList<string> labels,
            string targetParentLabel)
        {
            return ApplyCategoryMoves(snapshot, PlanNestMoves(snapshot, labels, targetParentLabel));
        }

        /// <summary>
        /// Applies a run of source-to-target label moves as one unit: membership is rewritten in a
        /// single pass over the achievements, the label-keyed metadata plans are chained, and both
        /// halves land in one store write.
        /// </summary>
        /// <param name="orderOverride">
        /// A gap drop settles the moved subtrees' positions in the same write as the reparent, so
        /// the order it computed replaces the plans' in-place rewrites.
        /// </param>
        public CategoryEditOutcome ApplyCategoryMoves(
            CategoryEditSnapshot snapshot,
            IReadOnlyList<KeyValuePair<string, string>> moves,
            IReadOnlyList<string> orderOverride = null,
            IReadOnlyList<string> movedSelectionLabels = null)
        {
            if (snapshot == null || moves == null || moves.Count == 0)
            {
                return CategoryEditOutcome.None;
            }

            var gameId = snapshot.GameId;
            var categoryOverrideMap = GetCategoryOverrideMap(gameId);
            var kinds = GetCategoryKinds(gameId);
            var order = GameCustomDataLookup.GetAchievementCategoryOrder(gameId, Settings, _store);
            var images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, Settings, _store);
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(gameId, Settings, _store);
            var summaryCategoryBefore = summaryCategory;
            var imagesBefore = images;

            var applied = false;
            foreach (var move in moves)
            {
                if (ReassignEffectiveCategoryRows(
                        snapshot.Members,
                        move.Key,
                        move.Value,
                        categoryOverrideMap,
                        rewriteDescendantPaths: true))
                {
                    applied = true;
                }

                if (MoveCategoryKinds(kinds, move.Key, move.Value))
                {
                    applied = true;
                }

                // Metadata moves even when no achievement did: an intermediate node can exist purely
                // as ordering and art, and it still has to follow its subtree.
                var plan = CategoryMetadataRenamer.Plan(move.Key, move.Value, order, images, summaryCategory);
                if (plan == null)
                {
                    continue;
                }

                order = plan.Order;
                images = plan.Images;
                summaryCategory = plan.SummaryCategory;
                applied = true;
            }

            if (!applied)
            {
                return CategoryEditOutcome.None;
            }

            if (orderOverride != null)
            {
                order = orderOverride.ToList();
            }

            // Moving an achievement between this game's categories changes nothing any library
            // rollup reads, so the write is scoped out of the overview's delta tick unless the
            // summary art itself moved.
            var affectsSummaryData = SummaryArtChanged(summaryCategoryBefore, imagesBefore, summaryCategory, images);
            _writer.SetAchievementCategoryAssignmentAndMetadata(
                gameId,
                categoryOverrideMap,
                kinds,
                order,
                images,
                summaryCategory,
                affectsSummaryData: affectsSummaryData);
            return new CategoryEditOutcome(
                applied: true,
                affectsSummaryData: affectsSummaryData,
                categoryOverrideMap: categoryOverrideMap,
                movedSelectionLabels: movedSelectionLabels?.ToList() ?? moves.Select(move => move.Value).ToList(),
                createdLabels: null);
        }

        /// <summary>
        /// Folds every achievement in <paramref name="sourceCategoryLabel"/> into
        /// <paramref name="targetCategoryLabel"/>: re-labels them to the target, which they take
        /// the kind of. If the source category was the game-summary-art source, that selection is
        /// reset; the target category's own state is left untouched. Merging the Default bucket
        /// away is refused; merging into Default remains a way to un-categorize achievements.
        /// </summary>
        public CategoryEditOutcome MergeCategoryInto(
            CategoryEditSnapshot snapshot,
            string sourceCategoryLabel,
            string targetCategoryLabel)
        {
            var normalizedSourceCategory = AchievementCategoryHelper.NormalizeCategory(sourceCategoryLabel);
            if (snapshot == null ||
                string.IsNullOrWhiteSpace(normalizedSourceCategory) ||
                CategoryEditSnapshot.IsDefaultLabel(normalizedSourceCategory))
            {
                return CategoryEditOutcome.None;
            }

            var normalizedTargetCategory = AchievementCategoryHelper.NormalizeCategoryOrDefault(targetCategoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedTargetCategory) ||
                string.Equals(normalizedSourceCategory, normalizedTargetCategory, CategoryPathHelper.LabelComparison))
            {
                return CategoryEditOutcome.None;
            }

            var gameId = snapshot.GameId;
            var categoryOverrideMap = GetCategoryOverrideMap(gameId);
            var membershipChanged = ReassignEffectiveCategoryRows(
                snapshot.Members,
                normalizedSourceCategory,
                normalizedTargetCategory,
                categoryOverrideMap);
            // The source folds into the target and takes its kind, so the source's own goes.
            var kinds = GetCategoryKinds(gameId);
            MoveCategoryKinds(kinds, normalizedSourceCategory, null);

            // A node with no achievements moves none, but it still occupies an order slot and can
            // hold art, so the merge proceeds on the metadata alone and folds the node away.
            if (!membershipChanged && !snapshot.ContainsLabel(normalizedSourceCategory))
            {
                return CategoryEditOutcome.None;
            }

            var summaryCategoryBefore = GameCustomDataLookup.GetGameSummaryCategory(gameId, Settings, _store);
            var imagesBefore = GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, Settings, _store);
            var metadata = PlanCategoryMergeMetadata(gameId, normalizedSourceCategory, normalizedTargetCategory);
            var affectsSummaryData = SummaryArtChanged(
                summaryCategoryBefore,
                imagesBefore,
                metadata.SummaryCategory,
                metadata.Images);
            _writer.SetAchievementCategoryAssignmentAndMetadata(
                gameId,
                categoryOverrideMap,
                kinds,
                metadata.Order,
                metadata.Images,
                metadata.SummaryCategory,
                affectsSummaryData: affectsSummaryData);
            return new CategoryEditOutcome(
                applied: true,
                affectsSummaryData: affectsSummaryData,
                categoryOverrideMap: categoryOverrideMap,
                movedSelectionLabels: null,
                createdLabels: null);
        }

        /// <summary>
        /// Creates an empty top-level category under a unique placeholder name derived from
        /// <paramref name="baseLeafName"/> and persists it in one write. Nothing is created when
        /// no label renders, since the new one could never render either.
        /// </summary>
        public CategoryEditOutcome AddNewCategory(CategoryEditSnapshot snapshot, string baseLeafName)
        {
            if (snapshot == null || snapshot.RenderedLabels.Count == 0)
            {
                return CategoryEditOutcome.None;
            }

            var existing = snapshot.NonBlankLabels.ToList();
            var label = CategoryNameGenerator.GenerateUniqueLabel(
                existing,
                parentPath: null,
                baseLeafName: baseLeafName);
            return PersistNewCategory(snapshot.GameId, existing, label);
        }

        /// <summary>
        /// Creates an empty top-level category under a name the user gave. A name that already
        /// names a top-level category adopts it, reported as the created label with nothing
        /// written, so naming something twice is not a way to end up with two of it.
        /// </summary>
        public CategoryEditOutcome CreateCategory(CategoryEditSnapshot snapshot, string leafName)
        {
            if (snapshot == null || snapshot.RenderedLabels.Count == 0)
            {
                return CategoryEditOutcome.None;
            }

            var label = CategoryPathHelper.SanitizeSegment(leafName);
            if (string.IsNullOrWhiteSpace(label))
            {
                return CategoryEditOutcome.None;
            }

            var existing = snapshot.NonBlankLabels.ToList();
            var alreadyThere = existing.FirstOrDefault(candidate => CategoryPathHelper.IsSame(candidate, label));
            if (!string.IsNullOrWhiteSpace(alreadyThere))
            {
                return new CategoryEditOutcome(
                    applied: false,
                    affectsSummaryData: false,
                    categoryOverrideMap: null,
                    movedSelectionLabels: null,
                    createdLabels: new[] { alreadyThere });
            }

            return PersistNewCategory(snapshot.GameId, existing, label);
        }

        private CategoryEditOutcome PersistNewCategory(Guid gameId, List<string> existing, string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return CategoryEditOutcome.None;
            }

            var order = existing.ToList();
            order.Add(label);

            // Creating an empty node is pure per-game display state, so the write is scoped out
            // of the library-wide passes like every other order edit.
            _writer.SetAchievementCategoryMetadata(
                gameId,
                order,
                GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, Settings, _store),
                GameCustomDataLookup.GetGameSummaryCategory(gameId, Settings, _store),
                affectsSummaryData: false);
            return new CategoryEditOutcome(
                applied: true,
                affectsSummaryData: false,
                categoryOverrideMap: null,
                movedSelectionLabels: null,
                createdLabels: new[] { label });
        }

        /// <summary>
        /// Creates an empty sibling copy of each node: same parent, art override copied, unique
        /// leaf name ("DLC (2)"), placed right after the source's subtree. Achievements, the
        /// subtree, and the summary selection (single-choice) are not copied. One write for the
        /// whole batch.
        /// </summary>
        public CategoryEditOutcome DuplicateCategories(CategoryEditSnapshot snapshot, IReadOnlyList<string> labels)
        {
            if (snapshot == null)
            {
                return CategoryEditOutcome.None;
            }

            var rendered = snapshot.NonBlankLabels;
            var sources = (labels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(CategoryPathHelper.LabelComparer)
                .Where(label =>
                    !CategoryEditSnapshot.IsDefaultLabel(label) &&
                    rendered.Any(existing => CategoryPathHelper.IsSame(existing, label)))
                .ToList();
            if (sources.Count == 0)
            {
                return CategoryEditOutcome.None;
            }

            var gameId = snapshot.GameId;
            var order = rendered.ToList();
            var images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, Settings, _store)
                ?.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone(), CategoryPathHelper.LabelComparer)
                ?? new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);

            var created = new List<string>();
            foreach (var source in sources)
            {
                var newLabel = CategoryNameGenerator.GenerateUniqueLabel(
                    order,
                    CategoryPathHelper.GetParentPath(source),
                    CategoryPathHelper.GetLeafName(source));
                if (string.IsNullOrWhiteSpace(newLabel))
                {
                    continue;
                }

                // Beside the whole subtree, not inside it: the copy is a sibling of the source.
                var insertIndex = order.Count;
                for (var i = order.Count - 1; i >= 0; i--)
                {
                    if (CategoryPathHelper.IsSelfOrDescendantOf(order[i], source))
                    {
                        insertIndex = i + 1;
                        break;
                    }
                }

                order.Insert(insertIndex, newLabel);
                if (images.TryGetValue(source, out var sourceArt) && sourceArt != null)
                {
                    images[newLabel] = sourceArt.Clone();
                }

                created.Add(newLabel);
            }

            if (created.Count == 0)
            {
                return CategoryEditOutcome.None;
            }

            // Copies are empty nodes and the summary selection is untouched, so the write stays
            // scoped out of the library-wide passes.
            _writer.SetAchievementCategoryMetadata(
                gameId,
                order,
                images,
                GameCustomDataLookup.GetGameSummaryCategory(gameId, Settings, _store),
                affectsSummaryData: false);
            return new CategoryEditOutcome(
                applied: true,
                affectsSummaryData: false,
                categoryOverrideMap: null,
                movedSelectionLabels: null,
                createdLabels: created);
        }

        /// <summary>
        /// Deletes each category and its subtree: every achievement filed inside goes back to the
        /// Default bucket, and the labels leave the order, the art overrides, and the summary
        /// selection. Membership and metadata land in one write. The Default bucket itself is
        /// refused.
        /// </summary>
        public CategoryEditOutcome DeleteCategories(CategoryEditSnapshot snapshot, IReadOnlyList<string> labels)
        {
            if (snapshot == null)
            {
                return CategoryEditOutcome.None;
            }

            var rendered = snapshot.NonBlankLabels;
            var deletable = (labels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(CategoryPathHelper.LabelComparer)
                .Where(label =>
                    !CategoryEditSnapshot.IsDefaultLabel(label) &&
                    rendered.Any(existing => CategoryPathHelper.IsSame(existing, label)))
                .ToList();

            // A selected descendant of a selected ancestor is dropped: the ancestor's subtree
            // delete carries it.
            deletable = deletable
                .Where(label => !deletable.Any(other => CategoryPathHelper.IsDescendantOf(label, other)))
                .ToList();
            if (deletable.Count == 0)
            {
                return CategoryEditOutcome.None;
            }

            // Un-categorize the whole subtree's achievements: the reassign sweeps self and
            // descendants and flattens them onto the target, which for Default is exactly
            // "no category". The deleted categories' kinds go with them.
            var gameId = snapshot.GameId;
            var categoryOverrideMap = GetCategoryOverrideMap(gameId);
            var kinds = GetCategoryKinds(gameId);
            foreach (var label in deletable)
            {
                ReassignEffectiveCategoryRows(
                    snapshot.Members,
                    label,
                    AchievementCategoryHelper.DefaultCategoryLabel,
                    categoryOverrideMap);
                MoveCategoryKinds(kinds, label, null);
            }

            var order = rendered
                .Where(label => !deletable.Any(deleted => CategoryPathHelper.IsSelfOrDescendantOf(label, deleted)))
                .ToList();

            var imagesBefore = GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, Settings, _store);
            var images = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            foreach (var pair in imagesBefore ?? new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer))
            {
                if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Key) ||
                    deletable.Any(deleted => CategoryPathHelper.IsSelfOrDescendantOf(pair.Key, deleted)))
                {
                    continue;
                }

                images[pair.Key] = pair.Value.Clone();
            }

            var summaryBefore = GameCustomDataLookup.GetGameSummaryCategory(gameId, Settings, _store);
            var summaryCategory = summaryBefore;
            if (summaryCategory != null &&
                deletable.Any(deleted => CategoryPathHelper.IsSelfOrDescendantOf(summaryCategory.Label, deleted)))
            {
                summaryCategory = null;
            }

            var affectsSummaryData = SummaryArtChanged(summaryBefore, imagesBefore, summaryCategory, images);
            _writer.SetAchievementCategoryAssignmentAndMetadata(
                gameId,
                categoryOverrideMap,
                kinds,
                order,
                images,
                summaryCategory,
                affectsSummaryData: affectsSummaryData);
            return new CategoryEditOutcome(
                applied: true,
                affectsSummaryData: affectsSummaryData,
                categoryOverrideMap: categoryOverrideMap,
                movedSelectionLabels: null,
                createdLabels: null);
        }

        /// <summary>The stored per-achievement category overrides, keys trimmed and blank values dropped.</summary>
        public Dictionary<string, string> GetCategoryOverrideMap(Guid gameId)
        {
            var map = GameCustomDataLookup.GetAchievementCategoryOverrides(gameId, Settings, _store);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var category = (pair.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                normalized[apiName] = category;
            }

            return normalized;
        }

        /// <summary>The stored tag overrides; "" values are kept, recording that the user removed every tag.</summary>
        public Dictionary<string, string> GetTagOverrideMap(Guid gameId)
        {
            return GameCustomDataLookup.GetAchievementTagOverrides(gameId, Settings, _store);
        }

        public Dictionary<string, string> GetCategoryKinds(Guid gameId)
        {
            return GameCustomDataFile.CloneCategoryKindMap(
                       GameCustomDataLookup.GetAchievementCategoryKinds(gameId, Settings, _store)) ??
                   new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
        }

        private PersistedSettings Settings => _settings();

        /// <summary>
        /// Re-points every member whose effective category equals <paramref name="normalizedSource"/>
        /// to <paramref name="normalizedTarget"/> in <paramref name="categoryOverrideMap"/>. An
        /// override is removed rather than set when the resulting value matches the member's
        /// provider default. Returns true when at least one member was affected and something changed.
        /// </summary>
        /// <param name="rewriteDescendantPaths">
        /// True for a rename or reparent, where a descendant keeps its position beneath the moved
        /// node ("DLC::Winter" follows "DLC" to "Extras::Winter"). False for a merge, which folds
        /// the whole subtree flat into the target.
        /// </param>
        private static bool ReassignEffectiveCategoryRows(
            IReadOnlyList<CategoryEditMember> members,
            string normalizedSource,
            string normalizedTarget,
            Dictionary<string, string> categoryOverrideMap,
            bool rewriteDescendantPaths = false)
        {
            var affectedCount = 0;
            var changed = false;

            foreach (var member in members.Where(candidate => candidate != null))
            {
                var apiName = member.ApiName;
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var effectiveCategory = CategoryPathHelper.NormalizePath(member.Category);
                if (!CategoryPathHelper.IsSelfOrDescendantOf(effectiveCategory, normalizedSource))
                {
                    continue;
                }

                affectedCount++;

                var writtenCategory = rewriteDescendantPaths
                    ? CategoryPathHelper.RewritePrefix(effectiveCategory, normalizedSource, normalizedTarget)
                    : normalizedTarget;

                // Compare the value actually being written against the provider label, so the
                // economy of dropping a redundant override still holds per achievement.
                var providerCategory = CategoryPathHelper.NormalizePath(member.ProviderCategory);
                if (CategoryPathHelper.IsSame(providerCategory, writtenCategory))
                {
                    if (categoryOverrideMap.Remove(apiName))
                    {
                        changed = true;
                    }
                }
                else if (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                         !string.Equals(existingCategory, writtenCategory, StringComparison.Ordinal))
                {
                    categoryOverrideMap[apiName] = writtenCategory;
                    changed = true;
                }
            }

            return affectedCount > 0 && changed;
        }

        /// <summary>
        /// Moves the kinds stored for <paramref name="source"/> and its descendants to the same place
        /// under <paramref name="target"/> ("DLC::Winter" follows "DLC" to "Extras::Winter"), or
        /// drops them when <paramref name="target"/> is null. True when the map changed.
        /// </summary>
        private static bool MoveCategoryKinds(Dictionary<string, string> kinds, string source, string target)
        {
            var normalizedSource = CategoryPathHelper.NormalizePath(source);
            if (kinds == null || kinds.Count == 0 || normalizedSource == null)
            {
                return false;
            }

            var moved = kinds
                .Where(pair => CategoryPathHelper.IsSelfOrDescendantOf(pair.Key, normalizedSource))
                .ToList();
            foreach (var pair in moved)
            {
                kinds.Remove(pair.Key);
            }

            if (target != null)
            {
                var normalizedTarget = CategoryPathHelper.NormalizePath(target);
                foreach (var pair in moved)
                {
                    kinds[CategoryPathHelper.RewritePrefix(pair.Key, normalizedSource, normalizedTarget)] = pair.Value;
                }
            }

            return moved.Count > 0;
        }

        /// <summary>
        /// The label-keyed metadata a merge produces. Computed rather than written so the caller
        /// can land it together with the membership rewrite in a single store update.
        /// </summary>
        private CategoryMetadataPlan PlanCategoryMergeMetadata(Guid gameId, string sourceCategory, string targetCategory)
        {
            var normalizedSource = AchievementCategoryHelper.NormalizeCategoryOrDefault(sourceCategory);
            var normalizedTarget = AchievementCategoryHelper.NormalizeCategoryOrDefault(targetCategory);
            if (string.IsNullOrWhiteSpace(normalizedSource) ||
                string.IsNullOrWhiteSpace(normalizedTarget) ||
                string.Equals(normalizedSource, normalizedTarget, CategoryPathHelper.LabelComparison))
            {
                return CurrentCategoryMetadataPlan(gameId);
            }

            var currentOrder = GameCustomDataLookup.GetAchievementCategoryOrder(gameId, Settings, _store);
            var currentImages = GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, Settings, _store);

            // Collapse the source's order slot onto the target's existing position (dedupe).
            var nextOrder = new List<string>();
            var seen = new HashSet<string>(CategoryPathHelper.LabelComparer);
            foreach (var label in currentOrder ?? Enumerable.Empty<string>())
            {
                var normalized = CategoryPathHelper.NormalizePath(label);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                // The whole subtree collapses onto the target, so a descendant of the source
                // folds too rather than being left pointing at a label that no longer exists.
                if (CategoryPathHelper.IsSelfOrDescendantOf(normalized, normalizedSource))
                {
                    normalized = normalizedTarget;
                }

                if (seen.Add(normalized))
                {
                    nextOrder.Add(normalized);
                }
            }

            // Drop the merged-away subtree's art overrides. Unlike the rename path, a merge does not
            // fold the source's art into the target: the target is left exactly as it was.
            var nextImages = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            foreach (var pair in currentImages ?? new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer))
            {
                var key = CategoryPathHelper.NormalizePath(pair.Key);
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
                {
                    continue;
                }

                // A merge folds the whole subtree, so a descendant's art goes with it rather than
                // being stranded under a label nothing points at any more.
                if (CategoryPathHelper.IsSelfOrDescendantOf(key, normalizedSource))
                {
                    continue;
                }

                nextImages[key] = pair.Value.Clone();
            }

            // Reset the game-summary-art selection when the merged-away source held it; leave any
            // other selection (including the target's) untouched.
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(gameId, Settings, _store);
            if (summaryCategory != null &&
                CategoryPathHelper.IsSelfOrDescendantOf(summaryCategory.Label, normalizedSource))
            {
                summaryCategory = null;
            }

            return new CategoryMetadataPlan
            {
                Order = nextOrder,
                Images = nextImages,
                SummaryCategory = summaryCategory
            };
        }

        /// <summary>The stored metadata as it stands, for a plan that turns out to be a no-op.</summary>
        private CategoryMetadataPlan CurrentCategoryMetadataPlan(Guid gameId)
        {
            return new CategoryMetadataPlan
            {
                Order = GameCustomDataLookup.GetAchievementCategoryOrder(gameId, Settings, _store)?.ToList(),
                Images = GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, Settings, _store)
                    ?.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone(), CategoryPathHelper.LabelComparer),
                SummaryCategory = GameCustomDataLookup.GetGameSummaryCategory(gameId, Settings, _store)
            };
        }

        /// <summary>
        /// Whether a metadata write can move the game's summary art, which is the only thing a
        /// category edit changes that a library-wide surface reads. GameSummaryArtResolver looks at
        /// exactly two things: which category is selected, and that one category's art override.
        /// </summary>
        internal static bool SummaryArtChanged(
            GameSummaryCategoryData before,
            IReadOnlyDictionary<string, CategoryImageOverrideData> beforeImages,
            GameSummaryCategoryData after,
            IReadOnlyDictionary<string, CategoryImageOverrideData> afterImages)
        {
            if (before == null || after == null)
            {
                return !ReferenceEquals(before, after);
            }

            if (!string.Equals(before.Label, after.Label, CategoryPathHelper.LabelComparison) ||
                !string.Equals(before.ProviderLabel, after.ProviderLabel, CategoryPathHelper.LabelComparison))
            {
                return true;
            }

            // A move can fold the source category's art onto an unchanged target label, so the
            // selection matching is not on its own enough to call the art unchanged.
            return !string.Equals(
                ResolveCategoryArtOverride(beforeImages, before.Label),
                ResolveCategoryArtOverride(afterImages, after.Label),
                StringComparison.Ordinal);
        }

        private static string ResolveCategoryArtOverride(
            IReadOnlyDictionary<string, CategoryImageOverrideData> images,
            string categoryLabel)
        {
            CategoryImageOverrideData imageOverride = null;
            if (images != null && !string.IsNullOrWhiteSpace(categoryLabel))
            {
                images.TryGetValue(categoryLabel, out imageOverride);
            }

            return (imageOverride?.Art ?? string.Empty).Trim();
        }
    }
}
