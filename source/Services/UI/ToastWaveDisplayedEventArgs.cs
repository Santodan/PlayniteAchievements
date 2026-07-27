using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Raised by <see cref="ToastNotificationService"/> the moment a non-preview toast wave is
    /// fully on screen (slide-in finished and placement snapped). The unlock-recording service
    /// uses <see cref="ShownUtc"/> as the clip end anchor so every clip contains the toast.
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
