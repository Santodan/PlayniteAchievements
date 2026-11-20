using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Cache;
using System;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// One read and its last result. Loads when created and on <see cref="SetQuery"/>, and
    /// reloads at the end of a short window after a feed change touching the query. One load
    /// runs at a time; a load asked for meanwhile runs when it ends. A result is applied on the
    /// UI thread only while its query is still the current one.
    /// </summary>
    internal sealed class LiveQuery<T> : ObservableObject, IDisposable
    {
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(300);

        private readonly Func<AchievementQuery, T> _load;
        private readonly IReadScheduler _scheduler;
        private readonly Action<Exception> _onError;
        private readonly IDisposable _subscription;
        private AchievementQuery _query;
        private T _result;
        private bool _loading;
        private bool _loadAgain;
        private bool _disposed;

        /// <param name="load">Runs off the UI thread; reads through <see cref="AchievementReadService"/>.</param>
        /// <param name="window">How long after a touching change it reloads; <see cref="DefaultWindow"/> by default.</param>
        /// <param name="onError">Receives a failed load's exception; the last result stays.</param>
        /// <param name="gate">Holds back reloads for feed changes while closed (<see cref="AchievementDataFeed.Subscribe"/>).</param>
        public LiveQuery(
            AchievementDataFeed feed,
            AchievementQuery query,
            Func<AchievementQuery, T> load,
            IReadScheduler scheduler,
            Func<TimeSpan> window = null,
            Action<Exception> onError = null,
            FeedGate gate = null)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _onError = onError;
            _subscription = feed?.Subscribe(() => _query, scheduler, window ?? (() => DefaultWindow), Load, gate);
            Load();
        }

        public AchievementQuery Query => _query;

        /// <summary>The last applied result; default until the first load ends.</summary>
        public T Result
        {
            get => _result;
            private set => SetValue(ref _result, value);
        }

        /// <summary>Replaces the query and loads it now. UI thread.</summary>
        public void SetQuery(AchievementQuery query)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            Load();
        }

        public void Dispose()
        {
            _disposed = true;
            _subscription?.Dispose();
            _result = default;
        }

        private void Load()
        {
            if (_disposed)
            {
                return;
            }

            if (_loading)
            {
                _loadAgain = true;
                return;
            }

            _loading = true;
            var query = _query;
            _scheduler.Run(() =>
            {
                var loaded = false;
                var result = default(T);
                try
                {
                    result = _load(query);
                    loaded = true;
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                }

                _scheduler.Post(() => Loaded(query, loaded, result));
            });
        }

        private void Loaded(AchievementQuery query, bool loaded, T result)
        {
            _loading = false;
            if (_disposed)
            {
                return;
            }

            // An older load of the same query is still newer than what is shown.
            if (loaded && ReferenceEquals(query, _query))
            {
                Result = result;
            }

            if (_loadAgain)
            {
                _loadAgain = false;
                Load();
            }
        }
    }
}
