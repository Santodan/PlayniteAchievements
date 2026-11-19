using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    // Test-only seam for ThemeIntegrationService compilation in isolated test project.
    public class AchievementDataService
    {
        public Dictionary<Guid, GameAchievementData> GameDataById { get; } = new Dictionary<Guid, GameAchievementData>();
        public Dictionary<Guid, GameAchievementData> VisibleGameDataById { get; } = new Dictionary<Guid, GameAchievementData>();
        internal CachedSummaryData CachedSummaryDataForTheme { get; set; }
        public int CachedSummaryDataForThemeCalls { get; private set; }

        /// <summary>The rows each read returns; none by default.</summary>
        internal Func<AchievementQuery, List<CachedRecentUnlockData>> Rows { get; set; }

        internal List<AchievementQuery> RowReads { get; } = new List<AchievementQuery>();

        internal ICacheReadOptimizations CacheReads => null;

        public virtual GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GameDataById.TryGetValue(playniteGameId, out var data) ? data : null;
        }

        public virtual GameAchievementData GetVisibleGameAchievementData(Guid playniteGameId)
        {
            return VisibleGameDataById.TryGetValue(playniteGameId, out var data)
                ? data
                : GetGameAchievementData(playniteGameId);
        }

        internal virtual CachedSummaryData GetCachedSummaryDataForTheme(int recentAchievementDetailLimit = 0)
        {
            CachedSummaryDataForThemeCalls++;
            return CachedSummaryDataForTheme;
        }

        /// <summary>The summary query each call returns; the summary rules alone by default.</summary>
        internal Func<AchievementQuery> Summary { get; set; }

        internal AchievementQuery SummaryQuery() => Summary?.Invoke() ?? AchievementQuery.CurrentUser.WithSummaryRules();

        internal List<CachedRecentUnlockData> ReadRows(AchievementQuery query)
        {
            lock (RowReads)
            {
                RowReads.Add(query);
            }

            return Rows?.Invoke(query) ?? new List<CachedRecentUnlockData>();
        }

        public virtual List<string> GetCachedGameIds()
        {
            var result = new List<string>();
            foreach (var key in GameDataById.Keys)
            {
                result.Add(key.ToString());
            }

            return result;
        }
    }
}
