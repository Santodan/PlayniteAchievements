namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Sort mode for the game summaries grids. <see cref="PinOrder"/> preserves the source
    /// order and is offered only on surfaces whose source order is user-controlled (the
    /// showcase pinned-games grid); every sort helper treats it as "do not sort".
    /// </summary>
    public enum GameSummariesSortMode
    {
        RecentUnlock = 0,
        LastPlayed = 1,
        TotalAchievements = 2,
        Progress = 3,
        Alphabetical = 4,
        PinOrder = 5
    }
}
