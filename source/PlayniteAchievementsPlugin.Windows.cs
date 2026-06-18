using System;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        private void ShowRefreshProgressControlAndRun(Func<Task> refreshTask, Guid? singleGameRefreshId = null)
        {
            _windowService.ShowRefreshProgressControlAndRun(refreshTask, OpenViewAchievementsWindow, singleGameRefreshId);
        }

        private void ShowRefreshProgressControl(
            Guid? singleGameRefreshId = null,
            Func<Task> refreshTask = null,
            bool validateCanStart = false)
        {
            _windowService.ShowRefreshProgressControl(singleGameRefreshId, refreshTask, OpenViewAchievementsWindow, validateCanStart);
        }

        /// <summary>
        /// Opens the View Achievements window for the specified game.
        /// Public for access from theme integration controls.
        /// </summary>
        public void OpenViewAchievementsWindow(Guid gameId)
        {
            _windowService.OpenViewAchievementsWindow(gameId);
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

        public void OpenManageAchievementsView(Guid gameId, ManageAchievementsTab initialTab = ManageAchievementsTab.Overview)
        {
            _windowService.OpenManageAchievementsView(gameId, initialTab);
        }

        public void OpenCapstoneView(Guid gameId)
        {
            _windowService.OpenCapstoneView(gameId);
        }

        public void OpenLocalAchievementsEditorView(Guid gameId)
        {
            _windowService.OpenLocalAchievementsEditorView(gameId);
        }

        private void EnsureAchievementResourcesLoaded()
        {
            _windowService.EnsureAchievementResourcesLoaded();
        }

        private void OpenOverviewWindow()
        {
            _windowService.OpenOverviewWindow();
        }

        private void ToggleOverviewWindow()
        {
            _windowService.ToggleOverviewWindow();
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
