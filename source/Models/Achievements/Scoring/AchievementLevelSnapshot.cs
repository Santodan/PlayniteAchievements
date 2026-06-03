namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public sealed class AchievementLevelSnapshot
    {
        public int Level { get; set; }

        public int DisplayLevel { get; set; }

        public double LevelProgress { get; set; }

        public AchievementRank RankValue { get; set; } = AchievementRank.Bronze1;

        public string Rank { get; set; } = AchievementRank.Bronze1.ToString();
    }
}
