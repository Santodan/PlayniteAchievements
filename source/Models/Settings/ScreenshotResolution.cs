namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Output height of unlock screenshots. Native keeps the captured game-window resolution; the
    /// fixed options downscale (never upscale) the base capture before the notification card and
    /// frame are composited, so both scale with the image.
    /// </summary>
    public enum ScreenshotResolution
    {
        Native,
        P1080,
        P720
    }
}
