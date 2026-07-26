using System;

namespace PlayniteAchievements.Models
{
    public sealed class AchievementUnlockedEventArgs : EventArgs
    {
        public Guid PlayniteGameId { get; set; }
        public string GameName { get; set; }
        public string ProviderKey { get; set; }

        /// <summary>
        /// Absolute local paths to the Playnite game's icon and cover art, resolved from the
        /// Playnite database at event creation. Null when the game has no art (e.g. previews).
        /// </summary>
        public string GameIconPath { get; set; }
        public string GameCoverPath { get; set; }

        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string IconPath { get; set; }
        public double? GlobalPercent { get; set; }
        public string RarityTier { get; set; }
        public string TrophyType { get; set; }
        public bool IsHardcore { get; set; }
        public int? Points { get; set; }
        public int? ScaledPoints { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public int UnlockedCount { get; set; }
        public int TotalCount { get; set; }

        /// <summary>
        /// True when the game is complete after this unlock (all achievements unlocked, or the
        /// capstone unlocked) — the "completion achievement" state on a real unlock, distinct
        /// from the standalone IsGameCompleted notification.
        /// </summary>
        public bool IsCompletionAchievement { get; set; }

        public bool IsFriendUnlock { get; set; }
        public string FriendExternalUserId { get; set; }
        public string FriendDisplayName { get; set; }
        public string FriendAvatarPath { get; set; }
        public string FriendAvatarUrl { get; set; }
        public bool IsCapstone { get; set; }

        /// <summary>
        /// True for the standalone "Congratulations! Game Complete!" notification emitted in its
        /// own wave after the completing unlock's toasts. It runs the full notification pipeline
        /// like any other own unlock: toasts, screenshots, and recording clips.
        /// </summary>
        public bool IsGameCompleted { get; set; }

        /// <summary>
        /// 1-based position of this achievement within the game's provider/custom sort order.
        /// Used for stable, interpretable screenshot filenames. 0 when unknown (e.g. friends).
        /// </summary>
        public int AchievementNumber { get; set; }

        /// <summary>
        /// Set for example/test toasts fired from the settings preview. Bypasses the
        /// notification enablement gates in <see cref="Services.UI.ToastNotificationService"/> so
        /// the toast always shows on screen regardless of the user's enable toggles.
        /// </summary>
        public bool IsPreview { get; set; }
    }
}
