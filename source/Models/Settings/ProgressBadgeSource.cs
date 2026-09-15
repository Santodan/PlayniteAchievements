namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Which badges the progress column footer shows beneath the bar.
    /// </summary>
    /// <remarks>
    /// This was never a setting: a game with trophy data simply had its rarity badges replaced by
    /// trophy badges, which is a reasonable default and an unreasonable thing to be unable to turn
    /// off. Naming it makes the choice the user's.
    ///
    /// The default is <see cref="TrophyWhenAvailable"/>, set by the property initializer rather
    /// than by being the zero value, so the stored setting is written out even when it holds the
    /// zero value and a deliberate choice of <see cref="Rarity"/> cannot read back as unset.
    /// </remarks>
    public enum ProgressBadgeSource
    {
        /// <summary>Rarity badges always, even for a game that reports trophies.</summary>
        Rarity = 0,

        /// <summary>Trophy badges for a game that has trophies, rarity badges otherwise.</summary>
        TrophyWhenAvailable = 1
    }
}
