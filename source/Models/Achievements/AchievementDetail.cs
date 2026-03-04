using System;
using System.Runtime.Serialization;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Individual achievement detail with schema metadata and user unlock progress.
    /// </summary>
    public sealed class AchievementDetail
    {
        private bool? _unlocked;

        public string ApiName { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        public int? Points { get; set; }

        /// <summary>
        /// RetroAchievements TrueRatio (weighted points based on rarity).
        /// Null for providers that don't support scaled points.
        /// </summary>
        public int? ScaledPoints { get; set; }

        /// <summary>
        /// Optional structured category classification.
        /// Allowed canonical values: Default, Base, DLC, Singleplayer, Multiplayer.
        /// </summary>
        public string CategoryType { get; set; }

        public string Category { get; set; }

        /// <summary>
        /// Optional provider-specific type (used by PSN: "platinum", "gold", "silver", "bronze").
        /// Null/empty for most providers.
        /// </summary>
        public string TrophyType { get; set; }

        /// <summary>
        /// Indicates this achievement marks game completion.
        /// Auto-set to true for platinum trophies; can be manually configured via Capstone control.
        /// </summary>
        public bool IsCapstone { get; set; }

        [IgnoreDataMember]
        public string IconDisplay => UnlockedIconPath ?? AchievementIconResolver.GetDefaultIcon();

        public bool Hidden { get; set; }

        public DateTime? UnlockTimeUtc { get; set; }

        public double? GlobalPercentUnlocked { get; set; }

        /// <summary>
        /// Current progress value for achievements with partial completion (e.g., 25 out of 100).
        /// Null when the provider doesn't support progress or the achievement is all-or-nothing.
        /// </summary>
        public int? ProgressNum { get; set; }

        /// <summary>
        /// Total required value for progress tracking (e.g., 100 for "Kill 100 enemies").
        /// Null when the provider doesn't support progress or the achievement is all-or-nothing.
        /// </summary>
        public int? ProgressDenom { get; set; }


        /// <summary>
        /// Success Story-compatible properties
        /// </summary>


        public bool Unlocked
        {
            get => _unlocked ?? UnlockTimeUtc.HasValue;
            set => _unlocked = value;
        }

        [IgnoreDataMember]
        public bool IsUnlock => Unlocked;

        [IgnoreDataMember]
        public bool IsHidden => Hidden;

        [IgnoreDataMember]
        public string Name => DisplayName;

        [IgnoreDataMember]
        public string Icon => IconDisplay;

        [IgnoreDataMember]
        public string UnlockedIconDisplay => UnlockedIconPath ?? AchievementIconResolver.GetDefaultIcon();

        [IgnoreDataMember]
        public string LockedIconDisplay => LockedIconPath ?? UnlockedIconPath ?? IconDisplay;

        [IgnoreDataMember]
        public double? Percent
        {
            get
            {
                if (GlobalPercentUnlocked is double v)
                {
                    if (v > 0 && v <= 1) return v * 100.0;
                    return v;
                }

                return null;
            }
        }

        [IgnoreDataMember]
        public bool HasRarity => GlobalPercentUnlocked.HasValue;

        [IgnoreDataMember]
        public RarityTier? Rarity => GlobalPercentUnlocked.HasValue
            ? RarityHelper.GetRarityTier(GlobalPercentUnlocked.Value)
            : (RarityTier?)null;

        [IgnoreDataMember]
        public DateTime? DateUnlocked
        {
            get
            {
                if (!UnlockTimeUtc.HasValue) return null;

                var utc = UnlockTimeUtc.Value;
                if (utc == DateTime.MinValue)
                {
                    return null;
                }
                if (utc.Kind == DateTimeKind.Unspecified)
                {
                    utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                }

                return utc.ToLocalTime();
            }
        }

        [IgnoreDataMember]
        public int GamerScore
        {
            get
            {
                var percent = GlobalPercentUnlocked ?? 100;
                var tier = RarityHelper.GetRarityTier(percent);

                // Use values Aniki expects in its triggers (10/25/50/90/180).
                // - UltraRare -> 180 (platinum-ish)
                // - Rare -> 90 (gold-ish)
                // - Uncommon -> 50 (silver-ish)
                // - Common -> 25 (bronze-ish)
                // - Unknown/0 -> 10 (fallback)
                if (tier == RarityTier.UltraRare) return 180;
                if (tier == RarityTier.Rare) return 90;
                if (tier == RarityTier.Uncommon) return 50;
                if (tier == RarityTier.Common) return 25;
                return 10;
            }
        }
    }
}
