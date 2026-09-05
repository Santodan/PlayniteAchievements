using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Raised by <see cref="ToastNotificationService"/> the moment a non-preview wave reaches its
    /// settled state — slide-in finished and placement snapped — whether or not it was revealed on
    /// screen, and immediately for a windowless wave. A liveness signal for the unlock-recording
    /// service's overlay-track wait; the toast is composited into clips at export rather than
    /// filmed.
    /// </summary>
    internal sealed class ToastWaveDisplayedEventArgs : EventArgs
    {
        public ToastWaveDisplayedEventArgs(
            IReadOnlyList<AchievementToastViewModel> wave,
            DateTime shownUtc,
            DateTime? soundPlayedUtc,
            DateTime? surfaceCaptureUtc,
            string soundFilePath = null,
            double? soundFileGain = null,
            int? soundAlignmentDelayMs = null)
        {
            Wave = wave;
            ShownUtc = shownUtc;
            SoundPlayedUtc = soundPlayedUtc;
            SurfaceCaptureUtc = surfaceCaptureUtc;
            SoundFilePath = soundFilePath;
            SoundFileGain = soundFileGain;
            SoundAlignmentDelayMs = soundAlignmentDelayMs;
        }

        public IReadOnlyList<AchievementToastViewModel> Wave { get; }

        public DateTime ShownUtc { get; }

        /// <summary>
        /// When the plugin asked the sound host to play this wave's unlock sound. The recording
        /// service measures the sound-to-card gap from it and mixes the exact file into the wave's
        /// clips at that lead. Null when no sound fired — including an unrevealed wave, which
        /// deliberately plays none.
        /// </summary>
        public DateTime? SoundPlayedUtc { get; }

        /// <summary>
        /// The exact sound file the sound host played for this wave, snapshotted the moment it was
        /// asked for, or null when no sound played. Export mixes this file at the composited toast.
        /// </summary>
        public string SoundFilePath { get; }

        /// <summary>
        /// The sound-alignment delay the toast service applied between asking the host for the
        /// sound and revealing the card, in milliseconds. The delay models the launch-to-audible
        /// latency of the host, so the audible onset lands on the reveal; export subtracts it from
        /// the launch-to-card gap when placing the chime, because the mixed file has no such
        /// latency. Null when no sound fired.
        /// </summary>
        public int? SoundAlignmentDelayMs { get; }

        /// <summary>
        /// The gain the sound was played at (0..1, the user's volume setting), snapshotted with the
        /// path so the mixed chime is as loud as the live one the user heard. Null when unknown;
        /// export then uses its fixed fallback gain.
        /// </summary>
        public double? SoundFileGain { get; }

        /// <summary>
        /// The instant this wave's base surface capture is aimed at — the single frame every
        /// screenshot variant is built from. With either delay configured (a notification delay
        /// holding the wave past the unlock, or a capture delay pushing the capture past the wave
        /// start), the recording service anchors the clip here so the clip and the screenshot
        /// depict the same instant.
        ///
        /// A scheduled target, not an observation: it is reported when the wave settles, which may
        /// be before the capture actually runs, so the recorder can plan a clip window without
        /// waiting on the capture. The capture waits for this exact instant, so the two agree.
        ///
        /// Null when neither delay is configured, which keeps clips unlock-anchored.
        /// </summary>
        public DateTime? SurfaceCaptureUtc { get; }
    }
}
