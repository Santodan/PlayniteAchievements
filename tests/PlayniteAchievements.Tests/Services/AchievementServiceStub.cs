using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services
{
    // Test-only seam used to compile RefreshCoordinator source without pulling full plugin dependencies.
    public class AchievementService
    {
        public event EventHandler<ProgressReport> RebuildProgress;
        public event EventHandler CacheInvalidated;

        public List<Guid> LastRefreshedGameIds { get; } = new List<Guid>();
        public List<GameAchievementData> AllGameData { get; set; } = new List<GameAchievementData>();
        public Dictionary<Guid, GameAchievementData> GameDataById { get; } = new Dictionary<Guid, GameAchievementData>();

        public virtual bool ValidateCanStartRefresh()
        {
            return true;
        }

        public virtual Task ExecuteRefreshAsync(RefreshRequest request)
        {
            return Task.CompletedTask;
        }

        public virtual Task ExecuteRefreshAsync(RefreshRequest request, CancellationToken externalToken)
        {
            return ExecuteRefreshAsync(request);
        }

        public virtual GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GameDataById.TryGetValue(playniteGameId, out var data) ? data : null;
        }

        public virtual List<GameAchievementData> GetAllGameAchievementData()
        {
            return AllGameData ?? new List<GameAchievementData>();
        }

        public virtual void CancelCurrentRebuild()
        {
        }

        public void RaiseCacheInvalidated()
        {
            CacheInvalidated?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseRebuildProgress(ProgressReport report)
        {
            RebuildProgress?.Invoke(this, report);
        }
    }
}

