namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal sealed class ThemeRuntimeState
    {
        public SelectedGameRuntimeState SelectedGame { get; set; } = SelectedGameRuntimeState.Empty;
        public LibraryRuntimeState Library { get; set; } = new LibraryRuntimeState();
        public SelectedGameAchievementViewState SelectedGameAchievements { get; } = new SelectedGameAchievementViewState();
        public CategorySummaryViewState CategorySummaries { get; } = new CategorySummaryViewState();
        public LibraryAchievementViewState LibraryAchievements { get; } = new LibraryAchievementViewState();
        public GameSummaryViewState GameSummaries { get; } = new GameSummaryViewState();
        public FriendRuntimeState Friends { get; set; } = FriendRuntimeState.Empty;
        public FriendScopeViewState FriendScope { get; } = new FriendScopeViewState();
        public FriendSummaryViewState FriendSummaries { get; } = new FriendSummaryViewState();
        public FriendGameSummaryViewState FriendGameSummaries { get; } = new FriendGameSummaryViewState();
        public FriendAchievementViewState FriendAchievements { get; } = new FriendAchievementViewState();
    }
}
