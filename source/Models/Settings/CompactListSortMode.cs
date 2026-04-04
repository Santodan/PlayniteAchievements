namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Sort mode for modern compact list controls.
    /// None preserves default ordering: provider order for all/locked lists, newest-first for unlocked list.
    /// </summary>
    public enum CompactListSortMode
    {
        None = 0,
        UnlockTime = 1,
        Rarity = 2
    }
}
