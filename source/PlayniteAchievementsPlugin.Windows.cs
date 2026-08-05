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

        public void OpenCapturesViewer(ViewModels.Items.GameSummaryItem game)
        {
            _windowService.OpenCapturesViewer(game);
        }

        public void OpenCapturesViewer(ViewModels.Items.AchievementDisplayItem achievement)
        {
            _windowService.OpenCapturesViewer(achievement);
        }

        public void OpenCapturesViewerForGame(string gameName)
        {
            _windowService.OpenCapturesViewerForGame(gameName);
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

        /// <summary>
        /// Opens the plugin's color picker for the given owner window and current value,
        /// returning the chosen color string (or the current value on cancel). Exposed so
        /// settings sections can reuse the same picker.
        /// </summary>
        public string PickColor(System.Windows.Window owner, string currentValue)
        {
            return _windowService?.PickColor(owner, currentValue);
        }

        private bool _settingsPopoutOpen;

        /// <summary>
        /// Hosts the plugin's own settings UI in a managed popout window. Used from the
        /// fullscreen main menu, where Playnite's native plugin-settings dialog
        /// (OpenSettingsView) is unavailable. Drives BeginEdit on open and EndEdit on close so
        /// changes persist the same way the desktop settings dialog saves on OK.
        /// </summary>
        private void OpenSettingsWindow()
        {
            if (_settingsPopoutOpen)
            {
                return;
            }

            try
            {
                _settingsViewModel.BeginEdit();
                var view = GetSettingsView(false);
                _settingsPopoutOpen = true;

                _windowService.OpenManagedPopout(
                    ResourceProvider.GetString("LOCPlayAch_Landing_OpenSettings"),
                    view,
                    new Views.Helpers.WindowOptions
                    {
                        ShowMinimizeButton = false,
                        ShowMaximizeButton = true,
                        ShowCloseButton = true,
                        CanBeResizable = true,
                        Width = 1100,
                        Height = 820
                    },
                    "SettingsPopout",
                    closed: () =>
                    {
                        _settingsPopoutOpen = false;
                        try
                        {
                            _settingsViewModel.EndEdit();
                        }
                        catch (Exception ex)
                        {
                            _logger?.Error(ex, "Failed to persist settings from the popout window.");
                        }

                        (view as IDisposable)?.Dispose();
                    });
            }
            catch (Exception ex)
            {
                _settingsPopoutOpen = false;
                _logger?.Error(ex, "Failed to open the settings popout window.");
            }
        }

        private void ToggleOverviewWindowFromHotkey()
        {
            _windowService.ToggleOverviewWindowFromHotkey();
        }

        private bool _settingsViewOpen;

        /// <summary>
        /// Opens Playnite's plugin-settings dialog for hotkey invocations. The dialog is a
        /// blocking ShowDialog, so the flag stays set for its whole lifetime and repeated
        /// presses pumped by the nested dispatcher loop are ignored. Other modals (e.g. the
        /// add-ons window) are detected at the Win32 level: ShowDialog disables sibling
        /// windows via EnableWindow, which never flows into the IsEnabled dependency property.
        /// </summary>
        private void OpenSettingsViewFromHotkey()
        {
            if (_settingsViewOpen || _settingsPopoutOpen)
            {
                return;
            }

            var mainWindow = System.Windows.Application.Current?.MainWindow;
            if (mainWindow != null)
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
                if (handle != IntPtr.Zero && !IsWindowEnabled(handle))
                {
                    return;
                }
            }

            _settingsViewOpen = true;
            try
            {
                OpenSettingsView();
            }
            finally
            {
                _settingsViewOpen = false;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

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
