using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Raised by <see cref="ToastNotificationService"/> the moment a non-preview wave reaches its
    /// settled state — slide-in finished and placement snapped — whether or not it was revealed on
    /// screen. A liveness signal for the unlock-recording service's overlay-track wait; clip
    /// windows are unlock-anchored and the toast is composited into clips at export.
    /// </summary>
    internal sealed class ToastWaveDisplayedEventArgs : EventArgs
    {
        public ToastWaveDisplayedEventArgs(
            IReadOnlyList<AchievementToastViewModel> wave, DateTime shownUtc, DateTime? soundPlayedUtc)
        {
            Wave = wave;
            ShownUtc = shownUtc;
            SoundPlayedUtc = soundPlayedUtc;
        }

        public IReadOnlyList<AchievementToastViewModel> Wave { get; }

        public DateTime ShownUtc { get; }

        /// <summary>
        /// When this wave's unlock chime started playing. The recording service reads the chime
        /// sidecar track at this moment and mixes it into the wave's clips at the composited
        /// toast. Null when no sound fired — including a headless wave, which deliberately plays
        /// none, so its clips ship without a chime.
        /// </summary>
        public DateTime? SoundPlayedUtc { get; }
    }
}
