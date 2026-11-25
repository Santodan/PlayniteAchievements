using System;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
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
            bool selectManageCategoriesSubTab = false,
            NotificationSurface? notificationsSurface = null)
        {
            _windowService.OpenManageAchievementsView(gameId, initialTab, selectManageCategoriesSubTab, notificationsSurface);
        }

        public void OpenLocalAchievementsEditorView(Guid gameId)
        {
            _windowService.OpenLocalAchievementsEditorView(gameId);
        }

        internal void ReconfigureUnlockRecordingForSettingsSave()
        {
            ReconfigureUnlockRecording();
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
        /// The Achievement Editor: the Manage Achievements editor over every game. One window at a
        /// time; opening it again brings the open one to the front.
        /// </summary>
        private void OpenLibraryEditorWindow()
        {
            if (_windowService.TryActivateOpenWindowHosting<Views.ManageAchievements.LibraryAchievementEditorControl>())
            {
                return;
            }

            ShowLibraryEditorWindow();
        }

        /// <summary>
        /// The Achievement Editor locked to <paramref name="gameIds"/>, from a selection of games.
        /// An open editor is locked to them and brought to the front.
        /// </summary>
        private void OpenLibraryEditorWindow(System.Collections.Generic.IReadOnlyCollection<Guid> gameIds)
        {
            if (_windowService.TryActivateOpenWindowHosting<Views.ManageAchievements.LibraryAchievementEditorControl>(out var open))
            {
                open.LockToGames(gameIds);
                return;
            }

            ShowLibraryEditorWindow(gameIds);
        }

        /// <summary>The Achievement Editor hotkey: opens the editor, or brings it forward, or closes it when it is the active window.</summary>
        private void ToggleLibraryEditorWindow()
        {
            if (_windowService.TryToggleOpenWindowHosting<Views.ManageAchievements.LibraryAchievementEditorControl>())
            {
                return;
            }

            ShowLibraryEditorWindow();
        }

        private void ShowLibraryEditorWindow(System.Collections.Generic.IReadOnlyCollection<Guid> lockedGameIds = null)
        {
            Views.ManageAchievements.LibraryAchievementEditorControl view;
            try
            {
                EnsureAchievementResourcesLoaded();
                view = new Views.ManageAchievements.LibraryAchievementEditorControl(
                    _refreshService,
                    _achievementOverridesService,
                    _achievementDataService,
                    _settingsViewModel.Settings,
                    _logger,
                    lockedGameIds);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open the Achievement Editor.");
                PlayniteApi?.Dialogs?.ShowErrorMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                return;
            }

            _windowService.OpenManagedPopout(
                view.WindowTitle,
                view,
                new Views.Helpers.WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1280,
                    Height = 800
                },
                "LibraryAchievementEditor",
                view.Cleanup);
        }

        /// <summary>
        /// The Year in Review: one calendar year of unlocks as animated slides. One window at a
        /// time; opening it again brings the open one to the front.
        /// </summary>
        internal void OpenYearReviewWindow()
        {
            if (!Services.YearReview.YearReviewAvailability.IsReleased(DateTime.Today))
            {
                return;
            }

            if (_windowService.TryActivateOpenWindowHosting<Views.YearReview.YearReviewControl>())
            {
                return;
            }

            Views.YearReview.YearReviewControl view;
            try
            {
                EnsureAchievementResourcesLoaded();
                var coordinator = GetStartPageDataCoordinator();
                var reader = new Services.YearReview.YearReviewReader(
                    _achievementDataService,
                    _friendCacheManager,
                    cancel => coordinator.GetSnapshotAsync(cancel),
                    () => Settings?.Persisted,
                    PlayniteApi,
                    _logger);
                view = new Views.YearReview.YearReviewControl(
                    new ViewModels.YearReview.YearReviewViewModel(
                        reader,
                        _logger,
                        () => (Settings?.Persisted?.YearReviewDisplayName, Settings?.Persisted?.YearReviewAvatarPath),
                        (name, avatar) =>
                        {
                            var persisted = Settings?.Persisted;
                            if (persisted == null)
                            {
                                return;
                            }

                            persisted.YearReviewDisplayName = name;
                            persisted.YearReviewAvatarPath = avatar;
                            SavePluginSettings(_settingsViewModel.Settings);
                        },
                        () => Settings?.Persisted?.ShowYearReviewInOverview ?? true,
                        show =>
                        {
                            var persisted = Settings?.Persisted;
                            if (persisted == null)
                            {
                                return;
                            }

                            persisted.ShowYearReviewInOverview = show;
                            SavePluginSettings(_settingsViewModel.Settings);
                        }),
                    _logger);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open the Year in Review.");
                PlayniteApi?.Dialogs?.ShowErrorMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
                return;
            }

            _windowService.OpenManagedPopout(
                view.WindowTitle,
                view,
                new Views.Helpers.WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1280,
                    Height = 800
                },
                "YearReview",
                view.Cleanup);
        }

        /// <summary>
        /// Opens the Workshop browser, optionally landing on one kind's tab or on the game-data
        /// items that match one library game.
        /// </summary>
        internal void OpenWorkshopWindow(Guid? focusGameId = null, Services.Workshop.WorkshopItemKind? focusKind = null)
        {
            var view = new Views.Workshop.WorkshopControl(this, _logger, focusGameId, focusKind);
            _windowService.OpenManagedPopout(
                ResourceProvider.GetString("LOCPlayAch_Workshop_Title"),
                view,
                new Views.Helpers.WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1100,
                    Height = 720
                },
                "Workshop",
                () => view.Cleanup());
        }

        /// <summary>
        /// Opens the share dialog for one shareable thing: a kind, plus the game or showcase page
        /// when the kind is per-game or per-page. Returns false when nothing of that kind can be
        /// shared right now (for example no custom unlock sounds).
        /// </summary>
        internal bool OpenWorkshopShare(
            Services.Workshop.WorkshopItemKind kind,
            System.Windows.Window owner,
            Guid? gameId = null,
            string pageId = null,
            System.Collections.Generic.IReadOnlyDictionary<Services.Workshop.BundleParts, string> bundlePartFiles = null,
            string packagePath = null,
            string defaultName = null,
            string libraryItemId = null,
            string publishedItemId = null)
        {
            // A saved preset is shared as the file it already is; everything else is one of the
            // live-settings candidates the share service lists.
            var candidate = !string.IsNullOrWhiteSpace(packagePath)
                ? new Services.Workshop.WorkshopShareCandidate
                {
                    Kind = kind,
                    Label = ViewModels.Workshop.WorkshopItemViewModel.KindLabelFor(kind) + " \u00b7 " + defaultName,
                    DefaultName = defaultName,
                    PackagePath = packagePath,
                    LibraryItemId = libraryItemId,
                    PublishedItemId = publishedItemId
                }
                : WorkshopShareService.ListCandidates().FirstOrDefault(c =>
                    c.Kind == kind
                    && (gameId == null || c.GameId == gameId)
                    && (pageId == null || string.Equals(c.PageId, pageId, StringComparison.Ordinal)));
            if (candidate == null)
            {
                return false;
            }

            // A composed theme travels as the parts the user picked; the files live in the
            // caller's scratch folder for the life of this modal dialog.
            candidate.BundlePartFiles = bundlePartFiles;

            var dialog = new Views.Workshop.WorkshopShareDialog(this, _logger, candidate, WorkshopShareService, WorkshopIdentityStore);
            ShowWorkshopShareDialog(dialog, ResourceProvider.GetString("LOCPlayAch_Workshop_Share"), owner);
            return true;
        }

        /// <summary>
        /// Opens the form for changing the details of a Workshop item this install published,
        /// filled in from the Workshop.
        /// </summary>
        internal void OpenWorkshopEdit(Services.Workshop.WorkshopItem item, System.Windows.Window owner)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
            {
                return;
            }

            var dialog = new Views.Workshop.WorkshopShareDialog(this, _logger, item, WorkshopShareService, WorkshopIdentityStore);
            ShowWorkshopShareDialog(dialog, ResourceProvider.GetString("LOCPlayAch_Common_Edit"), owner);
        }

        private void ShowWorkshopShareDialog(Views.Workshop.WorkshopShareDialog dialog, string title, System.Windows.Window owner)
        {
            var window = Views.Helpers.PlayniteUiProvider.CreateExtensionWindow(
                title,
                dialog,
                new Views.Helpers.WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 620,
                    Height = 640
                });

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = owner ?? PlayniteApi?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch (InvalidOperationException)
            {
            }

            dialog.RequestClose += (s, args) => window.Close();
            window.ShowDialog();
            dialog.Cleanup();
        }

        /// <summary>Opens the settings on the Workshop tab, on <paramref name="pageKey"/> when given.</summary>
        internal void OpenWorkshopSettings(string pageKey = null)
        {
            OpenSettingsAt(new Models.Settings.SettingsNavigationRequest(Models.Settings.SettingsNavigationRequest.WorkshopTab, pageKey));
        }

        /// <summary>
        /// Shows a place in the settings. An open settings window switches to it and comes to
        /// the front; otherwise the settings open on it: the Playnite settings dialog on desktop,
        /// the managed popout in fullscreen. The place is handed over through
        /// <see cref="Views.SettingsControl.PendingNavigation"/> because neither opener takes one.
        /// </summary>
        internal void OpenSettingsAt(Models.Settings.SettingsNavigationRequest request)
        {
            var live = Views.SettingsControl.Live;
            if (live != null)
            {
                live.NavigateTo(request);
                System.Windows.Window.GetWindow(live)?.Activate();
                return;
            }

            Views.SettingsControl.PendingNavigation = request;
            if (IsFullscreenMode())
            {
                OpenSettingsWindow();
            }
            else
            {
                OpenSettingsView();
            }
        }

        /// <summary>
        /// Opens the Overview window on the Showcase on one page; an Overview window already open
        /// switches to it.
        /// </summary>
        internal void OpenShowcasePage(string pageId)
        {
            _windowService.OpenOverviewWindow(pageId);
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

        /// <summary>
        /// The Workshop hotkey and main-menu entry: the Settings > Workshop tab. An open settings
        /// window switches to that tab; otherwise the tab opens on its own in a popout, which comes to the front on
        /// the next press or closes when it is already active, so repeated presses never build a
        /// second one and reload the index.
        /// </summary>
        private void ToggleWorkshopTabWindow()
        {
            if (Views.SettingsControl.Live != null)
            {
                OpenWorkshopSettings();
                return;
            }

            if (_windowService.TryToggleOpenWindowHosting<Views.Settings.Workshop.WorkshopSettingsTab>())
            {
                return;
            }

            var view = new Views.Settings.Workshop.WorkshopSettingsTab(_settingsViewModel.Settings, this, _logger);
            _windowService.OpenManagedPopout(
                ResourceProvider.GetString("LOCPlayAch_Workshop_Title"),
                view,
                new Views.Helpers.WindowOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1100,
                    Height = 720
                },
                "WorkshopSettingsPopout",
                () => view.Dispose());
        }

        private bool _settingsViewOpen;

        /// <summary>
        /// Opens the plugin settings for hotkey invocations: Playnite's plugin-settings
        /// dialog in desktop mode, the managed settings popout in fullscreen mode where
        /// that dialog is unavailable. The desktop dialog is a blocking ShowDialog, so the
        /// flag stays set for its whole lifetime and repeated presses pumped by the nested
        /// dispatcher loop are ignored. Foreign modals (e.g. the add-ons window) are
        /// detected at the Win32 level: ShowDialog disables sibling windows via
        /// EnableWindow, which never flows into the IsEnabled dependency property. The
        /// plugin's own popouts also disable the main window that way, but they support a
        /// nested settings dialog on top, so the guard skips them via HasOpenPluginWindow.
        /// </summary>
        private void OpenSettingsViewFromHotkey()
        {
            if (_settingsViewOpen || _settingsPopoutOpen)
            {
                return;
            }

            if (IsFullscreenMode())
            {
                OpenSettingsWindow();
                return;
            }

            var mainWindow = System.Windows.Application.Current?.MainWindow;
            if (mainWindow != null)
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
                if (handle != IntPtr.Zero && !IsWindowEnabled(handle) && _windowService?.HasOpenPluginWindow() != true)
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
