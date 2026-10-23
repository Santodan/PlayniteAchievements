namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Statistics and Profile control bar: platform, game, tag and progress (Complete / In
    /// Progress) filters over the games the statistics and medal counts are taken from.
    /// </summary>
    public sealed class StatisticsControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public StatisticsControlBarAdapter()
            : base(chartFilters: true, includeTags: true)
        {
        }
    }
}
