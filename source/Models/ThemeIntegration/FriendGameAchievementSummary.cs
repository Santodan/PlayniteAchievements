using System;
using System.Runtime.Serialization;
using System.Windows.Input;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Friends;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Theme-facing contract for a single friend-aggregated game summary.
    /// Extends <see cref="GameAchievementSummary"/> so friend game bindings share the
    /// same rarity-stat objects, progress, trophy counts, and window commands as library
    /// games, and adds the friend-specific fields (friend counts, friend playtime, and
    /// friend unlock/scrape metadata).
    /// </summary>
    public sealed class FriendGameAchievementSummary : GameAchievementSummary
    {
        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicFriendScopeProviderCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicFriendScopeGameCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public string FriendGameScopeKey => FriendOverviewProjection.BuildGameUnlockKey(
            ProviderKey,
            ProviderGameKey,
            AppId,
            GameId != Guid.Empty ? GameId : (Guid?)null) ?? FriendOverviewProjection.AllScopeKey;

        [DontSerialize]
        [IgnoreDataMember]
        public string Key => FriendGameScopeKey;

        [DontSerialize]
        [IgnoreDataMember]
        public string Label => GameName;

        [DontSerialize]
        [IgnoreDataMember]
        public int Count => FriendUnlockedAchievementsCount;

        private int _friendCount;
        private int _friendsWithUnlocksCount;
        private int _friendUnlockedAchievementsCount;
        private int _uniqueFriendUnlockedAchievementsCount;
        private long _totalFriendPlaytimeMinutes;
        private long _averageFriendPlaytimeMinutes;
        private DateTime? _lastFriendUnlockUtc;
        private DateTime? _lastFriendPlayedUtc;
        private DateTime? _lastFriendScrapedUtc;
        private string _lastFriendScrapeStatus;

        public FriendGameAchievementSummary(
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
            ICommand openAchievementWindow = null,
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
            int achievementCount = 0,
            ICommand openViewAchievementsWindow = null,
            ICommand openManageAchievementsWindow = null,
            string sortingName = null)
            : base(
                gameId,
                name,
                platform,
                coverImagePath,
                progress,
                goldCount,
                silverCount,
                bronzeCount,
                isCompleted,
                lastUnlockDate,
                openAchievementWindow,
                common,
                uncommon,
                rare,
                ultraRare,
                rareAndUltraRare,
                overall,
                providerKey,
                providerName,
                lastPlayed,
                unlockedCount,
                achievementCount,
                openViewAchievementsWindow,
                openManageAchievementsWindow,
                sortingName)
        {
        }

        /// <summary>
        /// Provider app id for the game, when available. Zero when the game is only
        /// identified by its Playnite game id.
        /// </summary>
        public int AppId { get; set; }

        public string ProviderGameKey { get; set; }

        public int CollectionScore { get; set; }

        public int CollectionScoreTotal { get; set; }

        public int PrestigeScore { get; set; }

        public int PrestigeScoreTotal { get; set; }

        public int Points { get; set; }

        public int TrophyPlatinumCount { get; set; }

        public int TrophyGoldCount { get; set; }

        public int TrophySilverCount { get; set; }

        public int TrophyBronzeCount { get; set; }

        public int TrophyPlatinumTotal { get; set; }

        public int TrophyGoldTotal { get; set; }

        public int TrophySilverTotal { get; set; }

        public int TrophyBronzeTotal { get; set; }

        public bool HasTrophyTypes => TrophyPlatinumCount > 0 ||
                                      TrophyGoldCount > 0 ||
                                      TrophySilverCount > 0 ||
                                      TrophyBronzeCount > 0;

        /// <summary>
        /// Legacy alias for <see cref="GameAchievementSummary.Name"/> used by friend theme bindings.
        /// </summary>
        public string GameName => Name;

        /// <summary>
        /// Number of friends who own this game.
        /// </summary>
        public int FriendCount
        {
            get => _friendCount;
            set
            {
                if (SetValueAndReturn(ref _friendCount, Math.Max(0, value)))
                {
                    OnPropertyChanged(nameof(FriendCompletionPercent));
                    OnPropertyChanged(nameof(FriendCompletionText));
                }
            }
        }

        /// <summary>
        /// Number of friends with at least one unlock in this game.
        /// </summary>
        public int FriendsWithUnlocksCount
        {
            get => _friendsWithUnlocksCount;
            set => SetValue(ref _friendsWithUnlocksCount, Math.Max(0, value));
        }

        /// <summary>
        /// Alias for <see cref="FriendUnlockedAchievementsCount"/>.
        /// </summary>
        public int UnlockedAchievementsCount
        {
            get => FriendUnlockedAchievementsCount;
            set => FriendUnlockedAchievementsCount = value;
        }

        /// <summary>
        /// Total number of friend unlocks across all friends (counts duplicates).
        /// </summary>
        public int FriendUnlockedAchievementsCount
        {
            get => _friendUnlockedAchievementsCount;
            set
            {
                if (SetValueAndReturn(ref _friendUnlockedAchievementsCount, Math.Max(0, value)))
                {
                    OnPropertyChanged(nameof(Count));
                }
            }
        }

        /// <summary>
        /// Number of distinct achievements at least one friend has unlocked.
        /// </summary>
        public int UniqueFriendUnlockedAchievementsCount
        {
            get => _uniqueFriendUnlockedAchievementsCount;
            set
            {
                if (SetValueAndReturn(ref _uniqueFriendUnlockedAchievementsCount, Math.Max(0, value)))
                {
                    OnPropertyChanged(nameof(FriendCompletionPercent));
                    OnPropertyChanged(nameof(FriendCompletionText));
                }
            }
        }

        public int FriendCompletionPercent => AchievementCount > 0
            ? (int)Math.Round(Math.Max(0, UniqueFriendUnlockedAchievementsCount) * 100d / AchievementCount)
            : 0;

        public string FriendCompletionText => AchievementCount > 0
            ? PercentFormatter.FormatWhole(FriendCompletionPercent)
            : string.Empty;

        public DateTime? LastFriendUnlockUtc
        {
            get => _lastFriendUnlockUtc;
            set
            {
                if (SetValueAndReturn(ref _lastFriendUnlockUtc, value))
                {
                    OnPropertyChanged(nameof(LastFriendUnlockLocal));
                }
            }
        }

        public DateTime? LastFriendUnlockLocal => LastFriendUnlockUtc?.ToLocalTime();

        public long TotalFriendPlaytimeMinutes
        {
            get => _totalFriendPlaytimeMinutes;
            set
            {
                if (SetValueAndReturn(ref _totalFriendPlaytimeMinutes, Math.Max(0, value)))
                {
                    OnPropertyChanged(nameof(TotalFriendPlaytimeSeconds));
                    OnPropertyChanged(nameof(TotalFriendPlaytimeText));
                }
            }
        }

        public ulong TotalFriendPlaytimeSeconds => (ulong)Math.Max(0, TotalFriendPlaytimeMinutes) * 60UL;

        public string TotalFriendPlaytimeText =>
            PlayniteGameMetadataFormatter.FormatPlaytime(TotalFriendPlaytimeSeconds);

        public long AverageFriendPlaytimeMinutes
        {
            get => _averageFriendPlaytimeMinutes;
            set
            {
                if (SetValueAndReturn(ref _averageFriendPlaytimeMinutes, Math.Max(0, value)))
                {
                    OnPropertyChanged(nameof(AverageFriendPlaytimeSeconds));
                    OnPropertyChanged(nameof(AverageFriendPlaytimeText));
                }
            }
        }

        public ulong AverageFriendPlaytimeSeconds => (ulong)Math.Max(0, AverageFriendPlaytimeMinutes) * 60UL;

        public string AverageFriendPlaytimeText =>
            PlayniteGameMetadataFormatter.FormatPlaytime(AverageFriendPlaytimeSeconds);

        public DateTime? LastFriendPlayedUtc
        {
            get => _lastFriendPlayedUtc;
            set
            {
                if (SetValueAndReturn(ref _lastFriendPlayedUtc, value))
                {
                    OnPropertyChanged(nameof(LastFriendPlayedLocal));
                }
            }
        }

        public DateTime? LastFriendPlayedLocal => LastFriendPlayedUtc?.ToLocalTime();

        public DateTime? LastFriendScrapedUtc
        {
            get => _lastFriendScrapedUtc;
            set
            {
                if (SetValueAndReturn(ref _lastFriendScrapedUtc, value))
                {
                    OnPropertyChanged(nameof(LastFriendScrapedLocal));
                }
            }
        }

        public DateTime? LastFriendScrapedLocal => LastFriendScrapedUtc?.ToLocalTime();

        public string LastFriendScrapeStatus
        {
            get => _lastFriendScrapeStatus;
            set
            {
                if (SetValueAndReturn(ref _lastFriendScrapeStatus, value))
                {
                    OnPropertyChanged(nameof(LastFriendScrapeStatusDisplay));
                }
            }
        }

        public string LastFriendScrapeStatusDisplay => string.IsNullOrWhiteSpace(LastFriendScrapeStatus)
            ? string.Empty
            : LastFriendScrapeStatus;
    }
}
