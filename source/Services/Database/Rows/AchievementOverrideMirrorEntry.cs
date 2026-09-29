namespace PlayniteAchievements.Services.Database.Rows
{
    /// <summary>
    /// One achievement's row in the per-achievement override mirror: the SQL-side view of the
    /// custom-data store's override record, carrying only the fields summary aggregates need to
    /// resolve in a join.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <c>AchievementOverride</c>. Title, description, note and the icon
    /// paths are never aggregated, so they stay in the blob and are applied during hydration;
    /// mirroring them would add write churn for values no query reads.
    /// </remarks>
    internal sealed class AchievementOverrideMirrorEntry
    {
        public string ApiName { get; set; }

        public int? Points { get; set; }

        public string TrophyType { get; set; }

        public bool IsFiltered { get; set; }

        public bool IsSummaryFiltered { get; set; }

        /// <summary>
        /// True when the row carries nothing a query would read, so it can be left out of the
        /// mirror entirely rather than stored as an all-default row.
        /// </summary>
        public bool IsEmpty =>
            !Points.HasValue &&
            string.IsNullOrWhiteSpace(TrophyType) &&
            !IsFiltered &&
            !IsSummaryFiltered;

        /// <summary>
        /// Canonical text used to diff a game's desired rows against what is stored, so an
        /// unchanged save stays WAL-silent.
        /// </summary>
        public string ToSignature()
        {
            return string.Join(
                "\n",
                (ApiName ?? string.Empty).Trim(),
                Points.HasValue ? Points.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                (TrophyType ?? string.Empty).Trim(),
                IsFiltered ? "1" : "0",
                IsSummaryFiltered ? "1" : "0");
        }
    }
}
