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
            DateTime? surfaceCaptureUtc,
            string soundFilePath = null,
            double? soundFileGain = null)
        {
            Wave = wave;
            ShownUtc = shownUtc;
            SoundPlayedUtc = soundPlayedUtc;
            SurfaceCaptureUtc = surfaceCaptureUtc;
            SoundFilePath = soundFilePath;
            SoundFileGain = soundFileGain;
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
        /// The exact sound file UniPlaySong resolved for this wave, snapshotted the moment it
        /// fired, or null when it cannot be known (UniPlaySong before 1.8.4, resolution failure).
        /// With a path, export mixes this file at the composited toast instead of separating a
        /// captured copy of the chime.
        /// </summary>
        public string SoundFilePath { get; }

        /// <summary>
        /// The volume UniPlaySong played the sound at (0..1), snapshotted with the path so the
        /// mixed chime is as loud as the live one the user heard. Null when unknown; export then
        /// uses its fixed fallback gain.
        /// </summary>
        public double? SoundFileGain { get; }

        /// <summary>
        /// The instant this wave's base surface capture is aimed at — the single frame every
        /// screenshot variant is built from. With a capture delay configured, the recording service
        /// anchors the clip here so the clip and the screenshot depict the same instant.
        ///
        /// A scheduled target, not an observation: it is reported when the wave settles, which may
        /// be before the capture actually runs, so the recorder can plan a clip window without
        /// waiting on the capture. The capture waits for this exact instant, so the two agree.
        ///
        /// Null when no capture delay is configured, which keeps clips unlock-anchored.
        /// </summary>
        public DateTime? SurfaceCaptureUtc { get; }
    }
}
