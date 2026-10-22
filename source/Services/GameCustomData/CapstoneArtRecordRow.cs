namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// One row of the CapstoneArtRecords table in game_custom_data.db: the capstone art recorded
    /// for one achievement of a game. Stored beside, not inside, the GameCustomData payload, so
    /// it is not part of portable packages.
    /// </summary>
    internal sealed class CapstoneArtRecordRow
    {
        public string PlayniteGameId { get; set; }
        public string ApiName { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Source { get; set; }
        public string Path { get; set; }
        public long? Length { get; set; }
        public string WriteUtc { get; set; }
    }
}
