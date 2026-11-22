using System;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Resolves the screenshot capture timing for one unlock wave when the Santodan-Fork
    /// Memories page may own the capture.
    /// <para>
    /// Precedence: a game's own capture settings (Manage Achievements &gt; Notifications) win;
    /// otherwise, while Memories screenshots are enabled, the Memories capture delay applies
    /// and the upstream "capture at unlock and after delay" option is off, because Memories
    /// has no such option; otherwise the upstream global settings apply unchanged.
    /// </para>
    /// <para>
    /// Only screenshots use this. Clips resolve their own delay, so a Memories screenshot
    /// delay does not move a clip's anchor.
    /// </para>
    /// </summary>
    internal static class SantodanMemoriesCapturePolicy
    {
        public static double ResolveCaptureDelaySeconds(
            GameCaptureOverride gameCaptures,
            LocalSettings memories,
            PersistedSettings persisted)
        {
            if (gameCaptures != null)
            {
                return Math.Max(0, gameCaptures.CaptureDelaySeconds);
            }

            if (OwnsScreenshots(memories))
            {
                return Math.Max(0, memories.ScreenshotDelayMilliseconds) / 1000.0;
            }

            return Math.Max(0, persisted?.CaptureDelaySeconds ?? 0);
        }

        public static bool ResolveCaptureAtUnlockAndAfterDelay(
            GameCaptureOverride gameCaptures,
            LocalSettings memories,
            PersistedSettings persisted)
        {
            if (gameCaptures != null)
            {
                return gameCaptures.CaptureAtUnlockAndAfterDelay;
            }

            if (OwnsScreenshots(memories))
            {
                return false;
            }

            return persisted?.CaptureAtUnlockAndAfterDelay ?? false;
        }

        // Same ownership rule as ToastNotificationService.ResolveEffectiveScreenshotSettings.
        private static bool OwnsScreenshots(LocalSettings memories) =>
            memories?.EnableUnlockScreenshots == true;
    }
}
