using System;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Per-line text emphasis for a notification surface's text lines, combinable as flags.
    /// Applies to the whole line; inline markdown in the text itself composes on top.
    /// </summary>
    [Flags]
    public enum NotificationLineEmphasis
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strikethrough = 8
    }
}
