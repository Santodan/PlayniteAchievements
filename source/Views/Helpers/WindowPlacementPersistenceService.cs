using System;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Helpers
{
    internal static class WindowPlacementPersistenceService
    {
        private const double MinimumSavedWidth = 100d;
        private const double MinimumSavedHeight = 100d;

        public static void Attach(
            Window window,
            PersistedSettings settings,
            Action saveSettings,
            string key,
            ILogger logger = null)
        {
            if (window == null || settings == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            TryApply(window, settings, key, logger);
            window.Closed += (s, e) => TrySave(window, settings, saveSettings, key, logger);
        }

        private static void TryApply(
            Window window,
            PersistedSettings settings,
            string key,
            ILogger logger)
        {
            try
            {
                if (settings.WindowPlacements == null ||
                    !settings.WindowPlacements.TryGetValue(key, out var placement) ||
                    placement?.IsValid() != true)
                {
                    return;
                }

                var width = ClampDimension(placement.Width, window.MinWidth, window.MaxWidth, SystemParameters.VirtualScreenWidth);
                var height = ClampDimension(placement.Height, window.MinHeight, window.MaxHeight, SystemParameters.VirtualScreenHeight);
                var bounds = new Rect(placement.Left, placement.Top, width, height);
                if (!IntersectsVirtualScreen(bounds))
                {
                    return;
                }

                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = bounds.Left;
                window.Top = bounds.Top;
                window.Width = bounds.Width;
                window.Height = bounds.Height;

                if (placement.IsMaximized && window.ResizeMode != ResizeMode.NoResize)
                {
                    window.Loaded += SetMaximizedOnLoaded;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Failed to apply saved window placement for {key}.");
            }
        }

        private static void SetMaximizedOnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window)
            {
                window.Loaded -= SetMaximizedOnLoaded;
                window.WindowState = WindowState.Maximized;
            }
        }

        private static void TrySave(
            Window window,
            PersistedSettings settings,
            Action saveSettings,
            string key,
            ILogger logger)
        {
            try
            {
                var bounds = window.WindowState == WindowState.Normal
                    ? new Rect(window.Left, window.Top, window.Width, window.Height)
                    : window.RestoreBounds;

                if (!IsValidBounds(bounds))
                {
                    return;
                }

                if (settings.WindowPlacements == null)
                {
                    settings.WindowPlacements = new System.Collections.Generic.Dictionary<string, WindowPlacementState>(
                        StringComparer.OrdinalIgnoreCase);
                }

                var next = new WindowPlacementState
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    IsMaximized = window.WindowState == WindowState.Maximized
                };

                if (!HasChanged(settings.WindowPlacements.TryGetValue(key, out var existing) ? existing : null, next))
                {
                    return;
                }

                settings.WindowPlacements[key] = next;
                saveSettings?.Invoke();
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Failed to save window placement for {key}.");
            }
        }

        private static bool HasChanged(WindowPlacementState existing, WindowPlacementState next)
        {
            if (existing?.IsValid() != true)
            {
                return true;
            }

            return Math.Abs(existing.Left - next.Left) > 0.5 ||
                   Math.Abs(existing.Top - next.Top) > 0.5 ||
                   Math.Abs(existing.Width - next.Width) > 0.5 ||
                   Math.Abs(existing.Height - next.Height) > 0.5 ||
                   existing.IsMaximized != next.IsMaximized;
        }

        private static double ClampDimension(double value, double min, double max, double screenMax)
        {
            var resolvedMin = IsFinite(min) && min > 0 ? min : MinimumSavedWidth;
            var resolvedMax = IsFinite(max) && max > 0 ? max : screenMax;
            if (!IsFinite(resolvedMax) || resolvedMax < resolvedMin)
            {
                resolvedMax = Math.Max(resolvedMin, screenMax);
            }

            return Math.Max(resolvedMin, Math.Min(resolvedMax, value));
        }

        private static bool IsValidBounds(Rect bounds)
        {
            return IsFinite(bounds.Left) &&
                   IsFinite(bounds.Top) &&
                   IsFinite(bounds.Width) &&
                   IsFinite(bounds.Height) &&
                   bounds.Width >= MinimumSavedWidth &&
                   bounds.Height >= MinimumSavedHeight;
        }

        private static bool IntersectsVirtualScreen(Rect bounds)
        {
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            return screen.IntersectsWith(bounds);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
