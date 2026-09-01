namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Controls what a category row's numbers include in category mode, and with them the badge,
    /// the sort value, and what drilling into the row opens. Display only; the theme-facing
    /// category summaries always count each category on its own achievements.
    /// </summary>
    public enum CategoryProgressMode
    {
        // Each row counts only the achievements directly in that category (the historical
        // default). A category holding only subcategories shows no numbers of its own.
        OwnOnly = 0,

        // Each row counts its whole subtree, so a parent reads as the sum of everything under it.
        CombinedOnly = 1,

        // Subtree totals as above, and a category holding both its own achievements and
        // subcategories additionally gets a connected breakdown row reporting just its own.
        CombinedWithOwnRows = 2
    }
}
