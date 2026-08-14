using System;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The achievement a capstone or goal toggle acts on, flattened to primitives so callers can
    /// build one from any row type (theme <c>AchievementDetail</c>, plugin
    /// <c>AchievementDisplayItem</c>) without the toggle path depending on either.
    /// </summary>
    public struct AchievementMarkerTarget
    {
        public AchievementMarkerTarget(
            Guid gameId,
            string apiName,
            bool isCapstone,
            bool isGoal,
            bool unlocked)
        {
            GameId = gameId;
            ApiName = (apiName ?? string.Empty).Trim();
            IsCapstone = isCapstone;
            IsGoal = isGoal;
            Unlocked = unlocked;
        }

        public Guid GameId { get; }

        public string ApiName { get; }

        /// <summary>
        /// The hydrated capstone flag, which covers provider-assigned capstones as well as the
        /// user's manual one. Not sufficient on its own to decide toggle direction — see
        /// <see cref="AchievementMarkerToggle.IsEffectiveCapstone"/>.
        /// </summary>
        public bool IsCapstone { get; }

        public bool IsGoal { get; }

        public bool Unlocked { get; }

        public bool IsValid => GameId != Guid.Empty && !string.IsNullOrWhiteSpace(ApiName);
    }
}
