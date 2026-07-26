using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Cache;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    // Test-only seam for ThemeIntegrationService compilation in isolated test project.
    public class AchievementDataService
    {
        public Dictionary<Guid, GameAchievementData> GameDataById { get; } = new Dictionary<Guid, GameAchievementData>();
        public List<GameAchievementData> AllGameData { get; set; } = new List<GameAchievementData>();
        public Dictionary<Guid, GameAchievementData> VisibleGameDataById { get; } = new Dictionary<Guid, GameAchievementData>();
        public List<GameAchievementData> VisibleAllGameData { get; set; }
        internal CachedSummaryData CachedSummaryDataForTheme { get; set; }
        public int CachedSummaryDataForThemeCalls { get; private set; }
        public int VisibleAllGameDataForThemeCalls { get; private set; }

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

        public virtual List<GameAchievementData> GetAllGameAchievementData()
        {
            return AllGameData ?? new List<GameAchievementData>();
        }

        public virtual List<GameAchievementData> GetAllGameAchievementDataForTheme()
        {
            return GetAllGameAchievementData();
        }

        public virtual List<GameAchievementData> GetAllVisibleGameAchievementDataForTheme()
        {
            VisibleAllGameDataForThemeCalls++;
            return VisibleAllGameData ?? GetAllGameAchievementDataForTheme();
        }

        internal virtual CachedSummaryData GetCachedSummaryDataForTheme(int recentAchievementDetailLimit = 0)
        {
            CachedSummaryDataForThemeCalls++;
            return CachedSummaryDataForTheme;
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
