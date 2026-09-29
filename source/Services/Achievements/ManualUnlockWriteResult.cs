namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// What an unlock write did, and therefore what the caller still has to do about it. The two
    /// kinds store unlock state in different places and need different follow-up: an authored
    /// achievement is re-projected by the custom-data change itself, while a manual one needs its
    /// link applied onto the cached game data.
    /// </summary>
    public enum ManualUnlockWriteResult
    {
        /// <summary>Not an achievement the user owns, so nothing was written.</summary>
        NotApplicable,

        /// <summary>Already unlocked; nothing was written.</summary>
        AlreadyUnlocked,

        /// <summary>An authored achievement's definition was updated.</summary>
        Custom,

        /// <summary>A manually tracked game's link was updated and still needs re-projecting.</summary>
        Manual
    }
}
