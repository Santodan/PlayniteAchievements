using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Represents a manual achievement link for a Playnite game.
    /// Stores the source (e.g., Steam), source game ID, and unlock times.
    /// The full achievement schema is stored in the cache and rebuilt on refresh.
    /// </summary>
    public sealed class ManualAchievementLink
    {
        /// <summary>
        /// The source key identifying where achievements come from (e.g., "Steam", "Exophase").
        /// </summary>
        public string SourceKey { get; set; }

        /// <summary>
        /// The game ID in the source system (e.g., Steam AppID as string).
        /// </summary>
        public string SourceGameId { get; set; }

        /// <summary>
        /// Dictionary mapping achievement ApiName to unlock time (nullable).
        /// ApiName is the stable identifier (e.g., Steam's internal_name) that uniquely
        /// identifies an achievement within a game.
        /// </summary>
        public Dictionary<string, DateTime?> UnlockTimes { get; set; } = new Dictionary<string, DateTime?>();

        /// <summary>
        /// Dictionary mapping achievement ApiName to unlocked state.
        /// This allows representing unlocked achievements with unknown unlock time (null timestamp).
        /// </summary>
        public Dictionary<string, bool> UnlockStates { get; set; } = new Dictionary<string, bool>();

        /// <summary>
        /// UTC timestamp when this link was first created.
        /// </summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// UTC timestamp when this link was last modified.
        /// </summary>
        public DateTime LastModifiedUtc { get; set; }

        /// <summary>
        /// Creates a deep copy of this ManualAchievementLink.
        /// </summary>
        public ManualAchievementLink Clone()
        {
            return new ManualAchievementLink
            {
                SourceKey = this.SourceKey,
                SourceGameId = this.SourceGameId,
                UnlockTimes = this.UnlockTimes != null
                    ? new Dictionary<string, DateTime?>(this.UnlockTimes)
                    : new Dictionary<string, DateTime?>(),
                UnlockStates = this.UnlockStates != null
                    ? new Dictionary<string, bool>(this.UnlockStates)
                    : new Dictionary<string, bool>(),
                CreatedUtc = this.CreatedUtc,
                LastModifiedUtc = this.LastModifiedUtc
            };
        }
    }
}
