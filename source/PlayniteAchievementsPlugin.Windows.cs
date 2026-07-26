using System;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.ManageAchievements;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        private void ShowRefreshProgressControlAndRun(Func<Task> refreshTask, Guid? singleGameRefreshId = null)
        {
            _windowService.ShowRefreshProgressControlAndRun(refreshTask, gameId => OpenViewAchievementsWindow(gameId), singleGameRefreshId);
        }

        private void ShowRefreshProgressControl(
            Guid? singleGameRefreshId = null,
            Func<Task> refreshTask = null,
            bool validateCanStart = false)
        {
            _windowService.ShowRefreshProgressControl(singleGameRefreshId, refreshTask, gameId => OpenViewAchievementsWindow(gameId), validateCanStart);
        }

        /// <summary>
        /// Opens the View Achievements window for the specified game.
        /// Public for access from theme integration controls.
        /// When <paramref name="focusAchievementId"/> is provided (ApiName, or DisplayName as a
        /// fallback), the matching achievement row is selected and scrolled into view.
        /// </summary>
        public void OpenViewAchievementsWindow(Guid gameId, string focusAchievementId = null)
        {
            _windowService.OpenViewAchievementsWindow(gameId, focusAchievementId);
        }

        public void OpenViewFriendsAchievementsWindow(Guid gameId)
        {
            _windowService.OpenViewFriendsAchievementsWindow(gameId);
        }

        /// <summary>
        /// Opens the modern parity test view window for testing theme integration controls.
        /// </summary>
        public void OpenModernParityTestView(Guid gameId)
        {
            _windowService.OpenModernParityTestView(gameId);
        }

        /// <summary>
        /// Opens an interactive dynamic command tester window for theme filters and sort commands.
        /// </summary>
        public void OpenDynamicThemeCommandTestView(Guid? gameId = null)
        {
            _windowService.OpenDynamicThemeCommandTestView(gameId);
        }

        public void OpenManageAchievementsView(
            Guid gameId,
            ManageAchievementsTab initialTab = ManageAchievementsTab.Overview,
            bool selectManageCategoriesSubTab = false)
        {
            _windowService.OpenManageAchievementsView(gameId, initialTab, selectManageCategoriesSubTab);
        }

        public void OpenCapstoneView(Guid gameId)
        {
            _windowService.OpenCapstoneView(gameId);
        }

        private void EnsureAchievementResourcesLoaded()
        {
            _resourceService.EnsureAchievementResourcesLoaded(_settingsViewModel.Settings);
        }

        private void OpenOverviewWindow()
        {
            _windowService.OpenOverviewWindow();
        }

        private void ToggleOverviewWindowFromHotkey()
        {
            _windowService.ToggleOverviewWindowFromHotkey();
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
