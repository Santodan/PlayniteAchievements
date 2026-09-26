using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK.Events;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Notifications;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsControl : UserControl, IFullscreenControllerNavigable
    {
        private static readonly ManageAchievementsTab[] ControllerTabOrder =
        {
            ManageAchievementsTab.Overview,
            ManageAchievementsTab.Editor,
            ManageAchievementsTab.Category,
            ManageAchievementsTab.Notifications
        };

        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly ManageAchievementsViewModel _viewModel;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;

        private ManageAchievementsEditorTab _editorControl;
        private ManageAchievementsCategoryTab _categoryControl;
        private NotificationAppearanceSection _notificationsControl;
        private System.Windows.Threading.DispatcherTimer _iconOverridesChangedDebounce;
        private readonly HashSet<string> _pendingIconOverrideApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _pendingIconOverridesFromEditor;
        private bool _selfWriteMarkerClearQueued;
        private bool _librarySuspensionHeld;
        private ManageAchievementsEditorViewModel _editorViewModel;
        private ManageAchievementsCategoryViewModel _categoryViewModel;
        private bool _editorRefreshPending;
        private bool _categoryRefreshPending;
        private bool _notificationsRefreshPending;
        private bool _notificationsRefreshDiscardPending;
        private bool _selectManageCategoriesSubTab;
        private bool _ensureTabContentQueued;
        private bool _categoryEditsPendingPropagation;

        internal ManageAchievementsControl(
            Guid gameId,
            ManageAchievementsTab initialTab,
            RefreshRuntime refreshRuntime,
            ICacheManager cacheManager,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            ManualSourceRegistry manualSourceRegistry,
            bool selectManageCategoriesSubTab = false)
        {
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _manualSourceRegistry = manualSourceRegistry ?? throw new ArgumentNullException(nameof(manualSourceRegistry));
            _gameDataSnapshotProvider = new ManageAchievementsDataSnapshotProvider(gameId, _achievementDataService, logger);
            _selectManageCategoriesSubTab =
                initialTab == ManageAchievementsTab.Category && selectManageCategoriesSubTab;

            _viewModel = new ManageAchievementsViewModel(
                gameId,
                initialTab,
                PlayniteAchievementsPlugin.Instance,
                _refreshService,
                _persistSettingsForUi,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _playniteApi,
                _settings,
                _logger);

            DataContext = _viewModel;
            InitializeComponent();

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _refreshService.GameCacheUpdated += RefreshService_GameCacheUpdated;
            _refreshService.CacheDeltaUpdated += RefreshService_CacheDeltaUpdated;
            Loaded += ManageAchievementsControl_Loaded;

            // A window per game visited, each holding that game's rows and resolved art. If
            // either of these is still live after Cleanup, working through a list of games cannot
            // return memory and only a restart will.
            Common.LeakWatch.Track("ManageAchievementsControl", this);
            Common.LeakWatch.Track("ManageAchievementsViewModel", _viewModel);
            Common.LeakWatch.Track("ManageAchievementsSnapshotProvider", _gameDataSnapshotProvider);
        }

        public string WindowTitle
        {
            get
            {
                var format = ResourceProvider.GetString("LOCPlayAch_ManageAchievements_WindowTitle");
                if (string.IsNullOrWhiteSpace(format))
                {
                    format = "Manage Achievements - {0}";
                }

                return string.Format(format, _viewModel?.GameName ?? "Game");
            }
        }

        internal void SelectTab(ManageAchievementsTab tab, bool selectManageCategoriesSubTab = false)
        {
            if (_viewModel == null)
            {
                return;
            }

            if (tab == ManageAchievementsTab.Category && selectManageCategoriesSubTab)
            {
                _selectManageCategoriesSubTab = true;
            }

            _viewModel.SelectedTab = tab;

            // Built here, not queued. The window is shown with ShowDialog, which renders its
            // first frame before its nested dispatcher frame starts pumping queued operations,
            // so a queued build waits for the pump rather than the paint: measured at ~850ms
            // after ContentRendered, with the thread idle and no stall. (The ray animation kept
            // ticking through it, but CompositionTarget.Rendering is invoked from the render
            // pass, not the dispatcher queue, so that showed the thread rendering rather than
            // the queue draining.)
            //
            // Doing it inline costs ~150ms before the window appears and removes the second of
            // empty shell after it. The Loaded handler still queues, which covers a tab changed
            // before the window is up.
            EnsureSelectedTabContent();
            QueueFocusSelectedTab();
        }

        private void ManageAchievementsControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            // Marks where the shell finished loading. A capture of a slow open shows ~1.4s of
            // solid UI-thread work between the window being shown and the Background-priority
            // callback below running, with no plugin scope covering it and even the animation
            // tick starved. Loaded fires before WPF renders, so this line plus the
            // Manage.EnsureTabContent that follows brackets that span: if the gap sits after
            // this line, it is WPF laying out and rendering the shell, not plugin code.
            _logger?.Debug("[ManageOpen] shell loaded; queueing tab content at Background priority.");

            // Held for as long as this window is up, and released once in Cleanup. Editing here
            // raises a custom-data change per edit, and each one otherwise rebuilds every game's
            // theme lists -- work behind this window that nothing can see until it closes. The
            // game's own theme surface is untouched by the hold and still repaints per edit.
            if (!_librarySuspensionHeld)
            {
                _librarySuspensionHeld = true;
                PlayniteAchievementsPlugin.Instance?.ThemeIntegrationService?.SuspendLibraryRefresh();
            }

            QueueEnsureSelectedTabContent();
        }

        public void Cleanup()
        {
            Loaded -= ManageAchievementsControl_Loaded;


            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            if (_refreshService != null)
            {
                _refreshService.GameCacheUpdated -= RefreshService_GameCacheUpdated;
                _refreshService.CacheDeltaUpdated -= RefreshService_CacheDeltaUpdated;
            }

            CleanupEditor();
            CleanupCategory();
            CleanupNotifications();

            // A deferred shell reload must not be dropped on the way out: it is what leaves the
            // view model's own state consistent with the last edit.
            _viewModel?.FlushPendingShellReload();

            // Releases the hold taken on Loaded, which issues the single library rebuild standing
            // in for every edit made in here.
            if (_librarySuspensionHeld)
            {
                _librarySuspensionHeld = false;
                PlayniteAchievementsPlugin.Instance?.ThemeIntegrationService?.ResumeLibraryRefresh();
            }

            // Reported after a delay and a forced collection, so the ManageAchievements* live
            // counts in this line answer directly whether closing the window released it.
            PlayniteAchievementsPlugin.Instance?.ScheduleRetentionDiagnostics(
                "manage.closed",
                delaySeconds: 8);
        }

        // Closing no longer compacts the large object heap. It was added on the theory that the
        // per-edit row and hydration arrays were fragmenting it, and captures refute that: the
        // two runs that actually fired reclaimed 60.6 MB from a 539 MB heap and then 10.7 MB from
        // an 824 MB one. The memory is live, not fragmented, so a blocking compacting collection
        // -- which suspends every thread including the UI -- was stalling the window close to
        // reclaim almost nothing. The growth it was meant to answer is a retention problem and is
        // being chased as one.

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ManageAchievementsViewModel.SelectedTab))
            {
                // Marks the instant the tab actually changed. Without it a capture cannot tell
                // how long the user took to click from how long the queued build then waited,
                // and those need opposite fixes.
                if (Common.PerfScope.PerfTracingEnabled)
                {
                    _logger?.Debug("[ManageTab] selected " + _viewModel?.SelectedTab + "; queueing build.");
                }

                QueueEnsureSelectedTabContent();
            }
            else if (e.PropertyName == nameof(ManageAchievementsViewModel.CustomDataRevision))
            {
                HandleCustomDataRevisionChanged();
            }
        }

        private void QueueEnsureSelectedTabContent()
        {
            if (_ensureTabContentQueued)
            {
                return;
            }

            _ensureTabContentQueued = true;

            // Normal, not Background. Background sits below Input and Render, so this waited
            // behind whatever else the dispatcher had -- and with the ray animation driving a
            // continuous render loop on the surface behind this window, that was measured at
            // about a second between the shell loading and the tab content appearing, with the
            // UI thread responsive throughout. It was starvation, not work: the build itself is
            // ~150ms, and the window showed an empty shell for the whole wait.
            //
            // Still queued rather than called inline, so the shell lays out first; it just no
            // longer yields to everything else once it has.
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _ensureTabContentQueued = false;
                EnsureSelectedTabContent();
            }), DispatcherPriority.Normal);
        }

        private void EnsureSelectedTabContent()
        {
            if (_viewModel == null)
            {
                return;
            }

            using var tabScope = Common.PerfScope.Start(
                _logger,
                "Manage.EnsureTabContent",
                thresholdMs: 25,
                context: "tab=" + _viewModel.SelectedTab);

            if (_viewModel.SelectedTab != ManageAchievementsTab.Category)
            {
                PropagateCategoryEditsToSiblingTabs();
            }

            if (_viewModel.SelectedTab == ManageAchievementsTab.Overview)
            {
                EnsureOverviewControl();
            }
            else if (_viewModel.SelectedTab == ManageAchievementsTab.Editor)
            {
                var hadEditorControl = _editorControl != null;
                EnsureEditorControl(forceRecreate: false);
                if (_editorRefreshPending)
                {
                    if (hadEditorControl)
                    {
                        _editorControl?.RefreshData();
                    }

                    _editorRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == ManageAchievementsTab.Category)
            {
                var hadCategoryControl = _categoryControl != null;
                EnsureCategoryControl(forceRecreate: false);
                ApplyPendingCategorySubTabSelection();
                if (_categoryRefreshPending)
                {
                    if (hadCategoryControl)
                    {
                        _categoryViewModel?.ReloadData();
                    }

                    _categoryRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == ManageAchievementsTab.Notifications)
            {
                var hadNotificationsControl = _notificationsControl != null;
                EnsureNotificationsControl(forceRecreate: false);
                if (_notificationsRefreshPending)
                {
                    if (hadNotificationsControl)
                    {
                        _notificationsControl?.RefreshData(_notificationsRefreshDiscardPending);
                    }

                    _notificationsRefreshPending = false;
                    _notificationsRefreshDiscardPending = false;
                }
            }

            // Tabs stay lazy: only the selected one is built. Building the editor with the
            // window was tried and rejected - it put its construction and layout into every
            // open, including the ones that never touch it. The cost belongs on the click; the
            // work is to make it smaller, not to move it.
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (FullscreenControllerNavigationService.IsBackInput(input))
            {
                Window.GetWindow(this)?.Close();
                return true;
            }

            if (FullscreenControllerNavigationService.IsLeftShoulderInput(input))
            {
                return MoveSelectedTab(-1);
            }

            if (FullscreenControllerNavigationService.IsRightShoulderInput(input))
            {
                return MoveSelectedTab(1);
            }

            if (TryHandleSelectedTabControllerInput(input))
            {
                return true;
            }

            if (TryHandleDirectionalNavigation(input))
            {
                return true;
            }

            if (FullscreenControllerNavigationService.IsAcceptInput(input))
            {
                if (FullscreenControllerNavigationService.ActivateFocusedElement())
                {
                    return true;
                }

                return TryHandleGenericReveal();
            }

            return false;
        }

        private bool TryHandleDirectionalNavigation(ControllerInput input)
        {
            if (FullscreenControllerNavigationService.TryGetHorizontalDelta(input, out var horizontalDelta))
            {
                if (horizontalDelta > 0 && IsKeyboardFocusWithinTabSelector())
                {
                    return FocusCurrentContentFirstElement();
                }

                if (horizontalDelta < 0 && TryFocusTabSelectorFromContentLeftEdge())
                {
                    return true;
                }

                return TryMoveContentFocus(horizontalDelta < 0
                    ? FocusNavigationDirection.Left
                    : FocusNavigationDirection.Right);
            }

            if (FullscreenControllerNavigationService.TryGetVerticalDelta(input, out var verticalDelta))
            {
                return TryMoveContentFocus(verticalDelta < 0
                    ? FocusNavigationDirection.Up
                    : FocusNavigationDirection.Down);
            }

            return false;
        }

        private bool TryMoveContentFocus(FocusNavigationDirection direction)
        {
            if (!FullscreenControllerNavigationService.IsKeyboardFocusWithin(ManageAchievementsContentHost))
            {
                return false;
            }

            if (FullscreenControllerNavigationService.MoveFocus(direction, ManageAchievementsContentHost))
            {
                return true;
            }

            var delta = direction == FocusNavigationDirection.Up || direction == FocusNavigationDirection.Left
                ? -1
                : 1;
            return FullscreenControllerNavigationService.FocusElementByDelta(
                GetCurrentContentControllerElements(),
                delta);
        }

        private bool TryFocusTabSelectorFromContentLeftEdge()
        {
            if (!FullscreenControllerNavigationService.IsKeyboardFocusWithin(ManageAchievementsContentHost))
            {
                return false;
            }

            var focused = Keyboard.FocusedElement as DependencyObject;
            var focusedGrid = FullscreenControllerNavigationService.FindAncestor<DataGrid>(focused)
                              ?? focused as DataGrid;
            if (focusedGrid != null &&
                FullscreenControllerNavigationService.IsDescendantOf(focusedGrid, ManageAchievementsContentHost))
            {
                return FullscreenControllerNavigationService.IsFocusAtDataGridLeftEdge(focusedGrid) &&
                       FocusSelectedTabButton();
            }

            if (FullscreenControllerNavigationService.MoveFocus(FocusNavigationDirection.Left, ManageAchievementsContentHost))
            {
                return true;
            }

            return FocusSelectedTabButton();
        }

        private bool TryHandleGenericReveal()
        {
            var focused = Keyboard.FocusedElement as FrameworkElement;
            if (focused == null)
            {
                return false;
            }

            // If we're already on an interactive element, ActivateFocusedElement should have handled it.
            // But if it didn't (e.g. it was a DataGridRow with a nested checkbox), we might be here.
            // Check if the focused element is a checkbox/button first.
            if (focused is ButtonBase || focused is Selector || focused is DatePicker || focused is Expander)
            {
                return false;
            }

            if (focused.DataContext is AchievementDisplayItem item)
            {
                item.ToggleReveal();
                return true;
            }

            // Fallback for manual tracking which uses a different model
            if (focused.DataContext is ManualAchievementEditItem manualItem)
            {
                manualItem.ToggleReveal();
                return true;
            }

            return false;
        }

        private IList<RadioButton> GetVisibleTabButtons()
        {
            return new[]
                {
                    OverviewTabButton,
                    EditorTabButton,
                    CategoryTabButton,
                    NotificationsTabButton
                }
                .Where(button => button != null && button.IsVisible && button.IsEnabled)
                .ToList();
        }

        private bool IsKeyboardFocusWithinTabSelector()
        {
            return GetVisibleTabButtons().Any(button => button.IsKeyboardFocusWithin);
        }

        private bool FocusSelectedTabButton()
        {
            var tabButton = GetVisibleTabButtons()
                .FirstOrDefault(button => button.IsChecked == true)
                ?? GetVisibleTabButtons().FirstOrDefault();

            return tabButton != null && FullscreenControllerNavigationService.FocusElement(tabButton);
        }

        private bool FocusCurrentContentFirstElement()
        {
            return FullscreenControllerNavigationService.FocusFirstElement(GetCurrentContentControllerElements());
        }

        private IList<UIElement> GetCurrentContentControllerElements()
        {
            EnsureSelectedTabContent();

            DependencyObject root;
            switch (_viewModel?.SelectedTab)
            {
                case ManageAchievementsTab.Overview:
                    root = _overviewControl ?? (DependencyObject)OverviewHost;
                    break;
                case ManageAchievementsTab.Editor:
                    return _editorControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.Category:
                    return _categoryControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.Notifications:
                    root = _notificationsControl ?? (DependencyObject)NotificationsHost;
                    break;
                default:
                    root = ManageAchievementsContentHost;
                    break;
            }

            return FullscreenControllerNavigationService.GetVisibleFocusableElements(root);
        }

        private bool MoveSelectedTab(int delta)
        {
            if (_viewModel == null || delta == 0)
            {
                return false;
            }

            var tabs = ControllerTabOrder
                .Where(IsControllerTabVisible)
                .ToList();
            if (tabs.Count <= 1)
            {
                return false;
            }

            var currentIndex = tabs.IndexOf(_viewModel.SelectedTab);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            var nextIndex = (currentIndex + delta + tabs.Count) % tabs.Count;
            _viewModel.SelectedTab = tabs[nextIndex];
            QueueFocusSelectedTab();
            return true;
        }

        private bool TryHandleSelectedTabControllerInput(ControllerInput input)
        {
            if (_viewModel == null)
            {
                return false;
            }

            switch (_viewModel.SelectedTab)
            {
                case ManageAchievementsTab.Editor:
                    return _editorControl?.HandleFullscreenControllerInput(input) == true;
                case ManageAchievementsTab.Category:
                    return _categoryControl?.HandleFullscreenControllerInput(input) == true;
                default:
                    return false;
            }
        }

        private bool IsControllerTabVisible(ManageAchievementsTab tab)
        {
            if (_viewModel == null)
            {
                return false;
            }

            if (ManageAchievementsTabs.RequireAchievementData.Contains(tab))
            {
                return _viewModel.HasAchievementData;
            }

            return true;
        }

        private void QueueFocusSelectedTab()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var tabButton = FindVisualChildren<RadioButton>(this)
                    .FirstOrDefault(button =>
                        button.Visibility == Visibility.Visible &&
                        button.IsChecked == true);
                if (tabButton != null)
                {
                    tabButton.Focus();
                    Keyboard.Focus(tabButton);
                }
            }), DispatcherPriority.Input);
        }

        // Sidebar stat groups in the order the sidebar shows them; null marks a separator.
        private static readonly Tuple<Models.Settings.ManageSidebarStatGroups, string>[] SidebarStatMenuEntries =
        {
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Capstones, "LOCPlayAch_Dynamic_Capstone"),
            null,
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Rarity, "LOCPlayAch_Column_Rarity"),
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Trophies, "LOCPlayAch_Column_Trophy"),
            null,
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Points, "LOCPlayAch_Column_Points"),
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Goals, "LOCPlayAch_ManageAchievements_Editor_Goal"),
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Categorized, "LOCPlayAch_ManageAchievements_Overview_Categorized"),
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Filtered, "LOCPlayAch_Menu_Filters"),
            Tuple.Create(Models.Settings.ManageSidebarStatGroups.Notes, "LOCPlayAch_ManageAchievements_Tab_Notes")
        };

        private void SidebarStatsHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            var menu = new ContextMenu();
            foreach (var entry in SidebarStatMenuEntries)
            {
                if (entry == null)
                {
                    menu.Items.Add(new Separator());
                    continue;
                }

                var group = entry.Item1;
                var item = new MenuItem
                {
                    Header = ResourceProvider.GetString(entry.Item2),
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = _viewModel.IsSidebarStatGroupShown(group)
                };
                item.Click += (_, __) => _viewModel?.SetSidebarStatGroupShown(group, item.IsChecked);
                menu.Items.Add(item);
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            menu.PlacementTarget = SidebarStatsHost;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (var nested in FindVisualChildren<T>(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>
        /// Re-projects the game's manual link onto its cached achievement data after the editor
        /// records unlocks, so counts, summaries and themes see them without a provider refresh.
        /// </summary>
        private void ApplyManualLinkToCache(Guid gameId)
        {
            if (!ManualAchievementsProvider.TryGetManualLink(gameId, out var link) || link == null)
            {
                return;
            }

            var source = _manualSourceRegistry?.GetSourceByKey(link.SourceKey);
            new ManualLinkCacheApplier(
                _achievementDataService,
                _cacheManager,
                _settings,
                _logger).Apply(gameId, link, source);
        }

        /// <summary>
        /// Runs the manual-link wizard in a window. Returns true when a link was committed.
        /// </summary>
        /// <remarks>
        /// The wizard persists the link as soon as the refresh confirms usable schema data, which is
        /// the moment it leaves the search and refresh stages — so that transition is what closes
        /// the window. Unlock editing then happens in the editor grid rather than in the wizard.
        /// </remarks>
        private bool ShowManualLinkDialog()
        {
            var game = _playniteApi?.Database?.Games?.Get(_viewModel.GameId);
            if (game == null)
            {
                return false;
            }

            if (!_viewModel.ConfirmManualTrackingOverride())
            {
                return false;
            }

            var availableSources = _manualSourceRegistry?.GetAllSources()?.ToList();
            if (availableSources == null || availableSources.Count == 0)
            {
                return false;
            }

            var initialSource = ManualAchievementsProvider.TryGetManualLink(_viewModel.GameId, out var existingLink)
                ? _manualSourceRegistry.GetSourceByKey(existingLink?.SourceKey) ?? _manualSourceRegistry.GetDefaultSource()
                : _manualSourceRegistry.GetDefaultSource();

            var viewModel = new ManualAchievementsViewModel(
                game,
                _refreshService,
                _cacheManager,
                _achievementDataService,
                availableSources,
                initialSource,
                _settings,
                SaveSettings,
                _logger,
                _playniteApi);

            var control = new ManageAchievementsManualTrackingTab(viewModel);
            control.UnlinkCommand = _viewModel.UnlinkManualTrackingCommand;

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tab_ManualTracking"),
                control,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 900,
                    Height = 700
                });

            var linked = false;
            PropertyChangedEventHandler onStageChanged = (_, args) =>
            {
                if (args?.PropertyName != nameof(ManualAchievementsViewModel.CurrentStage))
                {
                    return;
                }

                if (viewModel.CurrentStage == WizardStage.Editing ||
                    viewModel.CurrentStage == WizardStage.Completed)
                {
                    linked = true;
                    window.Close();
                }
            };

            viewModel.PropertyChanged += onStageChanged;
            WindowPlacementPersistenceService.Attach(window, "ManualAchievementLink");
            try
            {
                window.ShowDialog();
            }
            finally
            {
                viewModel.PropertyChanged -= onStageChanged;
                viewModel.Cleanup();
            }

            return linked;
        }

        private void EnsureEditorControl(bool forceRecreate)
        {
            if (_editorControl != null && !forceRecreate)
            {
                return;
            }

            // Builds the editor's view model and its view. Dispatched at Background priority
            // from Loaded, so it lands after the window has already appeared -- which is the
            // span between the window showing and Editor.ReloadData that a capture of a slow
            // open shows as an unexplained gap.
            using var scope = Common.PerfScope.Start(
                _logger,
                "Manage.EnsureEditorControl",
                thresholdMs: 25,
                context: "recreate=" + forceRecreate);

            CleanupEditor();

            // Same view model and view as the Custom tab, told to include provider achievements:
            // the merged editor is that editor pointed at every achievement rather than only the
            // authored ones, so it inherits the icon, date, rarity and category editing wholesale.
            _editorViewModel = new ManageAchievementsEditorViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                PlayniteAchievementsPlugin.Instance?.GameCustomDataStore,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                _gameDataSnapshotProvider,
                _settings,
                _logger,
                PlayniteAchievementsPlugin.Instance?.CustomProviderStore,
                currentValue => PlayniteAchievementsPlugin.Instance?.PickColor(Window.GetWindow(this), currentValue),
                editor => CustomProviderEditorDialog.Show(Window.GetWindow(this), editor),
                includeProviderAchievements: true,
                manualLinkApplier: ApplyManualLinkToCache,
                showManualLinkDialog: ShowManualLinkDialog,
                unlinkManualTracking: () => _viewModel.UnlinkManualTrackingCommand?.Execute(null));
            _editorViewModel.CustomAchievementsSaved += CustomViewModel_CustomAchievementsSaved;
            _editorViewModel.AssignmentsChanged += EditorViewModel_CustomizationPersisted;
            _editorViewModel.IconOverridesSaved += EditorViewModel_IconOverridesSaved;
            _editorViewModel.CapstoneChanged += CustomViewModel_CapstoneChanged;
            _editorControl = new ManageAchievementsEditorTab(_editorViewModel);
            EditorHost.Content = _editorControl;

            Common.LeakWatch.Track("ManageAchievementsEditorViewModel", _editorViewModel);
            Common.LeakWatch.Track("ManageAchievementsEditorTab", _editorControl);
        }

        // An edit here changes the same data the per-facet tabs show, so it propagates exactly as
        // a Category tab edit does. The editor already shows its own change, so it is not marked
        // for refresh and keeps its rows and selection.
        private void EditorViewModel_CustomizationPersisted(object sender, EventArgs e)
        {
            // Marked before propagating, not after: the propagation bumps CustomDataRevision
            // synchronously, so the refresh this is meant to pre-empt had already run - rebuilding
            // every row and taking the grid's selection with it - by the time control returned
            // here. Set on the view model rather than per raise site so every editor-originated
            // notification is covered.
            if (_editorViewModel != null)
            {
                _editorViewModel.SuppressExternalRefresh = true;
            }

            CategoryViewModel_DeferredLibraryRefreshRequired(sender, e);
            _editorRefreshPending = false;
        }

        private void CleanupEditor()
        {
            if (_iconOverridesChangedDebounce?.IsEnabled == true)
            {
                // A commit is still waiting on the debounce; apply it before tearing down.
                _iconOverridesChangedDebounce.Stop();
                FlushPendingIconOverrideChanges();
            }

            // Before the view model's own teardown: the tab's subscriptions point at it.
            _editorControl?.Cleanup();

            if (_editorViewModel != null)
            {
                _editorViewModel.CustomAchievementsSaved -= CustomViewModel_CustomAchievementsSaved;
                _editorViewModel.AssignmentsChanged -= EditorViewModel_CustomizationPersisted;
                _editorViewModel.IconOverridesSaved -= EditorViewModel_IconOverridesSaved;
                _editorViewModel.CapstoneChanged -= CustomViewModel_CapstoneChanged;
                _editorViewModel.Detach();
            }

            _editorControl = null;
            _editorViewModel = null;

            if (EditorHost != null)
            {
                EditorHost.Content = null;
            }
        }

        private void EnsureCategoryControl(bool forceRecreate)
        {
            if (_categoryControl != null && !forceRecreate)
            {
                return;
            }

            CleanupCategory();

            _categoryViewModel = new ManageAchievementsCategoryViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                _settings,
                _logger);
            _categoryViewModel.CategoryMetadataPersisted += CategoryViewModel_CategoryMetadataPersisted;
            _categoryViewModel.DeferredLibraryRefreshRequired += CategoryViewModel_DeferredLibraryRefreshRequired;
            _categoryControl = new ManageAchievementsCategoryTab(_categoryViewModel);
            CategoryHost.Content = _categoryControl;
            Common.LeakWatch.Track("ManageAchievementsCategoryTab", _categoryControl);
            Common.LeakWatch.Track("ManageAchievementsCategoryTabViewModel", _categoryViewModel);
        }

        private void ApplyPendingCategorySubTabSelection()
        {
            if (!_selectManageCategoriesSubTab || _categoryControl == null)
            {
                return;
            }

            _selectManageCategoriesSubTab = false;
            _categoryControl.SelectManageCategoriesSubTab();
        }

        private void EnsureNotificationsControl(bool forceRecreate)
        {
            if (_notificationsControl != null && !forceRecreate)
            {
                return;
            }

            CleanupNotifications();
            _notificationsControl = new NotificationAppearanceSection(
                _settings,
                PlayniteAchievementsPlugin.Instance,
                _logger,
                _viewModel.GameId,
                _viewModel.EffectiveProviderKey);
            NotificationsHost.Content = _notificationsControl;
        }

        // The overview and overrides tabs take no constructor arguments and read everything
        // from the shared DataContext, so hosting them costs nothing beyond the instance.
        private void EnsureOverviewControl()
        {
            if (_overviewControl != null)
            {
                return;
            }

            _overviewControl = new ManageAchievementsOverviewTab();
            OverviewHost.Content = _overviewControl;
        }

        private ManageAchievementsOverviewTab _overviewControl;

        private void CustomViewModel_CustomAchievementsSaved(object sender, EventArgs e)
        {
            // The Custom tab persists on every completed edit and already reflects the change;
            // reloading it here would rebuild its rows under the user's focus.
            HandleStateChanged(refreshCustom: false);
        }

        private void CustomViewModel_AssignmentsChanged(object sender, EventArgs e)
        {
            // Same propagation as a Category tab edit; the Custom tab already shows the change,
            // so it keeps its rows and selection instead of reloading on its own edit.
            CategoryViewModel_DeferredLibraryRefreshRequired(sender, e);
            _editorRefreshPending = false;
        }

        private void CustomViewModel_CapstoneChanged(object sender, CapstoneChangedEventArgs e)
        {
            _gameDataSnapshotProvider?.Invalidate();
            _viewModel?.NotifyCapstoneChanged(e?.DisplayName);
        }

        /// <summary>
        /// The refresh an icon write sets off arrives after the assignments cascade has already
        /// consumed the editor's self-write marker. Marking the flush as the editor's own re-arms
        /// it, so the editor keeps its rows and the selection the user is still editing.
        /// </summary>
        private void EditorViewModel_IconOverridesSaved(object sender, IconOverridesSavedEventArgs e)
        {
            _pendingIconOverridesFromEditor = true;
            HandleIconOverridesSaved(sender, e);
        }

        private void HandleIconOverridesSaved(object sender, IconOverridesSavedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => HandleIconOverridesSaved(sender, e)));
                return;
            }

            _gameDataSnapshotProvider?.Invalidate();
            _pendingIconOverrideApiNames.UnionWith(e?.ChangedApiNames ?? Array.Empty<string>());

            // Icon overrides persist per interaction; debounce so one editing burst applies
            // the changed icons once, shortly after the last commit.
            if (_iconOverridesChangedDebounce == null)
            {
                _iconOverridesChangedDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(750)
                };
                _iconOverridesChangedDebounce.Tick += (_, __) =>
                {
                    _iconOverridesChangedDebounce.Stop();
                    FlushPendingIconOverrideChanges();
                };
            }

            _iconOverridesChangedDebounce.Stop();
            _iconOverridesChangedDebounce.Start();
        }

        private void FlushPendingIconOverrideChanges()
        {
            if (_pendingIconOverrideApiNames.Count == 0)
            {
                return;
            }

            var changedApiNames = _pendingIconOverrideApiNames.ToList();
            _pendingIconOverrideApiNames.Clear();

            if (_pendingIconOverridesFromEditor)
            {
                _pendingIconOverridesFromEditor = false;
                if (_editorViewModel != null)
                {
                    _editorViewModel.SuppressExternalRefresh = true;
                }
            }

            _viewModel?.NotifyIconOverridesChanged(changedApiNames);

            // Custom achievement icons are written into their definitions, which the Custom
            // tab edits; it reloads on its next visit unless it holds unsaved edits.
            if (changedApiNames.Any(CustomAchievementProjectionService.IsCustomApiName))
            {
                _editorRefreshPending = true;
            }
        }

        private void RefreshService_GameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            if (_viewModel == null ||
                !Guid.TryParse(e?.GameId, out var updatedGameId) ||
                updatedGameId != _viewModel.GameId)
            {
                return;
            }

            DispatchHandleStateChanged();
        }

        private void RefreshService_CacheDeltaUpdated(object sender, CacheDeltaEventArgs e)
        {
            if (e?.IsFullReset != true)
            {
                return;
            }

            DispatchHandleStateChanged();
        }

        private void DispatchHandleStateChanged()
        {
            if (Dispatcher.CheckAccess())
            {
                HandleStateChanged();
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => HandleStateChanged()));
        }

        /// <summary>
        /// Whether the refresh being handled was caused by the editor's own write.
        /// </summary>
        /// <remarks>
        /// The marker is deliberately NOT cleared here. One write can fan out into more than one
        /// refresh leg -- a cache-updated path reaching <see cref="HandleStateChanged"/> and the
        /// revision-changed path reaching <see cref="HandleCustomDataRevisionChanged"/> -- and
        /// clearing on the first read let the second leg mistake the editor's own edit for an
        /// external change. That ran a full ReloadData: 641 rows rebuilt, the grid reset, and
        /// every visible container re-realized, measured at about a second of UI-thread work
        /// starting ~14ms after the save. It is the per-edit hitch.
        ///
        /// Clearing is instead posted at Background priority. Every leg of one write's cascade is
        /// synchronous and runs before that callback, so all of them see the marker; a genuinely
        /// external change arriving afterwards finds it cleared and still refreshes. This is
        /// scoped to the cascade rather than to a wall-clock window, so it stays deterministic.
        /// </remarks>
        private bool ConsumeEditorSelfWrite()
        {
            if (_editorViewModel?.SuppressExternalRefresh != true)
            {
                return false;
            }

            ScheduleSelfWriteMarkerClear();
            return true;
        }

        private void ScheduleSelfWriteMarkerClear()
        {
            if (_selfWriteMarkerClearQueued)
            {
                return;
            }

            _selfWriteMarkerClearQueued = true;
            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _selfWriteMarkerClearQueued = false;
                    if (_editorViewModel != null)
                    {
                        _editorViewModel.SuppressExternalRefresh = false;
                    }
                }),
                DispatcherPriority.Background);
        }

        private void HandleStateChanged(bool refreshCustom = true)
        {
            using (PlayniteAchievements.Common.PerfScope.Start(_logger, "Manage.HandleStateChanged.Invalidate", thresholdMs: 10))
            {
                _gameDataSnapshotProvider?.Invalidate();
            }

            using (PlayniteAchievements.Common.PerfScope.Start(_logger, "Manage.HandleStateChanged.ShellReload", thresholdMs: 10))
            {
                // Coalesced and deferred: EnsureSelectedTabContent below rehydrates the snapshot
                // for the visible tab, so letting the shell reload land after it reads a warm
                // snapshot instead of forcing its own cold load on the UI thread.
                _viewModel.ScheduleShellReload();
            }

            // Not for the editor's own write: it already shows the change, and reloading
            // would rebuild every row and revert the control the user just touched.
            _editorRefreshPending = refreshCustom && !ConsumeEditorSelfWrite();
            _categoryRefreshPending = true;
            _notificationsRefreshPending = true;

            using (PlayniteAchievements.Common.PerfScope.Start(_logger, "Manage.HandleStateChanged.EnsureTabContent", thresholdMs: 10))
            {
                EnsureSelectedTabContent();
            }
        }

        private void HandleCustomDataRevisionChanged()
        {
            // Deliberately does not invalidate the snapshot. The revision is bumped in exactly one
            // place -- ManageAchievementsViewModel.NotifyCustomDataChanged -- which invalidates and
            // then reloads immediately before bumping it. Invalidating here threw that reload away
            // one line after it finished, so every edit paid a second full hydration of the game's
            // achievements before the visible tab then paid a third. On a game with hundreds of
            // achievements that is the UI thread stalling on each edit.
            _editorRefreshPending = !ConsumeEditorSelfWrite();
            _categoryRefreshPending = true;
            _notificationsRefreshPending = true;
            _notificationsRefreshDiscardPending = true;
            EnsureSelectedTabContent();
        }

        private void CleanupCategory()
        {
            if (_categoryViewModel != null)
            {
                // Before unsubscribing: the tab's edits skipped the library-wide passes, and this
                // is where they are paid for, once.
                _categoryViewModel.FlushDeferredLibraryRefresh();
                _categoryViewModel.CategoryMetadataPersisted -= CategoryViewModel_CategoryMetadataPersisted;
                _categoryViewModel.DeferredLibraryRefreshRequired -= CategoryViewModel_DeferredLibraryRefreshRequired;
            }

            _categoryControl = null;
            _categoryViewModel = null;

            if (CategoryHost != null)
            {
                CategoryHost.Content = null;
            }
        }

        private void CategoryViewModel_CategoryMetadataPersisted(object sender, EventArgs e)
        {
            _viewModel?.RefreshGameImage();

            // The Category tab's writes skip the library-wide passes until the tab is torn down,
            // and the other tabs in this window were only marked stale by that same teardown: a
            // category created or renamed here reached the Editor's picker once the window had
            // been closed and reopened. Recorded here and drained when the user leaves the tab,
            // so a click on this tab still costs no rebuild.
            _categoryEditsPendingPropagation = true;
        }

        /// <summary>
        /// Marks the tabs that read this game's categories stale after a Category tab edit. Runs on
        /// the way out of that tab rather than per edit, so the tab's own click cost is unchanged
        /// and each sibling reloads once, when it is next shown.
        /// </summary>
        private void PropagateCategoryEditsToSiblingTabs()
        {
            if (!_categoryEditsPendingPropagation)
            {
                return;
            }

            _categoryEditsPendingPropagation = false;
            _gameDataSnapshotProvider?.Invalidate();
            _editorRefreshPending = true;

            // The overview's categorized count and customization chips come from the shell
            // reload, which nothing on the Category tab schedules. Coalesced, and paid once per
            // exit from the tab like the rest of this.
            _viewModel?.ScheduleShellReload();
        }

        private void CategoryViewModel_DeferredLibraryRefreshRequired(object sender, EventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            // Scoped to this game: the overview routes a named game through its per-game fragment
            // path rather than a full rebuild, and the projection is dropped once.
            _viewModel.NotifyCustomDataChanged(requiresRefresh: false);

            // A cache invalidation alone does not rebuild the desktop theme's library-wide lists,
            // and those carry every game's achievements with their category labels.
            try
            {
                PlayniteAchievementsPlugin.Instance?.ThemeIntegrationService?
                    .NotifyCustomDataChanged(_viewModel.GameId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to refresh theme state after deferred category edits.");
            }
        }

        private void CleanupNotifications()
        {
            if (_notificationsControl != null)
            {
                try
                {
                    _notificationsControl.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to cleanup notifications tab control.");
                }
            }

            _notificationsControl = null;
            if (NotificationsHost != null)
            {
                NotificationsHost.Content = null;
            }
        }

        private void SaveSettings(PlayniteAchievementsSettings settings)
        {
            try
            {
                PlayniteAchievementsPlugin.Instance?.SavePluginSettings(settings);
                HandleStateChanged();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist settings from Manage Achievements view.");
            }
        }
    }
}




