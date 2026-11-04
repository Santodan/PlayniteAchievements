using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using Playnite.SDK;
using PlayniteAchievements.Services.Database.Rows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    public sealed partial class AchievementDataService
    {
        private readonly Lazy<NotificationGameSummaryCache> _notificationGameSummaries =
            new Lazy<NotificationGameSummaryCache>(() => new NotificationGameSummaryCache());
        private void InvalidateSantodanNotificationDelta(CacheDeltaEventArgs delta)
        {
            if (_notificationGameSummaries.IsValueCreated) _notificationGameSummaries.Value.InvalidateDelta(delta);
        }

        private void InvalidateSantodanNotificationSummaries(IReadOnlyList<Guid> gameIds)
        {
            if (_notificationGameSummaries.IsValueCreated) _notificationGameSummaries.Value.Invalidate(gameIds);
        }

        internal IReadOnlyList<CachedGameSummaryData> GetNotificationGameSummaries()
        {
            return _notificationGameSummaries.Value.Get(
                () =>
                {
                    var data = GetCachedSummaryData(1);
                    return data == null ? null : ApplyOverviewSummaryHydration(data, 1);
                },
                gameId =>
                {
                    var data = _cacheReadOptimizations?.LoadCachedSummaryDataForGameFast(gameId);
                    return data == null ? null : ApplyOverviewSummaryHydration(data, 0, new[] { gameId });
                });
        }

    }
}
