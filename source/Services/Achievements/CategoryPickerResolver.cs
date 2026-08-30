using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// One row in a category picker: the storage path it resolves to, the leaf the list shows, the
    /// full path it offers on hover, and where it sits in the tree the list draws.
    /// </summary>
    public sealed class CategoryPickerOption
    {
        public CategoryPickerOption(
            string label,
            string leafDisplay,
            string pathDisplay,
            CategoryTreeShape treeShape = null,
            bool isSelectable = true)
        {
            Label = label;
            LeafDisplay = leafDisplay;
            PathDisplay = pathDisplay;
            TreeShape = treeShape;
            IsSelectable = isSelectable;
        }

        /// <summary>Storage form - the value written back, never shown.</summary>
        public string Label { get; }

        /// <summary>What the list renders: the last path segment.</summary>
        public string LeafDisplay { get; }

        /// <summary>Full display path, offered on hover so two same-named leaves stay tellable apart.</summary>
        public string PathDisplay { get; }

        /// <summary>
        /// Connector geometry for this row, or null when the list has no nesting to show. A null
        /// shape collapses the tree guide to zero width, so a flat list costs nothing.
        /// </summary>
        public CategoryTreeShape TreeShape { get; }

        /// <summary>
        /// False for a row that exists only to carry the tree - an ancestor synthesised to give a
        /// nested label a parent to hang off. It is drawn, so the shape reads, but it is not a
        /// target a caller can resolve to.
        /// </summary>
        public bool IsSelectable { get; }
    }

    /// <summary>
    /// Turns what a user did in a category picker - picked a row, typed a name, or both - into the
    /// category label to store.
    ///
    /// Kept separate from the control so the rules are testable without a UI: which of several
    /// same-named leaves a typed name resolves to, and when typing creates a category rather than
    /// selecting one, are decisions worth pinning down independently of how the box is drawn.
    /// </summary>
    internal static class CategoryPickerResolver
    {
        /// <summary>
        /// Builds the options for a set of existing labels, arranged as the tree they describe:
        /// pre-order, siblings contiguous, each row carrying the connectors that place it.
        ///
        /// Ancestors missing from the input are synthesised, because a nested row whose parent has
        /// no row of its own would otherwise draw a stem hanging off nothing.
        /// <paramref name="synthesizedAreSelectable"/> decides what those rows are worth: a picker
        /// assigning a category can target a parent that holds no achievements of its own, while a
        /// filter cannot - selecting it would match nothing.
        ///
        /// A list with no nesting in it leaves every shape null, so a flat picker looks exactly as
        /// it did before there was a tree to draw.
        /// </summary>
        public static List<CategoryPickerOption> BuildOptions(
            IEnumerable<string> labels,
            IReadOnlyList<string> preferredOrder = null,
            bool synthesizedAreSelectable = true)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<string>();
            foreach (var raw in labels ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var label = CategoryPathHelper.NormalizePath(raw);
                if (present.Add(label))
                {
                    normalized.Add(label);
                }
            }

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(
                normalized,
                preferredOrder);

            // Built over the emitted rows, which is what the guide draws against: gating on nesting
            // here is what keeps a flat list from reserving a gutter it has no use for.
            var shapes = CategoryTreeShapeBuilder.HasNesting(ordered)
                ? CategoryTreeShapeBuilder.Build(ordered)
                : null;

            var options = new List<CategoryPickerOption>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var label = ordered[i];
                options.Add(new CategoryPickerOption(
                    label,
                    AchievementCategoryTypeHelper.ToCategoryLeafDisplayText(label),
                    AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(label),
                    shapes?[i],
                    synthesizedAreSelectable || present.Contains(label)));
            }

            return options;
        }

        /// <summary>
        /// The label to store for the current state of a picker.
        ///
        /// A row the user actually picked wins outright, including when its leaf is ambiguous - it
        /// is the one unambiguous signal available. Otherwise the typed text is matched against the
        /// existing leaves: exactly one match adopts that category, so typing the name of something
        /// that already exists targets it instead of creating a second one alongside it. Anything
        /// else - no match, or several - becomes a new root category.
        ///
        /// Rows that only carry the tree are not candidates for either route: they are structure,
        /// not categories this picker offers.
        ///
        /// Typed text can only ever produce a root. The path separator is internal and rejected on
        /// input, so nesting stays something created structurally by indent/outdent rather than a
        /// second syntax a user has to know about.
        /// </summary>
        public static string Resolve(
            string typedText,
            CategoryPickerOption pickedOption,
            IReadOnlyList<CategoryPickerOption> options)
        {
            var text = (typedText ?? string.Empty).Trim();

            if (pickedOption != null &&
                pickedOption.IsSelectable &&
                string.Equals(pickedOption.LeafDisplay, text, StringComparison.OrdinalIgnoreCase))
            {
                return pickedOption.Label;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var matches = (options ?? new List<CategoryPickerOption>())
                .Where(option => option != null &&
                    option.IsSelectable &&
                    string.Equals(option.LeafDisplay, text, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.Count == 1
                ? matches[0].Label
                : CategoryPathHelper.NormalizePath(text);
        }
    }
}
