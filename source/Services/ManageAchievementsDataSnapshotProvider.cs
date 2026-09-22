using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;

namespace PlayniteAchievements.Services
{
    public sealed class ManageAchievementsDataSnapshotProvider
    {
        private readonly Guid _gameId;
        private readonly AchievementDataService _achievementDataService;
        private readonly object _sync = new object();

        private GameAchievementData _hydratedGameData;
        private GameAchievementData _rawGameData;
        private readonly ILogger _logger;

        // How often the cached snapshots have been dropped. Carried on the read scopes below so a
        // slow Manage tab shows both what a re-read cost and how much churn forced it: the reads
        // are only expensive because something invalidated them, and the invalidation itself is
        // too cheap to time.
        private int _invalidations;

        public ManageAchievementsDataSnapshotProvider(
            Guid gameId,
            AchievementDataService achievementDataService,
            ILogger logger = null)
        {
            _gameId = gameId;
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _logger = logger;
        }

        public GameAchievementData GetHydratedGameData()
        {
            lock (_sync)
            {
                if (_hydratedGameData == null)
                {
                    // Scoped inside the null check: a cache hit is a field read, and timing it
                    // would bury the misses that actually cost something.
                    using (PerfScope.Start(
                        _logger,
                        "Snapshot.HydratedRead",
                        thresholdMs: 10,
                        context: "invalidations=" + _invalidations))
                    {
                        _hydratedGameData = _achievementDataService.GetGameAchievementData(_gameId);
                    }
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
                    // Manage tabs build their row lists from this copy, so it carries the custom
                    // achievement rows too; only display overlays are left to the hydrated copy.
                    using (PerfScope.Start(
                        _logger,
                        "Snapshot.RawRead",
                        thresholdMs: 10,
                        context: "invalidations=" + _invalidations))
                    {
                        _rawGameData = _achievementDataService.GetRawGameAchievementDataWithCustomAchievements(_gameId);
                    }
                }

                return _rawGameData;
            }
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                _invalidations++;
                _hydratedGameData = null;
                _rawGameData = null;
            }
        }
    }
}
