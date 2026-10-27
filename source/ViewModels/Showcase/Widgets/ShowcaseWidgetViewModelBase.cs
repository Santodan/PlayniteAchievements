using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Base for per-kind Showcase widget view models. The host control feeds a projection and the
    /// current viewport; subclasses expose bindable state rebuilt in <see cref="Refresh"/> whenever
    /// either changes. Keeping the data as a plain projection (not WPF types) lets the projection
    /// layer stay UI-free and off-thread-capable.
    /// </summary>
    public abstract class ShowcaseWidgetViewModelBase : ObservableObject
    {
        private ShowcaseWidgetProjection _projection;
        private WidgetViewportState _viewport = WidgetViewportState.Classify(0, 0);

        protected ShowcaseWidgetProjection Projection => _projection;

        protected WidgetViewportDensity Density => _viewport.Density;

        protected WidgetViewportOrientation Orientation => _viewport.Orientation;

        public void Update(ShowcaseWidgetProjection projection, WidgetViewportState viewport)
        {
            _projection = projection;
            _viewport = viewport ?? WidgetViewportState.Classify(0, 0);
            Refresh();
        }

        protected abstract void Refresh();

        /// <summary>
        /// The widget host's body inset per density. A widget whose host drops the inset (the
        /// full-bleed profile) reapplies it to its own foreground.
        /// </summary>
        public static double GetBodyInset(WidgetViewportDensity density) =>
            density == WidgetViewportDensity.Compact
                ? 6
                : density == WidgetViewportDensity.Expanded ? 10 : 8;
    }

    /// <summary>
    /// Base for the mosaic widgets. A refresh re-reads the layout options, then runs the tile
    /// pass; a control-bar filter change runs only the tile pass. The projection hands over the
    /// whole source, so the tile pass filters first and applies the Count cap after.
    /// </summary>
    public abstract class ShowcaseMosaicWidgetViewModelBase : ShowcaseWidgetViewModelBase
    {
        private GridControlBarViewModel _controlBar;

        /// <summary>The search and filter items the widget's settings show; null when they show none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            protected set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

        protected sealed override void Refresh()
        {
            RefreshLayout();
            RefreshTiles();
        }

        /// <summary>Re-reads the widget's size, spacing and appearance options.</summary>
        protected abstract void RefreshLayout();

        /// <summary>
        /// Filters the projected source through the widget instance's control bar state (which
        /// stays in effect while the bar is hidden), caps it at Count, sorts, and syncs the tiles
        /// in place.
        /// </summary>
        protected abstract void RefreshTiles();
    }

    /// <summary>
    /// Base for the widgets that render one of the shared data grids. They differ only in the
    /// row type, which projection slice feeds them, and their persisted column surface; the
    /// per-instance surface key and the grid's display-options record are handled here.
    /// </summary>
    public abstract class ShowcaseGridWidgetViewModelBase<TItem> : ShowcaseWidgetViewModelBase
    {
        private string _columnSettingsKey;
        private object _gridOptions;
        private GridControlBarViewModel _controlBar;
        private string _pinCollectionId;

        protected ShowcaseGridWidgetViewModelBase()
        {
            _columnSettingsKey = BaseSurfaceKey;
        }

        public BulkObservableCollection<TItem> Items { get; } = new BulkObservableCollection<TItem>();

        /// <summary>The search and filter items the widget's settings show; null when they show none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            protected set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

        /// <summary>
        /// The surface's former whole-bar option, which decides an item's visibility until the
        /// widget's settings choose per item.
        /// </summary>
        protected bool LegacyShowControlBar => (GridOptions as GridCommonOptions)?.ShowControlBar == true;

        /// <summary>
        /// Persisted column-layout surface. Widgets that allow multiple instances get one
        /// surface per instance so each placed grid keeps its own columns.
        /// </summary>
        public string ColumnSettingsKey
        {
            get => _columnSettingsKey;
            private set => SetValue(ref _columnSettingsKey, value);
        }

        /// <summary>Resolved collection used by pinned grids for row reorder operations.</summary>
        public string PinCollectionId
        {
            get => _pinCollectionId;
            private set => SetValue(ref _pinCollectionId, value);
        }

        /// <summary>
        /// The live display-options record for this widget's grid surface
        /// (AchievementGridOptions or GameSummaryGridOptions). Templates bind grid display
        /// DPs through it, and the view model listens to it for the row-shaping members
        /// (MaxRows, per-kind sort), so edits from the widget editor propagate without
        /// re-projection or any global change broadcast.
        /// </summary>
        public object GridOptions
        {
            get => _gridOptions;
            private set
            {
                if (ReferenceEquals(_gridOptions, value))
                {
                    return;
                }

                // Weak subscription: the persisted record outlives replaced widget view
                // models, so a strong handler would keep dead view models reachable.
                if (_gridOptions is System.ComponentModel.INotifyPropertyChanged previous)
                {
                    System.ComponentModel.PropertyChangedEventManager.RemoveHandler(
                        previous, GridOptions_PropertyChanged, string.Empty);
                }

                SetValue(ref _gridOptions, value);
                if (_gridOptions is System.ComponentModel.INotifyPropertyChanged next)
                {
                    System.ComponentModel.PropertyChangedEventManager.AddHandler(
                        next, GridOptions_PropertyChanged, string.Empty);
                }
            }
        }

        /// <summary>The widget kind's base surface key, expanded with the instance ID during refresh.</summary>
        protected abstract string BaseSurfaceKey { get; }

        protected abstract System.Collections.Generic.IEnumerable<TItem> SelectItems(
            ShowcaseWidgetProjection projection);

        protected override void Refresh()
        {
            // Derived from the INSTANCE kind, not the view-model type: collapsed widget kinds
            // reuse another kind's view model for their pinned mode, and the projection resolves
            // GridWidgetOptions from the instance kind's surface key. BaseSurfaceKey only covers
            // the projection-less case.
            ColumnSettingsKey = (Projection?.Instance != null
                    ? ShowcaseGridSurfaces.ResolveWidgetSurface(
                        Projection.Instance.Kind,
                        Projection.Instance.InstanceId)
                    : null)
                ?? ShowcaseGridSurfaces.ForInstance(BaseSurfaceKey, Projection?.Instance?.InstanceId);
            PinCollectionId = Projection?.ResolvedPinCollectionId;
            GridOptions = Projection?.GridWidgetOptions;
            RefreshItems();
        }

        /// <summary>Filter hook for the control bar; runs before the sort and the MaxRows cap.</summary>
        protected virtual IEnumerable<TItem> FilterItems(IEnumerable<TItem> items) => items;

        /// <summary>Sort hook applied between the filter and the MaxRows cap; identity by default.</summary>
        protected virtual IEnumerable<TItem> OrderItems(IEnumerable<TItem> items) => items;

        /// <summary>
        /// Last step before the rows reach the grid, after the filter, sort and cap: identity by
        /// default. Must return the same objects for the same inputs, or every refresh resets the grid.
        /// </summary>
        protected virtual IEnumerable<TItem> ScopeVisibleItems(IEnumerable<TItem> visible) => visible;

        /// <summary>The grid-options members whose edits require re-running <see cref="RefreshItems"/>.</summary>
        protected virtual bool ShouldRefreshItemsFor(string propertyName)
        {
            return string.IsNullOrEmpty(propertyName) ||
                propertyName == nameof(GridCommonOptions.MaxRows);
        }

        private void GridOptions_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (ShouldRefreshItemsFor(e?.PropertyName))
            {
                RefreshItems();
            }
        }

        /// <summary>
        /// Re-applies the control-bar filter, the sort, and the surface's MaxRows cap over the
        /// projected rows. The filter runs before the cap so searching reaches rows beyond it.
        /// </summary>
        protected void RefreshItems()
        {
            var items = SelectItems(Projection) ?? Array.Empty<TItem>();
            var visible = ScopeVisibleItems(DisplayGridRowLimitHelper.Limit(
                OrderItems(FilterItems(items)),
                (GridOptions as GridCommonOptions)?.MaxRows)).ToList();

            // Replacing the collection resets the grid, which rebuilds every row (and re-resolves
            // its art). The projection hands back the same row objects when nothing changed, so
            // an unrelated refresh - another widget's option, a resize, a pin toggle - leaves the
            // grid alone.
            if (!SameRows(visible))
            {
                Items.ReplaceAll(visible);
            }

            OnItemsRefreshed();
        }

        /// <summary>Runs after every <see cref="RefreshItems"/>, with <see cref="Items"/> current.</summary>
        protected virtual void OnItemsRefreshed()
        {
        }

        private bool SameRows(System.Collections.Generic.IEnumerable<TItem> items)
        {
            var index = 0;
            foreach (var item in items)
            {
                if (index >= Items.Count || !ReferenceEquals(Items[index], item))
                {
                    return false;
                }

                index++;
            }

            return index == Items.Count;
        }
    }

    /// <summary>
    /// Grid widget base for achievement rows: contributes the cross-game achievement control bar
    /// (search plus provider/platform filter) applied before the MaxRows cap.
    /// </summary>
    public abstract class ShowcaseAchievementGridWidgetViewModelBase
        : ShowcaseGridWidgetViewModelBase<AchievementDisplayItem>
    {
        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<CrossGameAchievementControlBarAdapter> _controlBarSlot;

        protected ShowcaseAchievementGridWidgetViewModelBase()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<CrossGameAchievementControlBarAdapter>(RefreshItems);
        }

        protected override IEnumerable<AchievementDisplayItem> FilterItems(
            IEnumerable<AchievementDisplayItem> items)
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.UpdateShownBar(Projection?.Instance, LegacyShowControlBar);
            ControlBar = _controlBarSlot.ShownBar;

            var adapter = _controlBarSlot.Adapter;
            var list = (items ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();
            adapter.UpdateGames(Projection?.Snapshot?.GameSummaries);
            adapter.GameFilter.SetScope(Projection?.Collection?.GameIds);
            adapter.LocksRows = Projection?.Instance?.Kind == ShowcaseWidgetKind.RecentAchievements &&
                ShowcaseWidgetOptions.GetAchievementGridSource(Projection.Instance) == ShowcaseAchievementGridSource.UnlockNext;
            return adapter.Apply(list);
        }

        /// <summary>
        /// Applies the surface's configured sort. None preserves the projection's source order
        /// (pin order for pinned grids, unlock recency for recent grids).
        /// </summary>
        protected override IEnumerable<AchievementDisplayItem> OrderItems(
            IEnumerable<AchievementDisplayItem> items)
        {
            var options = GridOptions as AchievementGridOptions;
            var spec = new AchievementSortSpec(
                options?.SortMode ?? CompactListSortMode.None,
                options?.SortDescending == false
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending);
            if (spec.PreservesSourceOrder)
            {
                return items;
            }

            var list = (items ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();
            var comparison = AchievementSortHelper.GetComparison(
                spec.SortMemberPath,
                spec.Direction,
                AchievementSortScope.RecentAchievements);
            if (comparison == null)
            {
                return list;
            }

            if (!AchievementSortHelper.IsOrdered(list, comparison))
            {
                list.Sort(AchievementSortHelper.WithStableOrder(
                    comparison,
                    AchievementSortHelper.CreateStableOrderMap(list)));
            }

            return list;
        }

        protected override bool ShouldRefreshItemsFor(string propertyName)
        {
            return base.ShouldRefreshItemsFor(propertyName) ||
                propertyName == nameof(AchievementGridOptions.SortMode) ||
                propertyName == nameof(AchievementGridOptions.SortDescending);
        }

        private readonly WidgetRevealScope _reveals = new WidgetRevealScope();

        // This grid's own copies of any revealable rows, so a reveal stays in this widget.
        protected override IEnumerable<AchievementDisplayItem> ScopeVisibleItems(
            IEnumerable<AchievementDisplayItem> visible) => _reveals.Map(visible);

        private static readonly ILogger Logger = LogManager.GetLogger();

        private Guid? _categoryGameId;
        private WeakReference<OverviewDataSnapshot> _categorySnapshot;
        private CancellationTokenSource _categoryLoad;
        private AchievementSelectionPipeline _categoryPipeline;

        /// <summary>
        /// The whole achievement list (locked included) of the one game the rows show, which the
        /// grid's category mode totals its categories from and lists when drilled into one; empty
        /// while the rows span several games, where category mode is not offered.
        /// </summary>
        public BulkObservableCollection<AchievementDisplayItem> CategoryItems { get; } =
            new BulkObservableCollection<AchievementDisplayItem>();

        private CategorySummaryGridOptions _categoryGridOptions;

        /// <summary>The widget's own category grid display options, which category mode's list follows.</summary>
        public CategorySummaryGridOptions CategoryGridOptions
        {
            get => _categoryGridOptions;
            private set => SetValue(ref _categoryGridOptions, value);
        }

        protected override void Refresh()
        {
            base.Refresh();
            var categorySurface = ShowcaseGridSurfaces.ResolveCategorySurface(ColumnSettingsKey);
            CategoryGridOptions = categorySurface == null
                ? null
                : PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.GridOptions?.GetCategorySummaries(categorySurface);
        }

        /// <summary>Only the achievement grid widget backs category mode with the game's whole list.</summary>
        private bool OffersCategoryMode => Projection?.Instance?.Kind == ShowcaseWidgetKind.RecentAchievements;

        protected override void OnItemsRefreshed()
        {
            var gameId = OffersCategoryMode ? ResolveSingleGameId(Items) : null;
            var snapshot = Projection?.Snapshot;
            OverviewDataSnapshot loadedFrom = null;
            _categorySnapshot?.TryGetTarget(out loadedFrom);
            // Reloaded when the game changes, and when a new snapshot (a refresh) may have
            // changed the game's achievements.
            if (gameId == _categoryGameId && ReferenceEquals(loadedFrom, snapshot))
            {
                return;
            }

            _categoryGameId = gameId;
            _categorySnapshot = new WeakReference<OverviewDataSnapshot>(snapshot);
            _categoryLoad?.Cancel();
            _categoryLoad = null;
            if (!gameId.HasValue)
            {
                CategoryItems.Clear();
                return;
            }

            LoadCategoryItems(gameId.Value);
        }

        private async void LoadCategoryItems(Guid gameId)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            if (plugin?.AchievementDataService == null)
            {
                return;
            }

            var load = new CancellationTokenSource();
            _categoryLoad = load;
            try
            {
                if (_categoryPipeline == null)
                {
                    _categoryPipeline = new AchievementSelectionPipeline(plugin.AchievementDataService, plugin.Settings);
                }

                // The pipeline's cache would hold the game's rows from before a refresh.
                _categoryPipeline.Invalidate(gameId);
                var result = await _categoryPipeline
                    .LoadAsync(gameId, null, load.Token)
                    .ConfigureAwait(true);
                if (!load.IsCancellationRequested)
                {
                    CategoryItems.ReplaceAll(result.Items ?? new List<AchievementDisplayItem>());
                    // The Category dropdown lists every category of the game, not only the shown rows'.
                    _controlBarSlot.Adapter.SetCategoryOptionRows(CategoryItems.ToList());
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Failed to load category rows for game {gameId}.");
            }
        }

        // The rows' one game, or null when they are empty or span several.
        private static Guid? ResolveSingleGameId(IEnumerable<AchievementDisplayItem> items)
        {
            Guid? gameId = null;
            foreach (var item in items)
            {
                var id = item?.PlayniteGameId;
                if (!id.HasValue)
                {
                    return null;
                }

                if (gameId.HasValue && gameId.Value != id.Value)
                {
                    return null;
                }

                gameId = id;
            }

            return gameId;
        }
    }

    /// <summary>
    /// Grid widget base for game rows: contributes the shared game-summaries control bar
    /// (search plus provider, progress and activity filters) applied before the MaxRows cap.
    /// </summary>
    public abstract class ShowcaseGameGridWidgetViewModelBase
        : ShowcaseGridWidgetViewModelBase<GameSummaryItem>
    {
        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<GameSummaryGridControlBarAdapter> _controlBarSlot;

        protected ShowcaseGameGridWidgetViewModelBase()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<GameSummaryGridControlBarAdapter>(RefreshItems);
        }

        protected override IEnumerable<GameSummaryItem> FilterItems(IEnumerable<GameSummaryItem> items)
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.UpdateShownBar(Projection?.Instance, LegacyShowControlBar);
            ControlBar = _controlBarSlot.ShownBar;

            var adapter = _controlBarSlot.Adapter;
            var list = (items ?? Enumerable.Empty<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();
            // The dropdown lists the whole library's platforms, not only the ones shown now.
            adapter.UpdateOptions(Projection?.Snapshot?.GameSummaries);
            adapter.GameFilter.SetScope(Projection?.Collection?.GameIds);
            return adapter.Apply(list);
        }

        /// <summary>Sort fallback when the surface record is unavailable.</summary>
        protected virtual GameSummariesSortMode DefaultSortMode => GameSummariesSortMode.RecentUnlock;

        /// <summary>
        /// Applies the surface's configured sort. PinOrder preserves the projection's source
        /// order, which for pinned grids is the user-controlled pin order. Sorting runs here
        /// (not in the projection) so a sort edit re-orders this widget's rows without
        /// re-projecting the whole dashboard.
        /// </summary>
        protected override IEnumerable<GameSummaryItem> OrderItems(IEnumerable<GameSummaryItem> items)
        {
            var list = (items ?? Enumerable.Empty<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();
            var options = GridOptions as GameSummaryGridOptions;
            GameSummariesSortHelper.Sort(
                list,
                options?.SortMode ?? DefaultSortMode,
                options?.SortDescending == false
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending);
            return list;
        }

        protected override bool ShouldRefreshItemsFor(string propertyName)
        {
            return base.ShouldRefreshItemsFor(propertyName) ||
                propertyName == nameof(GameSummaryGridOptions.SortMode) ||
                propertyName == nameof(GameSummaryGridOptions.SortDescending);
        }
    }
}
