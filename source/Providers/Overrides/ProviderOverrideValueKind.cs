namespace PlayniteAchievements.Providers.Overrides
{
    /// <summary>
    /// Describes how a provider's per-game override value is entered and validated.
    /// </summary>
    public enum ProviderOverrideValueKind
    {
        /// <summary>Presence-only binding with no value (e.g. FFXIV).</summary>
        None = 0,

        /// <summary>Free-text identifier or slug (e.g. Steam AppID, Xenia TitleID).</summary>
        Text,

        /// <summary>Selection from a fixed set of choices (e.g. Hoyoverse/BattleNet titles).</summary>
        Choice
    }
}
