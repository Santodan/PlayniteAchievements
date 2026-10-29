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
    /// Works out which data areas a refreshed game got from its own platform and which from a
    /// metadata source. The overlay writes into the payload in place, so the platform's values are
    /// captured before it runs and compared with the result afterwards.
    /// </summary>
    /// <remarks>
    /// An achievement's value counts as the platform's when it survives the overlay unchanged, and
    /// as metadata when the overlay left a different, non-empty value. A source that failed and
    /// had its last saved values carried forward is judged the same way: carried platform values
    /// equal the snapshot, carried metadata values differ from it.
    /// </remarks>
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

        /// <summary>
        /// Each value area's non-empty values by ApiName, plus whether any row is the platform's
        /// own capstone.
        /// </summary>
        public sealed class Snapshot
        {
            internal Snapshot(Dictionary<CustomizationArea, Dictionary<string, string>> values, bool hasCapstone, int rowCount)
            {
                Values = values;
                HasCapstone = hasCapstone;
                RowCount = rowCount;
            }

            public Dictionary<CustomizationArea, Dictionary<string, string>> Values { get; }

            public bool HasCapstone { get; }

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

            return new Snapshot(values, rows.Any(IsPlatformCapstone), rows.Count);
        }

        /// <summary>
        /// Per value area, the ApiNames whose value after the overlay is non-empty and differs from
        /// <paramref name="before"/>.
        /// </summary>
        public static Dictionary<CustomizationArea, HashSet<string>> ChangedSince(
            Snapshot before,
            IEnumerable<AchievementDetail> after)
        {
            var current = Take(after);
            return ValueAreas.ToDictionary(
                area => area,
                area =>
                {
                    var was = before?.Values[area] ?? new Dictionary<string, string>();
                    return new HashSet<string>(
                        current.Values[area]
                            .Where(pair => !was.TryGetValue(pair.Key, out var old) || !string.Equals(old, pair.Value, StringComparison.Ordinal))
                            .Select(pair => pair.Key),
                        StringComparer.OrdinalIgnoreCase);
                });
        }

        /// <summary>
        /// The platform and metadata masks for a refreshed game.
        /// </summary>
        /// <param name="before">The payload as the provider built it, before the overlay.</param>
        /// <param name="after">The payload after the overlay.</param>
        /// <param name="metadataByArea">
        /// ApiNames a metadata source supplied per value area: the overlay's changes.
        /// </param>
        /// <param name="metadataCapstone">Whether the overlay borrowed a capstone for the game.</param>
        public static (CustomizationArea Platform, CustomizationArea Metadata) Resolve(
            Snapshot before,
            IEnumerable<AchievementDetail> after,
            IReadOnlyDictionary<CustomizationArea, HashSet<string>> metadataByArea,
            bool metadataCapstone)
        {
            var current = Take(after);
            var platform = CustomizationArea.None;
            var metadata = CustomizationArea.None;

            foreach (var area in ValueAreas)
            {
                if (!current.Has(area))
                {
                    continue;
                }

                HashSet<string> supplied = null;
                metadataByArea?.TryGetValue(area, out supplied);
                var hasMetadata = supplied != null && supplied.Any(name => current.Values[area].ContainsKey(name));
                if (hasMetadata)
                {
                    metadata |= area;
                }

                // The platform's value has to reach the result untouched and not be one a source wrote.
                var now = current.Values[area];
                var hasPlatform = before.Values[area].Any(pair =>
                    (supplied == null || !supplied.Contains(pair.Key)) &&
                    now.TryGetValue(pair.Key, out var value) &&
                    string.Equals(value, pair.Value, StringComparison.Ordinal));
                if (hasPlatform)
                {
                    platform |= area;
                }
            }

            if (before.HasCapstone)
            {
                platform |= CustomizationArea.Capstones;
            }

            if (metadataCapstone)
            {
                metadata |= CustomizationArea.Capstones;
            }

            return (platform, metadata);
        }

        /// <summary>The value areas any achievement carries a value for, from whatever source.</summary>
        public static CustomizationArea Present(IEnumerable<AchievementDetail> achievements)
        {
            var snapshot = Take(achievements);
            var present = ValueAreas
                .Where(snapshot.Has)
                .Aggregate(CustomizationArea.None, (mask, area) => mask | area);
            var anyCapstone = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Any(row => row != null && (row.IsCapstone || IsPlatinum(row)));
            return anyCapstone ? present | CustomizationArea.Capstones : present;
        }

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

        private static bool IsPlatformCapstone(AchievementDetail row) => row.IsCapstone || IsPlatinum(row);

        private static bool IsPlatinum(AchievementDetail row) =>
            string.Equals(row.TrophyType?.Trim(), "platinum", StringComparison.OrdinalIgnoreCase);

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
