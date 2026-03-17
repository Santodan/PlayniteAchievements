using System;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    internal static class AchievementDataGridPreviewHeightResolver
    {
        public static double Resolve(
            double? persistedMaxHeight,
            bool isPreview,
            double? previewMaxHeightOverride,
            double previewMinimumMaxHeight)
        {
            var resolved = previewMaxHeightOverride ?? persistedMaxHeight ?? double.NaN;
            if (!isPreview || !IsFinitePositive(previewMinimumMaxHeight) || double.IsNaN(resolved))
            {
                return resolved;
            }

            return IsFinitePositive(resolved)
                ? Math.Max(resolved, previewMinimumMaxHeight)
                : resolved;
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
        }
    }
}
