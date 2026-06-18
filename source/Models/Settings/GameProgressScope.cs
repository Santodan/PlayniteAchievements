using System;

namespace PlayniteAchievements.Models.Settings
{
    [Flags]
    public enum GameProgressScope
    {
        None = 0,
        Completed = 1,
        InProgress = 2,
        NoProgress = 4,
        All = Completed | InProgress | NoProgress
    }
}
