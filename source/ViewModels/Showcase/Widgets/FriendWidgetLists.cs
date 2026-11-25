using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// A friends widget's link to the shared friend lists: the lists of the last read, and a
    /// callback each time a new read lands. The first read of <see cref="Current"/> starts the
    /// shared read.
    /// </summary>
    internal sealed class FriendWidgetLists : IDisposable
    {
        private readonly Action _changed;
        private FriendListsSource _source;

        /// <param name="changed">Runs on the UI thread when a read of the lists lands.</param>
        public FriendWidgetLists(Action changed)
        {
            _changed = changed;
        }

        /// <summary>
        /// The friend lists; empty while friends features are off, and null until the first read
        /// lands, which the widget shows as loading.
        /// </summary>
        public FriendsOverviewLists Current
        {
            get
            {
                var plugin = PlayniteAchievementsPlugin.Instance;
                if (plugin?.Settings?.Persisted?.EnableFriendsFeatures != true)
                {
                    return FriendsOverviewLists.Empty;
                }

                var source = Bind(plugin.FriendLists);
                return source != null && source.Ensure() ? source.Current : FriendsOverviewLists.Empty;
            }
        }

        /// <summary>The shared source <see cref="Current"/> last read; null before then.</summary>
        public FriendListsSource Source => _source;

        /// <summary>
        /// The friends a widget shows: those picked by <paramref name="accountKeys"/> (every friend
        /// when none is), kept to favorites when <paramref name="favoritesOnly"/>.
        /// </summary>
        public static IReadOnlyList<FriendSummaryItem> ScopeFriends(
            FriendsOverviewLists lists,
            IReadOnlyCollection<string> accountKeys,
            bool favoritesOnly)
        {
            IEnumerable<FriendSummaryItem> friends = (lists ?? FriendsOverviewLists.Empty).Friends
                ?? (IReadOnlyList<FriendSummaryItem>)Array.Empty<FriendSummaryItem>();
            friends = friends.Where(friend => friend != null);
            if (accountKeys?.Count > 0)
            {
                var keys = new HashSet<string>(accountKeys, StringComparer.OrdinalIgnoreCase);
                friends = friends.Where(friend =>
                    FriendsOverviewLists.AccountsOf(friend).Any(account => keys.Contains(account.Key)));
            }

            if (favoritesOnly)
            {
                friends = friends.Where(friend => friend.IsFavorite);
            }

            return friends.ToList();
        }

        /// <summary>True when the scope keeps every friend, so no account filter applies.</summary>
        public static bool IsEveryFriend(IReadOnlyCollection<string> accountKeys, bool favoritesOnly) =>
            (accountKeys == null || accountKeys.Count == 0) && !favoritesOnly;

        public void Dispose() => Bind(null);

        private FriendListsSource Bind(FriendListsSource source)
        {
            if (ReferenceEquals(source, _source))
            {
                return source;
            }

            if (_source != null)
            {
                _source.Changed -= Source_Changed;
            }

            _source = source;
            if (_source != null)
            {
                _source.Changed += Source_Changed;
            }

            return source;
        }

        private void Source_Changed(object sender, EventArgs e) => _changed?.Invoke();
    }
}
