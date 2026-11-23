using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.Achievements
{
    public sealed partial class AchievementDataService
    {
        private readonly Lazy<NotificationGameSummaryCache> _notificationGameSummaries =
            new Lazy<NotificationGameSummaryCache>(() => new NotificationGameSummaryCache());
        private int _notificationFeedAttached;

        /// <summary>
        /// Every summary game's totals, reused between notifications. The rows are read again
        /// after the change feed reports a write: a full change drops them, a scoped one marks
        /// its games, which the next read reloads with the library (there is no per-game read).
        /// </summary>
        internal IReadOnlyList<CachedGameSummaryData> GetNotificationGameSummaries()
        {
            AttachNotificationFeed();
            return _notificationGameSummaries.Value.Get(
                () => GetCachedSummaryDataForOverview(0, days: false),
                gameId => null);
        }

        /// <summary>Every summary game's totals, read now.</summary>
        internal IReadOnlyList<CachedGameSummaryData> LoadNotificationGameSummaries() =>
            GetCachedSummaryDataForOverview(0, days: false)?.Games;

        private void AttachNotificationFeed()
        {
            var feed = _cacheReadOptimizations?.Feed;
            if (feed == null || Interlocked.Exchange(ref _notificationFeedAttached, 1) == 1)
            {
                return;
            }

            feed.AchievementDataChanged += OnNotificationDataChanged;
        }

        private void OnNotificationDataChanged(object sender, AchievementDataChange change)
        {
            InvalidateNotificationSummaries(change == null || change.IsFull ? null : change.GameIds?.ToList());
        }

        private void InvalidateNotificationSummaries(IReadOnlyList<Guid> gameIds)
        {
            if (_notificationGameSummaries.IsValueCreated)
            {
                _notificationGameSummaries.Value.Invalidate(gameIds);
            }
        }
    }
}
