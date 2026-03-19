using System;
using System.Runtime.Serialization;
using Playnite.SDK.Models;
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
        /// Allowed canonical values: Default, Base, DLC, Singleplayer, Multiplayer, Collectable, Missable.
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

        /// <summary>
        /// Playnite Game reference for theme bindings.
        /// Populated during snapshot building for all-games views.
        /// Not persisted to cache.
        /// </summary>
        [IgnoreDataMember]
        public Game Game { get; set; }

        /// <summary>
        /// Runtime provider key used for non-persisted provider-specific rarity logic.
        /// Hydrated from the parent GameAchievementData.
        /// </summary>
        [IgnoreDataMember]
        public string ProviderKey { get; set; }

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
            get => AchievementRarityResolver.NormalizePercent(GlobalPercentUnlocked);
        }

        [IgnoreDataMember]
        public double RarityPercentValue => Percent ?? 0;

        [IgnoreDataMember]
        public bool HasRarityPercent => Percent.HasValue;

        [IgnoreDataMember]
        public RarityTier? Rarity => AchievementRarityResolver.GetRarityTier(ProviderKey, GlobalPercentUnlocked, Points);

        [IgnoreDataMember]
        public bool HasRarity => Rarity.HasValue;

        [IgnoreDataMember]
        public string RarityText => AchievementRarityResolver.GetDisplayText(ProviderKey, GlobalPercentUnlocked, Points);

        [IgnoreDataMember]
        public string RarityDetailText => AchievementRarityResolver.GetDetailText(ProviderKey, GlobalPercentUnlocked, Points);

        [IgnoreDataMember]
        public double RaritySortValue => AchievementRarityResolver.GetSortValue(ProviderKey, GlobalPercentUnlocked, Points);

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
                var tier = Rarity;
                if (!tier.HasValue)
                {
                    return 10;
                }

                // Use values Aniki expects in its triggers (10/25/50/90/180).
                // - UltraRare -> 180 (platinum-ish)
                // - Rare -> 90 (gold-ish)
                // - Uncommon -> 50 (silver-ish)
                // - Common -> 25 (bronze-ish)
                // - Unknown/0 -> 10 (fallback)
                if (tier.Value == RarityTier.UltraRare) return 180;
                if (tier.Value == RarityTier.Rare) return 90;
                if (tier.Value == RarityTier.Uncommon) return 50;
                if (tier.Value == RarityTier.Common) return 25;
                return 10;
            }
        }
    }
}
