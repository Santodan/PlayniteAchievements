namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// What a game's finish badge shows. The game's leading progress detail decides it: the
    /// first enabled <see cref="ProgressDetailStyle"/> the game has data for.
    /// </summary>
    public enum CompletionBadgeKind
    {
        /// <summary>The completed badge.</summary>
        Completed = 0,

        /// <summary>The platinum trophy: trophies lead and the game's capstones are its platinums.</summary>
        Platinum = 1,

        /// <summary>The mythic badge: points lead and every point is earned.</summary>
        Mythic = 2
    }
}
