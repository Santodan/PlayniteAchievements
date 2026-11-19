using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Cache
{
    /// <summary>
    /// Holds back the reruns of the feed subscriptions given it while closed; opening it reruns,
    /// once, each that a change touched meanwhile. Open at first. UI thread.
    /// </summary>
    internal sealed class FeedGate
    {
        private bool _isOpen = true;

        public event Action Opened;

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (_isOpen == value)
                {
                    return;
                }

                _isOpen = value;
                if (value)
                {
                    Opened?.Invoke();
                }
            }
        }
    }

    /// <summary>
    /// Raises the cache's change events: <c>CacheInvalidated</c> and <c>FriendCacheInvalidated</c>
    /// through the callbacks it is given, and one <see cref="AchievementDataChanged"/> per raise
    /// of either or both. Friend changes inside a batch window are collected and raised once per
    /// flush.
    /// Every event is raised on the writer's thread, after the write.
    /// </summary>
    internal sealed class AchievementDataFeed
    {
        private sealed class FriendBatch : IFriendCacheInvalidationBatch
        {
            private AchievementDataFeed _owner;

            public FriendBatch(AchievementDataFeed owner)
            {
                _owner = owner;
            }

            public void Flush() => _owner?.FlushFriendBatch(endBatch: false);

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                owner?.FlushFriendBatch(endBatch: true);
            }
        }

        private readonly object _sync = new object();
        private readonly FriendCacheInvalidationScopeAccumulator _friendScope = new FriendCacheInvalidationScopeAccumulator();
        private readonly Action<CacheInvalidatedEventArgs> _raiseCacheInvalidated;
        private readonly Action<FriendCacheInvalidatedEventArgs> _raiseFriendCacheInvalidated;
        private readonly Action<Exception> _onHandlerError;
        private readonly Func<IReadOnlyList<FriendCacheChange>, IEnumerable<Guid>> _libraryGamesOf;
        private int _friendBatchDepth;

        /// <param name="onHandlerError">Receives an exception an <see cref="AchievementDataChanged"/> handler threw.</param>
        /// <param name="libraryGamesOf">
        /// The library games of the provider games some friend definition changes name: their
        /// definitions are the current user's too.
        /// </param>
        public AchievementDataFeed(
            Action<CacheInvalidatedEventArgs> raiseCacheInvalidated,
            Action<FriendCacheInvalidatedEventArgs> raiseFriendCacheInvalidated,
            Action<Exception> onHandlerError = null,
            Func<IReadOnlyList<FriendCacheChange>, IEnumerable<Guid>> libraryGamesOf = null)
        {
            _raiseCacheInvalidated = raiseCacheInvalidated;
            _raiseFriendCacheInvalidated = raiseFriendCacheInvalidated;
            _onHandlerError = onHandlerError;
            _libraryGamesOf = libraryGamesOf;
        }

        /// <summary>Raised once per write batch, after the write.</summary>
        public event EventHandler<AchievementDataChange> AchievementDataChanged;

        /// <summary>Raises the given cache events, either of which may be null, then one <see cref="AchievementDataChanged"/>.</summary>
        public void Raise(CacheInvalidatedEventArgs games, FriendCacheInvalidatedEventArgs friends)
        {
            if (games != null)
            {
                _raiseCacheInvalidated?.Invoke(games);
            }

            if (friends != null)
            {
                _raiseFriendCacheInvalidated?.Invoke(friends);
            }

            var definitions = friends == null || friends.IsFull
                ? null
                : friends.Changes.Where(change => change.Kind == FriendCacheChangeKind.GameDefinition).ToList();
            Publish(AchievementDataChange.Of(
                games,
                friends,
                definitions?.Count > 0 ? _libraryGamesOf?.Invoke(definitions) : null));
        }

        /// <summary>Raises <see cref="AchievementDataChanged"/> alone, for writes the cache events do not cover.</summary>
        public void Publish(AchievementDataChange change)
        {
            try { AchievementDataChanged?.Invoke(this, change); }
            catch (Exception ex)
            {
                _onHandlerError?.Invoke(ex);
            }
        }

        /// <summary>
        /// Calls <paramref name="rerun"/> on the UI thread once per <paramref name="window"/> in
        /// which a change touching <paramref name="query"/> arrived, at the end of that window; the
        /// window is read when its first change arrives. While <paramref name="gate"/> is closed such
        /// a change only marks the subscription stale, and opening the gate then calls it once.
        /// Disposing stops it, a pending call included.
        /// </summary>
        public IDisposable Subscribe(Func<AchievementQuery> query, IReadScheduler scheduler, Func<TimeSpan> window, Action rerun, FeedGate gate = null)
        {
            var pending = false;
            var stale = false;
            var disposed = false;
            bool HeldBack()
            {
                if (gate?.IsOpen != false)
                {
                    return false;
                }

                stale = true;
                return true;
            }

            EventHandler<AchievementDataChange> handler = (_, change) =>
            {
                if (disposed || !change.Touches(query()))
                {
                    return;
                }

                scheduler.Post(() =>
                {
                    if (disposed || pending || HeldBack())
                    {
                        return;
                    }

                    pending = true;
                    scheduler.PostAfter(window(), () =>
                    {
                        pending = false;
                        if (!disposed && !HeldBack())
                        {
                            rerun();
                        }
                    });
                });
            };
            Action opened = () =>
            {
                if (stale && !disposed)
                {
                    stale = false;
                    rerun();
                }
            };

            AchievementDataChanged += handler;
            if (gate != null)
            {
                gate.Opened += opened;
            }

            return new Unsubscriber(() =>
            {
                disposed = true;
                AchievementDataChanged -= handler;
                if (gate != null)
                {
                    gate.Opened -= opened;
                }
            });
        }

        private sealed class Unsubscriber : IDisposable
        {
            private Action _dispose;

            public Unsubscriber(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                _dispose?.Invoke();
                _dispose = null;
            }
        }

        /// <summary>
        /// Raises a friend change now, or inside a batch window collects it for the next flush.
        /// Null means unscoped and makes the change full.
        /// </summary>
        public void RaiseFriendChange(FriendCacheChange change)
        {
            lock (_sync)
            {
                if (_friendBatchDepth > 0)
                {
                    _friendScope.Add(change);
                    return;
                }
            }

            Raise(null, change == null
                ? FriendCacheInvalidatedEventArgs.FullInvalidation
                : FriendCacheInvalidatedEventArgs.Scoped(new[] { change }));
        }

        /// <summary>Opens a batch window; disposing it closes the window and flushes when it was the outermost.</summary>
        public IFriendCacheInvalidationBatch BeginFriendBatch()
        {
            lock (_sync)
            {
                _friendBatchDepth++;
            }

            return new FriendBatch(this);
        }

        private void FlushFriendBatch(bool endBatch)
        {
            FriendCacheInvalidatedEventArgs args = null;
            lock (_sync)
            {
                if (endBatch && _friendBatchDepth > 0)
                {
                    _friendBatchDepth--;
                }

                // A flush drains at any depth; the end of a nested window waits for the outermost.
                if ((!endBatch || _friendBatchDepth == 0) && !_friendScope.IsEmpty)
                {
                    args = _friendScope.Drain();
                }
            }

            if (args != null)
            {
                Raise(null, args);
            }
        }
    }
}
