using System;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Category labels: normalization of a stored label and its display forms. A blank label
    /// belongs to the Default category.
    /// </summary>
    internal static class AchievementCategoryHelper
    {
        public const string DefaultCategoryLabel = "Default";

        public static string NormalizeCategory(string rawValue)
        {
            var normalized = (rawValue ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public static string NormalizeCategoryOrDefault(string rawValue)
        {
            // The Default bucket matches in any case and keeps one spelling, as in
            // CategoryPathHelper.NormalizePath; every other label keeps its case.
            var normalized = NormalizeCategory(rawValue);
            return string.IsNullOrWhiteSpace(normalized) ||
                   string.Equals(normalized, DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? DefaultCategoryLabel
                : normalized;
        }

        /// <summary>
        /// A nested label renders with its separator spelled out: "DLC::Season Pass" reads as
        /// "DLC &gt; Season Pass". A flat label is returned exactly as before.
        /// </summary>
        public static string ToCategoryLabelDisplayText(string rawValue)
        {
            var label = CategoryPathHelper.NormalizePath(rawValue);
            return string.Equals(label, DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? L("LOCDefault", DefaultCategoryLabel)
                : CategoryPathHelper.ToDisplayPath(label);
        }

        /// <summary>
        /// Leaf variant of <see cref="ToCategoryLabelDisplayText"/>, for surfaces that convey
        /// ancestry structurally rather than in the text - a nested dropdown, an indented row.
        /// "DLC::Season Pass" reads as "Season Pass".
        /// </summary>
        public static string ToCategoryLeafDisplayText(string rawValue)
        {
            var label = CategoryPathHelper.NormalizePath(rawValue);
            return string.Equals(label, DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? L("LOCDefault", DefaultCategoryLabel)
                : CategoryPathHelper.ToDisplayLeaf(label);
        }

        /// <summary>
        /// Grid-cell variant of <see cref="ToCategoryLabelDisplayText"/>: the Default bucket
        /// renders as an empty string instead of the localized "Default" placeholder. Category
        /// management rows, summaries, and theme options keep the named bucket.
        ///
        /// <paramref name="display"/> picks the leaf (the default), the top level, or the full path;
        /// the full path is also the cell's tooltip - see <see cref="ToCategoryLabelCellPathText"/>.
        /// </summary>
        public static string ToCategoryLabelCellText(
            string rawValue,
            CategoryColumnDisplay display = CategoryColumnDisplay.Subcategory)
        {
            if (NormalizeCategory(rawValue) == null)
            {
                return string.Empty;
            }

            var label = CategoryPathHelper.NormalizePath(rawValue);
            if (string.Equals(label, DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            switch (display)
            {
                case CategoryColumnDisplay.TopLevel:
                    return CategoryPathHelper.GetRootName(label);
                case CategoryColumnDisplay.FullPath:
                    return CategoryPathHelper.ToDisplayPath(label);
                default:
                    return CategoryPathHelper.ToDisplayLeaf(label);
            }
        }

        /// <summary>
        /// Tooltip companion to <see cref="ToCategoryLabelCellText"/>: the full path, so hovering a
        /// cell reveals where a nested category sits. For a flat label this is the label itself,
        /// which also keeps the pre-existing reveal of a name the column had to ellipsize.
        /// </summary>
        public static string ToCategoryLabelCellPathText(string rawValue)
        {
            if (NormalizeCategory(rawValue) == null)
            {
                return string.Empty;
            }

            var label = CategoryPathHelper.NormalizePath(rawValue);
            return string.Equals(label, DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : CategoryPathHelper.ToDisplayPath(label);
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Length > 4 &&
                value.StartsWith("<!", StringComparison.Ordinal) &&
                value.EndsWith("!>", StringComparison.Ordinal)
                ? fallback
                : value;
        }
    }
}
