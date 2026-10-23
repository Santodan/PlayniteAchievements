namespace PlayniteAchievements.Models.Tagging
{
    /// <summary>
    /// Stable keys for achievement-related tags that can be applied to Playnite games.
    /// These keys are used for settings storage and tag lookup.
    /// </summary>
    public enum TagType
    {
        /// <summary>
        /// Game has achievement data tracked by the plugin.
        /// </summary>
        HasAchievements,

        /// <summary>
        /// Game has achievements with some unlocked, but not 100% complete.
        /// </summary>
        InProgress,

        /// <summary>
        /// All achievements unlocked (or capstone achievement earned).
        /// </summary>
        Completed,

        /// <summary>
        /// Game was scanned but has no achievement data available.
        /// </summary>
        NoAchievements,

        /// <summary>
        /// Game has visible per-game customization data stored by the plugin.
        /// </summary>
        Customized,

        /// <summary>
        /// Game has no visible per-game customization data stored by the plugin.
        /// </summary>
        NotCustomized,

        /// <summary>
        /// Game is excluded from all plugin tracking.
        /// </summary>
        Excluded,

        /// <summary>
        /// Game is excluded from overview summaries.
        /// </summary>
        ExcludedFromSummaries,

        // Per-area customization pairs, disabled by default. The custom kind applies when the
        // game carries that customization, the default kind when it does not. See
        // CustomizationTagAreas for the area each pair belongs to.
        CustomIcons,
        DefaultIcons,
        CustomTrophyTypes,
        DefaultTrophyTypes,
        CustomPoints,
        DefaultPoints,
        CustomNotes,
        DefaultNotes,
        CustomCategories,
        DefaultCategories,
        CustomOrder,
        DefaultOrder,
        CustomCapstones,
        DefaultCapstones,
        CustomGoals,
        DefaultGoals,
        CustomSource,
        // Retired: Source and Manual Tracking tag only the games that carry them.
        DefaultSource,
        CustomManualTracking,
        // Retired, as DefaultSource.
        DefaultManualTracking,
        CustomNotifications,
        DefaultNotifications
    }
}
