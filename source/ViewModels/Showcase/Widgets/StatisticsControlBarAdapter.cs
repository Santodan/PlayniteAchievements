namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Statistics and Profile control bar: platform, game and progress (Complete / In
    /// Progress) filters over the games, and tag, rarity and trophy filters over the achievements
    /// the achievement statistics and medals count.
    /// </summary>
    public sealed class StatisticsControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public StatisticsControlBarAdapter()
            : base(chartFilters: true, achievementFilters: true)
        {
        }
    }
}
