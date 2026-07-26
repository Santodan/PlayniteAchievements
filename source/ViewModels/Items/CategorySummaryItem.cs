namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// A <see cref="GameSummaryItem"/> that represents a rollup over one achievement category
    /// rather than a game. Carries the normalized category label as a stable key so the grid can
    /// map a clicked summary row back to its category when drilling into the achievement list.
    /// The display name (localized label) is held by <see cref="GameSummaryItem.GameName"/> so the
    /// existing game-summary column templates render it unchanged.
    /// </summary>
    public sealed class CategorySummaryItem : GameSummaryItem
    {
        public string CategoryLabel { get; set; }

        /// <summary>
        /// The category's group-based type token (one of Base/DLC/Update/Subset, or Default when the
        /// bucket has no group membership). Carries the locale-independent classification so the
        /// theme-facing summary can expose type flags (IsBaseCategory, etc.).
        /// </summary>
        public string CategoryType { get; set; }
    }
}
