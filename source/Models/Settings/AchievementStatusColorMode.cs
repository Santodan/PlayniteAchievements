namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// How an achievement grid colors the Status column glyph.
    /// MissableUnobtainable colors the lock of a locked Missable or Unobtainable achievement with
    /// that tag's color, or the missable red when the tag has none. AllTags colors a locked
    /// achievement with its first colored tag, and an unlocked one with its first colored tag
    /// other than Missable and Unobtainable.
    /// </summary>
    public enum AchievementStatusColorMode
    {
        None = 0,
        MissableUnobtainable = 1,
        AllTags = 2
    }
}
