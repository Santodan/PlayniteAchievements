using System;
using System.Windows.Input;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Summary of achievement progress for a single game, used in all-games overview displays.
    /// Represents a "trophy card" showing progress, trophy counts, and metadata for one game.
    /// </summary>
    public sealed class GameAchievementSummary : ObservableObject
    {
        private int _progress;
        private int _goldCount;
        private int _silverCount;
        private int _bronzeCount;

        /// <summary>
        /// Unique identifier for the game.
        /// </summary>
        public Guid GameId { get; }

        /// <summary>
        /// Per-game stats for the common rarity tier.
        /// </summary>
        public AchievementRarityStats Common { get; }

        /// <summary>
        /// Per-game stats for the uncommon rarity tier.
        /// </summary>
        public AchievementRarityStats Uncommon { get; }

        /// <summary>
        /// Per-game stats for the rare rarity tier.
        /// </summary>
        public AchievementRarityStats Rare { get; }

        /// <summary>
        /// Per-game stats for the ultra-rare rarity tier.
        /// </summary>
        public AchievementRarityStats UltraRare { get; }

        /// <summary>
        /// Per-game combined stats for rare and ultra-rare achievements.
        /// </summary>
        public AchievementRarityStats RareAndUltraRare { get; }

        /// <summary>
        /// Per-game aggregate across all rarity-classified achievements.
        /// </summary>
        public AchievementRarityStats Overall { get; }

        /// <summary>
        /// Game name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Platform/source name (e.g., "Steam", "GOG").
        /// </summary>
        public string Platform { get; }

        /// <summary>
        /// Stable provider key used for theme filtering and icon triggers.
        /// </summary>
        public string ProviderKey { get; }

        /// <summary>
        /// Localized provider name for theme display.
        /// </summary>
        public string ProviderName { get; }

        /// <summary>
        /// Full path to the game's cover image.
        /// </summary>
        public string CoverImagePath { get; }

        /// <summary>
        /// Legacy alias for CoverImagePath used by older fullscreen themes.
        /// </summary>
        public string CoverImageObject => CoverImagePath;

        /// <summary>
        /// Legacy alias for CoverImagePath used by older cached-cover bindings.
        /// </summary>
        public string CoverImageObjectCached => CoverImagePath;

        /// <summary>
        /// Achievement completion percentage (0-100).
        /// Writable by design: some fullscreen themes rely on default TwoWay
        /// binding behavior for ProgressBar.Value.
        /// </summary>
        public int Progress
        {
            get => _progress;
            set => SetValue(ref _progress, Math.Max(0, Math.Min(100, value)));
        }

        /// <summary>
        /// Number of ultra-rare achievements unlocked (gold trophy equivalent).
        /// Writable by design for compatibility with legacy fullscreen bindings.
        /// </summary>
        public int GoldCount
        {
            get => _goldCount;
            set => SetValue(ref _goldCount, Math.Max(0, value));
        }

        /// <summary>
        /// Number of uncommon achievements unlocked (silver trophy equivalent).
        /// Writable by design for compatibility with legacy fullscreen bindings.
        /// </summary>
        public int SilverCount
        {
            get => _silverCount;
            set => SetValue(ref _silverCount, Math.Max(0, value));
        }

        /// <summary>
        /// Number of common achievements unlocked (bronze trophy equivalent).
        /// Writable by design for compatibility with legacy fullscreen bindings.
        /// </summary>
        public int BronzeCount
        {
            get => _bronzeCount;
            set => SetValue(ref _bronzeCount, Math.Max(0, value));
        }

        /// <summary>
        /// Provider-aware completion flag for the game.
        /// True when the provider marks the game complete (for example, PSN platinum earned)
        /// or when all tracked achievements are unlocked.
        /// </summary>
        public bool IsCompleted { get; }

        /// <summary>
        /// Date of the most recent achievement unlock.
        /// </summary>
        public DateTime LastUnlockDate { get; }

        /// <summary>
        /// Date the game was last played in Playnite, when available.
        /// </summary>
        public DateTime? LastPlayed { get; }

        /// <summary>
        /// Number of unlocked achievements for this game.
        /// </summary>
        public int UnlockedCount { get; }

        /// <summary>
        /// Total tracked achievements for this game.
        /// </summary>
        public int AchievementCount { get; }

        /// <summary>
        /// Command to open the detailed achievement view for this game.
        /// </summary>
        public ICommand OpenAchievementWindow { get; }

        #region Legacy Aliases for Aniki ReMake/Helper Compatibility

        /// <summary>
        /// Legacy alias for GoldCount (Aniki ReMake compatibility).
        /// </summary>
        public int GS90Count => GoldCount;

        /// <summary>
        /// Legacy alias for SilverCount (Aniki ReMake compatibility).
        /// </summary>
        public int GS30Count => SilverCount;

        /// <summary>
        /// Legacy alias for BronzeCount (Aniki ReMake compatibility).
        /// </summary>
        public int GS15Count => BronzeCount;

        /// <summary>
        /// Legacy alias for LastUnlockDate (Aniki ReMake compatibility).
        /// </summary>
        public DateTime LatestUnlocked => LastUnlockDate;

        #endregion

        public GameAchievementSummary(
            Guid gameId,
            string name,
            string platform,
            string coverImagePath,
            int progress,
            int goldCount,
            int silverCount,
            int bronzeCount,
            bool isCompleted,
            DateTime lastUnlockDate,
            ICommand openAchievementWindow,
            AchievementRarityStats common = null,
            AchievementRarityStats uncommon = null,
            AchievementRarityStats rare = null,
            AchievementRarityStats ultraRare = null,
            AchievementRarityStats rareAndUltraRare = null,
            AchievementRarityStats overall = null,
            string providerKey = null,
            string providerName = null,
            DateTime? lastPlayed = null,
            int unlockedCount = 0,
            int achievementCount = 0)
        {
            GameId = gameId;
            Name = name ?? string.Empty;
            Platform = platform ?? "Unknown";
            ProviderKey = providerKey ?? string.Empty;
            ProviderName = providerName ?? string.Empty;
            CoverImagePath = string.IsNullOrWhiteSpace(coverImagePath)
                ? null
                : coverImagePath.Trim();
            _progress = Math.Max(0, Math.Min(100, progress));
            _goldCount = Math.Max(0, goldCount);
            _silverCount = Math.Max(0, silverCount);
            _bronzeCount = Math.Max(0, bronzeCount);
            IsCompleted = isCompleted;
            LastUnlockDate = lastUnlockDate;
            LastPlayed = lastPlayed;
            UnlockedCount = Math.Max(0, unlockedCount);
            AchievementCount = Math.Max(0, achievementCount);
            OpenAchievementWindow = openAchievementWindow;
            Common = common ?? new AchievementRarityStats();
            Uncommon = uncommon ?? new AchievementRarityStats();
            Rare = rare ?? new AchievementRarityStats();
            UltraRare = ultraRare ?? new AchievementRarityStats();
            RareAndUltraRare = rareAndUltraRare ?? AchievementRarityStatsCombiner.Combine(Rare, UltraRare);
            Overall = overall ?? AchievementRarityStatsCombiner.Combine(Common, Uncommon, Rare, UltraRare);
        }
    }
}
