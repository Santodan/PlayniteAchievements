using System;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Resolves the effective unlock-notification behavior and capture settings for one unlock.
    /// A game's own snapshot (<see cref="GameCustomDataFile.NotificationBehaviorOverride"/>,
    /// <see cref="GameCustomDataFile.CaptureOverride"/>) replaces everything below it, master
    /// switches included. Without one, the globals apply with the provider's stored override
    /// (null = inherit) in place of the feature switches, and the master switches always win.
    /// Recomputed per call; unlock events are rare so no caching is needed.
    /// </summary>
    internal static class ProviderNotificationPolicy
    {
        internal readonly struct EffectiveNotifications
        {
            public EffectiveNotifications(
                GameNotificationBehaviorOverride behavior,
                GameCaptureOverride captures,
                bool recordings)
            {
                Behavior = behavior;
                Captures = captures;
                Recordings = recordings;
            }

            /// <summary>
            /// The resolved behavior values (timing, position, vibration and the toast switches as
            /// stored). Read the gated flags below rather than its switches. Null only for the
            /// default instance.
            /// </summary>
            public GameNotificationBehaviorOverride Behavior { get; }

            /// <summary>
            /// The resolved capture values (rarity filters, completion bypasses, timing and the
            /// variant switches as stored). Read the gated flags below rather than its switches.
            /// Null only for the default instance.
            /// </summary>
            public GameCaptureOverride Captures { get; }

            public bool UnlockToasts => Behavior != null && Behavior.EnableNotifications && Behavior.EnableUnlockToasts;

            public bool FriendUnlockToasts => Behavior != null && Behavior.EnableNotifications && Behavior.EnableFriendUnlockToasts;

            /// <summary>Incremental-progress notifications (silent, capture-free).</summary>
            public bool ProgressToasts => Behavior != null && Behavior.EnableNotifications && Behavior.EnableProgressToasts;

            /// <summary>The progress notification step as a percentage of the goal; 0 is every advance.</summary>
            public int ProgressToastIntervalPercent => Behavior?.ProgressToastIntervalPercent ?? 0;

            public bool ScreenshotClean => Captures != null && Captures.EnableUnlockScreenshots && Captures.UnlockScreenshotClean;

            public bool ScreenshotWithToast => Captures != null && Captures.EnableUnlockScreenshots && Captures.UnlockScreenshotWithToast;

            public bool ScreenshotFramed => Captures != null && Captures.EnableUnlockScreenshots && Captures.UnlockScreenshotFramed;

            /// <summary>
            /// The provider's stored all-clips filter (true when unset, or when the game has its
            /// own capture settings). Does not include the recordings master switch; see
            /// <see cref="RecordingsEnabled"/>.
            /// </summary>
            public bool Recordings { get; }

            /// <summary>The recordings master switch: the game's own when it has one, else the global.</summary>
            public bool RecordingsEnabled => Captures != null && Captures.EnableUnlockRecordings;

            // The clip variants resolve without the recordings master switch, like Recordings:
            // callers AND in RecordingsEnabled themselves.
            public bool RecordingClean => Captures != null && Captures.UnlockRecordingClean;

            public bool RecordingWithToast => Captures != null && Captures.UnlockRecordingWithToast;

            public bool RecordingFramed => Captures != null && Captures.UnlockRecordingFramed;

            public bool AnyScreenshot => ScreenshotClean || ScreenshotWithToast || ScreenshotFramed;
        }

        /// <summary>
        /// The effective notification settings for a provider and, when given, a game's custom
        /// data. A null or blank provider key (and a provider without a stored override) resolves
        /// to the globals; null settings resolve to all-false.
        /// </summary>
        public static EffectiveNotifications Resolve(
            PersistedSettings settings,
            string providerKey,
            GameCustomDataFile gameData = null)
        {
            if (settings == null)
            {
                return default;
            }

            var overrides = settings.GetProviderNotificationOverride(providerKey);
            var gameCaptures = gameData?.CaptureOverride;

            return new EffectiveNotifications(
                gameData?.NotificationBehaviorOverride ??
                    GameNotificationBehaviorOverride.FromEffective(settings, overrides),
                gameCaptures ?? GameCaptureOverride.FromEffective(settings, overrides),
                // A game's own capture settings carry every clip variant explicitly, so the
                // provider's legacy all-clips switch no longer applies to it.
                recordings: gameCaptures != null || (overrides?.Recordings ?? true));
        }

        /// <summary>
        /// Whether progress moving from <paramref name="baseline"/> to <paramref name="current"/>
        /// passes another multiple of <paramref name="percent"/>% of <paramref name="denominator"/>.
        /// A non-positive percent or denominator passes every advance.
        /// </summary>
        public static bool CrossesProgressInterval(int baseline, int current, int denominator, int percent)
        {
            if (percent <= 0 || denominator <= 0)
            {
                return current > baseline;
            }

            long step = (long)denominator * percent;
            return (long)current * 100 / step > (long)Math.Max(0, baseline) * 100 / step;
        }
    }
}
