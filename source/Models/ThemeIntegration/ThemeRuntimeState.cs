namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal sealed class ThemeRuntimeState
    {
        public SelectedGameRuntimeState SelectedGame { get; set; } = SelectedGameRuntimeState.Empty;
        public LibraryRuntimeState Library { get; set; } = new LibraryRuntimeState();
        public SelectedGameAchievementViewState SelectedGameAchievements { get; } = new SelectedGameAchievementViewState();
        public LibraryAchievementViewState LibraryAchievements { get; } = new LibraryAchievementViewState();
        public GameSummaryViewState GameSummaries { get; } = new GameSummaryViewState();
    }
}
