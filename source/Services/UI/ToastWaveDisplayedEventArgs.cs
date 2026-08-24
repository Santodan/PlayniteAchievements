using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Raised by <see cref="ToastNotificationService"/> the moment a non-preview wave reaches its
    /// settled state — slide-in finished and placement snapped — whether or not it was revealed on
    /// screen. A liveness signal for the unlock-recording service's overlay-track wait; the toast
    /// is composited into clips at export rather than filmed.
    /// </summary>
    internal sealed class ToastWaveDisplayedEventArgs : EventArgs
    {
        public ToastWaveDisplayedEventArgs(
            IReadOnlyList<AchievementToastViewModel> wave,
            DateTime shownUtc,
            DateTime? soundPlayedUtc,
            DateTime? surfaceCaptureUtc)
        {
            Wave = wave;
            ShownUtc = shownUtc;
            SoundPlayedUtc = soundPlayedUtc;
            SurfaceCaptureUtc = surfaceCaptureUtc;
        }

        public IReadOnlyList<AchievementToastViewModel> Wave { get; }

        public DateTime ShownUtc { get; }

        /// <summary>
        /// When this wave's unlock chime started playing. The recording service reads the chime
        /// sidecar track at this moment and mixes it into the wave's clips at the composited
        /// toast. Null when no sound fired — including an unrevealed wave, which deliberately plays
        /// none, so its clips ship without a chime.
        /// </summary>
        public DateTime? SoundPlayedUtc { get; }

        /// <summary>
        /// When this wave grabbed its base surface capture — the single frame every screenshot
        /// variant is built from, and therefore the moment the notification is understood to have
        /// reached the screen. With a notification delay configured, the recording service anchors
        /// the clip here so the clip and the screenshot depict the same instant.
        ///
        /// Null when the wave was never revealed (an unrevealed wave renders its card only to feed
        /// a screenshot variant or an overlay track), because there is no on-screen moment to
        /// anchor to — such clips stay unlock-anchored.
        /// </summary>
        public DateTime? SurfaceCaptureUtc { get; }
    }
}
