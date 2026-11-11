namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Pie widget's control bar: platform, game and progress (Complete / In Progress / No
    /// Progress) filters over the games the pie counts, and tag, rarity and trophy filters over
    /// their achievements. On a Tag pie the picked tags are also the slices it draws, and on a
    /// Completions pie the progress filter picks its slices the same way.
    /// </summary>
    public sealed class PieControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public PieControlBarAdapter()
            : base(chartFilters: true, achievementFilters: true, includeNoProgress: true)
        {
        }
    }
}
