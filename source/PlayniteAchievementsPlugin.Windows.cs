using System;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        private void ShowRefreshProgressControlAndRun(Func<Task> refreshTask, Guid? singleGameRefreshId = null)
        {
            _windowService.ShowRefreshProgressControlAndRun(refreshTask, OpenSingleGameAchievementsView, singleGameRefreshId);
        }

        private void ShowRefreshProgressControl(
            Guid? singleGameRefreshId = null,
            Func<Task> refreshTask = null,
            bool validateCanStart = false)
        {
            _windowService.ShowRefreshProgressControl(singleGameRefreshId, refreshTask, OpenSingleGameAchievementsView, validateCanStart);
        }

        /// <summary>
        /// Opens the per-game achievements view window for the specified game.
        /// Public for access from theme integration controls.
        /// </summary>
        public void OpenSingleGameAchievementsView(Guid gameId)
        {
            _windowService.OpenSingleGameAchievementsView(gameId);
        }

        /// <summary>
        /// Opens the modern parity test view window for testing theme integration controls.
        /// </summary>
        public void OpenModernParityTestView(Guid gameId)
        {
            _windowService.OpenModernParityTestView(gameId);
        }

        public void OpenGameOptionsView(Guid gameId, GameOptionsTab initialTab = GameOptionsTab.Overview)
        {
            _windowService.OpenGameOptionsView(gameId, initialTab);
        }

        public void OpenCapstoneView(Guid gameId)
        {
            _windowService.OpenCapstoneView(gameId);
        }

        private void EnsureAchievementResourcesLoaded()
        {
            _windowService.EnsureAchievementResourcesLoaded();
        }

        private void OpenOverviewWindow()
        {
            var view = new SidebarControl(
                PlayniteApi, _logger, _refreshService, _cacheManager, PersistSettingsForUi,
                _achievementOverridesService, _achievementDataService, _refreshCoordinator, _settingsViewModel.Settings);

            var windowOptions = new WindowOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = true,
                ShowCloseButton = true,
                CanBeResizable = true,
                Width = 1280,
                Height = 800
            };

            var window = PlayniteUiProvider.CreateExtensionWindow(
                string.Empty,
                view,
                windowOptions,
                isFullscreen: true);

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = PlayniteApi?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch { }

            window.Loaded += (s, e) => view.Activate();
            window.Closed += (s, e) =>
            {
                view.Deactivate();
                view.Dispose();
            };

            window.Show();
            try
            {
                window.Topmost = true;
                window.Activate();
                window.Topmost = false;
            }
            catch { }
        }

        private enum ParityTestMode
        {
            Modern,
            Compatibility
        }

        private void OpenParityTestView(Guid gameId, ParityTestMode mode)
        {
            _windowService.OpenParityTestView(gameId, mode == ParityTestMode.Modern);
        }
    }
}
