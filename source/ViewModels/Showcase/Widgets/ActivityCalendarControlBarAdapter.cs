namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Activity Calendar control bar: platform, game and progress (Complete / In Progress)
    /// filters over the games, and tag, rarity and trophy filters over the unlocks that fill the
    /// days.
    /// </summary>
    public sealed class ActivityCalendarControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public ActivityCalendarControlBarAdapter()
            : base(chartFilters: true, achievementFilters: true)
        {
        }
    }
}
