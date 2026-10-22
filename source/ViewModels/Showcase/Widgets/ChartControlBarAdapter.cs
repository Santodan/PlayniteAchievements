namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The points Score card control bar: platform, game and progress (Complete / In
    /// Progress) filters over the games whose points the card sums.
    /// </summary>
    public sealed class ChartControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public ChartControlBarAdapter()
            : base(chartFilters: true)
        {
        }
    }
}
