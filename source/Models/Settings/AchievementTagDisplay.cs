using System;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// How an achievement grid's Tags column draws each tag; Icons by default. None lists the names as plain text.
    /// Icons replaces a name with the tag's icon (a tag without one keeps its name); Colors draws
    /// the icon or name in the tag's color. The two combine.
    /// </summary>
    [Flags]
    public enum AchievementTagDisplay
    {
        None = 0,
        Icons = 1,
        Colors = 2
    }
}
