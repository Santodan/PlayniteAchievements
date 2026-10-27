namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The points Score card control bar: platform, game and progress (Complete / In
    /// Progress) filters over the games whose points the card sums.
    /// </summary>
    public sealed class ChartControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        private int _shownGameCount = -1;

        public ChartControlBarAdapter()
            : base(chartFilters: true)
        {
        }

        /// <summary>
        /// How many games the card sums after these filters, as its view model last counted them;
        /// -1 before any card has. The widget settings read it to offer the one-game options.
        /// </summary>
        public int ShownGameCount
        {
            get => _shownGameCount;
            set => SetValue(ref _shownGameCount, value);
        }
    }
}
