using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Unified request model for triggering refresh operations from UI and services.
    /// </summary>
    public sealed class RefreshRequest
    {
        /// <summary>
        /// Refresh mode enum, when known at compile-time.
        /// </summary>
        public RefreshModeType? Mode { get; set; }

        /// <summary>
        /// Refresh mode key, primarily for UI flows that store mode as string.
        /// </summary>
        public string ModeKey { get; set; }

        /// <summary>
        /// Optional game id for single-game refresh mode.
        /// </summary>
        public Guid? SingleGameId { get; set; }

        /// <summary>
        /// Optional explicit game targets. When set, this takes precedence over mode.
        /// </summary>
        public IReadOnlyCollection<Guid> GameIds { get; set; }

        /// <summary>
        /// Unified refresh options for current-user and friend refreshes.
        /// </summary>
        public RefreshOptions Options { get; set; }
    }
}
