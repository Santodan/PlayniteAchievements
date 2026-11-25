using System;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// The merged, scored friend and friend-game lists, read once for every consumer that shows
    /// them outside the Friends Overview window (the theme friend lists and the friends widgets).
    /// Nothing is read until the first <see cref="Ensure"/>; from then on the lists are read again
    /// each time the friend data under them changes.
    /// </summary>
    internal sealed class FriendListsSource : IDisposable
    {
        private readonly Func<IFriendsOverviewReads> _reads;
        private readonly Func<PersistedSettings> _settings;
        private readonly Func<IReadScheduler> _scheduler;
        private readonly Func<TimeSpan> _window;
        private readonly Action<Exception> _onError;
        private LiveQuery<FriendsOverviewLists> _lists;

        /// <param name="reads">The friend reads; null while friend data is unavailable.</param>
        /// <param name="window">How long a burst of friend data changes is gathered before a read.</param>
        public FriendListsSource(
            Func<IFriendsOverviewReads> reads,
            Func<PersistedSettings> settings,
            Func<IReadScheduler> scheduler,
            Func<TimeSpan> window,
            Action<Exception> onError)
        {
            _reads = reads ?? (() => null);
            _settings = settings ?? (() => null);
            _scheduler = scheduler;
            _window = window;
            _onError = onError;
        }

        /// <summary>Raised on the scheduler's thread each time a read of the lists lands.</summary>
        public event EventHandler Changed;

        /// <summary>The friend reads the lists come from; null while friend data is unavailable.</summary>
        public IFriendsOverviewReads Reads => _reads();

        /// <summary>The lists of the last read; null until the first read lands.</summary>
        public FriendsOverviewLists Current => _lists?.Result;

        /// <summary>The read window, for a consumer's own reads of the same friend data.</summary>
        public TimeSpan Window() => _window?.Invoke() ?? LiveQuery<object>.DefaultWindow;

        /// <summary>Starts reading the lists, once; false while friend data is unavailable.</summary>
        public bool Ensure()
        {
            if (_lists != null)
            {
                return true;
            }

            var reads = _reads();
            if (reads == null)
            {
                return false;
            }

            _lists = new LiveQuery<FriendsOverviewLists>(
                reads.Feed,
                AchievementQuery.AllFriends,
                _ => FriendsOverviewLists.Load(reads, _settings()),
                _scheduler(),
                Window,
                _onError);
            _lists.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LiveQuery<object>.Result))
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            };
            return true;
        }

        /// <summary>Reads the lists again, for a setting they show; nothing when they were never read.</summary>
        public void Reload() => _lists?.SetQuery(_lists.Query);

        public void Dispose()
        {
            _lists?.Dispose();
            _lists = null;
        }
    }
}
