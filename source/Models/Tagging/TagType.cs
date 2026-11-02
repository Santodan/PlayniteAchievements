namespace PlayniteAchievements.Models.Tagging
{
    /// <summary>
    /// Stable keys for achievement-related tags that can be applied to Playnite games.
    /// These keys are used for settings storage and tag lookup.
    /// </summary>
    public enum TagType
    {
        /// <summary>
        /// Game has achievements from its platform, a manually tracked list included (shown as
        /// Platform Achievements). The key is kept from when the kind meant any achievements, so
        /// stored configs and their Playnite tags carry over.
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

        // Per-area customization kinds, disabled by default. See CustomizationTagAreas for the area
        // and layer each kind belongs to.
        CustomIcons,
        CustomTrophies,
        CustomPoints,
        CustomNotes,
        NoNotes,
        CustomCategories,
        CustomOrder,
        PlatformOrder,
        CustomCapstones,
        CustomGoals,
        NoGoals,
        CustomSource,
        CustomManualTracking,
        CustomNotifications,
        CustomTags,

        // Where a layered area's data comes from: the game's platform, a metadata source, or
        // nowhere.
        MetadataIcons,
        PlatformTrophies,
        MetadataTrophies,
        NoTrophies,
        PlatformPoints,
        MetadataPoints,
        NoPoints,
        PlatformCategories,
        MetadataCategories,
        NoCategories,
        PlatformTags,
        MetadataTags,
        NoTags,
        PlatformCapstones,
        MetadataCapstones,
        NoCapstones,
        PlatformIcons,
        NoIcons,
        PlatformText,
        MetadataText,
        CustomText,

        /// <summary>Game has achievements the user authored, an auto capstone aside.</summary>
        CustomAchievements
    }
}
