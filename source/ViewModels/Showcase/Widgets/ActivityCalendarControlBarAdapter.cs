namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Activity Calendar control bar: platform, game, tag and progress (Complete / In
    /// Progress) filters. Tags narrow the unlocks that fill the days.
    /// </summary>
    public sealed class ActivityCalendarControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public ActivityCalendarControlBarAdapter()
            : base(chartFilters: true, includeTags: true)
        {
        }
    }
}