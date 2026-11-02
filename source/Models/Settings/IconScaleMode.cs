namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// How achievement icons, category art and game art scale in grid columns whose size the
    /// user controls. Both modes shrink art to fit a smaller cell.
    /// </summary>
    public enum IconScaleMode
    {
        // Art grows to fill the cell, past its own pixel size (the historical default).
        Stretch = 0,

        // Art grows no larger than its own pixel size.
        OwnSizeMax = 1
    }
}
