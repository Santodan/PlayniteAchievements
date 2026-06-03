namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public sealed class AchievementRankDebugRow
    {
        public AchievementRank Rank { get; set; }

        public int MinLevel { get; set; }

        public int MaxLevel { get; set; }

        public int MinScore { get; set; }

        public int MaxScore { get; set; }

        public int ScoreNeededFromPreviousRank { get; set; }
    }
}
