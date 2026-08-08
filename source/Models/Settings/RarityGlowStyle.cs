namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Which look the rarity and completion glows take wherever they appear. The default
    /// (<see cref="Soft"/>) preserves the original chrome, so an upgrade never changes an existing
    /// setup's appearance.
    /// </summary>
    public enum RarityGlowStyle
    {
        /// <summary>Soft halo in the tier color, blurred from the icon's own silhouette (original behavior).</summary>
        Soft,

        /// <summary>Rotating sunburst of tier-colored rays behind the icon.</summary>
        Rays
    }
}
