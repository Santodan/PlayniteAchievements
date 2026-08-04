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
        public ToastWaveDisplayedEventArgs(IReadOnlyList<AchievementToastViewModel> wave, DateTime shownUtc)
        {
            Wave = wave;
            ShownUtc = shownUtc;
        }

        public IReadOnlyList<AchievementToastViewModel> Wave { get; }

        public DateTime ShownUtc { get; }
    }
}
