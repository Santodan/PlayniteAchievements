namespace PlayniteAchievements.Services.Database.Rows
{
    internal sealed class FriendOwnershipRow
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public long GameId { get; set; }
        public int PlaytimeForeverMinutes { get; set; }
        public int? Playtime2WeeksMinutes { get; set; }
        public string LastPlayedUtc { get; set; }
        public string LastOwnershipRefreshUtc { get; set; }
        public string LastScrapedUtc { get; set; }
        public string LastScrapeStatus { get; set; }
        public string LastScrapeDetail { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
    }
}
