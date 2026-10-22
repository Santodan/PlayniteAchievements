namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Where an achievement grid takes the color of achievement names from. A row the source gives
    /// no color (no trophy grade, no colored tag) keeps the plain text color.
    /// </summary>
    public enum AchievementNameColorSource
    {
        None = 0,
        Rarity = 1,
        Trophy = 2,
        // The first tag in tag list order that has a color.
        Tag = 3
    }
}
