namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// When manual tracking may be offered for a game. One rule, shared by the Manage window's nav
    /// rail and by the merged editor's link button, so the two surfaces cannot drift.
    /// </summary>
    public static class ManualTrackingAvailability
    {
        /// <summary>
        /// True when manual linking should be offered.
        /// </summary>
        /// <remarks>
        /// An existing link always qualifies, so a linked game can always be inspected or unlinked.
        /// The global override setting qualifies unconditionally, which is what lets a user replace
        /// a real provider's data on purpose. Otherwise it is offered only where it cannot displace
        /// data the user did not ask to replace: the game is not excluded, and either it has nothing
        /// cached or what it has did not come from another provider.
        /// </remarks>
        public static bool CanLink(
            bool hasManualLink,
            bool trackingOverrideEnabled,
            bool isExcluded,
            bool hasCachedAchievements,
            bool hasNonManualProviderData)
        {
            return hasManualLink ||
                   trackingOverrideEnabled ||
                   (!isExcluded && (!hasCachedAchievements || !hasNonManualProviderData));
        }
    }
}
