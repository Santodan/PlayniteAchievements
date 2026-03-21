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

        public virtual GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GameDataById.TryGetValue(playniteGameId, out var data) ? data : null;
        }

        public virtual List<GameAchievementData> GetAllGameAchievementData()
        {
            return AllGameData ?? new List<GameAchievementData>();
        }
    }
}
