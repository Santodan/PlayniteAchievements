using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Summary information after a cache rebuild operation completes.
    /// </summary>
    public sealed class RebuildSummary
    {
        public int GamesRefreshed { get; set; }
        public int GamesWithAchievements { get; set; }
        public int GamesWithoutAchievements { get; set; }

        /// <summary>
        /// List of game IDs that were actually refreshed.
        /// </summary>
        public List<Guid> RefreshedGameIds { get; set; } = new List<Guid>();
    }

    /// <summary>
    /// Payload information for cache rebuild events.
    /// </summary>
    public sealed class RebuildPayload
    {
        public RebuildSummary Summary { get; set; } = new RebuildSummary();
        public bool AuthRequired { get; set; }
    }
}
