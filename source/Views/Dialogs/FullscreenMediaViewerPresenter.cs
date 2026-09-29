using System;
using System.Windows;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>Hosts the shared capture lightbox for gallery-style surfaces.</summary>
    internal static class FullscreenMediaViewerPresenter
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(FullscreenMediaViewerPresenter));

        public static void Show(FrameworkElement owner, string path, bool isVideo)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var content = new FullscreenMediaViewer(path, isVideo);
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = System.Windows.Media.Brushes.Black,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(owner),
                Content = content
            };
            content.RequestClose += (_, __) => window.Close();
            window.Loaded += (_, __) => window.WindowState = WindowState.Maximized;

            // Same per-monitor realization as the gallery popout, so the screenshot renders at the
            // monitor's native scale instead of being bitmap-stretched by Windows.
            PerMonitorWindowRealizer.Apply(window, window.Owner, Logger, "Lightbox");
            window.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    window.Close();
                    args.Handled = true;
                }
            };
            window.ShowDialog();
        }
    }
}
