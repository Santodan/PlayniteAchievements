namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Which part of a nested category the grids' Category column shows. The cell's tooltip
    /// always shows the full path.
    /// </summary>
    public enum CategoryColumnDisplay
    {
        // The deepest level: "DLC::Season Pass" shows "Season Pass".
        Subcategory = 0,

        // The top level: "DLC::Season Pass" shows "DLC".
        TopLevel = 1,

        // Every level: "DLC::Season Pass" shows "DLC > Season Pass".
        FullPath = 2
    }
}
