namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public sealed class AchievementScoreSnapshot
    {
        public int LegacyScore { get; set; }

        public AchievementLevelSnapshot LegacyLevel { get; set; } = new AchievementLevelSnapshot();

        public int CollectorScore { get; set; }

        public AchievementLevelSnapshot CollectorLevel { get; set; } = new AchievementLevelSnapshot();

        public int PrestigeScore { get; set; }

        public AchievementLevelSnapshot PrestigeLevel { get; set; } = new AchievementLevelSnapshot();
    }
}
