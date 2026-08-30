using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// One selectable category in a picker: the storage path it resolves to, the leaf the list
    /// shows, and the full path it offers on hover.
    /// </summary>
    public sealed class CategoryPickerOption
    {
        public CategoryPickerOption(string label, string leafDisplay, string pathDisplay)
        {
            Label = label;
            LeafDisplay = leafDisplay;
            PathDisplay = pathDisplay;
        }

        /// <summary>Storage form - the value written back, never shown.</summary>
        public string Label { get; }

        /// <summary>What the list renders: the last path segment.</summary>
        public string LeafDisplay { get; }

        /// <summary>Full display path, offered on hover so two same-named leaves stay tellable apart.</summary>
        public string PathDisplay { get; }
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
        /// Builds the options for a set of existing labels, in the order given, de-duplicated.
        /// </summary>
        public static List<CategoryPickerOption> BuildOptions(IEnumerable<string> labels)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new List<CategoryPickerOption>();
            foreach (var raw in labels ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var label = CategoryPathHelper.NormalizePath(raw);
                if (!seen.Add(label))
                {
                    continue;
                }

                options.Add(new CategoryPickerOption(
                    label,
                    AchievementCategoryTypeHelper.ToCategoryLeafDisplayText(label),
                    AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(label)));
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
                    string.Equals(option.LeafDisplay, text, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.Count == 1
                ? matches[0].Label
                : CategoryPathHelper.NormalizePath(text);
        }
    }
}
