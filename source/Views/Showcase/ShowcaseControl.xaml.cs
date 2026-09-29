using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Controls;
using static PlayniteAchievements.Services.Showcase.ShowcaseGeometry;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    public partial class ShowcaseControl : UserControl, IDisposable
    {
        private const string WidgetDragFormat = "PlayniteAchievements.Showcase.Widget";
        private static readonly ILogger Logger =
            Services.Logging.PluginLogger.GetLogger(nameof(ShowcaseControl));
        private readonly OverviewViewModel _overview;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action _persist;
        private readonly IPlayniteAPI _api;
        private bool _updatingPageSelector;
        private bool _publishingConfigurationChange;
        private bool _disposed;
        private string _layoutSignature;
        private string _builtPageId;
        private Point _dragStart;
        private string _selectedBlockId;
        private string _dragSourceBlockId;

        // Undo history for structural layout edits. Snapshots are whole-layout clones taken at
        // each settle point rather than per-operation deltas: every mutation already funnels
        // through SaveAndPublish, so one hook there covers add, delete, paste, move, swap,
        // split, merge, track resizes and the page operations without each site opting in.
        // Session-scoped and edit-mode only; the layout is persisted as it changes, so undo
        // rewinds saved state rather than uncommitted state.
        private const int MaxHistoryDepth = 50;
        private readonly LinkedList<ShowcaseSettings> _undoHistory = new LinkedList<ShowcaseSettings>();
        private readonly Stack<ShowcaseSettings> _redoHistory = new Stack<ShowcaseSettings>();
        private ShowcaseSettings _historyBaseline;
        private bool _restoringHistory;

        // Static so a widget copied on one page is pastable on another, and after the overview
        // window is closed and reopened. A cut holds the live instance id instead of a clone so
        // that cut-then-paste is a move: the widget keeps its identity, and with it the
        // per-instance grid surface holding its columns and sort.
        private static ShowcaseWidgetInstanceSettings _clipboardWidget;
        private static string _cutInstanceId;
        private readonly Dictionary<string, BlockVisualState> _blockVisuals =
            new Dictionary<string, BlockVisualState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<System.Windows.Controls.Primitives.Thumb> _trackGrippers =
            new List<System.Windows.Controls.Primitives.Thumb>();

        // Tactile layout affordances for the selected block: dashed cut lines on its interior
        // boundaries and merge chevrons on its legal shared edges, plus the drag ghost line.
        private readonly List<FrameworkElement> _layoutHandles = new List<FrameworkElement>();
        private readonly List<string> _mergePreviewBlockIds = new List<string>();
        private FrameworkElement _cutGhost;

        // Hover previews of a cut or merge's resulting blocks: outlines only, created on hover
        // and removed on leave, so they cost a couple of elements and never re-render a widget.
        private readonly List<FrameworkElement> _layoutPreviewGhosts = new List<FrameworkElement>();
        private readonly List<(Border Chrome, Visibility Previous)> _layoutPreviewHiddenChrome =
            new List<(Border Chrome, Visibility Previous)>();

        // The selected empty block's in-block + button, hidden while an overlay copy takes
        // hit-test priority over the cut lines; restored on the next handle rebuild.
        private Button _suppressedAddButton;
        private int _cutCandidate;
        private double _cutPixels;

        // Built widget controls keyed by widget instance id, kept alive across dashboard rebuilds
        // and page switches. Every layout edit (split, merge, page add/delete/rename, widget
        // settings) otherwise re-inflates each widget body, and the data grids and charts inside
        // them are expensive to build. Bounded by the number of configured widgets and pruned
        // whenever the widget instances change.
        private readonly Dictionary<string, ShowcaseWidgetControl> _hostCache =
            new Dictionary<string, ShowcaseWidgetControl>(StringComparer.OrdinalIgnoreCase);

        // Throttles SnapshotChanged-driven re-projections: the overview's delta pipeline
        // raises the event every few hundred milliseconds during a refresh, and each
        // re-projection walks the full snapshot per widget. The first event of a burst
        // refreshes promptly; the rest ride the window and land as one trailing refresh.
        private readonly System.Windows.Threading.DispatcherTimer _snapshotRefreshTimer;
        private bool _snapshotRefreshPending;

        // Widget projections are applied one per Background dispatcher pass rather than all in
        // one operation. A body apply is cheap by itself, but the layout pass it triggers
        // inflates the widget's template, realizes grid rows and mosaic tiles, and plots its
        // charts; nine of those in one operation held the UI thread for over a second on open.
        // Draining one widget per pass lets input, rendering, and the next widget interleave.
        // The queue is FIFO in enqueue order (visible blocks in reading order first), dedupes by
        // host, and is emptied by a dashboard rebuild or dispose, which is the only generation
        // tracking needed while this control is the sole producer.
        private sealed class WidgetApplyRequest
        {
            public ShowcaseWidgetControl Host;

            public ShowcaseWidgetInstanceSettings Widget;

            // Set for a request raised for a visible block; null for a cached off-page host.
            public string BlockId;

            // True when only the snapshot moved (data refresh), false when the widget's own
            // configuration changed and a re-projection is required regardless of the snapshot.
            public bool SnapshotOnly;
        }

        private readonly Queue<WidgetApplyRequest> _applyQueue = new Queue<WidgetApplyRequest>();
        private readonly HashSet<ShowcaseWidgetControl> _applyQueued = new HashSet<ShowcaseWidgetControl>();
        private bool _applyDrainScheduled;
        private int _applyDrainApplied;
        private long _applyDrainStartedTicks;
        private long _applyPassEndedTicks;

        // Edit-mode size labels: each column's width along the top edge, each row's height along
        // the left. Refreshed at most every TrackRulerInterval while the window resizes or a
        // gripper drags, so they track live without re-reading layout on every mouse move.
        private static readonly TimeSpan TrackRulerInterval = TimeSpan.FromMilliseconds(30);
        private readonly System.Windows.Threading.DispatcherTimer _trackRulerTimer;
        private readonly List<FrameworkElement> _trackRulers = new List<FrameworkElement>();
        private readonly List<TextBox> _columnRulerTexts = new List<TextBox>();
        private readonly List<TextBox> _rowRulerTexts = new List<TextBox>();

        internal ShowcaseControl(
            OverviewViewModel overview,
            PlayniteAchievementsSettings settings,
            Action persist,
            IPlayniteAPI api)
        {
            using var perf = PerfScope.Start(Logger, "Showcase.Ctor", thresholdMs: 30);
            InitializeComponent();
            _overview = overview ?? throw new ArgumentNullException(nameof(overview));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _persist = persist ?? throw new ArgumentNullException(nameof(persist));
            _api = api;
            // Widgets' control bar choices are saved in their own options across restarts.
            PlayniteAchievements.ViewModels.Showcase.Widgets.ShowcaseControlBarStates.Store =
                ShowcaseControlBarStateStore.Instance;
            _snapshotRefreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _snapshotRefreshTimer.Tick += SnapshotRefreshTimer_Tick;
            // Render priority: the tick reads the tracks' ActualWidth/ActualHeight, which the
            // layout pass after a resize or drag has settled by then.
            _trackRulerTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TrackRulerInterval
            };
            _trackRulerTimer.Tick += TrackRulerTimer_Tick;
            DashboardGrid.SizeChanged += (_, __) => ScheduleTrackRulerUpdate();
            _overview.SnapshotChanged += Overview_SnapshotChanged;
            ShowcaseConfigurationEvents.Changed += ShowcaseConfigurationEvents_Changed;
            // Rolling windows (the Timeline, Scores, Activity Calendar and played-within filters)
            // end at today; re-project after local midnight so they move with the calendar.
            Common.LocalDayRollover.Subscribe(LocalDayRollover_DayChanged);
            EnsureLayout();
            Rebuild();
        }

        private void LocalDayRollover_DayChanged(object sender, DateTime today)
        {
            QueueSnapshotRefresh();
        }

        /// <summary>
        /// Removes the page/edit control bar from this control's layout and returns it so the
        /// host can place it elsewhere (the overview window hosts it in its header's top-right
        /// slot). The named controls keep working wherever the bar lives.
        /// </summary>
        internal FrameworkElement DetachHeaderBar()
        {
            if (HeaderBar.Parent is Panel parent)
            {
                parent.Children.Remove(HeaderBar);
                HeaderBar.Margin = new Thickness(0);
            }

            return HeaderBar;
        }

        public void FocusInitialTarget()
        {
            PageSelector?.Focus();
        }

        public bool MovePage(int direction)
        {
            var pages = Layout.Pages;
            if (pages.Count <= 1)
            {
                return false;
            }

            var index = Math.Max(0, pages.IndexOf(CurrentPage));
            var target = (index + Math.Sign(direction) + pages.Count) % pages.Count;
            SelectPage(pages[target]);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            using var perf = PerfScope.Start(Logger, "Showcase.Dispose", thresholdMs: 20);
            ClearDragVisuals();
            _disposed = true;
            ShowcaseControlBarStateStore.Instance.Flush();
            _snapshotRefreshTimer.Stop();
            _trackRulerTimer.Stop();
            ClearApplyQueue();
            // The cached widget bodies hold PersistedSettings-subscribed grids and slideshow
            // timers that only release in Dispose; drop them explicitly rather than relying
            // on Unloaded, which WPF does not guarantee.
            foreach (var host in _hostCache.Values)
            {
                host?.DisposeBody();
            }

            _hostCache.Clear();
            _overview.SnapshotChanged -= Overview_SnapshotChanged;
            ShowcaseConfigurationEvents.Changed -= ShowcaseConfigurationEvents_Changed;
            Common.LocalDayRollover.Unsubscribe(LocalDayRollover_DayChanged);
        }

        private ShowcaseSettings Layout => _settings.Persisted.Showcase;

        private ShowcasePageSettings CurrentPage =>
            Layout.Pages.FirstOrDefault(page =>
                string.Equals(
                    page.PageId,
                    Layout.LastSelectedPageId,
                    StringComparison.OrdinalIgnoreCase)) ??
            Layout.Pages.First();

        private void EnsureLayout()
        {
            ShowcaseLayoutService.Normalize(Layout);
        }

        private void Rebuild()
        {
            if (_disposed)
            {
                return;
            }

            EnsureLayout();
            UpdatePageSelector();
            BuildDashboard();
        }

        private void UpdatePageSelector()
        {
            _updatingPageSelector = true;
            PageSelector.ItemsSource = null;
            PageSelector.ItemsSource = Layout.Pages;
            PageSelector.SelectedItem = CurrentPage;
            var index = Layout.Pages.IndexOf(CurrentPage);
            PageCountText.Text = $"{index + 1} / {Layout.Pages.Count}";
            PreviousPageButton.IsEnabled = Layout.Pages.Count > 1;
            NextPageButton.IsEnabled = Layout.Pages.Count > 1;
            _updatingPageSelector = false;
        }

        private void BuildDashboard()
        {
            if (_disposed)
            {
                return;
            }

            using var perf = PerfScope.Start(Logger, "Showcase.BuildDashboard", thresholdMs: 20);
            DashboardGrid.Children.Clear();
            _blockVisuals.Clear();
            // Every visible block re-enqueues through CreateWidgetHost below; anything still
            // queued targets containers this rebuild discards.
            ClearApplyQueue();
            _trackGrippers.Clear();
            _trackStrips.Clear();
            _trackRulers.Clear();
            _columnRulerTexts.Clear();
            _rowRulerTexts.Clear();
            _layoutHandles.Clear();
            _cutGhost = null;
            _layoutPreviewGhosts.Clear();
            _layoutPreviewHiddenChrome.Clear();
            _suppressedAddButton = null;
            DashboardGrid.RowDefinitions.Clear();
            DashboardGrid.ColumnDefinitions.Clear();
            var rowCount = PageRowCount;
            var columnCount = PageColumnCount;
            perf?.SetContext($"page={CurrentPage.PageId} blocks={CurrentPage.Blocks.Count} grid={rowCount}x{columnCount}");
            foreach (var weight in ShowcaseLayoutService.NormalizeTrackWeights(CurrentPage.RowWeights, rowCount))
            {
                DashboardGrid.RowDefinitions.Add(
                    new RowDefinition { Height = new GridLength(weight, GridUnitType.Star) });
            }

            foreach (var weight in ShowcaseLayoutService.NormalizeTrackWeights(CurrentPage.ColumnWeights, columnCount))
            {
                DashboardGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            }

            if (EditLayoutButton.IsChecked == true &&
                !CurrentPage.Blocks.Any(block => string.Equals(
                    block.BlockId,
                    _selectedBlockId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _selectedBlockId = CurrentPage.Blocks.FirstOrDefault()?.BlockId;
            }

            foreach (var block in CurrentPage.Blocks)
            {
                var container = CreateBlockContainer(block);
                Grid.SetRow(container, block.Row);
                Grid.SetColumn(container, block.Column);
                Grid.SetRowSpan(container, block.RowSpan);
                Grid.SetColumnSpan(container, block.ColumnSpan);
                DashboardGrid.Children.Add(container);
            }

            // Grippers and rulers are edit-mode chrome: 4x(N-1) thumbs and 2N text boxes that a
            // viewing dashboard only ever collapses. They are built when edit mode is entered
            // (UpdateTrackGripperVisibility), so a plain open skips them.
            if (EditLayoutButton.IsChecked == true)
            {
                AddTrackGrippers();
            }

            UpdateLayoutHandles();
            ApplyPendingCutVisual();

            // A rebuild destroys the focused block container, and with it the keyboard focus
            // the editor shortcuts need: PreviewKeyDown only fires while focus is inside this
            // control. Without this, the first paste worked and every later one did nothing.
            FocusSelectedBlock();
            _builtPageId = CurrentPage.PageId;
            _layoutSignature = ComputeLayoutSignature();
        }

        // Each handle's grab pad is centered on the grid's outer edge so edit mode never
        // resizes the dashboard; the handles render above the block layer (ZIndex 40).
        private const double TrackGripperSize = 24;

        // Grab handles straddling the page's outer edges, one pair per internal boundary:
        // column handles sit on the top and bottom edges, row handles on the left and right
        // edges. They hang half outside the grid, render above the block layer, and only
        // show in edit mode, so they never compete with block drag/split/merge gestures.
        /// <summary>The current page's normalized row count.</summary>
        private int PageRowCount => ShowcaseLayoutService.NormalizeTrackCount(CurrentPage?.RowCount ?? 0);

        /// <summary>The current page's normalized column count.</summary>
        private int PageColumnCount => ShowcaseLayoutService.NormalizeTrackCount(CurrentPage?.ColumnCount ?? 0);

        private int PageTrackCount(bool vertical) => vertical ? PageColumnCount : PageRowCount;

        private void AddTrackGrippers()
        {
            AddTrackStrips();
            foreach (var vertical in new[] { true, false })
            {
                for (var boundary = 0; boundary < PageTrackCount(vertical) - 1; boundary++)
                {
                    AddOverlay(CreateTrackGripper(vertical, boundary, nearEdge: true));
                    AddOverlay(CreateTrackGripper(vertical, boundary, nearEdge: false));
                }
            }

            AddTrackRulers();
            UpdateTrackGripperVisibility();
        }

        // How far the labels hang past the grid's top and left edges: the control's own 10px
        // margin around the dashboard, so they sit outside the grid without resizing it and
        // without reaching past this control's bounds into an ancestor's clip.
        private const double TrackRulerOverhang = 10;

        // One label per track, centred on it: columns above the top edge, rows (turned to read
        // along the edge) left of the left edge. Centred rather than at the boundaries, so they
        // never sit under the grippers. Each is an editable box: typing a size is the precise
        // way to set a track, where a drag only lands within a pixel or two.
        private void AddTrackRulers()
        {
            for (var index = 0; index < PageColumnCount; index++)
            {
                var column = CreateTrackRuler(vertical: true, index, out var columnBox);
                Grid.SetRow(column, 0);
                Grid.SetColumn(column, index);
                column.HorizontalAlignment = HorizontalAlignment.Center;
                column.VerticalAlignment = VerticalAlignment.Top;
                column.Margin = new Thickness(0, -TrackRulerOverhang, 0, 0);
                _columnRulerTexts.Add(columnBox);
                AddOverlay(column);
            }

            for (var index = 0; index < PageRowCount; index++)
            {
                var row = CreateTrackRuler(vertical: false, index, out var rowBox);
                Grid.SetRow(row, index);
                Grid.SetColumn(row, 0);
                row.HorizontalAlignment = HorizontalAlignment.Left;
                row.VerticalAlignment = VerticalAlignment.Center;
                row.Margin = new Thickness(-TrackRulerOverhang, 0, 0, 0);
                row.LayoutTransform = new System.Windows.Media.RotateTransform(-90);
                _rowRulerTexts.Add(rowBox);
                AddOverlay(row);
            }
        }

        // Every edit-mode element that is not a block goes through here: grippers, rulers, lattice
        // and cut lines, chevrons, ghosts and previews. Hosted so its size never reaches the
        // grid's tracks (see OverlayLayoutHost); RemoveOverlay takes the same element back out.
        private void AddOverlay(FrameworkElement element)
        {
            DashboardGrid.Children.Add(OverlayLayoutHost.Wrap(element));
        }

        private void RemoveOverlay(UIElement element)
        {
            if (element != null)
            {
                DashboardGrid.Children.Remove(OverlayLayoutHost.HostOf(element));
            }
        }

        // A bare text host rather than the theme's TextBox template, whose border, padding and
        // minimum height would not fit the overhang.
        private static readonly ControlTemplate TrackRulerBoxTemplate = CreateTrackRulerBoxTemplate();

        // Marks a box whose Enter or Escape already settled the edit.
        private static readonly object TrackRulerEditHandled = new object();

        private static ControlTemplate CreateTrackRulerBoxTemplate()
        {
            var host = new FrameworkElementFactory(typeof(Decorator)) { Name = "PART_ContentHost" };
            return new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        }

        private FrameworkElement CreateTrackRuler(bool vertical, int index, out TextBox box)
        {
            // Caption text with no border, so the pill stays slim enough to sit mostly in the overhang.
            var editor = new TextBox
            {
                Template = TrackRulerBoxTemplate,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                TextAlignment = TextAlignment.Center,
                Cursor = Cursors.IBeam,
                AcceptsReturn = false
            };
            editor.SetResourceReference(TextBox.ForegroundProperty, "PlayAch.Brush.Text");
            editor.SetResourceReference(TextBox.CaretBrushProperty, "PlayAch.Brush.Text");
            editor.SetResourceReference(TextBox.FontSizeProperty, "PlayAch.FontSize.Caption");
            editor.GotKeyboardFocus += (_, __) =>
            {
                // Just the number while editing; the unit comes back with the next refresh.
                editor.Text = FormatTrackNumber(ReadTrackSize(vertical, index));
                editor.SelectAll();
            };
            editor.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (!editor.IsKeyboardFocusWithin)
                {
                    editor.Focus();
                    args.Handled = true;
                }
            };
            editor.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    CommitTrackRulerEdit(vertical, index, editor.Text);
                    // Losing focus below must not commit again: the layout has not caught up yet,
                    // so a second pass would read the old size and apply the change twice.
                    editor.Tag = TrackRulerEditHandled;
                    args.Handled = true;
                    Keyboard.ClearFocus();
                    FocusSelectedBlock();
                }
                else if (args.Key == Key.Escape)
                {
                    editor.Tag = TrackRulerEditHandled;
                    args.Handled = true;
                    Keyboard.ClearFocus();
                    FocusSelectedBlock();
                }
            };
            editor.LostKeyboardFocus += (_, __) =>
            {
                if (ReferenceEquals(editor.Tag, TrackRulerEditHandled))
                {
                    editor.Tag = null;
                    ScheduleTrackRulerUpdate();
                    return;
                }

                CommitTrackRulerEdit(vertical, index, editor.Text);
            };

            var ruler = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                Opacity = 0.9,
                Child = editor
            };
            ruler.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.PopupSurface");
            // Above the block layer, like the grippers.
            Panel.SetZIndex(ruler, 41);
            // The label sits on its track's edge strip, so it answers hover and right-click the
            // same way instead of offering the text box's Cut/Copy/Paste menu.
            editor.ContextMenuOpening += (_, args) => args.Handled = true;
            AttachTrackMenu(ruler, vertical, index);
            _trackRulers.Add(ruler);
            box = editor;
            return ruler;
        }

        private double ReadTrackSize(bool vertical, int index)
        {
            if (vertical)
            {
                return index < DashboardGrid.ColumnDefinitions.Count
                    ? DashboardGrid.ColumnDefinitions[index].ActualWidth
                    : 0;
            }

            return index < DashboardGrid.RowDefinitions.Count
                ? DashboardGrid.RowDefinitions[index].ActualHeight
                : 0;
        }

        // Moves the boundary after the track (before it, for the last track) by the difference, the
        // same weight transfer a drag makes, so the neighbour absorbs it and the grid never changes
        // size. The track clamps to its min/max weight like a drag does; the label then shows what
        // it actually landed on.
        private void CommitTrackRulerEdit(bool vertical, int index, string text)
        {
            if (_disposed || EditLayoutButton.IsChecked != true)
            {
                return;
            }

            var digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var target) || target <= 0)
            {
                ScheduleTrackRulerUpdate();
                return;
            }

            if (MoveTrackToSize(vertical, index, target))
            {
                // Layout rounding can land the star-sized track a pixel off the typed size; once
                // the layout has settled, correct by that remainder (a single pass, so a size the
                // min/max weight clamp refuses cannot loop).
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (!_disposed && EditLayoutButton.IsChecked == true)
                        {
                            MoveTrackToSize(vertical, index, target);
                            CommitTrackWeights();
                            ScheduleTrackRulerUpdate();
                        }
                    }),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
                CommitTrackWeights();
            }

            ScheduleTrackRulerUpdate();
        }

        private bool MoveTrackToSize(bool vertical, int index, int target)
        {
            var count = vertical ? DashboardGrid.ColumnDefinitions.Count : DashboardGrid.RowDefinitions.Count;
            var delta = target - Math.Round(ReadTrackSize(vertical, index));
            if (count < 2 || index >= count || delta == 0)
            {
                return false;
            }

            if (index < count - 1)
            {
                AdjustTrackWeights(vertical, index, delta);
            }
            else
            {
                AdjustTrackWeights(vertical, index - 1, -delta);
            }

            return true;
        }

        // A throttle rather than a trailing debounce: during a drag the first change schedules a
        // tick and later ones ride it, so the labels keep moving instead of waiting for a pause.
        private void ScheduleTrackRulerUpdate()
        {
            if (!_disposed && EditLayoutButton.IsChecked == true && !_trackRulerTimer.IsEnabled)
            {
                _trackRulerTimer.Start();
            }
        }

        private void TrackRulerTimer_Tick(object sender, EventArgs e)
        {
            _trackRulerTimer.Stop();
            if (_disposed || EditLayoutButton.IsChecked != true)
            {
                return;
            }

            for (var index = 0; index < _columnRulerTexts.Count; index++)
            {
                UpdateTrackRulerText(_columnRulerTexts[index], ReadTrackSize(vertical: true, index));
            }

            for (var index = 0; index < _rowRulerTexts.Count; index++)
            {
                UpdateTrackRulerText(_rowRulerTexts[index], ReadTrackSize(vertical: false, index));
            }
        }

        // A box being typed in keeps what the user typed.
        private static void UpdateTrackRulerText(TextBox box, double size)
        {
            if (!box.IsKeyboardFocusWithin)
            {
                box.Text = FormatTrackNumber(size) + " px";
            }
        }

        private static string FormatTrackNumber(double size) =>
            Math.Round(size).ToString("0", PlayniteAchievements.Common.FormattingCulture.Current);

        private System.Windows.Controls.Primitives.Thumb CreateTrackGripper(
            bool vertical,
            int boundary,
            bool nearEdge)
        {
            var thumb = new System.Windows.Controls.Primitives.Thumb
            {
                Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS,
                Focusable = false,
                Template = vertical ? TrackGripperTemplateVertical : TrackGripperTemplateHorizontal
            };
            // The far edge of a column gripper is the last row, and of a row gripper the last column.
            var lastCell = PageTrackCount(!vertical) - 1;
            var edgeOffset = TrackGripperSize / 2;
            if (vertical)
            {
                // Straddles the column boundary, centered on the top (near) or bottom (far)
                // edge of the grid.
                thumb.Width = 22;
                thumb.Height = TrackGripperSize;
                thumb.HorizontalAlignment = HorizontalAlignment.Right;
                thumb.VerticalAlignment = nearEdge ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                thumb.Margin = nearEdge
                    ? new Thickness(0, -edgeOffset, -11, 0)
                    : new Thickness(0, 0, -11, -edgeOffset);
                Grid.SetColumn(thumb, boundary);
                Grid.SetRow(thumb, nearEdge ? 0 : lastCell);
            }
            else
            {
                // Straddles the row boundary, centered on the left (near) or right (far) edge.
                thumb.Width = TrackGripperSize;
                thumb.Height = 22;
                thumb.VerticalAlignment = VerticalAlignment.Bottom;
                thumb.HorizontalAlignment = nearEdge ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                thumb.Margin = nearEdge
                    ? new Thickness(-edgeOffset, 0, 0, -11)
                    : new Thickness(0, 0, -edgeOffset, -11);
                Grid.SetRow(thumb, boundary);
                Grid.SetColumn(thumb, nearEdge ? 0 : lastCell);
            }

            Panel.SetZIndex(thumb, 40);
            thumb.DragDelta += (_, args) => AdjustTrackWeights(
                vertical,
                boundary,
                vertical ? args.HorizontalChange : args.VerticalChange);
            thumb.DragCompleted += (_, __) => CommitTrackWeights();
            _trackGrippers.Add(thumb);
            return thumb;
        }

        // Built once per orientation, like TrackRulerBoxTemplate: the template is identical for
        // every gripper, and the accent brush inside it is a resource reference, so sharing it
        // across thumbs and dashboards loses nothing.
        private static readonly ControlTemplate TrackGripperTemplateVertical = CreateTrackGripperTemplate(vertical: true);
        private static readonly ControlTemplate TrackGripperTemplateHorizontal = CreateTrackGripperTemplate(vertical: false);

        private static ControlTemplate CreateTrackGripperTemplate(bool vertical)
        {
            // Transparent pad for a comfortable grab target, with a small accent pill
            // centered on the boundary line.
            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            var bar = new FrameworkElementFactory(typeof(Border));
            bar.SetValue(WidthProperty, vertical ? 8d : 18d);
            bar.SetValue(HeightProperty, vertical ? 18d : 8d);
            bar.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            bar.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bar.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bar.SetValue(OpacityProperty, 0.8);
            bar.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.Accent");
            root.AppendChild(bar);
            return new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb))
            {
                VisualTree = root
            };
        }

        private void UpdateTrackGripperVisibility()
        {
            var editing = EditLayoutButton.IsChecked == true;
            // First entry into edit mode on this dashboard: build the chrome now. AddTrackGrippers
            // re-enters here once the lists are populated (a one-track page has no grippers but
            // still gets rulers, so both lists gate the re-entry).
            if (editing && _blockVisuals.Count > 0 && _trackGrippers.Count == 0 && _trackRulers.Count == 0)
            {
                AddTrackGrippers();
                return;
            }

            foreach (var gripper in _trackGrippers)
            {
                gripper.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (var strip in _trackStrips)
            {
                strip.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (var ruler in _trackRulers)
            {
                ruler.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            }

            ScheduleTrackRulerUpdate();
        }

        private void AdjustTrackWeights(bool vertical, int boundary, double pixelDelta)
        {
            var totalPixels = vertical ? DashboardGrid.ActualWidth : DashboardGrid.ActualHeight;
            if (totalPixels <= 0 || double.IsNaN(pixelDelta) || pixelDelta == 0)
            {
                return;
            }

            var weights = ReadTrackWeights(vertical);
            var sum = weights.Sum();
            if (sum <= 0)
            {
                return;
            }

            // Convert the pixel drag into star units and move weight between the two tracks
            // that meet at this boundary, clamping both while keeping their total unchanged.
            var deltaStars = pixelDelta / (totalPixels / sum);
            var pairSum = weights[boundary] + weights[boundary + 1];
            var lower = Math.Max(
                ShowcaseLayoutService.MinTrackWeight,
                pairSum - ShowcaseLayoutService.MaxTrackWeight);
            var upper = Math.Min(
                ShowcaseLayoutService.MaxTrackWeight,
                pairSum - ShowcaseLayoutService.MinTrackWeight);
            var first = Math.Min(upper, Math.Max(lower, weights[boundary] + deltaStars));
            weights[boundary] = first;
            weights[boundary + 1] = pairSum - first;
            ApplyTrackWeights(vertical, weights);
            ScheduleTrackRulerUpdate();
        }

        private double[] ReadTrackWeights(bool vertical)
        {
            return vertical
                ? DashboardGrid.ColumnDefinitions.Select(definition => definition.Width.Value).ToArray()
                : DashboardGrid.RowDefinitions.Select(definition => definition.Height.Value).ToArray();
        }

        private void ApplyTrackWeights(bool vertical, double[] weights)
        {
            for (var index = 0; index < weights.Length; index++)
            {
                if (vertical)
                {
                    DashboardGrid.ColumnDefinitions[index].Width =
                        new GridLength(weights[index], GridUnitType.Star);
                }
                else
                {
                    DashboardGrid.RowDefinitions[index].Height =
                        new GridLength(weights[index], GridUnitType.Star);
                }
            }
        }

        // The drag already resized the live definitions, so persist and refresh the signature
        // in place instead of rebuilding the dashboard.
        private void CommitTrackWeights()
        {
            CurrentPage.RowWeights = ReadTrackWeights(vertical: false).ToList();
            CurrentPage.ColumnWeights = ReadTrackWeights(vertical: true).ToList();
            SaveAndPublish();
            _layoutSignature = ComputeLayoutSignature();
        }

        private void ResetTrackSizes()
        {
            CurrentPage.RowWeights = null;
            CurrentPage.ColumnWeights = null;
            ApplyTrackWeights(vertical: false, ShowcaseLayoutService.NormalizeTrackWeights(null, PageRowCount));
            ApplyTrackWeights(vertical: true, ShowcaseLayoutService.NormalizeTrackWeights(null, PageColumnCount));
            SaveAndPublish();
            _layoutSignature = ComputeLayoutSignature();
            ScheduleTrackRulerUpdate();
        }


        // Rebuilds the selected block's tactile layout affordances: dashed cut lines on each of
        // its interior cell boundaries (click cuts there; drag slides a ghost that snaps across
        // the block's boundaries and cuts on release) and merge chevrons on legal shared edges.
        // Handles are DashboardGrid siblings above the block layer, because block containers use
        // tunneling Preview* handlers that children could not pre-empt. Child order matters for
        // equal-ZIndex overlap resolution: cut lines first, chevrons second, ghost last.
        private void UpdateLayoutHandles()
        {
            foreach (var handle in _layoutHandles)
            {
                RemoveOverlay(handle);
            }

            _layoutHandles.Clear();
            HideCutGhost();
            ClearMergePreviewGlow();
            if (_suppressedAddButton != null)
            {
                _suppressedAddButton.Visibility = EditLayoutButton.IsChecked == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _suppressedAddButton = null;
            }

            var block = SelectedBlock;
            if (EditLayoutButton.IsChecked != true)
            {
                return;
            }

            AddLatticeLines();

            if (block == null)
            {
                return;
            }

            for (var line = block.Column + 1; line < block.Column + block.ColumnSpan; line++)
            {
                AddLayoutHandle(CreateCutLine(block, vertical: true, line));
            }

            for (var line = block.Row + 1; line < block.Row + block.RowSpan; line++)
            {
                AddLayoutHandle(CreateCutLine(block, vertical: false, line));
            }

            AddMergeChevrons(block);

            // An empty selected block's + button must win over the cut lines crossing it, but
            // it lives inside the block container (ZIndex 0) while cut lines are grid siblings
            // at 39 — nesting can't outrank them. So the selected block's + is re-hosted in
            // the overlay layer (above lines and grippers, below the non-hit-testable ghost)
            // and the in-block one is hidden until selection moves on.
            if (string.IsNullOrWhiteSpace(block.WidgetInstanceId))
            {
                var overlayAdd = CreateAddWidgetButton(block);
                overlayAdd.Visibility = Visibility.Visible;
                Grid.SetRow(overlayAdd, block.Row);
                Grid.SetColumn(overlayAdd, block.Column);
                Grid.SetRowSpan(overlayAdd, block.RowSpan);
                Grid.SetColumnSpan(overlayAdd, block.ColumnSpan);
                Panel.SetZIndex(overlayAdd, 44);
                AddLayoutHandle(overlayAdd);
                if (_blockVisuals.TryGetValue(block.BlockId, out var state) &&
                    state.AddButton != null)
                {
                    state.AddButton.Visibility = Visibility.Collapsed;
                    _suppressedAddButton = state.AddButton;
                }
            }
        }

        // The page-wide cell lattice, drawn only while editing. Blocks tile the grid and
        // span whole cells, so the lattice is the only thing that shows where a block could be
        // cut before one is selected. Deliberately quieter than the accent dashed cut lines and
        // below them in ZIndex: this is orientation, not an affordance, and it never takes a hit.
        private void AddLatticeLines()
        {
            for (var line = 1; line < PageColumnCount; line++)
            {
                AddLayoutHandle(CreateLatticeLine(vertical: true, line, PageRowCount));
            }

            for (var line = 1; line < PageRowCount; line++)
            {
                AddLayoutHandle(CreateLatticeLine(vertical: false, line, PageColumnCount));
            }
        }

        // crossCount is the number of tracks the line runs across: rows for a vertical line.
        private static System.Windows.Shapes.Rectangle CreateLatticeLine(
            bool vertical,
            int boundary,
            int crossCount)
        {
            var line = new System.Windows.Shapes.Rectangle
            {
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                Opacity = 0.5
            };
            // Accent rather than the border brush: PlayAch.Brush.Border follows the theme's
            // NormalBorderBrush, which in several themes is too low-contrast to see as a
            // hairline. Solid and thin still reads as secondary next to the dashed 2px cut lines.
            line.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "PlayAch.Brush.Accent");

            // Straddles the track edge by half its width so the line sits on the boundary
            // rather than inside the cell before it.
            if (vertical)
            {
                line.Width = 1;
                line.HorizontalAlignment = HorizontalAlignment.Right;
                line.VerticalAlignment = VerticalAlignment.Stretch;
                line.Margin = new Thickness(0, 0, -0.5, 0);
                Grid.SetColumn(line, boundary - 1);
                Grid.SetRow(line, 0);
                Grid.SetRowSpan(line, crossCount);
            }
            else
            {
                line.Height = 1;
                line.HorizontalAlignment = HorizontalAlignment.Stretch;
                line.VerticalAlignment = VerticalAlignment.Bottom;
                line.Margin = new Thickness(0, 0, 0, -0.5);
                Grid.SetRow(line, boundary - 1);
                Grid.SetColumn(line, 0);
                Grid.SetColumnSpan(line, crossCount);
            }

            // Below the cut lines (39) and grippers (40) so both keep visual and hit priority.
            Panel.SetZIndex(line, 38);
            return line;
        }

        private void AddLayoutHandle(FrameworkElement handle)
        {
            _layoutHandles.Add(handle);
            AddOverlay(handle);
        }

        private System.Windows.Controls.Primitives.Thumb CreateCutLine(
            ShowcaseBlockSettings block,
            bool vertical,
            int boundary)
        {
            var thumb = new System.Windows.Controls.Primitives.Thumb
            {
                Cursor = CutCursor.Value,
                Focusable = false,
                Template = CreateCutLineTemplate(vertical)
            };
            // Automation name only; the scissors cursor already communicates the action,
            // so no tooltip.
            System.Windows.Automation.AutomationProperties.SetName(
                thumb,
                FormatSplitName(block, vertical, boundary));
            if (vertical)
            {
                thumb.Width = 12;
                thumb.HorizontalAlignment = HorizontalAlignment.Right;
                thumb.Margin = new Thickness(0, 6, -6, 6);
                Grid.SetColumn(thumb, boundary - 1);
                Grid.SetRow(thumb, block.Row);
                Grid.SetRowSpan(thumb, block.RowSpan);
            }
            else
            {
                thumb.Height = 12;
                thumb.VerticalAlignment = VerticalAlignment.Bottom;
                thumb.Margin = new Thickness(6, 0, 6, -6);
                Grid.SetRow(thumb, boundary - 1);
                Grid.SetColumn(thumb, block.Column);
                Grid.SetColumnSpan(thumb, block.ColumnSpan);
            }

            // One below the track grippers (40): in the one outer band where they can overlap,
            // the gripper wins while the cut line stays grabbable along its remaining length.
            Panel.SetZIndex(thumb, 39);
            var pageId = CurrentPage.PageId;
            thumb.MouseEnter += (_, __) =>
            {
                if (!thumb.IsDragging)
                {
                    ShowSplitPreview(block, vertical, boundary);
                }
            };
            thumb.MouseLeave += (_, __) =>
            {
                if (!thumb.IsDragging)
                {
                    ClearLayoutPreview();
                }
            };
            thumb.DragStarted += (_, __) =>
            {
                _cutCandidate = boundary;
                _cutPixels = BoundaryOffset(vertical, boundary);
                thumb.Opacity = 0.15;
                ShowCutGhost(block, vertical, boundary);
            };
            thumb.DragDelta += (_, args) =>
            {
                _cutPixels += vertical ? args.HorizontalChange : args.VerticalChange;
                var start = vertical ? block.Column : block.Row;
                var span = vertical ? block.ColumnSpan : block.RowSpan;
                var best = _cutCandidate;
                var bestDistance = double.MaxValue;
                for (var line = start + 1; line < start + span; line++)
                {
                    var distance = Math.Abs(BoundaryOffset(vertical, line) - _cutPixels);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = line;
                    }
                }

                if (best != _cutCandidate)
                {
                    _cutCandidate = best;
                    MoveCutGhost(vertical, best);
                    ShowSplitPreview(block, vertical, best);
                }
            };
            thumb.DragCompleted += (_, args) =>
            {
                HideCutGhost();
                ClearLayoutPreview();
                thumb.Opacity = 1.0;
                if (!args.Canceled)
                {
                    // Click and drag share one commit: with no movement the candidate is
                    // still this line's own boundary.
                    SplitBlock(pageId, block.BlockId, vertical, _cutCandidate);
                }
                else
                {
                    UpdateLayoutHandles();
                }
            };
            return thumb;
        }

        private string FormatSplitName(ShowcaseBlockSettings block, bool vertical, int boundary)
        {
            var start = vertical ? block.Column : block.Row;
            var span = vertical ? block.ColumnSpan : block.RowSpan;
            return string.Format(
                Localize("LOCPlayAch_Showcase_SplitAtFormat"),
                boundary - start,
                start + span - boundary);
        }

        private static ControlTemplate CreateCutLineTemplate(bool vertical)
        {
            // Transparent pad for a comfortable grab target; the dashed accent line reads as
            // "cut here" and brightens on hover.
            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            var line = new FrameworkElementFactory(typeof(System.Windows.Shapes.Line)) { Name = "CutLine" };
            line.SetValue(System.Windows.Shapes.Line.X1Property, 0d);
            line.SetValue(System.Windows.Shapes.Line.Y1Property, 0d);
            line.SetValue(System.Windows.Shapes.Line.X2Property, vertical ? 0d : 1d);
            line.SetValue(System.Windows.Shapes.Line.Y2Property, vertical ? 1d : 0d);
            line.SetValue(System.Windows.Shapes.Shape.StretchProperty, System.Windows.Media.Stretch.Fill);
            line.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2d);
            line.SetValue(
                System.Windows.Shapes.Shape.StrokeDashArrayProperty,
                new System.Windows.Media.DoubleCollection { 4d, 3d });
            line.SetValue(
                System.Windows.Shapes.Shape.StrokeDashCapProperty,
                System.Windows.Media.PenLineCap.Round);
            line.SetValue(
                HorizontalAlignmentProperty,
                vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch);
            line.SetValue(
                VerticalAlignmentProperty,
                vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center);
            line.SetValue(OpacityProperty, 0.55);
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PlayAch.Brush.Accent");
            root.AppendChild(line);

            var template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb))
            {
                VisualTree = root
            };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, 1.0, "CutLine"));
            hover.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeThicknessProperty, 3d, "CutLine"));
            template.Triggers.Add(hover);
            return template;
        }

        // Scissors cursor for the cut lines, generated once from the Segoe MDL2 "Cut" glyph
        // (WPF ships no scissors cursor). Falls back to the crosshair if anything fails.
        private static readonly Lazy<Cursor> CutCursor =
            new Lazy<Cursor>(CreateCutCursor);

        private static Cursor CreateCutCursor()
        {
            try
            {
                const int size = 24;
                var typeface = new System.Windows.Media.Typeface("Segoe MDL2 Assets");
                var visual = new System.Windows.Media.DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    // Dark halo behind a light glyph keeps the cursor readable on any theme.
                    foreach (var offset in new[]
                             {
                                 new Point(0, 1), new Point(2, 1), new Point(1, 0), new Point(1, 2)
                             })
                    {
                        context.DrawText(
                            CreateCutGlyph(typeface, System.Windows.Media.Brushes.Black),
                            offset);
                    }

                    context.DrawText(
                        CreateCutGlyph(typeface, System.Windows.Media.Brushes.White),
                        new Point(1, 1));
                }

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                byte[] png;
                using (var pngStream = new System.IO.MemoryStream())
                {
                    encoder.Save(pngStream);
                    png = pngStream.ToArray();
                }

                // Minimal .cur container: ICONDIR + one entry (hotspot at the glyph center)
                // + the PNG payload (supported for cursors since Windows Vista).
                using (var stream = new System.IO.MemoryStream())
                using (var writer = new System.IO.BinaryWriter(stream))
                {
                    writer.Write((ushort)0);            // reserved
                    writer.Write((ushort)2);            // type: cursor
                    writer.Write((ushort)1);            // image count
                    writer.Write((byte)size);           // width
                    writer.Write((byte)size);           // height
                    writer.Write((byte)0);              // palette
                    writer.Write((byte)0);              // reserved
                    writer.Write((ushort)(size / 2));   // hotspot x
                    writer.Write((ushort)(size / 2));   // hotspot y
                    writer.Write(png.Length);           // payload size
                    writer.Write(22);                   // payload offset
                    writer.Write(png);
                    writer.Flush();
                    stream.Position = 0;
                    return new Cursor(stream);
                }
            }
            catch (Exception)
            {
                return Cursors.Cross;
            }
        }

        private static System.Windows.Media.FormattedText CreateCutGlyph(
            System.Windows.Media.Typeface typeface,
            System.Windows.Media.Brush brush)
        {
            return new System.Windows.Media.FormattedText(
                "\uE8C6",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                20,
                brush,
                1.0);
        }

        /// <summary>Pixel offset of an absolute grid line, from live track sizes (weight-proof).</summary>
        private double BoundaryOffset(bool vertical, int line)
        {
            var offset = 0d;
            var trackCount = vertical
                ? DashboardGrid.ColumnDefinitions.Count
                : DashboardGrid.RowDefinitions.Count;
            for (var index = 0; index < line && index < trackCount; index++)
            {
                offset += vertical
                    ? DashboardGrid.ColumnDefinitions[index].ActualWidth
                    : DashboardGrid.RowDefinitions[index].ActualHeight;
            }

            return offset;
        }

        private void ShowCutGhost(ShowcaseBlockSettings block, bool vertical, int boundary)
        {
            HideCutGhost();
            var ghost = new System.Windows.Shapes.Line
            {
                X1 = 0,
                Y1 = 0,
                X2 = vertical ? 0 : 1,
                Y2 = vertical ? 1 : 0,
                Stretch = System.Windows.Media.Stretch.Fill,
                StrokeThickness = 3,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4d, 3d },
                StrokeDashCap = System.Windows.Media.PenLineCap.Round,
                IsHitTestVisible = false
            };
            ghost.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PlayAch.Brush.Accent");
            if (vertical)
            {
                ghost.HorizontalAlignment = HorizontalAlignment.Right;
                ghost.Margin = new Thickness(0, 6, -1, 6);
                Grid.SetRow(ghost, block.Row);
                Grid.SetRowSpan(ghost, block.RowSpan);
            }
            else
            {
                ghost.VerticalAlignment = VerticalAlignment.Bottom;
                ghost.Margin = new Thickness(6, 0, 6, -1);
                Grid.SetColumn(ghost, block.Column);
                Grid.SetColumnSpan(ghost, block.ColumnSpan);
            }

            Panel.SetZIndex(ghost, 45);
            _cutGhost = ghost;
            AddOverlay(ghost);
            MoveCutGhost(vertical, boundary);
        }

        // Discrete snap: the ghost only ever sits on a boundary, moved by cell assignment
        // (no per-pixel transforms, no allocation while dragging).
        private void MoveCutGhost(bool vertical, int boundary)
        {
            if (_cutGhost == null)
            {
                return;
            }

            if (vertical)
            {
                Grid.SetColumn(_cutGhost, boundary - 1);
            }
            else
            {
                Grid.SetRow(_cutGhost, boundary - 1);
            }
        }

        private void HideCutGhost()
        {
            if (_cutGhost != null)
            {
                RemoveOverlay(_cutGhost);
                _cutGhost = null;
            }
        }

        // A chevron per direction whose merge is geometrically legal (rectangular closure);
        // hover previews the closure with a glow, click commits through MergeSelectedWith
        // (which owns the multi-widget confirmation and survivor choice).
        private void AddMergeChevrons(ShowcaseBlockSettings block)
        {
            AddMergeChevron(block, rowDirection: 0, columnDirection: -1, "LOCPlayAch_Showcase_MergeLeftLabel");
            AddMergeChevron(block, rowDirection: -1, columnDirection: 0, "LOCPlayAch_Showcase_MergeUpLabel");
            AddMergeChevron(block, rowDirection: 1, columnDirection: 0, "LOCPlayAch_Showcase_MergeDownLabel");
            AddMergeChevron(block, rowDirection: 0, columnDirection: 1, "LOCPlayAch_Showcase_MergeRightLabel");
        }

        private void AddMergeChevron(
            ShowcaseBlockSettings block,
            int rowDirection,
            int columnDirection,
            string labelKey)
        {
            var target = FindAdjacentBlocks(block, rowDirection, columnDirection).FirstOrDefault();
            if (target == null ||
                !ShowcaseLayoutService.TryGetMergePreview(
                    Layout,
                    CurrentPage.PageId,
                    block.BlockId,
                    target.BlockId,
                    out _))
            {
                return;
            }

            var chevron = new Button
            {
                Focusable = false,
                Template = CreateMergeChevronTemplate(rowDirection, columnDirection),
                ToolTip = Localize(labelKey)
            };
            System.Windows.Automation.AutomationProperties.SetName(chevron, Localize(labelKey));
            // Sits just inside the widget's own edge (like the cut lines, nothing hangs into
            // the gap), centered along that edge.
            var vertical = columnDirection != 0;
            if (vertical)
            {
                chevron.Width = 28;
                chevron.Height = 28;
                chevron.VerticalAlignment = VerticalAlignment.Center;
                chevron.HorizontalAlignment = columnDirection < 0
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Right;
                chevron.Margin = columnDirection < 0
                    ? new Thickness(6, 0, 0, 0)
                    : new Thickness(0, 0, 6, 0);
                Grid.SetColumn(chevron, columnDirection < 0 ? block.Column : block.Column + block.ColumnSpan - 1);
                Grid.SetRow(chevron, block.Row);
                Grid.SetRowSpan(chevron, block.RowSpan);
            }
            else
            {
                chevron.Width = 28;
                chevron.Height = 28;
                chevron.HorizontalAlignment = HorizontalAlignment.Center;
                chevron.VerticalAlignment = rowDirection < 0
                    ? VerticalAlignment.Top
                    : VerticalAlignment.Bottom;
                chevron.Margin = rowDirection < 0
                    ? new Thickness(0, 6, 0, 0)
                    : new Thickness(0, 0, 0, 6);
                Grid.SetRow(chevron, rowDirection < 0 ? block.Row : block.Row + block.RowSpan - 1);
                Grid.SetColumn(chevron, block.Column);
                Grid.SetColumnSpan(chevron, block.ColumnSpan);
            }

            Panel.SetZIndex(chevron, 39);
            var targetBlockId = target.BlockId;
            var blockId = block.BlockId;
            chevron.MouseEnter += (_, __) => ShowMergePreviewGlow(blockId, targetBlockId);
            chevron.MouseLeave += (_, __) => ClearMergePreviewGlow();
            chevron.Click += (_, __) => MergeSelectedWith(targetBlockId);
            AddLayoutHandle(chevron);
        }

        private static ControlTemplate CreateMergeChevronTemplate(int rowDirection, int columnDirection)
        {
            // Accent pill with an outward-pointing chevron, on a transparent grab pad.
            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            var pill = new FrameworkElementFactory(typeof(Border)) { Name = "ChevronPill" };
            var vertical = columnDirection != 0;
            pill.SetValue(WidthProperty, vertical ? 16d : 26d);
            pill.SetValue(HeightProperty, vertical ? 26d : 16d);
            pill.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            pill.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            pill.SetValue(OpacityProperty, 0.8);
            pill.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.Accent");

            // Direction-specific geometry authored centered inside a fixed box, with round
            // caps AND joins so the stroke extends symmetrically. Explicit Width/Height give
            // integer centering offsets inside the pill; a rotated shared glyph lands on
            // different subpixels per direction and reads as misaligned.
            var geometry = columnDirection < 0 ? "M 6,1 L 2,5 L 6,9"
                : columnDirection > 0 ? "M 2,1 L 6,5 L 2,9"
                : rowDirection < 0 ? "M 1,6 L 5,2 L 9,6"
                : "M 1,2 L 5,6 L 9,2";
            var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            arrow.SetValue(
                System.Windows.Shapes.Path.DataProperty,
                System.Windows.Media.Geometry.Parse(geometry));
            arrow.SetValue(WidthProperty, vertical ? 8d : 10d);
            arrow.SetValue(HeightProperty, vertical ? 10d : 8d);
            arrow.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5d);
            arrow.SetValue(
                System.Windows.Shapes.Shape.StrokeStartLineCapProperty,
                System.Windows.Media.PenLineCap.Round);
            arrow.SetValue(
                System.Windows.Shapes.Shape.StrokeEndLineCapProperty,
                System.Windows.Media.PenLineCap.Round);
            arrow.SetValue(
                System.Windows.Shapes.Shape.StrokeLineJoinProperty,
                System.Windows.Media.PenLineJoin.Round);
            arrow.SetValue(System.Windows.Shapes.Shape.StretchProperty, System.Windows.Media.Stretch.None);
            arrow.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            arrow.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PlayAch.Brush.Surface");

            pill.AppendChild(arrow);
            root.AppendChild(pill);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = root };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, 1.0, "ChevronPill"));
            template.Triggers.Add(hover);
            return template;
        }

        // Lights the closure that WOULD merge using each block's existing drop-glow layer.
        // Guarded by DragVisualKind.None on set and clear so a hover can never restyle or
        // clear live drag-and-drop visuals.
        // The merge preview is the merged block's single outline, drawn in place of the members'
        // own outlines (not on top of them, and without the per-block glow), so the page shows
        // the one shape the click produces.
        private void ShowMergePreviewGlow(string firstBlockId, string secondBlockId)
        {
            ClearMergePreviewGlow();
            if (!ShowcaseLayoutService.TryGetMergePreview(
                    Layout,
                    CurrentPage.PageId,
                    firstBlockId,
                    secondBlockId,
                    out var closure))
            {
                return;
            }

            ShowMergePreview(closure);
        }
        // The closure's bounding rectangle is the merged block (TryGetMergePreview only returns
        // rectangular closures).
        private void ShowMergePreview(IReadOnlyList<ShowcaseBlockSettings> closure)
        {
            if (closure == null || closure.Count == 0)
            {
                return;
            }

            var row = closure.Min(member => member.Row);
            var column = closure.Min(member => member.Column);
            var rowEnd = closure.Max(member => member.Row + member.RowSpan);
            var columnEnd = closure.Max(member => member.Column + member.ColumnSpan);
            ShowLayoutPreview(
                new[] { (row, column, rowEnd - row, columnEnd - column, BlockInset) },
                closure.Select(member => member.BlockId));
        }

        // The container's inset on each side; two neighbouring blocks sit 2 x this apart.
        private static readonly Thickness BlockInset = new Thickness(4);

        // Extra room each half leaves at the cut side, so the preview's gap is clearly wider than
        // the ordinary space between blocks and the new boundary reads at a glance (a still gap,
        // not an animation). The outer edges keep the block's own inset.
        private const double SplitPreviewGap = 8;

        private void ShowSplitPreview(ShowcaseBlockSettings block, bool vertical, int boundary)
        {
            var cut = BlockInset.Left + SplitPreviewGap;
            if (vertical)
            {
                ShowLayoutPreview(new[]
                {
                    (block.Row, block.Column, block.RowSpan, boundary - block.Column,
                        new Thickness(BlockInset.Left, BlockInset.Top, cut, BlockInset.Bottom)),
                    (block.Row, boundary, block.RowSpan, block.Column + block.ColumnSpan - boundary,
                        new Thickness(cut, BlockInset.Top, BlockInset.Right, BlockInset.Bottom))
                }, new[] { block.BlockId });
            }
            else
            {
                ShowLayoutPreview(new[]
                {
                    (block.Row, block.Column, boundary - block.Row, block.ColumnSpan,
                        new Thickness(BlockInset.Left, BlockInset.Top, BlockInset.Right, cut)),
                    (boundary, block.Column, block.Row + block.RowSpan - boundary, block.ColumnSpan,
                        new Thickness(BlockInset.Left, cut, BlockInset.Right, BlockInset.Bottom))
                }, new[] { block.BlockId });
            }
        }

        // Each preview shape sits exactly where a real block would (the container's 4px inset,
        // outline thickness and corner radius), in the accent outline over a faint wash, and the
        // outlines of the blocks it replaces hide while it shows: the page reads as the result of
        // the click rather than as extra borders on top of the current layout. Not hit-testable,
        // so it never steals the hover that shows it.
        private void ShowLayoutPreview(
            IEnumerable<(int Row, int Column, int RowSpan, int ColumnSpan, Thickness Margin)> cells,
            IEnumerable<string> replacedBlockIds)
        {
            ClearLayoutPreview();
            foreach (var blockId in replacedBlockIds ?? Enumerable.Empty<string>())
            {
                if (blockId != null &&
                    _blockVisuals.TryGetValue(blockId, out var state) &&
                    state?.EditChrome != null &&
                    state.DragVisual == DragVisualKind.None)
                {
                    _layoutPreviewHiddenChrome.Add((state.EditChrome, state.EditChrome.Visibility));
                    state.EditChrome.Visibility = Visibility.Hidden;
                }
            }

            foreach (var cell in cells)
            {
                AddLayoutPreviewGhost(cell, "PlayAch.Brush.Accent");
            }
        }

        // One preview shape: an outline in the given brush over a faint wash of it. It is
        // removed with the rest by ClearLayoutPreview.
        private void AddLayoutPreviewGhost(
            (int Row, int Column, int RowSpan, int ColumnSpan, Thickness Margin) cell,
            string brushKey)
        {
            if (cell.RowSpan <= 0 || cell.ColumnSpan <= 0)
            {
                return;
            }

            var wash = new Border { Opacity = 0.06 };
            wash.SetResourceReference(Border.BackgroundProperty, brushKey);
            wash.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Section");
            var outline = new Border { BorderThickness = new Thickness(2) };
            outline.SetResourceReference(Border.BorderBrushProperty, brushKey);
            outline.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Section");
            var ghost = new Grid
            {
                Margin = cell.Margin,
                IsHitTestVisible = false
            };
            ghost.Children.Add(wash);
            ghost.Children.Add(outline);
            Grid.SetRow(ghost, cell.Row);
            Grid.SetColumn(ghost, cell.Column);
            Grid.SetRowSpan(ghost, cell.RowSpan);
            Grid.SetColumnSpan(ghost, cell.ColumnSpan);
            // Above the blocks and the cut line, below the drag ghost line.
            Panel.SetZIndex(ghost, 44);
            _layoutPreviewGhosts.Add(ghost);
            AddOverlay(ghost);
        }

        private void ClearLayoutPreview()
        {
            foreach (var ghost in _layoutPreviewGhosts)
            {
                RemoveOverlay(ghost);
            }

            _layoutPreviewGhosts.Clear();
            foreach (var hidden in _layoutPreviewHiddenChrome)
            {
                // Only undo our own hide; a chrome refresh during the hover already set its own state.
                if (hidden.Chrome.Visibility == Visibility.Hidden)
                {
                    hidden.Chrome.Visibility = hidden.Previous;
                }
            }

            _layoutPreviewHiddenChrome.Clear();
        }
        private void ClearMergePreviewGlow()
        {
            ClearLayoutPreview();
            foreach (var blockId in _mergePreviewBlockIds)
            {
                if (!_blockVisuals.TryGetValue(blockId, out var state) ||
                    state?.Glow == null ||
                    state.DragVisual != DragVisualKind.None)
                {
                    continue;
                }

                state.Glow.BeginAnimation(OpacityProperty, null);
                state.Glow.Opacity = 0;
                state.Glow.Visibility = Visibility.Collapsed;
            }

            _mergePreviewBlockIds.Clear();
        }

        // Captures everything that forces block containers to be recreated: the page set, the
        // current page's block partition, and which widget instance (and kind) each block hosts.
        // Widget options and custom titles are deliberately excluded - Apply() refreshes those in
        // place through the projection without discarding the visual tree.
        private string ComputeLayoutSignature()
        {
            var builder = new System.Text.StringBuilder();
            foreach (var page in Layout.Pages)
            {
                builder.Append(page.PageId).Append('|').Append(page.Name).Append(';');
            }

            var current = CurrentPage;
            builder.Append('#').Append(current.PageId);
            foreach (var block in current.Blocks)
            {
                builder.Append('#')
                    .Append(block.BlockId).Append(',')
                    .Append(block.Row).Append(',')
                    .Append(block.Column).Append(',')
                    .Append(block.RowSpan).Append(',')
                    .Append(block.ColumnSpan).Append(',')
                    .Append(block.WidgetInstanceId ?? string.Empty);
                var widget = Layout.WidgetInstances.FirstOrDefault(instance =>
                    string.Equals(instance.InstanceId, block.WidgetInstanceId, StringComparison.OrdinalIgnoreCase));
                if (widget != null)
                {
                    builder.Append(',').Append((int)widget.Kind);
                }
            }

            // Grid size and track weights participate so an externally changed page layout
            // rebuilds; local gripper drags refresh the stored signature themselves after
            // applying in place.
            builder.Append('#').Append(PageRowCount).Append('x').Append(PageColumnCount).Append('#');
            AppendTrackWeights(builder, current.RowWeights, PageRowCount);
            builder.Append('/');
            AppendTrackWeights(builder, current.ColumnWeights, PageColumnCount);
            return builder.ToString();
        }

        private void AppendTrackWeights(
            System.Text.StringBuilder builder,
            List<double> weights,
            int count)
        {
            foreach (var weight in ShowcaseLayoutService.NormalizeTrackWeights(weights, count))
            {
                builder
                    .Append(weight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(',');
            }
        }

        private FrameworkElement CreateBlockContainer(ShowcaseBlockSettings block)
        {
            // The container never changes BorderThickness or Padding: edit-mode chrome is drawn
            // by an overlay layer so toggling edit mode cannot shift the widget layout.
            var border = new Border
            {
                Margin = new Thickness(4),
                AllowDrop = true,
                Tag = block,
                Focusable = EditLayoutButton.IsChecked == true,
                BorderThickness = new Thickness(0)
            };
            border.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Section");
            border.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.GridSurface");
            // Widgets can contain buttons, scroll viewers, and charts that consume bubbling
            // drag events. Tunneling at the block host keeps the illuminated target and the
            // committing drop on the same reliable path.
            border.PreviewDrop += Block_Drop;
            border.PreviewDragOver += Block_DragOver;
            border.PreviewDragLeave += Block_DragLeave;
            border.PreviewMouseLeftButtonDown += Block_PreviewMouseLeftButtonDown;
            border.PreviewMouseMove += Block_PreviewMouseMove;
            border.GotKeyboardFocus += Block_GotKeyboardFocus;

            UIElement content;
            ShowcaseWidgetControl widgetHost = null;
            Button addButton = null;
            var widget = FindWidget(block.WidgetInstanceId);
            if (widget == null)
            {
                addButton = CreateAddWidgetButton(block);
                content = addButton;
            }
            else
            {
                widgetHost = CreateWidgetHost(block, widget);
                content = widgetHost;
            }

            var layers = new Grid();
            layers.Children.Add(content);
            var editChrome = new Border
            {
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                BorderThickness = new Thickness(2)
            };
            editChrome.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Section");
            editChrome.SetResourceReference(Border.BorderBrushProperty, "PlayAch.Brush.Border");
            layers.Children.Add(editChrome);

            // Edit-mode corner actions replace the old right-click menu: gear (settings) in the
            // top-left, trash (delete) in the top-right. Created for every block and shown by
            // RefreshBlockChrome only while editing a block that hosts a widget, so the widget
            // move/swap fast path just re-resolves visibility.
            var blockId = block.BlockId;
            var settingsButton = CreateBlockActionButton(
                "\uE713",
                "LOCPlayAch_Showcase_WidgetSettings",
                HorizontalAlignment.Left);
            settingsButton.Click += (_, __) =>
            {
                var current = CurrentPage.Blocks.FirstOrDefault(candidate =>
                    string.Equals(candidate.BlockId, blockId, StringComparison.OrdinalIgnoreCase));
                var currentWidget = FindWidget(current?.WidgetInstanceId);
                if (currentWidget != null)
                {
                    OpenWidgetSettings(currentWidget);
                }
            };
            layers.Children.Add(settingsButton);
            var deleteButton = CreateBlockActionButton(
                "\uE74D",
                "LOCPlayAch_Showcase_DeleteWidget",
                HorizontalAlignment.Right);
            deleteButton.Click += (_, __) =>
            {
                var current = CurrentPage.Blocks.FirstOrDefault(candidate =>
                    string.Equals(candidate.BlockId, blockId, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(current?.WidgetInstanceId))
                {
                    ShowcaseLayoutService.DeleteWidget(Layout, current.WidgetInstanceId);
                    SaveAndApplyBlocks();
                }
            };
            layers.Children.Add(deleteButton);

            var dropGlow = new Border
            {
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Margin = new Thickness(2),
                BorderThickness = new Thickness(3),
                Opacity = 0
            };
            dropGlow.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.Accent");
            dropGlow.SetResourceReference(Border.BorderBrushProperty, "PlayAch.Brush.Accent");
            dropGlow.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Section");
            layers.Children.Add(dropGlow);

            var dropStatus = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dropStatus.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            var dropStatusPanel = new Border
            {
                Child = dropStatus,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Margin = new Thickness(12),
                Padding = new Thickness(14, 10, 14, 10),
                BorderThickness = new Thickness(1),
                Opacity = 0.94,
                MaxWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dropStatusPanel.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.Surface");
            dropStatusPanel.SetResourceReference(Border.BorderBrushProperty, "PlayAch.Brush.Accent");
            dropStatusPanel.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Card");
            layers.Children.Add(dropStatusPanel);
            border.Child = layers;
            var visualState = new BlockVisualState
            {
                Block = block,
                Container = border,
                EditChrome = editChrome,
                SettingsButton = settingsButton,
                DeleteButton = deleteButton,
                Glow = dropGlow,
                StatusPanel = dropStatusPanel,
                Status = dropStatus,
                Host = widgetHost,
                Widget = widget,
                AddButton = addButton
            };
            _blockVisuals[block.BlockId] = visualState;
            RefreshBlockChrome(visualState);
            return border;
        }

        private static Grid LayersOf(FrameworkElement container)
        {
            return (container as Border)?.Child as Grid;
        }

        // Drops cached controls for widgets that no longer exist, so deleted widgets do not pin
        // their (grid-bearing) controls in memory.
        private void PruneHostCache()
        {
            var live = new HashSet<string>(
                Layout.WidgetInstances
                    .Where(widget => !string.IsNullOrWhiteSpace(widget?.InstanceId))
                    .Select(widget => widget.InstanceId),
                StringComparer.OrdinalIgnoreCase);
            foreach (var staleId in _hostCache.Keys.Where(id => !live.Contains(id)).ToList())
            {
                if (_hostCache.TryGetValue(staleId, out var stale))
                {
                    stale?.DisposeBody();
                }

                _hostCache.Remove(staleId);
            }

            // Deleted widgets' control bar state goes with them.
            PlayniteAchievements.ViewModels.Showcase.Widgets.ShowcaseControlBarStates.RemoveExcept(live);
        }

        // Reuses the cached control for this widget when there is one, otherwise builds a fresh
        // control. Either way the projection (and with it template inflation) is deferred to
        // background priority, so the click that triggered the change paints immediately and the
        // widget body fills in right after.
        private ShowcaseWidgetControl CreateWidgetHost(
            ShowcaseBlockSettings block,
            ShowcaseWidgetInstanceSettings widget)
        {
            ShowcaseWidgetControl host = null;
            if (!string.IsNullOrWhiteSpace(widget?.InstanceId) &&
                _hostCache.TryGetValue(widget.InstanceId, out var cached) &&
                cached != null)
            {
                (cached.Parent as Panel)?.Children.Remove(cached);
                host = cached;
            }

            host = host ?? new ShowcaseWidgetControl();
            if (!string.IsNullOrWhiteSpace(widget?.InstanceId))
            {
                _hostCache[widget.InstanceId] = host;
            }

            // While editing, clicks select and drag blocks instead of tunneling into widget
            // content - embedded grids and charts otherwise run hit tests, focus moves, and
            // selection work on every click. The widget menu rides on the block container so
            // it stays reachable with the body inert.
            host.IsHitTestVisible = EditLayoutButton.IsChecked != true;
            QueueWidgetApply(host, widget, block.BlockId, snapshotOnly: false);
            return host;
        }

        private void QueueWidgetApply(
            ShowcaseWidgetControl host,
            ShowcaseWidgetInstanceSettings widget,
            string blockId,
            bool snapshotOnly)
        {
            if (_disposed || host == null || widget == null)
            {
                return;
            }

            // A data refresh that carries the snapshot this host already projected has nothing
            // new for it. Every SnapshotChanged publishes a fresh snapshot instance, so
            // reference identity is the data identity (the projection service's per-snapshot
            // cache relies on the same fact). Configuration changes never take this exit.
            if (snapshotOnly && IsProjectedFrom(host, _overview.LatestSnapshot))
            {
                return;
            }

            if (!_applyQueued.Add(host))
            {
                // Already waiting: keep its place, but a configuration change must not be
                // downgraded to a snapshot-only request that a later guard could skip.
                if (!snapshotOnly)
                {
                    foreach (var pending in _applyQueue)
                    {
                        if (ReferenceEquals(pending.Host, host))
                        {
                            pending.SnapshotOnly = false;
                            break;
                        }
                    }
                }

                return;
            }

            if (_applyQueue.Count == 0)
            {
                _applyDrainStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _applyDrainApplied = 0;
                // Tracing only: name the dispatcher operations that run between the widget
                // passes (layout and render ticks, Loaded broadcasts, image completions).
                DispatcherOperationProbe.Arm(Logger, "showcase-fill", TimeSpan.FromSeconds(3));
            }

            _applyQueue.Enqueue(new WidgetApplyRequest
            {
                Host = host,
                Widget = widget,
                BlockId = blockId,
                SnapshotOnly = snapshotOnly
            });
            ScheduleApplyDrain();
        }

        private void ScheduleApplyDrain()
        {
            if (_applyDrainScheduled || _disposed || _applyQueue.Count == 0)
            {
                return;
            }

            _applyDrainScheduled = true;
            Dispatcher.BeginInvoke(
                new Action(DrainApplyQueue),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // Applies at most one widget, then re-posts itself. The snapshot is read when the
        // request runs, not when it was queued, so a widget reached after a newer snapshot
        // landed projects the newer one and is not queued twice for it.
        private void DrainApplyQueue()
        {
            _applyDrainScheduled = false;
            if (_disposed)
            {
                ClearApplyQueue();
                return;
            }

            try
            {
                while (_applyQueue.Count > 0)
                {
                    var request = _applyQueue.Dequeue();
                    _applyQueued.Remove(request.Host);
                    if (!IsLiveRequest(request))
                    {
                        continue;
                    }

                    var snapshot = _overview.LatestSnapshot;
                    if (request.SnapshotOnly && IsProjectedFrom(request.Host, snapshot))
                    {
                        continue;
                    }

                    if (PerfScope.PerfTracingEnabled && _applyDrainApplied > 0)
                    {
                        // Wall time between the previous pass ending and this one starting:
                        // render, Loaded broadcasts, image decodes and any other dispatcher work
                        // the drain yielded to. Large gaps mean the cost is outside the scopes.
                        var gapMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _applyPassEndedTicks) * 1000L /
                                    System.Diagnostics.Stopwatch.Frequency;
                        if (gapMs >= 50)
                        {
                            Logger.Debug($"[Showcase] apply gap ms={gapMs} before kind={request.Widget.Kind}");
                        }
                    }

                    ApplyWidgetProjection(request.Host, request.Widget, snapshot);
                    _applyPassEndedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    _applyDrainApplied++;
                    break;
                }
            }
            finally
            {
                if (_applyQueue.Count > 0)
                {
                    ScheduleApplyDrain();
                }
                else if (_applyDrainApplied > 0 && PerfScope.PerfTracingEnabled)
                {
                    var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _applyDrainStartedTicks) * 1000L /
                                    System.Diagnostics.Stopwatch.Frequency;
                    Logger.Debug($"[Showcase] apply drain done widgets={_applyDrainApplied} ms={elapsedMs}");
                    _applyDrainApplied = 0;
                }
            }
        }

        // A request is stale once its host no longer backs the block it was raised for (a
        // rebuild replaced the container) or, for an off-page host, once the cache dropped it.
        private bool IsLiveRequest(WidgetApplyRequest request)
        {
            if (request.BlockId != null)
            {
                return _blockVisuals.TryGetValue(request.BlockId, out var state) &&
                       ReferenceEquals(state?.Host, request.Host);
            }

            return !string.IsNullOrWhiteSpace(request.Widget?.InstanceId) &&
                   _hostCache.TryGetValue(request.Widget.InstanceId, out var live) &&
                   ReferenceEquals(live, request.Host);
        }

        private static bool IsProjectedFrom(ShowcaseWidgetControl host, OverviewDataSnapshot snapshot)
        {
            return snapshot != null && ReferenceEquals(host?.Projection?.Snapshot, snapshot);
        }

        private void ClearApplyQueue()
        {
            _applyQueue.Clear();
            _applyQueued.Clear();
        }

        // The one place a widget host receives its projection. Apply only sets the Projection
        // property; the body's template inflation, grid row and mosaic tile realization, and
        // chart plotting all run in the layout pass that follows, so a scope around Apply alone
        // would under-report. With tracing on, layout is forced here so the per-widget number
        // covers the whole cost; shipped builds leave layout to the dispatcher as before.
        //
        // A null snapshot means the overview has not produced one yet (a fresh open, before
        // RefreshViewAsync lands). Projecting against an empty stand-in would build a "No data"
        // body per widget only for SnapshotChanged to rebuild every one of them moments later,
        // so the host shows a loading caption instead and the snapshot pass does the first
        // real projection.
        private void ApplyWidgetProjection(
            ShowcaseWidgetControl host,
            ShowcaseWidgetInstanceSettings widget,
            OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                host.ShowLoadingPlaceholder();
                return;
            }

            var context = $"kind={widget?.Kind} id={widget?.InstanceId}";
            ShowcaseWidgetProjection projection;
            using (var build = PerfScope.Start(Logger, "Showcase.Widget.Build", thresholdMs: 10, context: context))
            {
                projection = ShowcaseWidgetProjectionService.Build(
                    snapshot,
                    Layout,
                    widget,
                    gridOptions: _settings.Persisted?.GridOptions);
                build?.SetContext(context + " " + DescribeProjection(projection));
            }

            using (PerfScope.Start(Logger, "Showcase.Widget.Apply", thresholdMs: 10, context: context))
            {
                host.Apply(projection);
            }

            if (PerfScope.PerfTracingEnabled)
            {
                using (var layout = PerfScope.Start(Logger, "Showcase.Widget.Layout", thresholdMs: 10, context: context))
                {
                    host.UpdateLayout();
                    layout?.SetContext(context + " " + DescribeRealizedRows(host));
                }
            }
        }

        // Tracing only: how many DataGrid rows the layout pass realized inside the host, so a
        // slow grid widget can be read as per-row cost versus a virtualization failure.
        private static string DescribeRealizedRows(ShowcaseWidgetControl host)
        {
            // The achievement grid control hosts several DataGrids (category list, drill, rows);
            // the populated one is the one whose rows were realized.
            var grid = Views.Helpers.VisualTreeHelpers.FindVisualChildren<DataGrid>(host)
                .OrderByDescending(candidate => candidate.Items.Count)
                .FirstOrDefault();
            if (grid == null)
            {
                return string.Empty;
            }

            var realized = 0;
            for (var index = 0; index < grid.Items.Count; index++)
            {
                if (grid.ItemContainerGenerator.ContainerFromIndex(index) != null)
                {
                    realized++;
                }
            }

            return $"items={grid.Items.Count} realized={realized} gridHeight={grid.ActualHeight:0}";
        }

        private static string DescribeProjection(ShowcaseWidgetProjection projection)
        {
            return
                $"rows={projection?.AchievementRows?.Count ?? 0} " +
                $"mosaic={projection?.MosaicAchievements?.Count ?? 0} " +
                $"games={projection?.Games?.Count ?? 0} " +
                $"chart={projection?.ChartEntries?.Count ?? 0} " +
                $"snapshot={projection?.Snapshot?.Achievements?.Count ?? 0}";
        }

        private Button CreateAddWidgetButton(ShowcaseBlockSettings block)
        {
            var addGlyph = new TextBlock
            {
                Text = "\uE710",
                FontSize = 34,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85
            };
            addGlyph.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Accent");
            // A fixed-size centered target instead of a block-filling one: clicking anywhere
            // else in the empty block only selects it, keeping the cut lines reachable.
            var add = new Button
            {
                Content = addGlyph,
                Tag = block,
                Width = 68,
                Height = 68,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(0),
                Visibility = EditLayoutButton.IsChecked == true
                    ? Visibility.Visible
                    : Visibility.Collapsed
            };
            add.SetResourceReference(Control.BackgroundProperty, "PlayAch.Brush.Overlay.Tint.08");
            add.Click += AddWidgetButton_Click;
            return add;
        }

        // Small glyph button pinned to a top corner of a widget block in edit mode.
        private Button CreateBlockActionButton(
            string glyph,
            string labelKey,
            HorizontalAlignment alignment)
        {
            var button = new Button
            {
                Content = glyph,
                HorizontalAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                ToolTip = Localize(labelKey),
                Visibility = Visibility.Collapsed
            };
            button.SetResourceReference(
                FrameworkElement.StyleProperty,
                "PlayAch.Showcase.BlockActionButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(button, Localize(labelKey));
            Panel.SetZIndex(button, 5);
            return button;
        }

        private void AddWidgetButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ShowcaseBlockSettings block)
            {
                OpenWidgetPicker(block, button);
            }
        }

        private void OpenWidgetPicker(ShowcaseBlockSettings block, FrameworkElement target)
        {
            var menu = new ContextMenu();
            foreach (var definition in ShowcaseWidgetCatalog.Definitions.Where(definition => !definition.Hidden))
            {
                var captured = definition;
                var item = WidgetPickerItem(
                    captured,
                    () =>
                    {
                        var widget = ShowcaseLayoutService.CreateWidget(Layout, captured.Kind);
                        if (!ShowcaseLayoutService.PlaceWidget(
                                Layout,
                                CurrentPage.PageId,
                                block.BlockId,
                                widget.InstanceId))
                        {
                            ShowcaseLayoutService.DeleteWidget(Layout, widget.InstanceId);
                            return;
                        }

                        SaveAndApplyBlocks();
                    });
                item.IsEnabled = !captured.SingleInstancePerPage ||
                    !CurrentPage.Blocks
                        .Select(candidate => FindWidget(candidate.WidgetInstanceId))
                        .Any(candidate => candidate?.Kind == captured.Kind);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = target ?? DashboardSurface;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private void MergeSelectedWith(string secondBlockId)
        {
            var selected = SelectedBlock;
            if (selected == null)
            {
                return;
            }

            var closure = ShowcaseLayoutService.GetMergeClosure(
                Layout,
                CurrentPage.PageId,
                selected.BlockId,
                secondBlockId);
            var widgets = closure
                .Select(block => FindWidget(block.WidgetInstanceId))
                .Where(widget => widget != null)
                .GroupBy(widget => widget.InstanceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (widgets.Count > 1 &&
                !Confirm("LOCPlayAch_Showcase_MergeDeleteConfirm"))
            {
                return;
            }

            var selectedWidgetId = selected.WidgetInstanceId;
            var adjacentWidgetId = closure.FirstOrDefault(block => string.Equals(
                block.BlockId,
                secondBlockId,
                StringComparison.OrdinalIgnoreCase))?.WidgetInstanceId;
            var preferredWidgetInstanceId = widgets.Any(widget => string.Equals(
                widget.InstanceId,
                selectedWidgetId,
                StringComparison.OrdinalIgnoreCase))
                ? selectedWidgetId
                : widgets.Any(widget => string.Equals(
                    widget.InstanceId,
                    adjacentWidgetId,
                    StringComparison.OrdinalIgnoreCase))
                    ? adjacentWidgetId
                    : widgets.FirstOrDefault()?.InstanceId;
            if (ShowcaseLayoutService.TryMergeWithFallback(
                    Layout,
                    CurrentPage.PageId,
                    selected.BlockId,
                    secondBlockId,
                    preferredWidgetInstanceId))
            {
                SaveAndApplyBlocks();
            }
        }

        private void Block_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            if (EditLayoutButton.IsChecked != true)
            {
                return;
            }

            if (sender is Border border &&
                border.Tag is ShowcaseBlockSettings block)
            {
                if (!string.Equals(_selectedBlockId, block.BlockId, StringComparison.OrdinalIgnoreCase))
                {
                    SelectBlock(block);
                }

                // Clicking a block is how focus comes back after it has been taken by a
                // widget's own control, so the editor shortcuts keep working.
                border.Focus();
            }
        }

        private void Block_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (EditLayoutButton.IsChecked == true &&
                sender is Border border &&
                border.Tag is ShowcaseBlockSettings block)
            {
                SelectBlock(block);
            }
        }

        private void SelectBlock(ShowcaseBlockSettings block)
        {
            if (block == null ||
                string.Equals(_selectedBlockId, block.BlockId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBlockId = block.BlockId;
            foreach (var state in _blockVisuals.Values)
            {
                RefreshBlockChrome(state);
            }

            UpdateLayoutHandles();
            Dispatcher.BeginInvoke(
                new Action(ReportDashboardClip),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Diagnostic for the edit-mode overhang (grippers and rulers) being cut off: names every
        // element from the dashboard grid up to the root that WPF is clipping after a selection
        // change, with the sizes that decided it. Silent while nothing is clipped.
        private void ReportDashboardClip()
        {
            if (_disposed || EditLayoutButton.IsChecked != true)
            {
                return;
            }

            var root = Window.GetWindow(this) as FrameworkElement ?? this;
            var gripper = _trackGrippers.FirstOrDefault();
            try
            {
                var gridBounds = DashboardGrid.TransformToAncestor(root)
                    .TransformBounds(new Rect(DashboardGrid.RenderSize));
                var gripperBounds = gripper == null
                    ? Rect.Empty
                    : gripper.TransformToAncestor(root).TransformBounds(new Rect(gripper.RenderSize));
                Logger.Info(
                    $"Dashboard geometry after selection: root {root.GetType().Name} {root.ActualWidth:0.##}x{root.ActualHeight:0.##}, " +
                    $"grid {gridBounds}, first gripper {gripperBounds} visible={gripper?.IsVisible}, " +
                    $"grid desired {DashboardGrid.DesiredSize.Width:0.##}x{DashboardGrid.DesiredSize.Height:0.##}");
            }
            catch (InvalidOperationException)
            {
                // Not connected to the root yet; the clip walk below still runs.
            }

            DependencyObject current = DashboardGrid;
            while (current is FrameworkElement element)
            {
                var layoutClip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element);
                if (layoutClip != null || element.Clip != null || element.ClipToBounds)
                {
                    Logger.Info(
                        $"Dashboard overhang clipped by {element.GetType().Name} '{element.Name}': " +
                        $"desired {element.DesiredSize.Width:0.##}x{element.DesiredSize.Height:0.##}, " +
                        $"rendered {element.RenderSize.Width:0.##}x{element.RenderSize.Height:0.##}, " +
                        $"layoutClip={(layoutClip == null ? "none" : layoutClip.Bounds.ToString())}, " +
                        $"clip={(element.Clip == null ? "none" : "set")}, clipToBounds={element.ClipToBounds}");
                }

                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }
        }

        private void Block_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (EditLayoutButton.IsChecked != true ||
                e.LeftButton != MouseButtonState.Pressed ||
                !(sender is Border border) ||
                !(border.Tag is ShowcaseBlockSettings block) ||
                string.IsNullOrWhiteSpace(block.WidgetInstanceId))
            {
                return;
            }

            var current = e.GetPosition(null);
            if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var widget = FindWidget(block.WidgetInstanceId);
            _dragSourceBlockId = block.BlockId;
            ShowDragStatus(
                block.BlockId,
                string.Format(
                    Localize("LOCPlayAch_Showcase_DragMoving"),
                    GetWidgetName(widget)),
                DragVisualKind.Source);
            try
            {
                DragDrop.DoDragDrop(
                    border,
                    new DataObject(WidgetDragFormat, block.WidgetInstanceId),
                    DragDropEffects.Move);
            }
            finally
            {
                ClearDragVisuals();
            }
        }

        private void Block_DragOver(object sender, DragEventArgs e)
        {
            if (EditLayoutButton.IsChecked != true ||
                !(sender is Border border) ||
                !(border.Tag is ShowcaseBlockSettings block) ||
                !(e.Data.GetData(WidgetDragFormat) is string instanceId))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var movingWidget = FindWidget(instanceId);
            var movingName = GetWidgetName(movingWidget);
            if (string.Equals(block.BlockId, _dragSourceBlockId, StringComparison.OrdinalIgnoreCase))
            {
                e.Effects = DragDropEffects.None;
                ShowDragStatus(
                    block.BlockId,
                    string.Format(
                        Localize("LOCPlayAch_Showcase_DropSame"),
                        movingName),
                    DragVisualKind.InvalidTarget);
            }
            else if (!ShowcaseLayoutService.CanPlaceWidget(
                         Layout,
                         CurrentPage.PageId,
                         block.BlockId,
                         instanceId))
            {
                e.Effects = DragDropEffects.None;
                ShowDragStatus(
                    block.BlockId,
                    Localize("LOCPlayAch_Showcase_DropUnavailable"),
                    DragVisualKind.InvalidTarget);
            }
            else
            {
                e.Effects = DragDropEffects.Move;
                var displacedWidget = FindWidget(block.WidgetInstanceId);
                var message = displacedWidget == null
                    ? string.Format(
                        Localize("LOCPlayAch_Showcase_DropMoveHere"),
                        movingName)
                    : string.Format(
                        Localize("LOCPlayAch_Showcase_DropSwap"),
                        movingName,
                        GetWidgetName(displacedWidget));
                ShowDragStatus(block.BlockId, message, DragVisualKind.ValidTarget);
            }

            e.Handled = true;
        }

        private void Block_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border && border.Tag is ShowcaseBlockSettings block)
            {
                RestoreSourceStatusOrHide(block.BlockId);
            }
        }

        private void Block_Drop(object sender, DragEventArgs e)
        {
            if (EditLayoutButton.IsChecked != true ||
                !(sender is Border border) ||
                !(border.Tag is ShowcaseBlockSettings block) ||
                !(e.Data.GetData(WidgetDragFormat) is string instanceId))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (!string.Equals(block.BlockId, _dragSourceBlockId, StringComparison.OrdinalIgnoreCase) &&
                ShowcaseLayoutService.PlaceWidget(
                    Layout,
                    CurrentPage.PageId,
                    block.BlockId,
                    instanceId))
            {
                // Stop the target clock before the visuals change. The drag source's finally
                // block also clears the states after WPF ends the operation.
                ClearDragVisuals();
                SaveAndApplyBlocks();
            }

            e.Handled = true;
        }

        private void ShowDragStatus(
            string blockId,
            string message,
            DragVisualKind visualKind)
        {
            if (!_blockVisuals.TryGetValue(blockId ?? string.Empty, out var state))
            {
                return;
            }

            var visualChanged = state.DragVisual != visualKind;
            if (state.Status.Text != message)
            {
                state.Status.Text = message ?? string.Empty;
            }

            state.DragVisual = visualKind;
            ApplyDragVisual(state, visualChanged);
            RefreshBlockChrome(state);
        }

        private void RestoreSourceStatusOrHide(string blockId)
        {
            if (!_blockVisuals.TryGetValue(blockId ?? string.Empty, out var state))
            {
                return;
            }

            if (string.Equals(blockId, _dragSourceBlockId, StringComparison.OrdinalIgnoreCase))
            {
                var movingWidget = FindWidget(state.Block.WidgetInstanceId);
                state.Status.Text = string.Format(
                    Localize("LOCPlayAch_Showcase_DragMoving"),
                    GetWidgetName(movingWidget));
                var visualChanged = state.DragVisual != DragVisualKind.Source;
                state.DragVisual = DragVisualKind.Source;
                ApplyDragVisual(state, visualChanged);
            }
            else
            {
                state.Status.Text = string.Empty;
                state.DragVisual = DragVisualKind.None;
                ApplyDragVisual(state, true);
            }

            RefreshBlockChrome(state);
        }

        private void ClearDragVisuals()
        {
            _dragSourceBlockId = null;
            foreach (var state in _blockVisuals.Values)
            {
                state.Status.Text = string.Empty;
                state.DragVisual = DragVisualKind.None;
                ApplyDragVisual(state, true);
                RefreshBlockChrome(state);
            }
        }

        private static void ApplyDragVisual(BlockVisualState state, bool visualChanged)
        {
            if (state?.Glow == null || state.StatusPanel == null)
            {
                return;
            }

            if (state.DragVisual == DragVisualKind.None)
            {
                state.Glow.BeginAnimation(UIElement.OpacityProperty, null);
                state.Glow.Opacity = 0;
                state.Glow.Visibility = Visibility.Collapsed;
                state.StatusPanel.Visibility = Visibility.Collapsed;
                return;
            }

            state.Glow.Visibility = Visibility.Visible;
            state.StatusPanel.Visibility = Visibility.Visible;
            if (state.DragVisual == DragVisualKind.ValidTarget)
            {
                if (visualChanged)
                {
                    state.Glow.BeginAnimation(
                        UIElement.OpacityProperty,
                        new DoubleAnimation
                        {
                            From = 0.12,
                            To = 0.30,
                            Duration = TimeSpan.FromMilliseconds(650),
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = new SineEase
                            {
                                EasingMode = EasingMode.EaseInOut
                            }
                        },
                        HandoffBehavior.SnapshotAndReplace);
                }

                return;
            }

            state.Glow.BeginAnimation(UIElement.OpacityProperty, null);
            state.Glow.Opacity = state.DragVisual == DragVisualKind.Source ? 0.12 : 0.06;
        }

        private void RefreshBlockChrome(BlockVisualState state)
        {
            if (state?.Container == null || state.Block == null)
            {
                return;
            }

            var editing = EditLayoutButton.IsChecked == true;
            var isDragSource = string.Equals(
                state.Block.BlockId,
                _dragSourceBlockId,
                StringComparison.OrdinalIgnoreCase);
            var isValidTarget = state.DragVisual == DragVisualKind.ValidTarget;
            var emphasized = isDragSource || state.DragVisual != DragVisualKind.None ||
                (editing && string.Equals(
                    state.Block.BlockId,
                    _selectedBlockId,
                    StringComparison.OrdinalIgnoreCase));
            state.EditChrome.Visibility = editing || isValidTarget
                ? Visibility.Visible
                : Visibility.Collapsed;
            state.EditChrome.BorderThickness = isValidTarget
                ? new Thickness(3)
                : new Thickness(2);
            state.EditChrome.SetResourceReference(
                Border.BorderBrushProperty,
                emphasized ? "PlayAch.Brush.Accent" : "PlayAch.Brush.Border");

            var isSelected = string.Equals(
                state.Block.BlockId,
                _selectedBlockId,
                StringComparison.OrdinalIgnoreCase);
            var actionVisibility = editing && isSelected &&
                !string.IsNullOrWhiteSpace(state.Block.WidgetInstanceId)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (state.SettingsButton != null)
            {
                state.SettingsButton.Visibility = actionVisibility;
            }

            if (state.DeleteButton != null)
            {
                state.DeleteButton.Visibility = actionVisibility;
            }
        }

        private void SelectPage(ShowcasePageSettings page)
        {
            if (page == null)
            {
                return;
            }

            Layout.LastSelectedPageId = page.PageId;
            SaveAndRebuild();
        }

        // Widget moves and swaps, merges, and cuts all keep the page and its grid size, so the
        // existing block containers are repositioned and the existing widget controls re-parented
        // instead of being recreated - rebuilding would re-inflate every data grid and chart on the
        // page. Only blocks that appeared get a new container, and only blocks that disappeared
        // lose theirs. Returns false before touching anything when the page differs, so the caller
        // can fall back to a full rebuild.
        private bool TryApplyBlocksInPlace()
        {
            if (_disposed || _blockVisuals.Count == 0)
            {
                return false;
            }

            var rowCount = PageRowCount;
            var columnCount = PageColumnCount;
            if (!string.Equals(_builtPageId, CurrentPage?.PageId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A row or column insert/delete changes the track counts. The blocks still map onto
            // their containers by id, so only the definitions and the per-track edit chrome need
            // rebuilding; a full rebuild would detach and re-lay-out every widget on the page.
            if (DashboardGrid.RowDefinitions.Count != rowCount ||
                DashboardGrid.ColumnDefinitions.Count != columnCount)
            {
                ResizeTrackDefinitions(rowCount, columnCount);
            }

            var blocks = CurrentPage.Blocks;
            ApplyTrackWeights(vertical: false, ShowcaseLayoutService.NormalizeTrackWeights(CurrentPage.RowWeights, rowCount));
            ApplyTrackWeights(vertical: true, ShowcaseLayoutService.NormalizeTrackWeights(CurrentPage.ColumnWeights, columnCount));
            ClearMergePreviewGlow();
            HideCutGhost();

            var hostsByInstanceId = new Dictionary<string, ShowcaseWidgetControl>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in _blockVisuals.Values)
            {
                if (state.Host != null && !string.IsNullOrWhiteSpace(state.Widget?.InstanceId))
                {
                    hostsByInstanceId[state.Widget.InstanceId] = state.Host;
                }
            }

            var liveIds = new HashSet<string>(blocks.Select(block => block.BlockId), StringComparer.OrdinalIgnoreCase);
            foreach (var removedId in _blockVisuals.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                var removed = _blockVisuals[removedId];
                // Detach the content first so a surviving widget's control can move to its new block.
                LayersOf(removed.Container)?.Children.Clear();
                DashboardGrid.Children.Remove(removed.Container);
                _blockVisuals.Remove(removedId);
            }

            if (EditLayoutButton.IsChecked == true && !liveIds.Contains(_selectedBlockId ?? string.Empty))
            {
                _selectedBlockId = blocks.FirstOrDefault()?.BlockId;
            }

            foreach (var block in blocks)
            {
                if (_blockVisuals.TryGetValue(block.BlockId, out var existing))
                {
                    existing.Block = block;
                    existing.Container.Tag = block;
                }
                else
                {
                    // An empty shell: the assignment pass below installs its widget control or +.
                    var created = CreateBlockContainer(
                        new ShowcaseBlockSettings
                        {
                            BlockId = block.BlockId,
                            Row = block.Row,
                            Column = block.Column,
                            RowSpan = block.RowSpan,
                            ColumnSpan = block.ColumnSpan
                        });
                    created.Tag = block;
                    _blockVisuals[block.BlockId].Block = block;
                    // Below the ZIndex'd overlays either way; first keeps child order stable.
                    DashboardGrid.Children.Insert(0, created);
                }

                var container = _blockVisuals[block.BlockId].Container;
                Grid.SetRow(container, block.Row);
                Grid.SetColumn(container, block.Column);
                Grid.SetRowSpan(container, block.RowSpan);
                Grid.SetColumnSpan(container, block.ColumnSpan);
            }

            var assignments = new List<(BlockVisualState State, ShowcaseWidgetInstanceSettings Widget, ShowcaseWidgetControl Host)>();
            foreach (var block in blocks)
            {
                var state = _blockVisuals[block.BlockId];
                var widget = FindWidget(block.WidgetInstanceId);
                ShowcaseWidgetControl host = null;
                if (widget != null &&
                    !hostsByInstanceId.TryGetValue(widget.InstanceId, out host))
                {
                    // A widget with no built control yet (just added): build only this one and
                    // let the rest of the page keep its existing controls.
                    host = CreateWidgetHost(block, widget);
                    hostsByInstanceId[widget.InstanceId] = host;
                }

                assignments.Add((state, widget, host));
            }

            foreach (var assignment in assignments)
            {
                var layers = LayersOf(assignment.State.Container);
                if (layers == null || layers.Children.Count == 0)
                {
                    return false;
                }

                var currentContent = layers.Children[0];
                UIElement nextContent;
                if (assignment.Host != null)
                {
                    // Detach from whichever block currently owns it before re-parenting.
                    if (assignment.Host.Parent is Grid previousLayers && !ReferenceEquals(previousLayers, layers))
                    {
                        previousLayers.Children.Remove(assignment.Host);
                    }

                    assignment.Host.IsHitTestVisible = EditLayoutButton.IsChecked != true;
                    nextContent = assignment.Host;
                    assignment.State.AddButton = null;
                }
                else
                {
                    var add = assignment.State.AddButton ?? CreateAddWidgetButton(assignment.State.Block);
                    add.Tag = assignment.State.Block;
                    add.Visibility = EditLayoutButton.IsChecked == true
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    assignment.State.AddButton = add;
                    nextContent = add;
                }

                if (!ReferenceEquals(currentContent, nextContent))
                {
                    layers.Children.RemoveAt(0);
                    layers.Children.Insert(0, nextContent);
                }

                assignment.State.Host = assignment.Host;
                assignment.State.Widget = assignment.Widget;
                RefreshBlockChrome(assignment.State);
            }

            UpdateLayoutHandles();
            ApplyPendingCutVisual();
            FocusSelectedBlock();
            _layoutSignature = ComputeLayoutSignature();
            return true;
        }

        // Normalizes, persists, and broadcasts the layout. The flag keeps our own broadcast from
        // bouncing back in as an external change and rebuilding a second time.
        private void SaveAndPublish()
        {
            RecordHistoryPoint();
            ShowcaseLayoutService.Normalize(Layout);
            ShowcaseLayoutService.PruneOrphanedWidgets(Layout);
            ShowcaseGridSurfaces.PruneOrphaned(_settings.Persisted?.GridOptions, Layout);
            PruneHostCache();
            _persist();
            // Stored profile images are shared by content, so a file goes only once no widget
            // (on any page or the start page) refers to it any more.
            PlayniteAchievementsPlugin.Instance?.ShowcaseImageStore?.Prune(Layout);
            _publishingConfigurationChange = true;
            try
            {
                ShowcaseConfigurationEvents.RaiseChanged();
            }
            finally
            {
                _publishingConfigurationChange = false;
            }
        }

        // Called at the head of every publish, when the baseline still holds the pre-mutation
        // layout. That ordering is what lets one hook cover every structural edit.
        private void RecordHistoryPoint()
        {
            if (_restoringHistory)
            {
                return;
            }

            if (_historyBaseline != null)
            {
                _undoHistory.AddLast(_historyBaseline);
                while (_undoHistory.Count > MaxHistoryDepth)
                {
                    _undoHistory.RemoveFirst();
                }
            }

            _redoHistory.Clear();
            _historyBaseline = Layout.Clone();
        }

        private void Undo()
        {
            if (_undoHistory.Count == 0)
            {
                return;
            }

            var restore = _undoHistory.Last.Value;
            _undoHistory.RemoveLast();
            _redoHistory.Push(Layout.Clone());
            RestoreLayout(restore);
        }

        private void Redo()
        {
            if (_redoHistory.Count == 0)
            {
                return;
            }

            var restore = _redoHistory.Pop();
            _undoHistory.AddLast(Layout.Clone());
            RestoreLayout(restore);
        }

        // Copies the layout members back into the live ShowcaseSettings instance rather than
        // replacing it: Layout is a property over Persisted.Showcase and other surfaces hold
        // that instance. Pin collections and the profile are left alone because they are not
        // edited through the dashboard and are not part of what undo covers.
        private void RestoreLayout(ShowcaseSettings snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            _restoringHistory = true;
            try
            {
                var live = Layout;
                live.LayoutVersion = snapshot.LayoutVersion;
                live.LastSelectedPageId = snapshot.LastSelectedPageId;
                live.Pages = (snapshot.Pages ?? new List<ShowcasePageSettings>())
                    .Select(page => page.Clone())
                    .ToList();
                live.WidgetInstances = (snapshot.WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
                    .Select(widget => widget.Clone())
                    .ToList();

                // The cut target may no longer exist in the restored layout.
                if (FindWidget(_cutInstanceId) == null)
                {
                    _cutInstanceId = null;
                }

                if (FindWidget(SelectedBlock?.WidgetInstanceId) == null &&
                    CurrentPage.Blocks.All(block => !string.Equals(
                        block.BlockId,
                        _selectedBlockId,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    _selectedBlockId = CurrentPage.Blocks.FirstOrDefault()?.BlockId;
                }

                _historyBaseline = Layout.Clone();
                SaveAndPublish();
                Rebuild();
            }
            finally
            {
                _restoringHistory = false;
            }
        }

        // Persists a widget add/move/swap/delete, merge, or cut, keeping the built widget controls in place.
        private void SaveAndApplyBlocks()
        {
            SaveAndPublish();
            if (!TryApplyBlocksInPlace())
            {
                Rebuild();
            }
        }

        private void SaveAndRebuild()
        {
            SaveAndPublish();
            Rebuild();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // The constructor already built the dashboard; rebuilding here would re-project
            // every widget a second time before the first paint.
            if (_blockVisuals.Count == 0)
            {
                Rebuild();
            }

            // The baseline has to exist before the first edit, because RecordHistoryPoint
            // pushes what it finds here as the state to undo back to.
            if (_historyBaseline == null)
            {
                _historyBaseline = Layout.Clone();
            }
        }

        private void Overview_SnapshotChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(QueueSnapshotRefresh));
        }

        private void QueueSnapshotRefresh()
        {
            if (_disposed)
            {
                return;
            }

            if (_snapshotRefreshTimer.IsEnabled)
            {
                _snapshotRefreshPending = true;
                return;
            }

            RefreshWidgetData(includeCachedHosts: true);
            _snapshotRefreshTimer.Start();
        }

        private void SnapshotRefreshTimer_Tick(object sender, EventArgs e)
        {
            if (_disposed)
            {
                _snapshotRefreshTimer.Stop();
                return;
            }

            if (_snapshotRefreshPending)
            {
                // Keep the timer running so the next burst stays windowed.
                _snapshotRefreshPending = false;
                RefreshWidgetData(includeCachedHosts: true);
            }
            else
            {
                _snapshotRefreshTimer.Stop();
            }
        }

        // A snapshot change carries new data but the same layout, so update each widget host's
        // projection in place rather than tearing down and rebuilding every block container (which
        // would re-run the drag wiring and recreate every control). The per-kind view models update
        // their bindings without discarding their visual tree.
        //
        // This only enqueues; the apply queue projects one widget per dispatcher pass, visible
        // blocks first in reading order. includeCachedHosts re-projects the off-page hosts as
        // well; see the comment on that loop for why only a snapshot change needs it.
        private void RefreshWidgetData(bool includeCachedHosts)
        {
            if (_disposed)
            {
                return;
            }

            if (_blockVisuals.Count == 0)
            {
                BuildDashboard();
                return;
            }

            var queued = new HashSet<ShowcaseWidgetControl>();
            foreach (var block in CurrentPage.Blocks.OrderBy(block => block.Row).ThenBy(block => block.Column))
            {
                if (!_blockVisuals.TryGetValue(block.BlockId, out var visual) ||
                    visual?.Host == null ||
                    visual.Widget == null)
                {
                    continue;
                }

                QueueWidgetApply(visual.Host, visual.Widget, block.BlockId, snapshotOnly: includeCachedHosts);
                queued.Add(visual.Host);
            }

            // Re-project the cached hosts for the other pages too, but only when the snapshot
            // itself moved. Their projections carry the snapshot they last saw, so leaving them
            // would pin one whole snapshot generation per visited page (the process is 32-bit);
            // re-projecting from the current snapshot keeps every host on the single live
            // generation while their visuals stay warm for instant page switches. The
            // per-snapshot derived cache makes the extra projections cheap.
            //
            // A configuration change leaves the snapshot alone, so the cached hosts already hold
            // the live generation and there is nothing to unpin. Re-projecting them there buys
            // nothing and costs a full uncached library rescan plus a body rebuild per off-page
            // widget, so the per-edit cost would grow with the number of pages visited this
            // session. Whichever page is shown next rebuilds through CreateWidgetHost, which
            // always applies a freshly built projection.
            if (!includeCachedHosts)
            {
                return;
            }

            var widgetsById = Layout.WidgetInstances
                .Where(widget => !string.IsNullOrWhiteSpace(widget?.InstanceId))
                .ToDictionary(widget => widget.InstanceId, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _hostCache)
            {
                if (entry.Value == null ||
                    queued.Contains(entry.Value) ||
                    entry.Value.Projection == null ||
                    !widgetsById.TryGetValue(entry.Key, out var widget))
                {
                    continue;
                }

                QueueWidgetApply(entry.Value, widget, blockId: null, snapshotOnly: true);
            }
        }

        private void ShowcaseConfigurationEvents_Changed(object sender, EventArgs e)
        {
            if (!_publishingConfigurationChange)
            {
                Dispatcher.BeginInvoke(new Action(RefreshAfterExternalConfigurationChange));
            }
        }

        // External configuration changes (pin toggles, widget options, row-menu edits) usually keep
        // the block layout intact, so refresh projections in place; a full rebuild - which recreates
        // every widget control, including embedded data grids - only runs when the layout signature
        // actually changed.
        private void RefreshAfterExternalConfigurationChange()
        {
            if (_disposed)
            {
                return;
            }

            EnsureLayout();
            EnsureSnapshotCoversLayout();
            if (string.Equals(ComputeLayoutSignature(), _layoutSignature, StringComparison.Ordinal))
            {
                RefreshWidgetData(includeCachedHosts: false);
                return;
            }

            Rebuild();
        }

        // Every other widget option re-projects from the snapshot already in hand. Unlock Next is
        // the exception: its locked achievements are not in the snapshot at all, and the overview
        // builder only hydrates them when a widget asks. Switching a mosaic to that source is
        // therefore the one option edit that needs the snapshot rebuilt.
        //
        // A new achievement pin is the same case: the snapshot only holds unlocked rows plus the
        // locked pinned rows hydrated at build time, so a pin the build never saw would render as
        // a bare placeholder until the next rebuild.
        private void EnsureSnapshotCoversLayout()
        {
            var snapshot = _overview.LatestSnapshot;
            var needsUnlockNextPool = ShowcaseWidgetOptions.RequiresUnlockNextPool(Layout) &&
                                      snapshot?.UnlockNextPoolBuilt != true;
            var needsPinHydration = snapshot != null && !snapshot.HasSeenAchievementPins(Layout);
            if (!needsUnlockNextPool && !needsPinHydration)
            {
                return;
            }

            _ = _overview.RefreshViewAsync();
        }

        private void PageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingPageSelector && PageSelector.SelectedItem is ShowcasePageSettings page)
            {
                SelectPage(page);
            }
        }

        private void PreviousPageButton_Click(object sender, RoutedEventArgs e) => MovePage(-1);

        private void NextPageButton_Click(object sender, RoutedEventArgs e) => MovePage(1);

        // Editor shortcuts. Scoped to this control rather than the window so they cannot
        // compete with the data grids on the other overview tabs, and gated on edit mode so
        // the dashboard stays inert while it is being read rather than arranged.
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (EditLayoutButton.IsChecked != true ||
                (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            // A text box inside a widget (a grid's search field) owns its own Ctrl shortcuts.
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.C:
                    CopySelectedWidget();
                    break;
                case Key.X:
                    CutSelectedWidget();
                    break;
                case Key.V:
                    PasteWidget();
                    break;
                case Key.Z:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    {
                        Redo();
                    }
                    else
                    {
                        Undo();
                    }

                    break;
                case Key.Y:
                    Redo();
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        // Nothing focuses the dashboard on its own, so the shortcuts would need a Tab press
        // first. Focusing the selected block on entering edit mode also gives the keyboard a
        // sensible starting point for arrowing between blocks.
        private void FocusSelectedBlock()
        {
            if (EditLayoutButton.IsChecked != true)
            {
                return;
            }

            if (_selectedBlockId != null &&
                _blockVisuals.TryGetValue(_selectedBlockId, out var state) &&
                state?.Container != null &&
                state.Container.Focus())
            {
                return;
            }

            // No block took it (an empty page, or a container not yet loaded), so hold focus
            // on the control itself rather than letting it escape the dashboard.
            Focus();
        }

        private void EditLayoutButton_Changed(object sender, RoutedEventArgs e)
        {
            // The button shows the mode a click switches to: pencil (edit) or eye (view).
            var switchingToEdit = EditLayoutButton.IsChecked != true;
            EditLayoutButton.Content = switchingToEdit ? "\uE70F" : "\uE890";
            EditLayoutButton.ToolTip = Localize(switchingToEdit
                ? "LOCPlayAch_Common_Edit"
                : "LOCPlayAch_Common_View");
            if (EditLayoutButton.IsChecked == true && string.IsNullOrWhiteSpace(_selectedBlockId))
            {
                _selectedBlockId = CurrentPage.Blocks.FirstOrDefault()?.BlockId;
            }

            if (_blockVisuals.Count == 0)
            {
                BuildDashboard();
                return;
            }

            // Toggling edit mode only changes block chrome and affordances; a full rebuild would
            // recreate every widget control (including the embedded data grids) and stall the click.
            UpdateTrackGripperVisibility();
            var editing = EditLayoutButton.IsChecked == true;
            foreach (var state in _blockVisuals.Values)
            {
                state.Container.Focusable = editing;
                if (state.Host != null)
                {
                    state.Host.IsHitTestVisible = !editing;
                }

                if (state.AddButton != null)
                {
                    state.AddButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
                }

                RefreshBlockChrome(state);
            }

            // After the loop: the handle rebuild may suppress the selected empty block's own
            // + button in favor of the overlay copy, and the loop above resets visibility.
            UpdateLayoutHandles();
            FocusSelectedBlock();
        }

        private ShowcaseBlockSettings SelectedBlock => CurrentPage.Blocks.FirstOrDefault(block =>
            string.Equals(block.BlockId, _selectedBlockId, StringComparison.OrdinalIgnoreCase));

        private void SplitBlock(
            string pageId,
            string blockId,
            bool vertical,
            int gridLine)
        {
            ApplySplit(
                pageId,
                blockId,
                () => ShowcaseLayoutService.TrySplit(
                    Layout,
                    pageId,
                    blockId,
                    vertical,
                    gridLine));
        }

        private void ApplySplit(string pageId, string blockId, Func<bool> split)
        {
            var page = Layout.Pages.FirstOrDefault(candidate => string.Equals(
                candidate?.PageId,
                pageId,
                StringComparison.OrdinalIgnoreCase));
            var block = page?.Blocks.FirstOrDefault(candidate => string.Equals(
                candidate?.BlockId,
                blockId,
                StringComparison.OrdinalIgnoreCase));
            var widgetInstanceId = block?.WidgetInstanceId;
            if (block == null || split == null || !split())
            {
                return;
            }

            page = Layout.Pages.FirstOrDefault(candidate => string.Equals(
                candidate?.PageId,
                pageId,
                StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(widgetInstanceId))
            {
                _selectedBlockId = page?.Blocks.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.WidgetInstanceId,
                        widgetInstanceId,
                        StringComparison.OrdinalIgnoreCase))?.BlockId ?? blockId;
            }
            else
            {
                _selectedBlockId = page?.Blocks.FirstOrDefault(candidate => string.Equals(
                    candidate.BlockId,
                    blockId,
                    StringComparison.OrdinalIgnoreCase))?.BlockId;
            }

            SaveAndApplyBlocks();
        }

        private List<ShowcaseBlockSettings> FindAdjacentBlocks(
            ShowcaseBlockSettings block,
            int rowDirection,
            int columnDirection)
        {
            if (block == null)
            {
                return new List<ShowcaseBlockSettings>();
            }

            return CurrentPage.Blocks
                .Where(candidate => candidate != null && !ReferenceEquals(candidate, block))
                .Where(candidate =>
                    rowDirection < 0
                        ? candidate.Row + candidate.RowSpan == block.Row &&
                          RangesOverlap(candidate.Column, candidate.ColumnSpan, block.Column, block.ColumnSpan)
                        : rowDirection > 0
                            ? block.Row + block.RowSpan == candidate.Row &&
                              RangesOverlap(candidate.Column, candidate.ColumnSpan, block.Column, block.ColumnSpan)
                            : columnDirection < 0
                                ? candidate.Column + candidate.ColumnSpan == block.Column &&
                                  RangesOverlap(candidate.Row, candidate.RowSpan, block.Row, block.RowSpan)
                                : block.Column + block.ColumnSpan == candidate.Column &&
                                  RangesOverlap(candidate.Row, candidate.RowSpan, block.Row, block.RowSpan))
                .OrderByDescending(candidate => rowDirection == 0
                    ? Overlap(candidate.Row, candidate.RowSpan, block.Row, block.RowSpan)
                    : Overlap(candidate.Column, candidate.ColumnSpan, block.Column, block.ColumnSpan))
                .ThenBy(candidate => candidate.Row)
                .ThenBy(candidate => candidate.Column)
                .ToList();
        }

        private void PageActionsButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = PageActionsButton,
                Placement = PlacementMode.Bottom
            };
            var add = new MenuItem
            {
                Header = Localize("LOCPlayAch_Showcase_AddPage")
            };
            add.Items.Add(PageTemplateItem(ShowcasePageTemplate.Blank));
            add.Items.Add(PageTemplateItem(ShowcasePageTemplate.Analytics));
            add.Items.Add(PageTemplateItem(ShowcasePageTemplate.Collection));
            menu.Items.Add(add);
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_DuplicatePage"),
                () =>
                {
                    ShowcaseLayoutService.DuplicatePage(
                        Layout,
                        CurrentPage.PageId,
                        Localize("LOCPlayAch_Showcase_CopySuffix"),
                        _settings.Persisted?.GridOptions);
                    SaveAndRebuild();
                }));
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_RenamePage"),
                RenameCurrentPage));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_ExportPage"),
                ExportCurrentPage));
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_ImportPage"),
                ImportPageFromFile));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_MovePageLeft"),
                () =>
                {
                    ShowcaseLayoutService.MovePage(Layout, CurrentPage.PageId, -1);
                    SaveAndRebuild();
                }));
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_MovePageRight"),
                () =>
                {
                    ShowcaseLayoutService.MovePage(Layout, CurrentPage.PageId, 1);
                    SaveAndRebuild();
                }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_ResetTrackSizes"),
                ResetTrackSizes));
            menu.Items.Add(MenuItem(
                Localize("LOCPlayAch_Showcase_ResetPage"),
                () =>
                {
                    if (!Confirm("LOCPlayAch_Showcase_ResetPageConfirm"))
                    {
                        return;
                    }

                    ShowcaseLayoutService.ResetPage(Layout, CurrentPage.PageId);
                    SaveAndRebuild();
                }));
            var delete = MenuItem(
                Localize("LOCPlayAch_Showcase_DeletePage"),
                DeleteCurrentPage);
            delete.IsEnabled = Layout.Pages.Count > 1;
            menu.Items.Add(delete);
            menu.IsOpen = true;
        }

        private MenuItem PageTemplateItem(ShowcasePageTemplate template)
        {
            return MenuItem(
                Localize($"LOCPlayAch_Showcase_Template_{template}"),
                () =>
                {
                    ShowcaseLayoutService.AddPage(
                        Layout,
                        template,
                        Localize($"LOCPlayAch_Showcase_Template_{template}"));
                    SaveAndRebuild();
                });
        }

        private void RenameCurrentPage()
        {
            var result = _api?.Dialogs?.SelectString(
                Localize("LOCPlayAch_Showcase_RenamePrompt"),
                Localize("LOCPlayAch_Showcase_RenamePage"),
                CurrentPage.Name);
            var name = result?.Result == true ? result.SelectedString : null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                // A rename only shows in the page selector; the built blocks stay as they are.
                ShowcaseLayoutService.RenamePage(Layout, CurrentPage.PageId, name);
                SaveAndPublish();
                UpdatePageSelector();
                _layoutSignature = ComputeLayoutSignature();
            }
        }

        private void OpenWidgetSettings(ShowcaseWidgetInstanceSettings widget)
        {
            if (ShowcaseWidgetSettingsDialog.Show(widget))
            {
                SaveAndPublish();
                EnsureSnapshotCoversLayout();
                ReprojectWidget(widget.InstanceId);
            }
        }

        // The settings dialog edits one widget's title, options, and profile, never its kind or
        // placement, so only that widget's host needs a fresh projection; the rest of the page
        // and its block containers stay as built.
        private void ReprojectWidget(string instanceId)
        {
            var widget = Layout.WidgetInstances.FirstOrDefault(instance =>
                string.Equals(instance?.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            if (widget == null ||
                !_hostCache.TryGetValue(widget.InstanceId, out var host) ||
                host == null)
            {
                Rebuild();
                return;
            }

            QueueWidgetApply(host, widget, blockId: null, snapshotOnly: false);
        }

        // Captures the current page as it renders on screen and saves it as a PNG the user
        // picks a location for. Rendering through a VisualBrush (instead of the element
        // directly) avoids the layout-offset blank margin RenderTargetBitmap adds, and the
        // capture is composed over the grid surface brush so gaps are not transparent.
        private void CapturePageButton_Click(object sender, RoutedEventArgs e)
        {
            if (DashboardGrid.ActualWidth < 1 || DashboardGrid.ActualHeight < 1)
            {
                return;
            }

            // WinForms picker (repo convention on net462) so a default filename can be offered.
            string path;
            using (var dialog = new System.Windows.Forms.SaveFileDialog
            {
                FileName = "PlayniteAchievementsShowcase.png",
                Filter = "PNG|*.png",
                OverwritePrompt = true
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
                    string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    return;
                }

                path = dialog.FileName;
            }

            try
            {
                var width = DashboardGrid.ActualWidth;
                var height = DashboardGrid.ActualHeight;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(DashboardGrid);
                var bounds = new Rect(0, 0, width, height);
                var visual = new System.Windows.Media.DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    if (TryFindResource("PlayAch.Brush.GridSurface")
                        is System.Windows.Media.Brush backdrop)
                    {
                        context.DrawRectangle(backdrop, null, bounds);
                    }

                    context.DrawRectangle(
                        new System.Windows.Media.VisualBrush(DashboardGrid),
                        null,
                        bounds);
                }

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(width * dpi.DpiScaleX),
                    (int)Math.Ceiling(height * dpi.DpiScaleY),
                    dpi.PixelsPerInchX,
                    dpi.PixelsPerInchY,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = System.IO.File.Create(path))
                {
                    encoder.Save(stream);
                }
            }
            catch (Exception exception)
            {
                _api?.Dialogs?.ShowErrorMessage(exception.Message, string.Empty);
            }
        }

        // Writes the current page to a .pashowcase package: layout and appearance only, no pin
        // collections or control-bar filter state, and of a profile card only its background.
        private void ExportCurrentPage()
        {
            var page = CurrentPage;
            if (page == null)
            {
                return;
            }

            string path;
            using (var dialog = new System.Windows.Forms.SaveFileDialog
            {
                FileName = ShowcasePagePortableStore.SuggestFileName(page.Name),
                Filter = "*" + ShowcasePagePortableStore.PackageFileExtension +
                         "|*" + ShowcasePagePortableStore.PackageFileExtension,
                DefaultExt = ShowcasePagePortableStore.PackageFileExtension.TrimStart('.'),
                OverwritePrompt = true
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
                    string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    return;
                }

                path = ShowcasePagePortableStore.NormalizeExportPath(dialog.FileName);
            }

            try
            {
                ShowcasePagePortableStore.Write(
                    path,
                    ShowcasePagePortableStore.BuildPortable(
                        Layout,
                        _settings.Persisted?.GridOptions,
                        page.PageId));
                _api?.Dialogs?.ShowMessage(
                    Localize("LOCPlayAch_Status_Succeeded") + "\n" + path,
                    Localize("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Failed exporting showcase page.");
                ShowPortableFailure(exception);
            }
        }

        // Adds the page from a .pashowcase package after the current page. The import goes
        // through the normal save path, so it normalizes the page and can be undone.
        private void ImportPageFromFile()
        {
            string path;
            using (var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "*" + ShowcasePagePortableStore.PackageFileExtension +
                         "|*" + ShowcasePagePortableStore.PackageFileExtension +
                         ";*" + ShowcasePagePortableStore.PackageFileExtension + ".zip",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
                    string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    return;
                }

                path = dialog.FileName;
            }

            ShowcasePagePortableFile portable = null;
            try
            {
                portable = ShowcasePagePortableStore.Read(path);
                var imageStore = PlayniteAchievementsPlugin.Instance?.ShowcaseImageStore;
                ShowcasePagePortableStore.ApplyPortable(
                    Layout,
                    _settings.Persisted?.GridOptions,
                    portable,
                    CurrentPage?.PageId,
                    extracted => imageStore?.Import(extracted));
                SaveAndRebuild();
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Failed importing showcase page.");
                ShowPortableFailure(exception);
            }
            finally
            {
                ShowcasePagePortableStore.DeleteExtractedImages(portable);
            }
        }

        private void ShowPortableFailure(Exception exception)
        {
            _api?.Dialogs?.ShowMessage(
                string.Format(Localize("LOCPlayAch_Status_Failed"), exception.Message),
                Localize("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void DeleteCurrentPage()
        {
            if (Layout.Pages.Count <= 1 ||
                !Confirm("LOCPlayAch_Showcase_DeletePageConfirm"))
            {
                return;
            }

            ShowcaseLayoutService.DeletePage(Layout, CurrentPage.PageId);
            SaveAndRebuild();
        }

        private void CopySelectedWidget()
        {
            var widget = FindWidget(SelectedBlock?.WidgetInstanceId);
            if (widget == null)
            {
                return;
            }

            _clipboardWidget = widget.Clone();
            SetPendingCut(null);
        }

        // Explorer-style: the widget stays put until a paste lands, so cut-then-paste can move
        // the instance instead of destroying and recreating it.
        private void CutSelectedWidget()
        {
            var widget = FindWidget(SelectedBlock?.WidgetInstanceId);
            if (widget == null)
            {
                return;
            }

            _clipboardWidget = widget.Clone();
            SetPendingCut(widget.InstanceId);
        }

        private void PasteWidget()
        {
            var block = SelectedBlock;
            if (block == null)
            {
                return;
            }

            var cutWidget = FindWidget(_cutInstanceId);
            if (cutWidget != null)
            {
                MoveCutWidgetInto(block, cutWidget);
                return;
            }

            if (_clipboardWidget == null)
            {
                return;
            }

            var definition = ShowcaseWidgetCatalog.Get(_clipboardWidget.Kind);
            if (definition != null &&
                definition.SingleInstancePerPage &&
                CurrentPage.Blocks
                    .Where(candidate => !string.Equals(
                        candidate.BlockId,
                        block.BlockId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(candidate => FindWidget(candidate.WidgetInstanceId))
                    .Any(candidate => candidate?.Kind == _clipboardWidget.Kind))
            {
                return;
            }

            if (!ConfirmReplacingWidgetIn(block))
            {
                return;
            }

            var copy = _clipboardWidget.Clone();
            copy.InstanceId = Guid.NewGuid().ToString("N");
            Layout.WidgetInstances.Add(copy);
            if (!ShowcaseLayoutService.PlaceWidget(
                    Layout,
                    CurrentPage.PageId,
                    block.BlockId,
                    copy.InstanceId))
            {
                ShowcaseLayoutService.DeleteWidget(Layout, copy.InstanceId);
                return;
            }

            CopyGridSurfaceOptions(_clipboardWidget, copy);
            SelectBlockAfterPaste(block);
            SaveAndApplyBlocks();
        }

        private void MoveCutWidgetInto(ShowcaseBlockSettings block, ShowcaseWidgetInstanceSettings cutWidget)
        {
            if (string.Equals(
                block.WidgetInstanceId,
                cutWidget.InstanceId,
                StringComparison.OrdinalIgnoreCase))
            {
                SetPendingCut(null);
                return;
            }

            if (!ShowcaseLayoutService.CanPlaceWidget(
                    Layout,
                    CurrentPage.PageId,
                    block.BlockId,
                    cutWidget.InstanceId))
            {
                return;
            }

            // A move swaps rather than displaces, so nothing is destroyed and no confirmation
            // is owed: the occupant lands in the block the cut widget came from.
            if (!ShowcaseLayoutService.PlaceWidget(
                    Layout,
                    CurrentPage.PageId,
                    block.BlockId,
                    cutWidget.InstanceId))
            {
                return;
            }

            SetPendingCut(null);
            SelectBlockAfterPaste(block);
            SaveAndApplyBlocks();
        }

        // Pasting over an occupant orphans it, and the orphan sweep in SaveAndPublish then
        // deletes it for good, so ask first.
        private bool ConfirmReplacingWidgetIn(ShowcaseBlockSettings block)
        {
            return FindWidget(block?.WidgetInstanceId) == null ||
                Confirm("LOCPlayAch_Showcase_PasteReplaceConfirm");
        }

        // A pasted grid widget keeps the columns, sort and row cap of the one it was copied
        // from: those live in the catalog under a key built from the instance id, which the
        // copy does not share.
        private void CopyGridSurfaceOptions(
            ShowcaseWidgetInstanceSettings source,
            ShowcaseWidgetInstanceSettings copy)
        {
            ShowcaseGridSurfaces.CopySurface(_settings.Persisted?.GridOptions, source, copy);
        }

        private void SelectBlockAfterPaste(ShowcaseBlockSettings block)
        {
            _selectedBlockId = block?.BlockId;
        }

        // The pending cut is shown by fading its block, so a cut that is never pasted is
        // visibly still there rather than silently armed.
        private void SetPendingCut(string instanceId)
        {
            _cutInstanceId = instanceId;
            ApplyPendingCutVisual();
        }

        // Re-applied after every rebuild: the block visuals are recreated at full opacity, and
        // a cut that survived the rebuild would otherwise stop showing.
        private void ApplyPendingCutVisual()
        {
            var instanceId = _cutInstanceId;
            foreach (var state in _blockVisuals.Values)
            {
                if (state?.Container == null)
                {
                    continue;
                }

                var block = CurrentPage.Blocks.FirstOrDefault(candidate => string.Equals(
                    candidate.BlockId,
                    state.Block?.BlockId,
                    StringComparison.OrdinalIgnoreCase));
                var isCut = block != null &&
                    !string.IsNullOrWhiteSpace(instanceId) &&
                    string.Equals(block.WidgetInstanceId, instanceId, StringComparison.OrdinalIgnoreCase);
                state.Container.Opacity = isCut ? 0.45 : 1.0;
            }
        }

        private bool Confirm(string messageKey)
        {
            return ConfirmMessage(Localize(messageKey));
        }

        private bool ConfirmMessage(string message)
        {
            return _api?.Dialogs?.ShowMessage(
                       message,
                       Localize("LOCPlayAch_Showcase_Title"),
                       MessageBoxButton.YesNo,
                       MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private ShowcaseWidgetInstanceSettings FindWidget(string instanceId)
        {
            return Layout.WidgetInstances.FirstOrDefault(widget =>
                string.Equals(widget.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        }

        private string GetWidgetName(ShowcaseWidgetInstanceSettings widget)
        {
            if (!string.IsNullOrWhiteSpace(widget?.CustomTitle))
            {
                return widget.CustomTitle;
            }

            return widget == null
                ? Localize("LOCPlayAch_Showcase_Widget")
                : ShowcaseUiText.GetWidgetName(widget.Kind);
        }

        private static MenuItem MenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => action();
            return item;
        }

        private static MenuItem WidgetPickerItem(
            ShowcaseWidgetDefinition definition,
            Action action)
        {
            var item = new MenuItem
            {
                Header = Localize(definition.NameKey)
            };
            item.Click += (_, __) => action();
            return item;
        }

        private sealed class BlockVisualState
        {
            public ShowcaseBlockSettings Block { get; set; }

            public Border Container { get; set; }

            // Non-hit-testable overlay that draws the edit-mode outline; keeping the outline
            // off the container means toggling edit mode never changes the widget layout.
            public Border EditChrome { get; set; }

            // Edit-mode corner actions for blocks hosting a widget: gear opens settings,
            // trash deletes. Visibility is owned by RefreshBlockChrome.
            public Button SettingsButton { get; set; }

            public Button DeleteButton { get; set; }

            public Border Glow { get; set; }

            public Border StatusPanel { get; set; }

            public TextBlock Status { get; set; }

            public DragVisualKind DragVisual { get; set; }

            // Present only for blocks that host a widget; lets a data-only snapshot change refresh
            // the widget's projection in place instead of rebuilding the block container.
            public ShowcaseWidgetControl Host { get; set; }

            public ShowcaseWidgetInstanceSettings Widget { get; set; }

            // Present only for empty blocks; lets the edit-mode toggle show and hide the add
            // affordance without rebuilding the block container.
            public Button AddButton { get; set; }
        }

        private enum DragVisualKind
        {
            None,
            Source,
            ValidTarget,
            InvalidTarget
        }

    }
}
