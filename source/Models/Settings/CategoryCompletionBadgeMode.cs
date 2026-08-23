namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Controls which category-mode summary rows may render the completion badge under their
    /// progress bar. Display only; the completion glow and completed progress-bar fill are
    /// unaffected, as are game summary rows.
    /// </summary>
    public enum CategoryCompletionBadgeMode
    {
        // Every completed category row shows the badge (the historical default).
        All = 0,

        // Only the first category in the configured category order may show the badge.
        First = 1,

        // No category row shows the badge, even when completed.
        None = 2
    }
}
