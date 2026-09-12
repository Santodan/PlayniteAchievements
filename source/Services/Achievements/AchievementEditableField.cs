namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// A provider-supplied achievement field the user is allowed to override.
    /// </summary>
    /// <remarks>
    /// Unlock status is deliberately not a member: it stays provider-owned so an override cannot
    /// change unlocked counts, game completion, or appear to the in-game monitor as a real unlock.
    /// <see cref="UnlockTimeUtc"/> only corrects the timestamp of an achievement that is already
    /// unlocked. Rarity is likewise absent, because it is derived from the unlock percentages the
    /// provider supplies; a fully-custom achievement carries its own rarity on its definition.
    /// </remarks>
    public enum AchievementEditableField
    {
        DisplayName,
        Description,
        Points,
        TrophyType,
        UnlockTimeUtc
    }
}
