namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public sealed class AchievementLevelSnapshot
    {
        public int Level { get; set; }

        public int DisplayLevel { get; set; }

        public double LevelProgress { get; set; }

        public int CurrentLevelStartScore { get; set; }

        public int CurrentLevelEndScore { get; set; }

        public int CurrentLevelPoints { get; set; }

        public int CurrentLevelTotalPoints { get; set; }

        public int PointsUntilNextLevel { get; set; }

        public AchievementRank? NextRankValue { get; set; }

        public string NextRank { get; set; }

        public int NextRankScoreThreshold { get; set; }

        public int PointsUntilNextRank { get; set; }

        public bool IsMaxLevel { get; set; }

        public AchievementRank RankValue { get; set; } = AchievementRank.Bronze5;

        public string Rank { get; set; } = AchievementRank.Bronze5.ToString();
    }
}
