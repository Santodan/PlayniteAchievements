using System;
using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Sizes an option dialog to the rows it actually shows.
    ///
    /// Replaces the per-kind height constants these dialogs used to carry: those were measured
    /// against one font scale and one option set, so they left dead space or clipped as soon as
    /// either changed. Measuring is safe here because the dialogs stack their content and their
    /// visible row count is fixed by the time the window opens, and because the windows are
    /// resizable with scrolling content -- an inaccurate measure costs a little empty space, not a
    /// broken layout.
    /// </summary>
    internal static class SettingsDialogSizing
    {
        /// <summary>Window chrome plus the content margin the dialogs use.</summary>
        private const double ChromeAllowance = 56;

        /// <summary>Fraction of the work area a dialog may occupy before it should scroll.</summary>
        private const double WorkAreaCeiling = 0.85;

        public static double MeasureHeight(
            FrameworkElement content,
            double availableWidth,
            double minimumHeight,
            double fallbackHeight)
        {
            if (content == null)
            {
                return fallbackHeight;
            }

            try
            {
                content.Measure(new Size(availableWidth, double.PositiveInfinity));
                var desired = content.DesiredSize.Height + ChromeAllowance;
                if (double.IsNaN(desired) || double.IsInfinity(desired) || desired <= 0)
                {
                    return fallbackHeight;
                }

                // Clamped to the work area so a tall option set on a short screen still fits on
                // screen and scrolls instead of running off the bottom.
                var ceiling = SystemParameters.WorkArea.Height * WorkAreaCeiling;
                return Math.Min(Math.Max(desired, minimumHeight), ceiling);
            }
            catch (Exception)
            {
                return fallbackHeight;
            }
        }
    }
}
