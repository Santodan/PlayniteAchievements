using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Base for the friends widgets: their control bar (search, Favorites, Sort By), their link
    /// to the shared friend lists, and whether there is any friend data to show. The rows come
    /// from <see cref="SelectFriendItems"/> once the lists are read; while they load, and while
    /// friends features are off, the grid is empty.
    /// </summary>
    public abstract class FriendGridWidgetViewModelBase<TItem, TAdapter>
        : ShowcaseGridWidgetViewModelBase<TItem>, IDisposable
        where TAdapter : FriendWidgetControlBarAdapter, new()
    {
        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<TAdapter> _controlBarSlot;
        private bool _hasNoFriendData;

        protected FriendGridWidgetViewModelBase()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<TAdapter>(RefreshItems);
            Lists = new FriendWidgetLists(RefreshItems);
        }

        internal FriendWidgetLists Lists { get; }

        /// <summary>True once the friend lists are read and hold no friend, for the grid's empty message.</summary>
        public bool HasNoFriendData
        {
            get => _hasNoFriendData;
            private set => SetValue(ref _hasNoFriendData, value);
        }

        // Each read of the friend data builds new rows.
        protected override bool ReplacesRowsInPlace => true;

        protected sealed override IEnumerable<TItem> SelectItems(ShowcaseWidgetProjection projection)
        {
            var bar = Bar();
            var lists = Lists.Current;
            var disabled = ReferenceEquals(lists, FriendsOverviewLists.Empty);
            HasNoFriendData = lists != null && !disabled && lists.Friends.Count == 0;
            return lists == null || disabled
                ? Array.Empty<TItem>()
                : SelectFriendItems(projection, lists, bar) ?? Array.Empty<TItem>();
        }

        /// <summary>The widget's rows from the read friend lists, before the sort and the MaxRows cap.</summary>
        internal abstract IEnumerable<TItem> SelectFriendItems(
            ShowcaseWidgetProjection projection,
            FriendsOverviewLists lists,
            TAdapter bar);

        private TAdapter Bar()
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.Adapter.EnableSort();
            _controlBarSlot.UpdateShownBar(Projection?.Instance, LegacyShowControlBar);
            ControlBar = _controlBarSlot.ShownBar;
            return _controlBarSlot.Adapter;
        }

        public virtual void Dispose() => Lists.Dispose();
    }
}
