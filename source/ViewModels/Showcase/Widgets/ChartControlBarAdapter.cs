namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Activity Calendar and points Score card control bar: platform, game and progress (Complete / In
    /// Progress) filters over the games whose unlocks or completions the chart counts.
    /// </summary>
    public sealed class ChartControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public ChartControlBarAdapter()
            : base(chartFilters: true)
        {
        }
    }
}
