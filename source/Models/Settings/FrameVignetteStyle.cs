namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Vignette darkening applied to the screenshot frame surface. The default (<see cref="Full"/>)
    /// preserves the original chrome: a circular radial vignette plus the bottom contrast wash.
    /// </summary>
    public enum FrameVignetteStyle
    {
        /// <summary>Radial edge vignette plus the bottom contrast wash (original behavior).</summary>
        Full,

        /// <summary>Bottom contrast wash only; no radial edge vignette.</summary>
        Bottom,

        /// <summary>No darkening at all.</summary>
        None
    }
}
