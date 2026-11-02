namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Timeline control bar: platform and game filters over the games whose unlocks or
    /// completions the chart counts, and tag, rarity and trophy filters over the unlocks
    /// themselves.
    /// </summary>
    public sealed class TimelineControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public TimelineControlBarAdapter()
            : base(chartFilters: true, includeProgress: false, achievementFilters: true)
        {
        }
    }
}
