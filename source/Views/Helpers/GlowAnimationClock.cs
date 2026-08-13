using System;
using System.Diagnostics;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Wall-clock epoch shared by every glow animation (the <see cref="RarityGlowPulse"/> opacity
    /// fade and the rotating ray burst) so timelines started at different moments stay in phase
    /// with one another and across recreated elements. Each animation converts the epoch into a
    /// negative <c>BeginTime</c>, which offsets its looping timeline to the matching point of the
    /// current cycle instead of restarting it from zero.
    /// </summary>
    public static class GlowAnimationClock
    {
        private static readonly Stopwatch Epoch = Stopwatch.StartNew();

        /// <summary>
        /// Milliseconds elapsed since the epoch started, for animations that need to evaluate their
        /// current value directly rather than through a timeline.
        /// </summary>
        public static double ElapsedMilliseconds => Epoch.ElapsedMilliseconds;

        /// <summary>
        /// Negative begin time that places a looping timeline at the shared epoch's current point
        /// in the cycle, so a recreated element (grid recycling, settings mockup rebuilds) resumes
        /// mid-cycle. Stamp this immediately before each BeginAnimation call — computing it earlier
        /// bakes the deferral delay in as a phase error.
        /// </summary>
        public static TimeSpan PhaseLockBeginTime(double cycleMilliseconds) =>
            cycleMilliseconds <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(-(Epoch.ElapsedMilliseconds % cycleMilliseconds));

        /// <summary>
        /// Half a cycle back: a min-&gt;max auto-reversed timeline sits at its PEAK at the
        /// half-cycle point, so phase-lock opt-outs (fresh toast waves) reveal with the glow at
        /// full strength and fade down from there — a deterministic, strong glow in captures.
        /// </summary>
        public static TimeSpan PeakStartBeginTime(double cycleMilliseconds) =>
            cycleMilliseconds <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(-(cycleMilliseconds / 2.0));
    }
}
