using PlayniteAchievements.Models.Achievements;
using System;

namespace PlayniteAchievements.Services
{
    public sealed class GameOptionsDataSnapshotProvider
    {
        private readonly Guid _gameId;
        private readonly AchievementDataService _achievementDataService;
        private readonly object _sync = new object();

        private GameAchievementData _hydratedGameData;
        private GameAchievementData _rawGameData;

        public GameOptionsDataSnapshotProvider(
            Guid gameId,
            AchievementDataService achievementDataService)
        {
            _gameId = gameId;
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
        }

        public GameAchievementData GetHydratedGameData()
        {
            lock (_sync)
            {
                if (_hydratedGameData == null)
                {
                    _hydratedGameData = _achievementDataService.GetGameAchievementData(_gameId);
                }

                return _hydratedGameData;
            }
        }

        public GameAchievementData GetRawGameData()
        {
            lock (_sync)
            {
                if (_rawGameData == null)
                {
                    _rawGameData = _achievementDataService.GetRawGameAchievementData(_gameId);
                }

                return _rawGameData;
            }
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                _hydratedGameData = null;
                _rawGameData = null;
            }
        }
    }
}
