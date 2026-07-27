namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Sort mode for selected-game achievement list controls.
    /// None preserves the control's source order, which for the selected-game default surfaces
    /// means custom order when configured, otherwise provider order.
    /// </summary>
    public enum CompactListSortMode
    {
        None = 0,
        UnlockTime = 1,
        Rarity = 2,
        DisplayOrder = 3,
        Custom = 4
    }
}
