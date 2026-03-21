using System;
using System.Collections.Generic;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services
{
    public interface ICacheManager
    {
        event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated;
        event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated;
        event EventHandler CacheInvalidated;

        void EnsureDiskCacheOrClearMemory();
        bool CacheFileExists();
        bool IsCacheValid();
        DateTime? GetMostRecentLastUpdatedUtc();

        // Per-game achievement cache
        List<string> GetCachedGameIds();
        GameAchievementData LoadGameData(string key);
        CacheWriteResult SaveGameData(string key, GameAchievementData data);
        void RemoveGameData(Guid playniteGameId);
        void RemoveGameCache(Guid playniteGameId);
        void NotifyCacheInvalidated();

        void ClearCache();

        // Debug export
        string ExportDatabaseToCsv(string exportDirectory);
    }
}
