using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Rounds completion/progress percents for whole-percent display. Values strictly between
    /// 99 and 100 floor to 99 so that only a true 100% renders as "100%"; everything else rounds
    /// half away from zero.
    /// </summary>
    internal static class AchievementCompletionPercentCalculator
    {
        internal static int ComputeRoundedPercent(int unlocked, int total)
        {
            if (total <= 0 || unlocked <= 0)
            {
                return 0;
            }

            if (unlocked >= total)
            {
                return 100;
            }

            return RoundPercentForDisplay(unlocked * 100.0 / total);
        }

        internal static int RoundPercentForDisplay(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent))
            {
                return 0;
            }

            if (percent >= 100)
            {
                return 100;
            }

            if (percent > 99)
            {
                return 99;
            }

            var rounded = (int)Math.Round(percent, 0, MidpointRounding.AwayFromZero);
            return Math.Max(0, Math.Min(100, rounded));
        }
    }
}
