using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// One achievement as a category edit sees it: where it is filed now and where its provider
    /// filed it. Both labels are in <see cref="AchievementCategoryHelper.NormalizeCategoryOrDefault"/>
    /// form, so an achievement with no category reads as the Default bucket.
    /// </summary>
    internal sealed class CategoryEditMember
    {
        public CategoryEditMember(string apiName, string category, string providerCategory)
        {
            ApiName = (apiName ?? string.Empty).Trim();
            Category = AchievementCategoryHelper.NormalizeCategoryOrDefault(category);
            ProviderCategory = AchievementCategoryHelper.NormalizeCategoryOrDefault(providerCategory);
        }

        public string ApiName { get; }

        /// <summary>The effective category: the override when one is stored, else the provider's.</summary>
        public string Category { get; }

        public string ProviderCategory { get; }
    }

    /// <summary>
    /// What a category edit plans against: the game's achievements and the category labels as they
    /// render, in tree order and including the Default bucket. Taken once per gesture, so every
    /// move in a batch resolves against the same picture rather than one earlier moves rewrote.
    /// </summary>
    internal sealed class CategoryEditSnapshot
    {
        private List<string> _movableLabels;
        private List<string> _nonBlankLabels;

        public CategoryEditSnapshot(
            Guid gameId,
            IReadOnlyList<CategoryEditMember> members,
            IReadOnlyList<string> renderedLabels)
        {
            GameId = gameId;
            Members = members ?? Array.Empty<CategoryEditMember>();
            RenderedLabels = renderedLabels ?? Array.Empty<string>();
        }

        public Guid GameId { get; }

        public IReadOnlyList<CategoryEditMember> Members { get; }

        /// <summary>Rendered labels in render order, Default included, as the writer stores the order.</summary>
        public IReadOnlyList<string> RenderedLabels { get; }

        /// <summary>Rendered labels with blanks dropped: the list Add, Duplicate and Delete rebuild the order from.</summary>
        public IReadOnlyList<string> NonBlankLabels =>
            _nonBlankLabels ?? (_nonBlankLabels = RenderedLabels
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList());

        /// <summary>
        /// Rendered labels the nest planner may move, normalized and with Default excluded: it has
        /// no provider identity and nothing may nest under it.
        /// </summary>
        public IReadOnlyList<string> MovableLabels =>
            _movableLabels ?? (_movableLabels = RenderedLabels
                .Where(label => !IsDefaultLabel(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList());

        public bool ContainsLabel(string label)
        {
            return !string.IsNullOrWhiteSpace(label) &&
                   RenderedLabels.Any(existing => CategoryPathHelper.IsSame(existing, label));
        }

        public static bool IsDefaultLabel(string label)
        {
            return string.Equals(
                label,
                AchievementCategoryHelper.DefaultCategoryLabel,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// What a category edit did, for the caller to fold into its own state. The write itself has
    /// already landed when this is returned.
    /// </summary>
    internal sealed class CategoryEditOutcome
    {
        public static readonly CategoryEditOutcome None = new CategoryEditOutcome(false, false, null, null, null);

        public CategoryEditOutcome(
            bool applied,
            bool affectsSummaryData,
            IReadOnlyDictionary<string, string> categoryOverrideMap,
            IReadOnlyList<string> movedSelectionLabels,
            IReadOnlyList<string> createdLabels)
        {
            Applied = applied;
            AffectsSummaryData = affectsSummaryData;
            CategoryOverrideMap = categoryOverrideMap;
            MovedSelectionLabels = movedSelectionLabels;
            CreatedLabels = createdLabels ?? Array.Empty<string>();
        }

        /// <summary>True when a store write happened.</summary>
        public bool Applied { get; }

        /// <summary>
        /// The flag the write carried: true only when the game's summary art selection moved, the
        /// one thing here a library-wide surface reads. A false write owes the library a scoped
        /// catch-up, which the Manage tab defers to its teardown and a grid menu pays at once.
        /// </summary>
        public bool AffectsSummaryData { get; }

        /// <summary>The ApiName-keyed membership as written, null when the edit left membership alone.</summary>
        public IReadOnlyDictionary<string, string> CategoryOverrideMap { get; }

        /// <summary>The labels the moved rows now carry, so a view can keep them selected.</summary>
        public IReadOnlyList<string> MovedSelectionLabels { get; }

        /// <summary>Labels created by the edit, in creation order.</summary>
        public IReadOnlyList<string> CreatedLabels { get; }

        public string CreatedLabel => CreatedLabels.Count > 0 ? CreatedLabels[0] : null;
    }
}
