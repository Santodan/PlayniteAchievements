using Playnite.SDK;
using PlayniteAchievements.Views.Dialogs;
using Microsoft.Win32;
using Playnite.SDK.Events;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsEditorTab : UserControl, IFullscreenControllerNavigable
    {
        private static readonly Regex HttpUrlRegex = new Regex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] SupportedImageExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".gif",
            ".tif",
            ".tiff"
        };

        private const string DragDataFormat = "PlayniteAchievements.ManageAchievementsEditorRows";

        /// <summary>
        /// The columns locked to the left edge, in order. Not hideable either, so they also seed
        /// the layout service's excluded-visibility set. The first of them is the drag handle, and
        /// <see cref="DataGridRowReorderBehavior"/> recognises that handle by its display index,
        /// which is what makes pinning it load-bearing rather than cosmetic.
        /// </summary>
        private static readonly string[] PinnedColumnKeys = { "EditorOrder", "EditorStatus", "EditorUnlocked" };

        /// <summary>
        /// Which columns a config that has never been touched shows. Merged over the persisted map
        /// on read, so a column added later appears at its default without a migration while an
        /// explicit hide still persists.
        /// </summary>
        private static readonly Dictionary<string, bool> DefaultColumnVisibility =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["EditorIcon"] = true,
                ["EditorName"] = true,
                ["EditorDescription"] = true,
                ["EditorUnlockTime"] = true,

                // The facet columns are opt-in. Every one of them is also on the details pane, so
                // nothing is out of reach, and showing all eleven by default would squeeze the
                // description column under its minimum and put a horizontal scrollbar on every
                // install. A key left out of this map keeps whatever the markup declared, which
                // for these is visible - so they have to be named here, not merely omitted.
                ["EditorHidden"] = false,
                ["EditorGoal"] = false,
                ["EditorCapstone"] = false,
                ["EditorRarity"] = false,
                ["EditorTrophy"] = false,
                ["EditorPoints"] = false,
                ["EditorProgress"] = false,
                ["EditorCategory"] = false,
                ["EditorType"] = false,
                ["EditorFilter"] = false,
                ["EditorNote"] = false
            };

        /// <summary>
        /// Starting widths for columns the user has never resized, matching the widths declared in
        /// the XAML so a fresh install looks the way the markup reads.
        /// </summary>
        private static readonly Dictionary<string, double> DefaultColumnWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["EditorIcon"] = 58,
                ["EditorName"] = 240,
                ["EditorDescription"] = 320,
                ["EditorUnlockTime"] = 320,
                ["EditorHidden"] = 64,
                ["EditorGoal"] = 64,
                ["EditorCapstone"] = 84,
                ["EditorRarity"] = 150,
                ["EditorTrophy"] = 118,
                ["EditorPoints"] = 76,
                ["EditorProgress"] = 124,
                ["EditorCategory"] = 168,
                ["EditorType"] = 144,
                ["EditorFilter"] = 124,
                ["EditorNote"] = 210
            };

        private DataGridColumnLayoutService _columnPersistence;

        private AchievementEditorRow _categoryPickerRow;

        private string _categoryPickerLabel;

        private DataGridRow _pendingRightClickRow;

        public ManageAchievementsEditorTab(ManageAchievementsEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // The picker applies on pick, like the rest of this window, and is seeded for the row
            // that was selected when editing began, so it always commits to the right one.
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            viewModel.FilterChanged += ViewModel_FilterChanged;
            AttachFilter();
            viewModel.AssignmentsChanged += ViewModel_AssignmentsChanged;
            viewModel.ScrollRowIntoViewRequested += ViewModel_ScrollRowIntoViewRequested;
            viewModel.RestoreSelectionRequested += ViewModel_RestoreSelectionRequested;
            CategoryPicker.SelectionCommitted += CategoryPicker_SelectionCommitted;

            // Arrow navigation is the window's, not the grid's: stepping through achievements is
            // what this window is for, so it should not depend on which control was clicked last.
            Loaded += AchievementNavigation_Loaded;
            Unloaded += AchievementNavigation_Unloaded;

            // Dragging artwork in from outside: hovering a row selects it, so the details pane is
            // showing that achievement's icon slots by the time the pointer reaches them. On the
            // tunnelling event at the tab root, which reaches here before the grid's own reorder
            // handlers rather than depending on the order those were attached in.
            PreviewDragOver += EditorTab_PreviewDragOver;
            PreviewDrop += EditorTab_EndArtworkDrag;
            DragLeave += EditorTab_EndArtworkDrag;
            CategoryPicker.CreateRequested += CategoryPicker_CreateRequested;
            SeedCategoryPicker();

            // Same behavior that drove the Order and Goals grids, including its ctrl/shift-
            // preserving press handling, so a multi-row drag behaves the way it did there.
            DataGridRowReorderBehavior.SetOptions(CustomAchievementsGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = DragDataFormat,
                DropIndicator = DropInsertLine,
                DragCountPopup = DragCountPopup,
                DragCountText = DragCountText,
                IsReorderableItem = item => item is AchievementEditorRow,
                ExtractDragKeys = items => AchievementOrderHelper.NormalizeApiNames(
                    items.OfType<AchievementEditorRow>().Select(item => item.OriginalApiName)),
                MoveItemsRelativeToTarget = (apiNames, target, insertAfter) =>
                    target is AchievementEditorRow targetRow &&
                    ViewModel?.MoveItemsByApiName(apiNames, targetRow.OriginalApiName, insertAfter) == true,
                MoveItemsToEnd = apiNames => ViewModel?.MoveItemsToEndByApiName(apiNames) == true,
                RestoreSelection = RestoreSelectionByApiNames,
                RowPressOutsideDragHandle = NormalizeSelectionForRoutedCell
            });

            AttachColumnPersistence();

            // Row realization, sampled. Scrolling this grid is reported as slow and the usual
            // causes are ruled out: virtualization is on with recycling, no DataGrid column is
            // Auto-width, and the icon decode is already off-thread. What is left is what a
            // realized row costs and how many realize per scroll, and neither was measured --
            // two hypotheses read from the markup did not survive checking, so this measures
            // instead of guessing a third time.
            CustomAchievementsGrid.LoadingRow += AchievementsGrid_LoadingRow;
            CustomAchievementsGrid.UnloadingRow += AchievementsGrid_UnloadingRow;

            // Lets a refresh skip notifying rows nothing is bound to. Without this the view
            // model has no way to tell, so it tells every row -- and WPF answering that for
            // hundreds of rows is what stalled the UI for the better part of a second.
            if (viewModel != null)
            {
                viewModel.IsRowRealized = row => row != null && _realizedRows.Contains(row);
            }

            // On this control, not the window: stepping achievements with the arrows is what the
            // whole window is for, but Ctrl+Z is not - a window-wide handler would fight the other
            // tabs and every text box in them.
            PreviewKeyDown += EditorTab_PreviewKeyDown;

            // Confirms the behavior attached at all: if no reorder line ever appears in the log,
            // this says whether the wiring ran or the drop is being lost before it reaches us.
            LogManager.GetLogger().Debug(
                $"[Editor] Row reorder behavior attached to the achievements grid. " +
                $"columns={CustomAchievementsGrid.Columns.Count}.");
        }

        /// <summary>
        /// Drops everything this tab hooked up in its constructor, so closing the window releases
        /// the tab and the rows behind it.
        /// </summary>
        /// <remarks>
        /// Explicit rather than Unloaded-driven: WPF does not guarantee Unloaded for a control
        /// whose window is closing, and the reorder options and collection-view filter below hold
        /// closures over this tab and its view model, which the grid's own teardown cannot reach.
        /// Called from <c>ManageAchievementsControl.CleanupEditor</c>.
        /// </remarks>
        public void Cleanup()
        {
            // First, while the grid, the view model and the settings object are all still alive:
            // disposing flushes the pending width writes, which needs all three.
            _columnPersistence?.Dispose();
            _columnPersistence = null;

            PreviewKeyDown -= EditorTab_PreviewKeyDown;
            AchievementNavigation_Unloaded(null, null);
            Loaded -= AchievementNavigation_Loaded;
            Unloaded -= AchievementNavigation_Unloaded;
            PreviewDragOver -= EditorTab_PreviewDragOver;
            PreviewDrop -= EditorTab_EndArtworkDrag;
            DragLeave -= EditorTab_EndArtworkDrag;

            if (CategoryPicker != null)
            {
                CategoryPicker.SelectionCommitted -= CategoryPicker_SelectionCommitted;
                CategoryPicker.CreateRequested -= CategoryPicker_CreateRequested;
            }

            var viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                viewModel.FilterChanged -= ViewModel_FilterChanged;
                viewModel.AssignmentsChanged -= ViewModel_AssignmentsChanged;
                viewModel.ScrollRowIntoViewRequested -= ViewModel_ScrollRowIntoViewRequested;
                viewModel.RestoreSelectionRequested -= ViewModel_RestoreSelectionRequested;
            }

            CustomAchievementsGrid.LoadingRow -= AchievementsGrid_LoadingRow;
            CustomAchievementsGrid.UnloadingRow -= AchievementsGrid_UnloadingRow;
            _realizedRows.Clear();

            // Both of these otherwise tear down on Unloaded alone, which is exactly what this
            // method exists because WPF does not guarantee. Each registers its hooks through
            // DependencyPropertyDescriptor.AddValueChanged, whose table is process-wide, so a
            // missed teardown roots the grid and every realized row for the life of the process.
            // Setting the attached property false routes through each behavior's own Detach,
            // which is guarded and safe to reach after an Unloaded that did fire.
            DataGridHoverScrollBarBehavior.SetIsEnabled(CustomAchievementsGrid, false);
            DataGridColumnGripperBehavior.SetIsEnabled(CustomAchievementsGrid, false);

            // Routes through OnOptionsChanged, which disposes the reorder state: its drag
            // subscriptions, its auto-scroll timer and the closures it holds over this tab.
            DataGridRowReorderBehavior.SetOptions(CustomAchievementsGrid, null);

            // WPF's view manager keeps the collection view for AchievementRows, and the predicate
            // captures this tab.
            DetachFilter();
        }

        private void ViewModel_AssignmentsChanged(object sender, EventArgs e)
        {
            SeedCategoryPicker();
        }

        private void ViewModel_ScrollRowIntoViewRequested(object sender, AchievementEditorRow row)
        {
            ScrollRowIntoView(row);
        }

        /// <summary>
        /// Posted at Background priority on purpose: the grid is still working through the
        /// collection reset and the SelectedRow push-back when this is raised, and reselecting
        /// inline would be undone by them.
        /// </summary>
        private void ViewModel_RestoreSelectionRequested(object sender, IReadOnlyList<string> apiNames)
        {
            Dispatcher.BeginInvoke(
                new Action(() => RestoreSelectionByApiNames(apiNames)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CategoryPicker_SelectionCommitted(object sender, EventArgs e)
        {
            ApplyCategoryFromPicker();
        }

        private void CategoryPicker_CreateRequested(object sender, EventArgs e)
        {
            PromptAndCreateCategory();
        }

        /// <summary>
        /// Reselects rows after a reorder rebuilds the collection, so a multi-row drag does not
        /// clear the user's selection.
        /// </summary>
        /// <summary>
        /// Points the grid's collection view at the view model's filter predicate, so narrowing the
        /// list never touches the rows behind it.
        /// </summary>
        /// <remarks>
        /// The filter lives on the view rather than on a second, filtered collection: every persist
        /// path in the view model walks <c>AchievementRows</c> as the game's complete, ordered list,
        /// and would write a truncated one if the filter removed rows from it.
        /// </remarks>
        private void AttachFilter()
        {
            var view = CollectionViewSource.GetDefaultView(ViewModel?.AchievementRows);
            if (view == null)
            {
                return;
            }

            view.Filter = candidate => ViewModel?.MatchesFilter(candidate as AchievementEditorRow) != false;
        }

        private void DetachFilter()
        {
            var view = CollectionViewSource.GetDefaultView(ViewModel?.AchievementRows);
            if (view != null)
            {
                view.Filter = null;
            }
        }

        private void ViewModel_FilterChanged(object sender, EventArgs e)
        {
            CollectionViewSource.GetDefaultView(ViewModel?.AchievementRows)?.Refresh();
        }

        private void ToggleDetailsPaneButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.IsDetailsPaneExpanded = !ViewModel.IsDetailsPaneExpanded;
            }
        }

        /// <summary>
        /// Opens a filter drop-down through the shared builder, the same one the grid control bar
        /// uses, so the editor's filters render and behave identically to every other grid's.
        /// </summary>
        private void MultiSelectFilter_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            MultiSelectFilterMenu.Open(button, button?.DataContext as GridMultiSelectFilter);
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            FilterTextBox.Clear();
            FilterTextBox.Focus();
        }

        /// <summary>
        /// Selects every achievement the grid is currently showing. With a filter active that is the
        /// matching subset, which is what makes a bulk edit over a search possible.
        /// </summary>
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            CustomAchievementsGrid.SelectAll();
            CustomAchievementsGrid.Focus();
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            CustomAchievementsGrid.UnselectAll();
        }

        private int _realizedRowCount;
        private System.Diagnostics.Stopwatch _rowRealizationWindow;
        private double _lastRowRealizationMs;

        // Longer than this between two realizations and the grid was idle, not realizing. The
        // window restarts rather than counting the gap. Comfortably above a single row's cost
        // and far below the pause between two scroll gestures.
        private const double RowRealizationIdleGapMs = 150;

        /// <summary>
        /// Counts realized rows and reports in batches. Per-row logging would cost more than the
        /// realization it measures, so this reports the elapsed time for a run of them: a burst
        /// far larger than a viewport means virtualization is not holding, and a slow one means
        /// the row template is the cost.
        /// </summary>
        /// <summary>
        /// The rows that currently have a container. Only these are bound to anything, so only
        /// these need telling when a refresh changes their values -- a row scrolled out of view
        /// reads its current state when the grid realizes it again.
        /// </summary>
        private readonly HashSet<AchievementEditorRow> _realizedRows =
            new HashSet<AchievementEditorRow>();

        private void AchievementsGrid_UnloadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e?.Row?.Item is AchievementEditorRow row)
            {
                _realizedRows.Remove(row);
            }
        }

        private void AchievementsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e?.Row?.Item is AchievementEditorRow realized)
            {
                _realizedRows.Add(realized);
            }

            if (!Common.PerfScope.PerfTracingEnabled)
            {
                return;
            }

            if (_rowRealizationWindow == null)
            {
                _rowRealizationWindow = System.Diagnostics.Stopwatch.StartNew();
                _lastRowRealizationMs = 0;
            }

            // The window must cover realization only. Timing from the first row of a burst to
            // whenever the threshold happens to trip charged every idle second between two
            // scrolls to the rows either side of it, and reported four-minute realizations of a
            // single row. Any gap wider than a realization ends the window: the pending count is
            // reported with the time it actually took, and the next burst starts clean.
            var now = _rowRealizationWindow.Elapsed.TotalMilliseconds;
            if (_realizedRowCount == 0)
            {
                // Nothing pending means the last batch was already reported, and everything since
                // was idle. The gap test below cannot catch this - it needs a pending row to
                // report - so the first row of a new window was charged the whole wait since the
                // previous report. That is what produced "realized=1 ms=5519" for one row, and it
                // inflated the leading edge of every batch that followed an idle pause.
                _rowRealizationWindow.Restart();
                now = 0;
            }
            else if (now - _lastRowRealizationMs > RowRealizationIdleGapMs)
            {
                ReportRowRealization(_realizedRowCount, _lastRowRealizationMs);
                _realizedRowCount = 0;
                _rowRealizationWindow.Restart();
                now = 0;
            }

            _lastRowRealizationMs = now;
            _realizedRowCount++;

            // Reports on whichever comes first: twenty rows, or a quarter second of realizing.
            // A fixed batch of fifty never reported at all on a game of seventy-seven rows,
            // because recycling means a scroll realizes far fewer containers than there are
            // rows. The elapsed bound also catches a slow trickle, which is the shape a costly
            // row template would produce.
            if (_realizedRowCount < 20 && now < 250)
            {
                return;
            }

            ReportRowRealization(_realizedRowCount, now);
            _realizedRowCount = 0;
            _rowRealizationWindow.Restart();
            _lastRowRealizationMs = 0;
        }

        private System.Windows.Controls.Panel _rowsPanel;

        /// <summary>
        /// What a scroll is accumulating, sampled on the same cadence as the realization report so
        /// a long scroll produces a trend rather than two endpoints. Answers the question the
        /// [MemPerf] lines cannot: those are logged only when the cache is invalidated or the
        /// window closes, so a session spent purely scrolling measures nothing at all, and "the
        /// heap was flat" read off the two samples either side of a scroll is not evidence.
        ///
        /// Deliberately cheap enough to sit in this path: GC.GetTotalMemory(false) never collects,
        /// and the panel is found once. Containers is the direct test of whether recycling is
        /// holding -- it should settle near a viewport's worth and stay there no matter how long
        /// the scrolling goes on. A number that climbs with distance scrolled is a leak; managed
        /// bytes climbing while containers hold flat is something retained per realization
        /// instead.
        /// </summary>
        private string DescribeScrollRetention()
        {
            var managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

            var containers = -1;
            if (_rowsPanel == null)
            {
                _rowsPanel = VisualTreeHelpers.FindVisualChild<System.Windows.Controls.VirtualizingStackPanel>(
                    CustomAchievementsGrid);
            }

            if (_rowsPanel != null)
            {
                containers = _rowsPanel.Children.Count;
            }

            var images = string.Empty;
            try
            {
                var imageService = PlayniteAchievementsPlugin.Instance?.ImageService;
                if (imageService != null)
                {
                    imageService.GetCacheStats(out var imageCount, out var imageBytes);
                    images = $" images={imageCount}/{imageBytes / (1024 * 1024)}MB";
                }
            }
            catch
            {
                // Diagnostics only; never fail a scroll over a stats read.
            }

            return $" managedMb={managedMb:F1} containers={containers}{images}";
        }

        private void ReportRowRealization(int realized, double elapsed)
        {
            var rows = ViewModel?.AchievementRows?.Count ?? 0;
            // The plugin's own logger, not LogManager.GetLogger(). That one writes to
            // playnite.log and its Debug output is not persisted, so the first two attempts at
            // this measurement produced no lines in either file and read as "the editor was
            // never opened" -- it had been.
            PlayniteAchievements.Services.Logging.PluginLogger.GetLogger(nameof(ManageAchievementsEditorTab)).Debug(
                $"[UiBlockRisk] tag=Editor.RowRealization ms={(long)elapsed} ui=true " +
                $"thread={System.Threading.Thread.CurrentThread.ManagedThreadId} " +
                $"context=realized={realized} rows={rows}{DescribeScrollRetention()}");
        }

        private void RestoreSelectionByApiNames(IReadOnlyList<string> apiNames)
        {
            if (apiNames == null || apiNames.Count == 0)
            {
                return;
            }

            var wanted = new HashSet<string>(apiNames, StringComparer.OrdinalIgnoreCase);
            var rows = CustomAchievementsGrid.Items.OfType<AchievementEditorRow>().ToList();
            var matches = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.OriginalApiName) &&
                              wanted.Contains(row.OriginalApiName))
                .ToList();

            using var scope = Common.PerfScope.Start(
                Services.Logging.PluginLogger.GetLogger(nameof(ManageAchievementsEditorTab)),
                "Editor.RestoreSelection",
                thresholdMs: 10,
                context: "rows=" + rows.Count + " selected=" + matches.Count);

            // Adding to SelectedItems one row at a time makes the DataGrid do its selection
            // bookkeeping per row, and restoring a selection of several hundred is what made
            // undo, redo and reset-all feel like they locked: the work lands after the reload,
            // at Background priority, so none of the reload's own scopes covered it.
            //
            // SelectAll goes through the Selector's own batched selection change instead, which
            // is one operation whatever the row count -- and "everything was selected" is
            // exactly the case those three produce.
            if (matches.Count == rows.Count && rows.Count > 0)
            {
                CustomAchievementsGrid.SelectAll();
                return;
            }

            CustomAchievementsGrid.SelectedItems.Clear();
            foreach (var row in matches)
            {
                CustomAchievementsGrid.SelectedItems.Add(row);
            }
        }

        private ManageAchievementsEditorViewModel ViewModel => DataContext as ManageAchievementsEditorViewModel;

        /// <summary>
        /// Hands the grid's selection to the view model so the details pane can edit several rows
        /// at once. WPF exposes SelectedItems only on the control, so it cannot be bound.
        /// </summary>
        private void AchievementsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel?.SetSelectedRows(CustomAchievementsGrid.SelectedItems.OfType<AchievementEditorRow>());
        }

        /// <summary>
        /// Selects the achievement under the pointer while an image is being dragged over the
        /// grid, so the drag can be carried on into one of that achievement's icon slots.
        /// </summary>
        /// <remarks>
        /// The grid as a whole is not a drop target for artwork -- the individual slots are, being
        /// the icon column's cells and the details pane's two boxes -- so this only moves the
        /// selection and leaves the event alone. Moving it is what puts the row a cell drop lands
        /// on in front of the pane as well. A row reorder carries the grid's own format and is
        /// left untouched.
        /// </remarks>
        private void EditorTab_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data == null || e.Data.GetDataPresent(DragDataFormat))
            {
                return;
            }

            if (!DragPayloadCarriesArtwork(e.Data))
            {
                return;
            }

            // Near the top or bottom edge the list keeps scrolling, so an achievement that is off
            // screen can still be reached without letting go. The arrow keys and the wheel are
            // not available here: during a drag the keyboard and wheel belong to the source
            // application's drag loop, and a drop target only ever sees the modifier keys.
            DataGridRowReorderBehavior.UpdateExternalDragAutoScroll(CustomAchievementsGrid);

            var row = FindRowUnderPointer(e.GetPosition(CustomAchievementsGrid));
            if (row == null || ReferenceEquals(row, CustomAchievementsGrid.SelectedItem))
            {
                return;
            }

            if (CustomAchievementsGrid.SelectedItems.Count > 1)
            {
                CustomAchievementsGrid.SelectedItems.Clear();
            }

            CustomAchievementsGrid.SelectedItem = row;
        }

        private void EditorTab_EndArtworkDrag(object sender, DragEventArgs e)
        {
            DataGridRowReorderBehavior.StopExternalDragAutoScroll(CustomAchievementsGrid);
        }

        private object _dragPayloadSource;

        private bool _dragPayloadCarriesArtwork;

        /// <summary>
        /// Whether the drag is carrying artwork, answered once per drag rather than per tick.
        /// </summary>
        /// <remarks>
        /// DragOver fires continuously while the pointer moves, and reading a browser drag means
        /// pulling its HTML fragment out of the data object and running a regex over it. The
        /// payload cannot change mid-drag, so the verdict is cached against the data object it
        /// was read from.
        /// </remarks>
        private bool DragPayloadCarriesArtwork(IDataObject data)
        {
            if (ReferenceEquals(data, _dragPayloadSource))
            {
                return _dragPayloadCarriesArtwork;
            }

            _dragPayloadSource = data;
            _dragPayloadCarriesArtwork = TryGetFirstImageFilePath(data, out _) ||
                                         TryGetFirstBrowserUrl(data, out _);
            return _dragPayloadCarriesArtwork;
        }

        /// <summary>
        /// The achievement whose row is under <paramref name="point"/>, in grid coordinates, or
        /// null when the pointer is off the rows.
        /// </summary>
        private AchievementEditorRow FindRowUnderPointer(Point point)
        {
            if (point.X < 0 || point.Y < 0 ||
                point.X > CustomAchievementsGrid.ActualWidth ||
                point.Y > CustomAchievementsGrid.ActualHeight)
            {
                return null;
            }

            var hit = System.Windows.Media.VisualTreeHelper.HitTest(CustomAchievementsGrid, point);
            var container = VisualTreeHelpers.FindVisualParent<DataGridRow>(hit?.VisualHit);
            return container?.Item as AchievementEditorRow;
        }

        private Window _achievementNavigationHost;

        private void AchievementNavigation_Loaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null || ReferenceEquals(window, _achievementNavigationHost))
            {
                return;
            }

            AchievementNavigation_Unloaded(null, null);
            _achievementNavigationHost = window;
            // Preview, so the step happens wherever focus is rather than only once the grid has
            // it. Anything that needs the arrows for itself is let through by ConsumesArrowKeys.
            _achievementNavigationHost.PreviewKeyDown += AchievementNavigationHost_PreviewKeyDown;
        }

        private void AchievementNavigation_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_achievementNavigationHost == null)
            {
                return;
            }

            // The window outlives this control, so the handler has to come off with it.
            _achievementNavigationHost.PreviewKeyDown -= AchievementNavigationHost_PreviewKeyDown;
            _achievementNavigationHost = null;
        }

        private void AchievementNavigationHost_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || (e.Key != Key.Up && e.Key != Key.Down))
            {
                return;
            }

            // The window hosts other tabs; only the one on screen owns the arrows.
            if (!IsVisible)
            {
                return;
            }

            if (ConsumesArrowKeys(Keyboard.FocusedElement as DependencyObject))
            {
                return;
            }

            // Handled either way once it is ours: at the first or last achievement the key does
            // nothing rather than falling through to a control that would move something else.
            MoveAchievementSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
        }

        /// <summary>
        /// Whether the focused control needs the arrow keys for itself: a drop-down picks its
        /// entry with them, and a multi-line box moves its caret. A single-line box does neither,
        /// so typing a name and stepping to the next achievement works without leaving the field.
        /// </summary>
        private static bool ConsumesArrowKeys(DependencyObject focused)
        {
            for (var node = focused; node != null; node = GetParent(node))
            {
                if (node is ComboBox || node is System.Windows.Controls.Primitives.Popup ||
                    node is ContextMenu || node is MenuItem || node is ListBoxItem)
                {
                    return true;
                }

                if (node is TextBox textBox && textBox.AcceptsReturn)
                {
                    return true;
                }
            }

            return false;
        }

        private static DependencyObject GetParent(DependencyObject node)
        {
            // Visual first, then logical: a focused element inside a popup has no visual parent
            // reaching back to the control that opened it.
            if (node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(node);
                if (visualParent != null)
                {
                    return visualParent;
                }
            }

            return LogicalTreeHelper.GetParent(node);
        }

        /// <summary>
        /// Steps the selection one achievement in the grid's own order, clamped at both ends so
        /// the list never wraps around.
        /// </summary>
        private void MoveAchievementSelection(int delta)
        {
            var view = CollectionViewSource.GetDefaultView(ViewModel?.AchievementRows);
            if (view == null)
            {
                return;
            }

            // The view, not the source collection: what the arrows walk is what is on screen,
            // in the order and with the filtering the grid is showing.
            var rows = view.Cast<AchievementEditorRow>().ToList();
            if (rows.Count == 0)
            {
                return;
            }

            var index = rows.IndexOf(CustomAchievementsGrid.SelectedItem as AchievementEditorRow);
            int next;
            if (index < 0)
            {
                next = delta > 0 ? 0 : rows.Count - 1;
            }
            else
            {
                next = index + delta;
                if (next < 0 || next >= rows.Count)
                {
                    return;
                }
            }

            // The grid takes an extended selection, so the step replaces it rather than adding to
            // it: this is moving through the list, not building a set. Cleared only when there is
            // really a multi-selection to collapse, because clearing drives the selection through
            // null on its way and the details pane follows it there.
            if (CustomAchievementsGrid.SelectedItems.Count > 1)
            {
                CustomAchievementsGrid.SelectedItems.Clear();
            }

            CustomAchievementsGrid.SelectedItem = rows[next];
            ScrollRowIntoView(rows[next]);
        }

        /// <summary>
        /// Brings a row the editor picked into view, after the grid has had a chance to realize it:
        /// a row added a moment ago has no container yet, and scrolling to one that does not exist
        /// does nothing.
        /// </summary>
        private void ScrollRowIntoView(AchievementEditorRow row)
        {
            if (row == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        CustomAchievementsGrid.ScrollIntoView(row);
                    }
                    catch (Exception)
                    {
                        // A row the filter is hiding has nowhere to scroll to.
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(ManageAchievementsEditorViewModel.SelectedRow) ||
                e?.PropertyName == nameof(ManageAchievementsEditorViewModel.EditTarget))
            {
                SeedCategoryPicker();
            }
        }

        /// <summary>
        /// Seeds the picker from whatever the pane is editing: the selected row, or the bulk proxy
        /// carrying the label the selection agrees on.
        /// </summary>
        private void SeedCategoryPicker()
        {
            var target = ViewModel?.EditTarget;
            // The effective label, so the picker opens showing the category the achievement is
            // actually in rather than only a category the user had overridden it to.
            var label = target?.EffectiveCategoryLabel;

            // The box's state is a function of exactly these two, and one selection change raises
            // EditTarget many times over - once for the grid's own reset, once for each row it
            // re-reports, and once more for the assignment notification.
            if (ReferenceEquals(target, _categoryPickerRow) &&
                string.Equals(label, _categoryPickerLabel, StringComparison.Ordinal))
            {
                return;
            }

            _categoryPickerRow = target;
            _categoryPickerLabel = label;
            CategoryPicker.SetInitialCategory(label);
        }

        /// <summary>
        /// Commits the picker to every selected achievement. The captured row is only the guard
        /// that editing had actually begun; the label lands on the selection, which is what the
        /// pane says it is editing.
        /// </summary>
        private void ApplyCategoryFromPicker()
        {
            var row = _categoryPickerRow;
            if (row == null || ViewModel == null || !row.CanEditAssignments)
            {
                return;
            }

            // The box is select-only, so it has no way to express "no category": an empty selection
            // means the list was rebuilt under it, and committing that would clear the assignment.
            // Clearing is the row menu's job.
            var picked = CategoryPicker.ResolveSelection();
            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            ViewModel.SetCategoryForSelection(picked);
        }

        /// <summary>
        /// Names and creates a category, filing the selection in it. Reached from the create row at
        /// the top of the picker and from the row menu, so both gestures share one set of rules.
        /// </summary>
        private void PromptAndCreateCategory()
        {
            if (ViewModel == null || !CategoryCreationPrompt.TryPrompt(out var leafName))
            {
                return;
            }

            var created = ViewModel.CreateAndAssignCategory(leafName);
            if (!string.IsNullOrWhiteSpace(created))
            {
                SeedCategoryPicker();
            }
        }

        /// <summary>
        /// Applies a filter scope picked in the details pane to every selected achievement.
        /// </summary>
        /// <remarks>
        /// The combo only reports what the user chose; re-seeding it as the selection changes
        /// raises this too, which the comparison against the edit target's current scope filters
        /// out. A proxy standing in for rows that disagree has no matching item at all, so the
        /// first real pick always reads as a change.
        /// </remarks>
        private void FilterScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(e?.AddedItems?.Count > 0) ||
                !(e.AddedItems[0] is AchievementFilterScopeOption option) ||
                ViewModel == null)
            {
                return;
            }

            var target = ViewModel.EditTarget;
            if (target == null || !target.CanEditAssignments || option.Value == target.FilterScope)
            {
                return;
            }

            ViewModel.SetFilterScopeForSelection(option.Value);
        }

        /// <summary>
        /// Opens the same note editor the Notes tab uses, so a note written here is written the
        /// same way and gets the markdown-capable editor rather than a bare cell.
        /// </summary>
        private void EditNoteButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                EditNote(row);
            }
        }

        private void EditNoteForSelectedRow()
        {
            var row = CustomAchievementsGrid.SelectedItems.OfType<AchievementEditorRow>().FirstOrDefault();
            if (row != null)
            {
                EditNote(row);
            }
        }

        private void EditNote(AchievementEditorRow row)
        {
            var dialog = new AchievementNoteDialog(
                row.DisplayName,
                row.OriginalApiName,
                row.AchievementNote,
                isReadOnly: false,
                achievementIconSource: row.DisplayIcon);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_NotesDialog_EditTitle"),
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 640,
                    Height = 560
                });

            WindowPlacementPersistenceService.Attach(window, "AchievementNoteEdit");
            dialog.RequestClose += (s, args) => window.Close();
            window.ShowDialog();

            if (dialog.DialogResult == true)
            {
                // Assigning the note raises the change that persists it, the same path a checkbox
                // or committed text box takes. Routed, so a note edited from a cell reaches the
                // whole selection the way the details pane's Edit button does; from the pane the
                // row already is the edit target, so this resolves to the same object.
                var target = EditorCellRouting.ResolveTarget(ViewModel, row) ?? row;
                target.AchievementNote = dialog.SavedNote;
            }
        }

        /// <summary>
        /// Text editors bind on focus loss so a half-typed value is not persisted; Enter commits
        /// the same way the other Manage tabs do.
        /// </summary>
        /// <remarks>
        /// Focus then goes back to the grid, as it does for a cell editor: with the caret left in
        /// the box nothing showed that the value had been taken, and a second Enter did nothing.
        /// </remarks>
        private void EditorTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CustomAchievementsGrid?.Focus();
            e.Handled = true;
        }

        /// <summary>
        /// Adds, replaces or drops the capstone for the edited achievement. The button says which,
        /// so a replacement is never silent.
        /// </summary>
        private void CapstoneActionButton_Click(object sender, RoutedEventArgs e)
        {
            var row = ViewModel?.EditTarget;
            if (row == null)
            {
                return;
            }

            ViewModel.SetCapstoneForSelection(!row.IsCapstone);
        }

        private void TypeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || TypeSelectionContextMenu == null || TypeSelectionButton == null)
            {
                return;
            }

            SelectorContextMenuHelper.OpenCategoryTypeMenu(
                TypeSelectionButton,
                TypeSelectionContextMenu,
                ViewModel.TypeSelectionOptions);
        }

        public void RefreshData()
        {
            ViewModel?.RefreshData();
        }

        private void ContextMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null)
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        /// <summary>
        /// Reveals a masked name by clicking it. The placeholder is only on screen while the name
        /// is masked, so the click has one meaning and does not need to re-mask; the toggle beside
        /// it is what puts the mask back.
        /// </summary>
        private void MaskedTitle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.RevealTitle();
                e.Handled = true;
            }
        }

        /// <summary>Reveals a masked description by clicking it.</summary>
        private void MaskedDescription_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.RevealDescription();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Reveals or re-masks one row's name. Separate from the description's toggle: each is
        /// spoiled on its own.
        /// </summary>
        private void ToggleTitleRevealButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.ToggleTitleReveal();
            }
        }

        /// <summary>Reveals or re-masks one row's description.</summary>
        private void ToggleDescriptionRevealButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.ToggleDescriptionReveal();
            }
        }

        /// <summary>Reveals a masked trophy grade by clicking it.</summary>
        private void MaskedTrophy_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.RevealTrophy();
                e.Handled = true;
            }
        }

        /// <summary>Reveals a masked point value by clicking it.</summary>
        private void MaskedPoints_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.RevealPoints();
                e.Handled = true;
            }
        }

        /// <summary>Reveals or re-masks one row's trophy grade.</summary>
        private void ToggleTrophyRevealButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.ToggleTrophyReveal();
            }
        }

        /// <summary>Reveals or re-masks one row's point value.</summary>
        private void TogglePointsRevealButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.TogglePointsReveal();
            }
        }

        private void IconImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is AchievementEditorRow row) || !row.CanReveal)
            {
                return;
            }

            row.AdvanceIconStage();
            e.Handled = true;
        }

        private void RarityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is MenuItem menuItem) ||
                !(menuItem.DataContext is CustomAchievementSelectionOption option))
            {
                return;
            }

            var menu = ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
            if ((menu?.PlacementTarget as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                // Routed, so a tier picked from a cell reaches the whole selection. From the
                // details pane the placement target's row already is the edit target.
                var target = EditorCellRouting.ResolveTarget(ViewModel, row) ?? row;
                target.RarityInput = option.DisplayName;
            }
        }

        private void BrowseIconButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SetIconPath(row, variant, dialog.FileName);
        }

        private void ClearIconButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            SetIconPath(row, variant, null);
            e.Handled = true;
        }

        private void IconDropTarget_PreviewDragOver(object sender, DragEventArgs e)
        {
            var hasDropPayload = TryGetFirstImageFilePath(e.Data, out _) || TryGetFirstBrowserUrl(e.Data, out _);
            e.Effects = hasDropPayload ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void IconDropTarget_Drop(object sender, DragEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            try
            {
                if (TryGetFirstImageFilePath(e.Data, out var imagePath))
                {
                    SetIconPath(row, variant, imagePath);
                    e.Handled = true;
                    return;
                }

                if (TryGetFirstBrowserUrl(e.Data, out var url))
                {
                    SetIconPath(row, variant, url);
                    e.Handled = true;
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            var candidate = BuildCandidateText(textBox, e?.Text);
            e.Handled = !IsValidNumericCandidate(candidate, textBox?.Tag as string);
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var textBox = sender as TextBox;
            var pastedText = e.DataObject?.GetData(typeof(string)) as string;
            if (!IsValidNumericCandidate(BuildCandidateText(textBox, pastedText), textBox?.Tag as string))
            {
                e.CancelCommand();
            }
        }

        /// <summary>
        /// A right-click that lands outside the current selection moves the selection to that row
        /// first, so the menu always acts on what the user sees highlighted.
        /// </summary>
        private void AchievementRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGridRow row))
            {
                return;
            }

            e.Handled = true;
            _pendingRightClickRow = row;
            if (!CustomAchievementsGrid.SelectedItems.Contains(row.Item))
            {
                CustomAchievementsGrid.SelectedItems.Clear();
                CustomAchievementsGrid.SelectedItem = row.Item;
            }
        }

        private void AchievementRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGridRow row))
            {
                return;
            }

            e.Handled = true;
            var targetRow = _pendingRightClickRow ?? row;
            _pendingRightClickRow = null;
            OpenContextMenuForRow(targetRow);
        }

        private bool OpenContextMenuForRow(DataGridRow row, bool useControllerPlacement = false)
        {
            if (!(row?.DataContext is AchievementEditorRow))
            {
                return false;
            }

            var menu = BuildRowContextMenu();
            if (menu == null || menu.Items.Count == 0)
            {
                return false;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            if (useControllerPlacement)
            {
                return FullscreenControllerNavigationService.OpenContextMenu(row, menu);
            }

            menu.PlacementTarget = row;
            menu.IsOpen = true;
            return true;
        }

        /// <summary>
        /// Builds the row menu from the current selection, so every entry applies to all selected
        /// achievements rather than to the row that was clicked.
        /// </summary>
        /// <remarks>
        /// A check mark means every selected row already carries that value; a mixed selection
        /// shows none, and picking the entry applies it to all of them.
        /// </remarks>
        private ContextMenu BuildRowContextMenu()
        {
            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return null;
            }

            var selection = CustomAchievementsGrid.SelectedItems.OfType<AchievementEditorRow>().ToList();
            if (selection.Count == 0)
            {
                return null;
            }

            var menu = new ContextMenu();

            // Capstone first, matching the Capstones tab's single-per-game rule: it is a property of
            // the game, not of a selection, so it is offered only for one row.
            if (viewModel.IsSingleCapstoneSelection(out var isCapstone))
            {
                var capstoneItem = new MenuItem
                {
                    Header = ResourceProvider.GetString("LOCPlayAch_Dynamic_Capstone"),
                    IsCheckable = true,
                    IsChecked = isCapstone
                };
                capstoneItem.Click += (_, __) => viewModel.SetCapstoneForSelection(capstoneItem.IsChecked);
                menu.Items.Add(capstoneItem);
            }

            var goalItem = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Editor_Goal"),
                IsCheckable = true,
                IsChecked = selection.All(row => row.IsGoal),
                IsEnabled = selection.Count == 1 && selection[0].CanEditGoal
            };
            goalItem.Click += (_, __) => viewModel.SetGoalForSelection(goalItem.IsChecked);
            menu.Items.Add(goalItem);

            var filterMenu = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_Menu_Filters"),
                IsEnabled = selection.All(row => row.CanEditAssignments)
            };
            AppendFilterScopeItems(filterMenu.Items, selection);
            menu.Items.Add(filterMenu);

            // Category and type, the same two the Category tab's row menu offers.
            var categoryMenu = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_Common_Label_Category"),
                IsEnabled = selection.All(row => row.CanEditAssignments)
            };

            AppendCategoryItems(categoryMenu.Items, selection);
            menu.Items.Add(categoryMenu);

            var typeMenu = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_Common_Label_Type"),
                IsEnabled = selection.All(row => row.CanEditAssignments)
            };
            AppendCategoryTypeItems(typeMenu.Items, selection);
            menu.Items.Add(typeMenu);

            menu.Items.Add(CreateMenuItem(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Notes_Note"),
                () => EditNoteForSelectedRow(),
                selection.Count == 1 && selection[0].CanEditAssignments));

            menu.Items.Add(new Separator());

            menu.Items.Add(CreateCommandMenuItem(
                ResourceProvider.GetString("LOCPlayAch_Common_Duplicate"),
                viewModel.DuplicateCommand));
            menu.Items.Add(CreateCommandMenuItem(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Editor_Revert"),
                viewModel.RevertCommand));
            menu.Items.Add(CreateCommandMenuItem(
                ResourceProvider.GetString("LOCPlayAch_Button_Delete"),
                viewModel.DeleteCommand));

            return menu;
        }

        /// <summary>
        /// A tick in a routed cell. Writes through the routed target, then pulls the binding back
        /// from the source so a write the row refused cannot leave the tick showing a state
        /// nothing actually holds.
        /// </summary>
        private void RoutedCheckBox_Click(object sender, RoutedEventArgs e)
        {
            var checkBox = sender as CheckBox;
            var row = EditorCellRouting.ResolveRow(sender);
            var field = EditorCellRouting.GetField(checkBox);
            if (row == null || string.IsNullOrEmpty(field))
            {
                return;
            }

            var target = EditorCellRouting.ResolveTarget(ViewModel, row);
            switch (field)
            {
                case nameof(AchievementEditorRow.HiddenState):
                    // The row's own value, not the tick's: the proxy's is blank for a selection
                    // that disagrees, and a blank is a display state rather than a value to apply.
                    target.HiddenState = !row.Hidden;
                    break;

                case nameof(AchievementEditorRow.UnlockedState):
                    target.UnlockedState = !row.Unlocked;
                    break;

                case nameof(AchievementEditorRow.HasUnlockTime):
                    target.HasUnlockTime = !row.HasUnlockTime;
                    break;

                default:
                    return;
            }

            checkBox?.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }

        /// <summary>
        /// Commits a routed cell's text box on focus loss, which is when every editable box in
        /// this tab commits.
        /// </summary>
        private void RoutedCellTextBox_Commit(object sender, RoutedEventArgs e)
        {
            CommitRoutedCellTextBox(sender as TextBox);
        }

        /// <summary>
        /// Enter commits a routed cell's text box, Shift+Enter starts a line, and Escape abandons
        /// the edit.
        /// </summary>
        /// <remarks>
        /// On the tunnelling event, which matters for the boxes that accept returns: a TextBox
        /// consumes Enter in its own class handler to insert the line break, and a class handler
        /// runs before an instance one, so a plain KeyDown handler never sees the key on those
        /// boxes at all.
        /// </remarks>
        private void RoutedCellTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!(sender is TextBox textBox))
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                // Shift+Enter is a new line, on the boxes that take one. Left unhandled so the
                // box inserts it itself, at the caret, rather than this guessing where it goes.
                if (textBox.AcceptsReturn &&
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    return;
                }

                CommitRoutedCellTextBox(textBox);

                // Focus back to the grid, so Enter reads as finishing with the field rather than
                // leaving the caret in it. It also puts the arrow keys back to stepping rows.
                CustomAchievementsGrid.Focus();

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                // One way, so re-reading the source is what puts the box back.
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                e.Handled = true;
            }
        }

        private void CommitRoutedCellTextBox(TextBox textBox)
        {
            var row = EditorCellRouting.ResolveRow(textBox);
            var field = EditorCellRouting.GetField(textBox);
            if (textBox == null || row == null || string.IsNullOrEmpty(field))
            {
                return;
            }

            var target = EditorCellRouting.ResolveTarget(ViewModel, row);
            var value = textBox.Text;
            switch (field)
            {
                case nameof(AchievementEditorRow.DisplayName):
                    target.DisplayName = value;
                    break;

                case nameof(AchievementEditorRow.Description):
                    target.Description = value;
                    break;

                case nameof(AchievementEditorRow.TimeText):
                    target.TimeText = value;
                    break;

                case nameof(AchievementEditorRow.RarityInput):
                    target.RarityInput = value;
                    break;

                case nameof(AchievementEditorRow.PointsText):
                    target.PointsText = value;
                    break;

                case nameof(AchievementEditorRow.ProgressNumText):
                    target.ProgressNumText = value;
                    break;

                case nameof(AchievementEditorRow.ProgressDenomText):
                    target.ProgressDenomText = value;
                    break;

                default:
                    return;
            }

            // The setters normalize and can refuse, so the box shows what was stored rather than
            // what was typed.
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }

        /// <summary>
        /// A date picked in a routed cell.
        /// </summary>
        /// <remarks>
        /// The guard compares against the cell's own row, not against the edit target. That is
        /// what makes it safe under container recycling: a recycled cell is re-bound to its new
        /// row and the one-way binding pushes that row's own date in, which compares equal and
        /// writes nothing. Comparing against the edit target would let such a refresh through and
        /// stamp one row's date across the whole selection.
        /// </remarks>
        private void UnlockDateCell_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            var picker = sender as DatePicker;
            var row = EditorCellRouting.ResolveRow(sender);
            if (picker == null || row == null || picker.SelectedDate == row.UnlockDate)
            {
                return;
            }

            EditorCellRouting.ResolveTarget(ViewModel, row).UnlockDate = picker.SelectedDate;
        }

        /// <summary>
        /// A time mode picked in a routed cell. Guarded against the cell's own row for the reason
        /// given on <see cref="UnlockDateCell_SelectedDateChanged"/>.
        /// </summary>
        private void TimeModeCell_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            var row = EditorCellRouting.ResolveRow(sender);
            var mode = combo?.SelectedItem as string;
            if (row == null ||
                string.IsNullOrEmpty(mode) ||
                string.Equals(mode, row.SelectedTimeModeText, StringComparison.Ordinal))
            {
                return;
            }

            EditorCellRouting.ResolveTarget(ViewModel, row).SelectedTimeModeText = mode;
        }

        /// <summary>
        /// The rows a cell's edit applies to, for building a menu that reflects all of them.
        /// </summary>
        private IReadOnlyList<AchievementEditorRow> ResolveCellMenuSelection(AchievementEditorRow row)
        {
            if (row == null)
            {
                return new List<AchievementEditorRow>();
            }

            if (ViewModel?.IsRowInSelection(row) == true)
            {
                var selection = CustomAchievementsGrid.SelectedItems
                    .OfType<AchievementEditorRow>()
                    .ToList();
                if (selection.Count > 0)
                {
                    return selection;
                }
            }

            return new List<AchievementEditorRow> { row };
        }

        /// <summary>
        /// Opens a menu built for this gesture and dropped when it closes, rather than one
        /// declared in the cell template: a template-declared menu would be realized per row and
        /// would outlive the container it was recycled from.
        /// </summary>
        private void OpenCellMenu(object sender, Action<ItemCollection, IReadOnlyList<AchievementEditorRow>> append)
        {
            var button = sender as Button;
            var row = EditorCellRouting.ResolveRow(sender);
            if (button == null || row == null || append == null)
            {
                return;
            }

            var menu = new ContextMenu();
            append(menu.Items, ResolveCellMenuSelection(row));
            if (menu.Items.Count == 0)
            {
                return;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            SelectorContextMenuHelper.Open(button, menu);
        }

        private void GoalCell_Click(object sender, RoutedEventArgs e)
        {
            var row = EditorCellRouting.ResolveRow(sender);
            if (row == null)
            {
                return;
            }

            // The selection setter, so this is the row menu's Goal item by another route and gets
            // its single write rather than one per row.
            ViewModel?.SetGoalForSelection(!row.IsGoal);
            (sender as CheckBox)?.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }

        /// <summary>
        /// The capstone action for a cell. Always takes its direction from the clicked row, so the
        /// button does what its own label says; the pane's handler reads the edit target instead,
        /// whose value is a shared one that can disagree with this row.
        /// </summary>
        private void CapstoneCell_Click(object sender, RoutedEventArgs e)
        {
            var row = EditorCellRouting.ResolveRow(sender);
            if (row == null || ViewModel == null)
            {
                return;
            }

            ViewModel.SetCapstoneForSelection(!row.IsCapstone);
        }

        private void TrophyCell_Click(object sender, RoutedEventArgs e)
        {
            OpenCellMenu(sender, AppendTrophyTypeItems);
        }

        private void FilterCell_Click(object sender, RoutedEventArgs e)
        {
            OpenCellMenu(sender, AppendFilterScopeItems);
        }

        private void CategoryCell_Click(object sender, RoutedEventArgs e)
        {
            OpenCellMenu(sender, AppendCategoryItems);
        }

        private void TypeCell_Click(object sender, RoutedEventArgs e)
        {
            OpenCellMenu(sender, AppendCategoryTypeItems);
        }

        private void NoteCell_Click(object sender, RoutedEventArgs e)
        {
            var row = EditorCellRouting.ResolveRow(sender);
            if (row != null)
            {
                // Seeded from the clicked row - its name, art and note - because that is the one
                // the user pointed at; the save is routed to the selection inside EditNote.
                EditNote(row);
            }
        }

        private void RarityMenuButton_Click(object sender, RoutedEventArgs e)
        {
            OpenCellMenu(sender, AppendRarityTierItems);
        }

        /// <summary>
        /// The rarity tiers, as the details pane's chevron menu offers them.
        /// </summary>
        private void AppendRarityTierItems(ItemCollection items, IReadOnlyList<AchievementEditorRow> selection)
        {
            var viewModel = ViewModel;
            if (items == null || viewModel == null || selection == null || selection.Count == 0)
            {
                return;
            }

            foreach (var option in ManageAchievementsEditorViewModel.RarityOptions)
            {
                var captured = option;
                var item = new MenuItem
                {
                    Header = captured.DisplayName,
                    IsCheckable = true,
                    IsChecked = selection.All(row =>
                        string.Equals(row.Rarity, captured.Value, StringComparison.OrdinalIgnoreCase)),
                    IsEnabled = selection.All(row => row.CanEditRarity)
                };
                item.Click += (_, __) =>
                {
                    var target = EditorCellRouting.ResolveTarget(viewModel, selection[0]);
                    target.RarityInput = captured.DisplayName;
                };
                items.Add(item);
            }
        }

        /// <summary>
        /// The trophy types. A menu rather than a per-cell ComboBox: a recycled selector
        /// re-resolves its selection when its container is re-bound and can push that value at the
        /// row, which for a routed cell would write the whole selection with no user gesture.
        /// </summary>
        private void AppendTrophyTypeItems(ItemCollection items, IReadOnlyList<AchievementEditorRow> selection)
        {
            var viewModel = ViewModel;
            if (items == null || viewModel == null || selection == null || selection.Count == 0)
            {
                return;
            }

            foreach (var option in viewModel.TrophyTypeOptions)
            {
                var captured = option;
                var item = new MenuItem
                {
                    Header = captured.DisplayName,
                    Icon = CreateTrophyBadge(captured.Value),
                    IsCheckable = true,
                    IsChecked = selection.All(row =>
                        string.Equals(row.TrophyType, captured.Value, StringComparison.OrdinalIgnoreCase))
                };
                item.Click += (_, __) =>
                {
                    var target = EditorCellRouting.ResolveTarget(viewModel, selection[0]);
                    target.TrophyType = captured.Value;
                };
                items.Add(item);
            }
        }

        /// <summary>
        /// The badge for a trophy type, so the menu reads the way the cell and the details pane's
        /// picker do. Null for the None option, which has no badge.
        /// </summary>
        private static Image CreateTrophyBadge(string trophyType)
        {
            string resourceKey;
            switch ((trophyType ?? string.Empty).ToLowerInvariant())
            {
                case "platinum":
                    resourceKey = "TrophyPlatinum";
                    break;
                case "gold":
                    resourceKey = "TrophyGold";
                    break;
                case "silver":
                    resourceKey = "TrophySilver";
                    break;
                case "bronze":
                    resourceKey = "TrophyBronze";
                    break;
                default:
                    return null;
            }

            var image = new Image { Width = 16, Height = 16 };

            // By reference, not resolved now: the badges come from the theme, so they have to
            // follow a theme change like every other themed brush and image here.
            image.SetResourceReference(Image.SourceProperty, resourceKey);
            return image;
        }

        /// <summary>
        /// The filter-scope choices for a selection. Shared by the row menu and the Filter
        /// column's cell menu so both offer the same scopes and apply them the same way.
        /// </summary>
        private void AppendFilterScopeItems(ItemCollection items, IReadOnlyList<AchievementEditorRow> selection)
        {
            var viewModel = ViewModel;
            if (items == null || viewModel == null || selection == null)
            {
                return;
            }

            foreach (var option in viewModel.FilterScopeOptions)
            {
                var scope = option.Value;
                var scopeItem = new MenuItem
                {
                    Header = option.DisplayName,
                    IsCheckable = true,
                    IsChecked = selection.All(row => row.FilterScope == scope)
                };
                scopeItem.Click += (_, __) => viewModel.SetFilterScopeForSelection(scope);
                items.Add(scopeItem);
            }
        }

        /// <summary>
        /// The category choices for a selection, with creating one above the list and clearing
        /// below it. Shared by the row menu and the Category column's cell menu.
        /// </summary>
        private void AppendCategoryItems(ItemCollection items, IReadOnlyList<AchievementEditorRow> selection)
        {
            var viewModel = ViewModel;
            if (items == null || viewModel == null || selection == null)
            {
                return;
            }

            // Creating one sits above the categories that exist, the same place the picker offers
            // it, so the gesture is in reach without going to the Categories tab.
            items.Add(CreateMenuItem(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Category_NewCategoryEllipsis"),
                PromptAndCreateCategory));
            items.Add(new Separator());

            foreach (var option in viewModel.AssignableCategoryPickerOptions.Where(option => option.IsSelectable))
            {
                var label = option.Label;
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var categoryItem = new MenuItem
                {
                    Header = option.LeafDisplay,
                    ToolTip = option.PathDisplay,
                    IsCheckable = true,
                    IsChecked = selection.All(row =>
                        string.Equals(row.EffectiveCategoryLabel, label, StringComparison.OrdinalIgnoreCase))
                };
                categoryItem.Click += (_, __) => viewModel.SetCategoryForSelection(label);
                items.Add(categoryItem);
            }

            if (!(items[items.Count - 1] is Separator))
            {
                items.Add(new Separator());
            }

            items.Add(CreateMenuItem(
                ResourceProvider.GetString("LOCPlayAch_Button_Clear"),
                () => viewModel.SetCategoryForSelection(null)));
        }

        /// <summary>
        /// The category-type ticks for a selection. Shared by the row menu and the Type column's
        /// cell menu.
        /// </summary>
        private void AppendCategoryTypeItems(ItemCollection items, IReadOnlyList<AchievementEditorRow> selection)
        {
            var viewModel = ViewModel;
            if (items == null || viewModel == null || selection == null)
            {
                return;
            }

            // Kept so a click can read every tick, not just its own: the menu stays open, and the
            // set the user leaves it in is what the whole selection takes.
            var typeItems = new List<MenuItem>();
            foreach (var categoryType in AchievementCategoryTypeHelper.AssignableCategoryTypes)
            {
                var captured = categoryType;
                var typeItem = new MenuItem
                {
                    Header = ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName(captured),
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    Tag = captured,
                    // The effective type, like the Category item above and the ticks in the details
                    // pane: reading the override alone left a provider-typed row showing the type
                    // unticked here and ticked there.
                    IsChecked = selection.All(row =>
                        AchievementCategoryTypeHelper.ParseValues(row.EffectiveCategoryTypeValue)
                            .Any(value => string.Equals(value, captured, StringComparison.OrdinalIgnoreCase)))
                };
                typeItem.Click += (_, __) => viewModel.SetCategoryTypesForSelection(
                    typeItems.Where(item => item.IsChecked).Select(item => item.Tag as string));
                typeItems.Add(typeItem);
                items.Add(typeItem);
            }
        }

        private static MenuItem CreateMenuItem(string header, Action onClick, bool isEnabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = isEnabled };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        // Command is not bound: the menu is rebuilt per right-click, so the enabled state is read
        // once here rather than tracked.
        private static MenuItem CreateCommandMenuItem(string header, Common.RelayCommand command)
        {
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = command?.CanExecute(null) == true
            };
            item.Click += (_, __) => command?.Execute(null);
            return item;
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (CustomAchievementsGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(CustomAchievementsGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(CustomAchievementsGrid);
                }

                return false;
            }

            // The same row menu the mouse opens, so a controller is not left without the actions.
            if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return TryOpenSelectedRowContextMenu();
            }

            return false;
        }

        private bool TryOpenSelectedRowContextMenu()
        {
            var item = CustomAchievementsGrid?.SelectedItem ?? CustomAchievementsGrid?.CurrentItem;
            if (item == null)
            {
                return false;
            }

            var row = CustomAchievementsGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            return row != null && OpenContextMenuForRow(row, useControllerPlacement: true);
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>
            {
                // Header provider row first (top-left); the IsVisible filter below drops it for
                // games that have real provider data.
                CustomProviderComboBox,
                AddCustomProviderButton,
                EditCustomProviderButton,
                AddButton,
                DuplicateButton,
                DeleteButton,
                ImportFileButton,
                ExportButton,
                ResetButton,
                CustomAchievementsGrid,
                AddRowFooterButton,
                CapstoneActionButton,
                CategoryPicker,
                TypeSelectionButton
            };

            return elements
                .Where(element => element != null && element.IsVisible && element.IsEnabled)
                .ToList();
        }

        private static bool TryResolveRowAndVariant(
            FrameworkElement element,
            out AchievementEditorRow row,
            out AchievementIconVariant variant)
        {
            row = element?.DataContext as AchievementEditorRow;
            variant = AchievementIconVariant.Unlocked;
            if (row == null)
            {
                return false;
            }

            var variantToken = (element as ButtonBase)?.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(variantToken))
            {
                variantToken = element?.Tag as string;
            }

            if (string.Equals((variantToken ?? string.Empty).Trim(), "Locked", StringComparison.OrdinalIgnoreCase))
            {
                variant = AchievementIconVariant.Locked;
            }

            return true;
        }

        private static void SetIconPath(
            AchievementEditorRow row,
            AchievementIconVariant variant,
            string value)
        {
            if (row == null)
            {
                return;
            }

            if (variant == AchievementIconVariant.Locked)
            {
                row.LockedIconPath = value;
            }
            else
            {
                row.UnlockedIconPath = value;
            }
        }

        private static string BuildCandidateText(TextBox textBox, string input)
        {
            var current = textBox?.Text ?? string.Empty;
            input ??= string.Empty;
            var start = Math.Max(0, textBox?.SelectionStart ?? current.Length);
            var length = Math.Max(0, textBox?.SelectionLength ?? 0);
            if (start > current.Length)
            {
                start = current.Length;
            }

            if (start + length > current.Length)
            {
                length = current.Length - start;
            }

            return current.Remove(start, length).Insert(start, input);
        }

        private static bool IsValidNumericCandidate(string value, string mode)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (string.Equals(mode, "Percent", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.Count(c => c == '.') > 1 ||
                    !double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    return false;
                }

                return percent >= 0 && percent <= 100;
            }

            return normalized.All(char.IsDigit);
        }

        private static bool TryGetFirstImageFilePath(IDataObject data, out string imagePath)
        {
            imagePath = null;
            if (data == null)
            {
                return false;
            }

            try
            {
                if (!data.GetDataPresent(DataFormats.FileDrop))
                {
                    return false;
                }

                var files = data.GetData(DataFormats.FileDrop) as string[];
                imagePath = files?.FirstOrDefault(IsSupportedImageFile);
                return !string.IsNullOrWhiteSpace(imagePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetFirstBrowserUrl(IDataObject data, out string url)
        {
            url = null;
            if (data == null)
            {
                return false;
            }

            try
            {
                var text = ReadDroppedText(data, DataFormats.UnicodeText) ??
                           ReadDroppedText(data, DataFormats.Text) ??
                           ReadDroppedText(data, DataFormats.Html);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                var match = HttpUrlRegex.Match(text);
                if (!match.Success)
                {
                    return false;
                }

                url = TrimTrailingUrlPunctuation(match.Value);
                return !string.IsNullOrWhiteSpace(url);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadDroppedText(IDataObject data, string format)
        {
            if (data == null || string.IsNullOrWhiteSpace(format))
            {
                return null;
            }

            try
            {
                if (!data.GetDataPresent(format))
                {
                    return null;
                }

                return data.GetData(format) as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSupportedImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path) ?? string.Empty;
            if (!SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string TrimTrailingUrlPunctuation(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('.', ',', ';', ')', ']', '}');
        }

        /// <summary>
        /// Undo and redo for the editor.
        /// </summary>
        /// <remarks>
        /// A focused text box keeps its own Ctrl+Z, and that is the correct split rather than a
        /// compromise: every editable box in this tab commits on losing focus, so while one has
        /// focus nothing has reached the store and there is nothing for the editor's history to
        /// reverse. WPF's in-box undo is exactly what the user wants at that moment.
        ///
        /// The window's arrow-key handler is unaffected: it returns unless the key is Up or Down
        /// and never marks Ctrl+Z handled, so the two do not interact whatever order they run in.
        /// </remarks>
        private void EditorTab_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (Keyboard.FocusedElement is TextBoxBase)
            {
                return;
            }

            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Z:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    {
                        Execute(viewModel.RedoCommand);
                    }
                    else
                    {
                        Execute(viewModel.UndoCommand);
                    }

                    break;

                case Key.Y:
                    Execute(viewModel.RedoCommand);
                    break;

                default:
                    return;
            }

            e.Handled = true;
        }

        /// <summary>
        /// Runs a command only when it is currently allowed, so a shortcut cannot reach past the
        /// guard its button respects.
        /// </summary>
        private static void Execute(System.Windows.Input.ICommand command)
        {
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }

        /// <summary>
        /// Settles the selection before a cell's own control swallows the click.
        /// </summary>
        /// <remarks>
        /// A DataGridCell selects its row from the bubbling mouse-down, and a CheckBox, Button or
        /// TextBox inside the cell marks that event handled, so pressing a control in an unselected
        /// row leaves the selection where it was. For a column whose edits route to the selection
        /// that is actively wrong: the click would write the previously selected rows and leave the
        /// row under the pointer alone.
        ///
        /// Runs on the grid's tunnelling press, ahead of the control, and deliberately does not
        /// mark the event handled so the control still receives its click. A Ctrl or Shift press is
        /// a selection gesture and belongs to the grid; a row already in the selection is left
        /// alone so editing one cell of a multi-row selection still applies to all of it.
        /// </remarks>
        private void NormalizeSelectionForRoutedCell(object item, MouseButtonEventArgs e)
        {
            if (!(item is AchievementEditorRow) ||
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
                !EditorCellRouting.IsRoutedCellHit(e?.OriginalSource as DependencyObject) ||
                CustomAchievementsGrid.SelectedItems.Contains(item))
            {
                return;
            }

            // Synchronous, so the view model's selection - and therefore the edit target the
            // control is about to commit through - is correct by the time its handler runs.
            CustomAchievementsGrid.SelectedItems.Clear();
            CustomAchievementsGrid.SelectedItem = item;
        }

        /// <summary>
        /// Gives the achievements grid the same persisted column layout the render grids have:
        /// show/hide from the header menu, drag to reorder, drag to resize, all remembered.
        /// </summary>
        private void AttachColumnPersistence()
        {
            _columnPersistence = new DataGridColumnLayoutService(
                CustomAchievementsGrid,
                LogManager.GetLogger(),
                getWidths: () => GetColumnLayout()?.Widths,
                setWidths: map =>
                {
                    var columns = GetColumnLayout();
                    if (columns != null)
                    {
                        columns.Widths = map;
                    }
                },
                getVisibility: GetColumnVisibility,
                setVisibility: map =>
                {
                    var columns = GetColumnLayout();
                    if (columns != null)
                    {
                        columns.Visibility = map;
                    }
                },
                saveSettings: SaveColumnSettings,
                defaultWidthSeeds: DefaultColumnWidthSeeds,
                getOrder: () => GetColumnLayout()?.Order,
                setOrder: map =>
                {
                    var columns = GetColumnLayout();
                    if (columns != null)
                    {
                        columns.Order = map;
                    }
                },
                getLocks: () => GetColumnLayout()?.Locked,
                setLocks: map =>
                {
                    var columns = GetColumnLayout();
                    if (columns != null)
                    {
                        columns.Locked = map;
                    }
                });

            _columnPersistence.PinnedLeadingKeys = PinnedColumnKeys;
            foreach (var key in PinnedColumnKeys)
            {
                _columnPersistence.ExcludedVisibilityKeys.Add(key);
            }

            _columnPersistence.Attach();
        }

        private GridColumnLayoutOptions GetColumnLayout()
        {
            return ViewModel?.Settings?.Persisted?.GridOptions
                ?.GetManageAchievements(GridOptionKeys.ManageAchievements.Editor)
                ?.Columns;
        }

        /// <summary>
        /// The persisted visibility map with any column it says nothing about filled in from the
        /// defaults, so a column added in a later version starts where it should.
        /// </summary>
        private Dictionary<string, bool> GetColumnVisibility()
        {
            var columns = GetColumnLayout();
            if (columns == null)
            {
                return null;
            }

            var map = columns.Visibility;
            if (map == null)
            {
                map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                columns.Visibility = map;
                map = columns.Visibility;
            }

            foreach (var pair in DefaultColumnVisibility)
            {
                if (!map.ContainsKey(pair.Key))
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return map;
        }

        private void SaveColumnSettings()
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            var settings = ViewModel?.Settings;
            if (plugin == null || settings == null)
            {
                return;
            }

            try
            {
                plugin.SavePluginSettings(settings);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "Failed to persist the achievement editor column settings.");
            }
        }

        /// <summary>
        /// Opens the column show/hide menu for a right-clicked header.
        /// </summary>
        /// <remarks>
        /// Tunnelling, so it runs before the row's own right-button handlers. Anything that is not
        /// a header is left entirely alone - unhandled and with nothing opened - because those
        /// handlers own the row menu.
        /// </remarks>
        private void CustomAchievementsGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var header = VisualTreeHelpers.FindVisualParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header?.Column == null)
            {
                return;
            }

            e.Handled = true;

            var menu = _columnPersistence?.BuildColumnVisibilityMenu(header.Column);
            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(header, menu);

            // Anchored to the grid rather than the header: the menu stays open across toggles, and
            // the header it was opened from is gone the moment its own column is unticked.
            _columnPersistence.PlaceColumnVisibilityMenu(menu, header);
            menu.IsOpen = true;
        }
    }
}
