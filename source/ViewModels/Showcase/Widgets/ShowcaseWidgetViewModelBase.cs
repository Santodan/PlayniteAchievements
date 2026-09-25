using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Search;
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

        /// <summary>Search/filter bar shown when the surface's ShowControlBar option is on.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            protected set => SetValue(ref _controlBar, value);
        }

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
    /// Grid widget base for achievement rows: contributes a search box that filters by game
    /// and achievement name before the MaxRows cap.
    /// </summary>
    public abstract class ShowcaseAchievementGridWidgetViewModelBase
        : ShowcaseGridWidgetViewModelBase<AchievementDisplayItem>
    {
        private readonly SearchTextIndex<AchievementDisplayItem> _searchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForRecentAchievement(item?.GameName, item?.DisplayName));
        private string _searchText = string.Empty;

        protected ShowcaseAchievementGridWidgetViewModelBase()
        {
            ControlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(SearchText),
                    () => SearchText,
                    value => SearchText = value,
                    ResourceProvider.GetString("LOCPlayAch_Filter_Achievements"),
                    () => SearchText = string.Empty)
            };
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(_searchText, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _searchText = normalized;
                OnPropertyChanged(nameof(SearchText));
                RefreshItems();
            }
        }

        protected override IEnumerable<AchievementDisplayItem> FilterItems(
            IEnumerable<AchievementDisplayItem> items)
        {
            var query = SearchQuery.From(SearchText);
            if (!query.HasValue)
            {
                return items;
            }

            var list = (items ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();
            _searchIndex.Rebuild(list);
            return list.Where(item => _searchIndex.Matches(item, query));
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

            list.Sort(AchievementSortHelper.WithStableOrder(
                comparison,
                AchievementSortHelper.CreateStableOrderMap(list)));
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
    }

    /// <summary>
    /// Grid widget base for game rows: contributes the shared game-summaries control bar
    /// (search plus provider, progress and activity filters) applied before the MaxRows cap.
    /// </summary>
    public abstract class ShowcaseGameGridWidgetViewModelBase
        : ShowcaseGridWidgetViewModelBase<GameSummaryItem>
    {
        private readonly GameSummaryGridControlBarAdapter _controlBarAdapter =
            new GameSummaryGridControlBarAdapter();

        protected ShowcaseGameGridWidgetViewModelBase()
        {
            _controlBarAdapter.FilterChanged += (_, __) => RefreshItems();
            ControlBar = _controlBarAdapter.ControlBar;
        }

        protected override IEnumerable<GameSummaryItem> FilterItems(IEnumerable<GameSummaryItem> items)
        {
            var list = (items ?? Enumerable.Empty<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();
            _controlBarAdapter.UpdateOptions(list);
            return _controlBarAdapter.Apply(list);
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
