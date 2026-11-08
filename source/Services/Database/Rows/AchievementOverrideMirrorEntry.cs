using PlayniteAchievements.Models.Achievements;
using System.Globalization;

namespace PlayniteAchievements.Services.Database.Rows
{
    /// <summary>
    /// One achievement's row in the custom layer mirror: the SQL-side copy of the custom-data
    /// store's override record and filter flags, so the resolved definitions view and the summary
    /// queries see the user's customization in a join.
    /// </summary>
    /// <remarks>
    /// Icon overrides and unlock times stay in the custom-data store and are applied during
    /// hydration.
    /// </remarks>
    internal sealed class AchievementOverrideMirrorEntry
    {
        public string ApiName { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public int? Points { get; set; }

        public string TrophyType { get; set; }

        public string Category { get; set; }

        public string Tags { get; set; }

        public bool? Hidden { get; set; }

        /// <summary>The <see cref="AchievementField"/> bits the user cleared.</summary>
        public long Cleared { get; set; }

        public bool IsFiltered { get; set; }

        public bool IsSummaryFiltered { get; set; }

        public string Note { get; set; }

        /// <summary>Position in the game's goal list; null when the achievement is not a goal.</summary>
        public int? GoalOrder { get; set; }

        /// <summary>Position in the user's custom order; null when the game has none or omits it.</summary>
        public int? CustomOrder { get; set; }

        /// <summary>Takes the fields of a custom layer.</summary>
        public void SetLayer(AchievementLayerValues layer)
        {
            DisplayName = layer?.DisplayName;
            Description = layer?.Description;
            Points = layer?.Points;
            TrophyType = layer?.TrophyType;
            Category = layer?.Category;
            Tags = layer?.Tags;
            Hidden = layer?.Hidden;
            Cleared = (long)(layer?.Cleared ?? AchievementField.None);
        }

        /// <summary>
        /// True when the row carries nothing a query would read, so it can be left out of the
        /// mirror entirely rather than stored as an all-default row.
        /// </summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(DisplayName) &&
            string.IsNullOrWhiteSpace(Description) &&
            !Points.HasValue &&
            string.IsNullOrWhiteSpace(TrophyType) &&
            string.IsNullOrWhiteSpace(Category) &&
            string.IsNullOrWhiteSpace(Tags) &&
            !Hidden.HasValue &&
            Cleared == 0 &&
            !IsFiltered &&
            !IsSummaryFiltered &&
            string.IsNullOrWhiteSpace(Note) &&
            !GoalOrder.HasValue &&
            !CustomOrder.HasValue;

        /// <summary>
        /// Canonical text used to diff a game's desired rows against what is stored, so an
        /// unchanged save stays WAL-silent.
        /// </summary>
        public string ToSignature()
        {
            return string.Join(
                "\n",
                (ApiName ?? string.Empty).Trim(),
                (DisplayName ?? string.Empty).Trim(),
                (Description ?? string.Empty).Trim(),
                Points.HasValue ? Points.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                (TrophyType ?? string.Empty).Trim(),
                (Category ?? string.Empty).Trim(),
                (Tags ?? string.Empty).Trim(),
                Hidden.HasValue ? (Hidden.Value ? "1" : "0") : string.Empty,
                Cleared.ToString(CultureInfo.InvariantCulture),
                IsFiltered ? "1" : "0",
                IsSummaryFiltered ? "1" : "0",
                (Note ?? string.Empty).Trim(),
                GoalOrder.HasValue ? GoalOrder.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                CustomOrder.HasValue ? CustomOrder.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }
    }
}
