using System;

namespace PlayniteAchievements.Models.Settings
{
    [Flags]
    public enum GameActivityScope
    {
        None = 0,
        Played = 1,
        Unplayed = 2,
        All = Played | Unplayed
    }
}
