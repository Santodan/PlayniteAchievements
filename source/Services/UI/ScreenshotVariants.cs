using System;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The screenshot outputs requested for one unlock. Any combination may be enabled:
    /// Clean is captured before the toast window exists, WithToast after the toast slides in,
    /// and Framed reuses the clean capture with the theme frame composited onto the image.
    /// </summary>
    [Flags]
    internal enum ScreenshotVariants
    {
        None = 0,
        Clean = 1,
        WithToast = 2,
        Framed = 4,
    }
}
