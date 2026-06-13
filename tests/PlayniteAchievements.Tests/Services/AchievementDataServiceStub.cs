using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services
{
    // Test-only seam for ThemeIntegrationService compilation in isolated test project.
    public class AchievementDataService
    {
        public Dictionary<Guid, GameAchievementData> GameDataById { get; } = new Dictionary<Guid, GameAchievementData>();
        public List<GameAchievementData> AllGameData { get; set; } = new List<GameAchievementData>();
        public Dictionary<Guid, GameAchievementData> VisibleGameDataById { get; } = new Dictionary<Guid, GameAchievementData>();
        public List<GameAchievementData> VisibleAllGameData { get; set; }
        internal CachedSummaryData CachedSummaryDataForSidebar { get; set; }

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
            return VisibleAllGameData ?? GetAllGameAchievementDataForTheme();
        }

        internal CachedSummaryData GetCachedSummaryDataForSidebar(int recentAchievementDetailLimit = 0)
        {
            return CachedSummaryDataForSidebar;
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

    internal sealed class CachedSummaryData
    {
        public List<CachedGameSummaryData> Games { get; set; } = new List<CachedGameSummaryData>();
    }

    internal sealed class CachedGameSummaryData
    {
        public int TotalAchievements { get; set; }
        public int UnlockedAchievements { get; set; }
        public int CommonCount { get; set; }
        public int UncommonCount { get; set; }
        public int RareCount { get; set; }
        public int UltraRareCount { get; set; }
        public bool HasAchievements { get; set; }
    }
}
