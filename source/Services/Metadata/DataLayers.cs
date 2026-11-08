using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Which data areas a game's achievements carry values for: the area masks tagging reads.
    /// </summary>
    internal static class DataLayers
    {
        /// <summary>The areas whose values live on the achievements, in a stable order.</summary>
        public static readonly IReadOnlyList<CustomizationArea> ValueAreas = new[]
        {
            CustomizationArea.Icons,
            CustomizationArea.TrophyTypes,
            CustomizationArea.Points,
            CustomizationArea.Text,
            CustomizationArea.Categories,
            CustomizationArea.Tags
        };

        /// <summary>Each value area's non-empty values by ApiName.</summary>
        public sealed class Snapshot
        {
            internal Snapshot(Dictionary<CustomizationArea, Dictionary<string, string>> values, int rowCount)
            {
                Values = values;
                RowCount = rowCount;
            }

            public Dictionary<CustomizationArea, Dictionary<string, string>> Values { get; }

            /// <summary>The achievements with an ApiName.</summary>
            public int RowCount { get; }

            /// <summary>
            /// Whether the achievements are sorted into categories: they are unless every one is
            /// filed under the same category, as SteamHunters files a game with no groups under the
            /// game's name.
            /// </summary>
            public bool IsCategorized
            {
                get
                {
                    var categories = Values[CustomizationArea.Categories];
                    return categories.Count > 0 &&
                           (categories.Count < RowCount ||
                            categories.Values.Distinct(CategoryPathHelper.LabelComparer).Skip(1).Any());
                }
            }

            /// <summary>Whether the area holds any value, with categories counting only when sorted.</summary>
            public bool Has(CustomizationArea area) =>
                area == CustomizationArea.Categories ? IsCategorized : Values[area].Count > 0;
        }

        public static Snapshot Take(IEnumerable<AchievementDetail> achievements)
        {
            var rows = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                .ToList();
            var values = ValueAreas.ToDictionary(
                area => area,
                area =>
                {
                    var byApiName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in rows)
                    {
                        var value = ValueOf(row, area);
                        if (value != null)
                        {
                            byApiName[row.ApiName.Trim()] = value;
                        }
                    }

                    return byApiName;
                });

            return new Snapshot(values, rows.Count);
        }

        /// <summary>The value areas any achievement carries a value for, from whatever source.</summary>
        public static CustomizationArea Present(IEnumerable<AchievementDetail> achievements)
        {
            var snapshot = Take(achievements);
            var present = ValueAreas
                .Where(snapshot.Has)
                .Aggregate(CustomizationArea.None, (mask, area) => mask | area);
            return HasCapstone(achievements) ? present | CustomizationArea.Capstones : present;
        }

        /// <summary>Whether any achievement is a capstone: flagged as one, or a platinum trophy.</summary>
        public static bool HasCapstone(IEnumerable<AchievementDetail> achievements) =>
            (achievements ?? Enumerable.Empty<AchievementDetail>()).Any(row => row != null && (row.IsCapstone || IsPlatinum(row)));

        internal static string ValueOf(AchievementDetail row, CustomizationArea area)
        {
            switch (area)
            {
                case CustomizationArea.Icons:
                    return Blank(row.UnlockedIconPath) ?? Blank(row.LockedIconPath);
                case CustomizationArea.TrophyTypes:
                    return Blank(row.TrophyType);
                case CustomizationArea.Points:
                    return row.Points?.ToString(CultureInfo.InvariantCulture);
                case CustomizationArea.Text:
                    // Name and description as one value, so a source that changed only the
                    // description supplied the text.
                    var name = Blank(row.DisplayName);
                    var description = Blank(row.Description);
                    return name == null && description == null ? null : name + "\u001F" + description;
                case CustomizationArea.Categories:
                    return NamedCategory(row.Category);
                case CustomizationArea.Tags:
                    return Blank(AchievementTagHelper.StripDerived(row.Tags));
                default:
                    return null;
            }
        }

        /// <summary>
        /// A category other than the Default bucket, or null: an achievement filed under Default
        /// has no category from any source.
        /// </summary>
        internal static string NamedCategory(string category)
        {
            var path = CategoryPathHelper.NormalizePath(category);
            return string.IsNullOrWhiteSpace(path) ||
                   string.Equals(path, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? null
                : path;
        }

        private static bool IsPlatinum(AchievementDetail row) =>
            string.Equals(row.TrophyType?.Trim(), "platinum", StringComparison.OrdinalIgnoreCase);

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
