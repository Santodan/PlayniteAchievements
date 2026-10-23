namespace PlayniteAchievements.Services.Database.Rows
{
    /// <summary>
    /// One row of the MetadataApplied table: the metadata applied to a game.
    /// </summary>
    internal sealed class MetadataAppliedRow
    {
        public string PlayniteGameId { get; set; }
        public string IconSourceKey { get; set; }
        public string CapstoneArtUrl { get; set; }
        public string CapstoneTitle { get; set; }
        public string CapstoneDescription { get; set; }

        public string CapstoneMatchApiName { get; set; }

        /// <summary>A collection's per-game platinums by category, as JSON; null for none.</summary>
        public string CategoryCapstonesJson { get; set; }
        public string IconApiNamesJson { get; set; }
        public string IconFallbackApiNamesJson { get; set; }
        public string UpdatedUtc { get; set; }
    }

    /// <summary>
    /// One row of the MetadataCoverage table: how many of a game's achievements one source matched.
    /// </summary>
    internal sealed class MetadataCoverageRow
    {
        public string PlayniteGameId { get; set; }
        public string SourceKey { get; set; }
        public int Matched { get; set; }
        public int Total { get; set; }
        public int Fields { get; set; }
    }

    /// <summary>
    /// One row of the MetadataSourceSnapshots table: the rows a source returned for a game.
    /// </summary>
    internal sealed class MetadataSourceSnapshotRow
    {
        public string PlayniteGameId { get; set; }
        public string SourceKey { get; set; }
        public string LinkId { get; set; }
        public string RowsJson { get; set; }
        public string MatchesJson { get; set; }
        public string UpdatedUtc { get; set; }
    }

    /// <summary>
    /// One row of the MetadataSourcePages table: a source's page slug for a game on one site.
    /// </summary>
    internal sealed class MetadataSourcePageRow
    {
        public string PlayniteGameId { get; set; }
        public string SourceKey { get; set; }
        public string LinkName { get; set; }
        public string Site { get; set; }
        public string PageSlug { get; set; }
    }
}
