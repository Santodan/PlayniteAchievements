using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Raised by <see cref="ToastNotificationService"/> the moment a non-preview toast wave is
    /// fully on screen (slide-in finished and placement snapped). A liveness signal for the
    /// unlock-recording service's overlay-track wait — clip windows are unlock-anchored and the
    /// toast is composited into clips at export.
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
        /// When this wave's unlock chime started playing (null when no sound fired). The
        /// recording service reads the chime sidecar track at this moment and mixes it into the
        /// wave's clips at the composited toast.
        /// </summary>
        public DateTime? SoundPlayedUtc { get; }
    }
}
