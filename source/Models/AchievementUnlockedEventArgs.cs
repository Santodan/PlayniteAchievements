using System;

namespace PlayniteAchievements.Models
{
    public enum UnlockVideoAnchorSource
    {
        Unknown = 0,
        ProviderReported = 1,
        SourceObservation = 2
    }

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

        /// <summary>
        /// The achievement is flagged hidden (secret) in the provider's schema. This describes the
        /// achievement definition, so it stays true after the achievement is unlocked and revealed.
        /// </summary>
        public bool IsHidden { get; set; }

        public int? Points { get; set; }
        public int? ScaledPoints { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }

        /// <summary>
        /// Immutable monitor-level observation time. This is stamped before the event is marshalled
        /// to the UI thread, so capture timing never depends on toast or subscriber latency.
        /// </summary>
        public DateTime ObservedUtc { get; set; }

        /// <summary>
        /// Best provider/source-specific timestamp for placing the synthetic notification in video.
        /// The provider's reported unlock time remains in <see cref="UnlockTimeUtc"/> for metadata.
        /// </summary>
        public DateTime? VideoAnchorUtc { get; set; }

        public UnlockVideoAnchorSource VideoAnchorSource { get; set; }

        /// <summary>
        /// Per-notification identity shared by the toast and recording subscribers. It prevents two
        /// achievements with the same display name (or the same achievement in overlapping games)
        /// from receiving one another's overlay track or chime.
        /// </summary>
        public Guid CaptureCorrelationId { get; set; } = Guid.NewGuid();
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

        /// <summary>
        /// Set for the real-but-manual fire behind the test-notification hotkey. Unlike
        /// <see cref="IsPreview"/> the full flow still runs (screenshot and recording included), but
        /// the captured screenshot and clip are routed to a separate "Test" capture subfolder so
        /// they do not mix with a game's genuine unlock captures.
        /// </summary>
        public bool IsTestFire { get; set; }

        /// <summary>
        /// For fire-test previews only: forces which template renders this notification (the
        /// plugin's own template, or a specific theme mode's override). Null for real unlocks,
        /// which resolve the template normally.
        /// </summary>
        public Services.UI.NotificationTemplatePreviewSource? PreviewTemplateSource { get; set; }

        /// <summary>
        /// For fire-test previews only: the exact style the settings editor is showing. When set,
        /// the notification renders this style verbatim instead of re-resolving from
        /// <see cref="ProviderKey"/> / <see cref="PlayniteGameId"/>, so a fired test matches the
        /// inline mockup exactly (re-resolution could otherwise pick up a different scope's
        /// override — e.g. the sample provider's per-provider style). Null for real unlocks.
        /// </summary>
        public Settings.NotificationStyleSettings PreviewStyleOverride { get; set; }
    }
}
