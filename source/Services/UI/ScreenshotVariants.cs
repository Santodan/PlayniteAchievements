using System;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The screenshot outputs requested for one unlock. Any combination may be enabled. One
    /// physical capture is taken per wave, before the toast window exists: Clean saves it as-is,
    /// WithToast composites the item's rendered toast card onto a copy at the anchor corner, and
    /// Framed composites the theme frame onto the image.
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
