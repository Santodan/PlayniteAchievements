namespace PlayniteAchievements.Services.Database.Rows
{
    internal sealed class AchievementDefinitionRow
    {
        public long Id { get; set; }
        public long GameId { get; set; }
        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string UnlockedIconPath { get; set; }
        public string LockedIconPath { get; set; }
        public int? Points { get; set; }
        public int? ScaledPoints { get; set; }
        public string Category { get; set; }
        public string CategoryType { get; set; }
        public string TrophyType { get; set; }
        public long Hidden { get; set; }
        public long IsCapstone { get; set; }
        public double? GlobalPercentUnlocked { get; set; }
        public int? ProgressMax { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
    }
}
