using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure timing for a framed unlock clip: the frame shows from the clip's first frame for a
    /// configured hold, fading out over the last part of it, or stays for the whole clip when no
    /// hold is set. The notification is composited only when the frame has fully gone by the time
    /// the card appears.
    /// </summary>
    internal static class FramedClipTiming
    {
        /// <summary>How long the frame takes to fade out at the end of its hold.</summary>
        public const double FadeSeconds = 1.0;

        /// <summary>
        /// The frame's opacity <paramref name="secondsIntoFrame"/> after it first shows. Null
        /// <paramref name="holdSeconds"/> keeps it at full opacity throughout. A hold shorter than
        /// <see cref="FadeSeconds"/> fades over the whole hold. The fade is a smoothstep, so it
        /// leaves and reaches its ends with zero slope.
        /// </summary>
        public static double Opacity(double secondsIntoFrame, double? holdSeconds)
        {
            if (secondsIntoFrame < 0)
            {
                return 0;
            }

            if (!holdSeconds.HasValue)
            {
                return 1;
            }

            var hold = holdSeconds.Value;
            if (hold <= 0 || secondsIntoFrame >= hold)
            {
                return 0;
            }

            var fade = Math.Min(FadeSeconds, hold);
            var remaining = (hold - secondsIntoFrame) / fade;
            if (remaining >= 1)
            {
                return 1;
            }

            return remaining * remaining * (3 - (2 * remaining));
        }

        /// <summary>
        /// Whether the framed clip also carries the notification: only when the frame, starting
        /// at <paramref name="frameStartSeconds"/>, has finished fading before the card appears at
        /// <paramref name="toastStartSeconds"/>. A frame over the whole clip never does.
        /// </summary>
        public static bool IncludesToast(double? holdSeconds, double frameStartSeconds, double toastStartSeconds)
        {
            return holdSeconds.HasValue && frameStartSeconds + holdSeconds.Value <= toastStartSeconds;
        }

        /// <summary>
        /// The last second the frame shows on: the end of its hold, or the clip's end when it has
        /// none. Never past <paramref name="clipEndSeconds"/>.
        /// </summary>
        public static double EndSeconds(double? holdSeconds, double frameStartSeconds, double clipEndSeconds)
        {
            return holdSeconds.HasValue
                ? Math.Min(clipEndSeconds, frameStartSeconds + holdSeconds.Value)
                : clipEndSeconds;
        }
    }
}
