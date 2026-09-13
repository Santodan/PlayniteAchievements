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

        /// <summary>First level belonging to the current rank, per the rank threshold table.</summary>
        public int RankStartLevel { get; set; }

        /// <summary>Last level belonging to the current rank.</summary>
        public int RankEndLevel { get; set; }

        /// <summary>
        /// How many levels the current rank spans. The default table gives every rank ten; it is
        /// read from the thresholds rather than assumed so a custom curve stays honest.
        /// </summary>
        public int LevelsInRank { get; set; }

        /// <summary>Levels of the current rank already behind the player, 0 to LevelsInRank.</summary>
        public int LevelsCompletedInRank { get; set; }

        /// <summary>Levels left before the next rank, 0 once the cap is reached.</summary>
        public int LevelsUntilNextRank { get; set; }

        public AchievementRank RankValue { get; set; } = AchievementRank.Bronze5;

        public string Rank { get; set; } = AchievementRank.Bronze5.ToString();
    }
}
