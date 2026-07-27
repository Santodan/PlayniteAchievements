using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Friends;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows.Input;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Items
{
    public sealed class FriendSummaryItem : ObservableObject
    {
        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicFriendScopeProviderCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicFriendScopeUserCommand { get; set; }

        private string _displayName;
        private string _avatarPath;
        private int _sharedGamesCount;
        private int _gamesWithUnlocksCount;
        private int _completedGamesCount;
        private int _unlockedAchievementsCount;
        private int _collectionScore;
        private int _prestigeScore;
        private int _collectionLevel;
        private int _prestigeLevel;
        private int _recentUnlockCount;
        private int _commonCount;
        private int _uncommonCount;
        private int _rareCount;
        private int _ultraRareCount;
        private int _trophyPlatinumCount;
        private int _trophyGoldCount;
        private int _trophySilverCount;
        private int _trophyBronzeCount;
        private DateTime? _lastUnlockUtc;
        private DateTime? _lastRefreshedUtc;
        private long _totalPlaytimeMinutes;
        private string _providerKey;
        private string _externalUserId;
        private string _mergedFriendId;

        public string ProviderKey
        {
            get => _providerKey;
            set
            {
                if (SetValueAndReturn(ref _providerKey, value))
                {
                    OnPropertyChanged(nameof(FriendScopeKey));
                    OnPropertyChanged(nameof(Key));
                }
            }
        }

        public string ExternalUserId
        {
            get => _externalUserId;
            set
            {
                if (SetValueAndReturn(ref _externalUserId, value))
                {
                    OnPropertyChanged(nameof(FriendScopeKey));
                    OnPropertyChanged(nameof(Key));
                }
            }
        }

        public string MergedFriendId
        {
            get => _mergedFriendId;
            set
            {
                if (SetValueAndReturn(ref _mergedFriendId, value))
                {
                    OnPropertyChanged(nameof(IsMergedFriend));
                    OnPropertyChanged(nameof(FriendScopeKey));
                    OnPropertyChanged(nameof(Key));
                    OnPropertyChanged(nameof(ProviderDisplayName));
                    OnPropertyChanged(nameof(ProviderIconKey));
                    OnPropertyChanged(nameof(ProviderColorHex));
                }
            }
        }

        public List<FriendAccountRef> MemberAccounts { get; set; } = new List<FriendAccountRef>();

        public List<string> MemberProviderKeys { get; set; } = new List<string>();

        [DontSerialize]
        [IgnoreDataMember]
        public bool IsMergedFriend => !string.IsNullOrWhiteSpace(MergedFriendId);

        [DontSerialize]
        [IgnoreDataMember]
        public string FriendScopeKey => FriendOverviewProjection.GetFriendScopeKey(this);

        [DontSerialize]
        [IgnoreDataMember]
        public string Key => FriendScopeKey;

        [DontSerialize]
        [IgnoreDataMember]
        public string Label => DisplayName;

        [DontSerialize]
        [IgnoreDataMember]
        public int Count => UnlockedAchievementsCount;

        public string ProviderDisplayName
        {
            get
            {
                if (IsMergedFriend)
                {
                    return MemberProviderDisplayText ??
                           ResourceProvider.GetString("LOCPlayAch_FriendsSettings_Merged");
                }

                var localized = PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName(ProviderKey);
                return string.IsNullOrWhiteSpace(localized) ? ProviderKey : localized;
            }
        }

        public string MemberProviderDisplayText
        {
            get
            {
                var providers = (MemberProviderKeys ?? new List<string>())
                    .Where(provider => !string.IsNullOrWhiteSpace(provider))
                    .Select(provider =>
                    {
                        var localized = PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName(provider);
                        return string.IsNullOrWhiteSpace(localized) ? provider : localized;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (providers.Count == 0)
                {
                    return null;
                }

                return string.Join(" + ", providers);
            }
        }

        public string ProviderIconKey => IsMergedFriend || string.IsNullOrWhiteSpace(ProviderKey) ? null : "ProviderIcon" + ProviderKey;
        public string ProviderColorHex =>
            IsMergedFriend ? null : PlayniteAchievements.Providers.ProviderRegistry.GetProviderColorHex(ProviderKey);

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetValueAndReturn(ref _displayName, value))
                {
                    OnPropertyChanged(nameof(Label));
                }
            }
        }

        public string AvatarPath
        {
            get => _avatarPath;
            set => SetValue(ref _avatarPath, value);
        }

        public int SharedGamesCount
        {
            get => _sharedGamesCount;
            set
            {
                if (SetValueAndReturn(ref _sharedGamesCount, value))
                {
                    OnPropertyChanged(nameof(CountsText));
                }
            }
        }

        public int GamesWithUnlocksCount
        {
            get => _gamesWithUnlocksCount;
            set => SetValue(ref _gamesWithUnlocksCount, value);
        }

        // Number of the friend's games that are 100% complete (all achievements unlocked),
        // aggregated across all their games. Matches the SharedGamesCount/GamesWithUnlocksCount convention.
        public int CompletedGamesCount
        {
            get => _completedGamesCount;
            set
            {
                if (SetValueAndReturn(ref _completedGamesCount, value))
                {
                    OnPropertyChanged(nameof(CompletedGames));
                }
            }
        }

        public int UnlockedAchievementsCount
        {
            get => _unlockedAchievementsCount;
            set
            {
                if (SetValueAndReturn(ref _unlockedAchievementsCount, value))
                {
                    OnPropertyChanged(nameof(CountsText));
                    OnPropertyChanged(nameof(Count));
                }
            }
        }

        public int CollectionScore
        {
            get => _collectionScore;
            set => SetValue(ref _collectionScore, Math.Max(0, value));
        }

        public int PrestigeScore
        {
            get => _prestigeScore;
            set => SetValue(ref _prestigeScore, Math.Max(0, value));
        }

        public int CollectionLevel
        {
            get => _collectionLevel;
            set => SetValue(ref _collectionLevel, Math.Max(0, value));
        }

        public int PrestigeLevel
        {
            get => _prestigeLevel;
            set => SetValue(ref _prestigeLevel, Math.Max(0, value));
        }

        public int RecentUnlockCount
        {
            get => _recentUnlockCount;
            set => SetValue(ref _recentUnlockCount, value);
        }

        // Per-friend unlocked counts by rarity tier, aggregated across all of the friend's games.
        // Named to match GameSummaryItem/FriendGameSummaryItem.
        public int CommonCount
        {
            get => _commonCount;
            set
            {
                if (SetValueAndReturn(ref _commonCount, value))
                {
                    OnPropertyChanged(nameof(TotalCommon));
                    OnPropertyChanged(nameof(TotalOverall));
                }
            }
        }

        public int UncommonCount
        {
            get => _uncommonCount;
            set
            {
                if (SetValueAndReturn(ref _uncommonCount, value))
                {
                    OnPropertyChanged(nameof(TotalUncommon));
                    OnPropertyChanged(nameof(TotalOverall));
                }
            }
        }

        public int RareCount
        {
            get => _rareCount;
            set
            {
                if (SetValueAndReturn(ref _rareCount, value))
                {
                    OnPropertyChanged(nameof(TotalRare));
                    OnPropertyChanged(nameof(TotalRareAndUltraRare));
                    OnPropertyChanged(nameof(TotalOverall));
                }
            }
        }

        public int UltraRareCount
        {
            get => _ultraRareCount;
            set
            {
                if (SetValueAndReturn(ref _ultraRareCount, value))
                {
                    OnPropertyChanged(nameof(TotalUltraRare));
                    OnPropertyChanged(nameof(TotalRareAndUltraRare));
                    OnPropertyChanged(nameof(TotalOverall));
                }
            }
        }

        // Per-friend unlocked trophy counts, aggregated across all of the friend's games.
        public int TrophyPlatinumCount
        {
            get => _trophyPlatinumCount;
            set
            {
                if (SetValueAndReturn(ref _trophyPlatinumCount, value))
                {
                    OnPropertyChanged(nameof(PlatinumTrophies));
                    OnPropertyChanged(nameof(TotalTrophies));
                }
            }
        }

        public int TrophyGoldCount
        {
            get => _trophyGoldCount;
            set
            {
                if (SetValueAndReturn(ref _trophyGoldCount, value))
                {
                    OnPropertyChanged(nameof(GoldTrophies));
                    OnPropertyChanged(nameof(TotalTrophies));
                }
            }
        }

        public int TrophySilverCount
        {
            get => _trophySilverCount;
            set
            {
                if (SetValueAndReturn(ref _trophySilverCount, value))
                {
                    OnPropertyChanged(nameof(SilverTrophies));
                    OnPropertyChanged(nameof(TotalTrophies));
                }
            }
        }

        public int TrophyBronzeCount
        {
            get => _trophyBronzeCount;
            set
            {
                if (SetValueAndReturn(ref _trophyBronzeCount, value))
                {
                    OnPropertyChanged(nameof(BronzeTrophies));
                    OnPropertyChanged(nameof(TotalTrophies));
                }
            }
        }

        // Theme-facing rarity contract. Names mirror LibraryRuntimeState's Total* convention,
        // since a friend summary is a cross-game rollup of one friend's whole library.
        // Unlocked-only: no possible-total denominator, so Total == Unlocked and Locked == 0.
        [DontSerialize]
        [IgnoreDataMember]
        public AchievementRarityStats TotalCommon => UnlockedOnlyStats(CommonCount);

        [DontSerialize]
        [IgnoreDataMember]
        public AchievementRarityStats TotalUncommon => UnlockedOnlyStats(UncommonCount);

        [DontSerialize]
        [IgnoreDataMember]
        public AchievementRarityStats TotalRare => UnlockedOnlyStats(RareCount);

        [DontSerialize]
        [IgnoreDataMember]
        public AchievementRarityStats TotalUltraRare => UnlockedOnlyStats(UltraRareCount);

        [DontSerialize]
        [IgnoreDataMember]
        public AchievementRarityStats TotalRareAndUltraRare =>
            AchievementRarityStatsCombiner.Combine(TotalRare, TotalUltraRare);

        [DontSerialize]
        [IgnoreDataMember]
        public AchievementRarityStats TotalOverall =>
            AchievementRarityStatsCombiner.Combine(TotalCommon, TotalUncommon, TotalRare, TotalUltraRare);

        // Theme-facing trophy contract. Flat ints mirroring LibraryRuntimeState's trophy convention.
        [DontSerialize]
        [IgnoreDataMember]
        public int PlatinumTrophies => TrophyPlatinumCount;

        [DontSerialize]
        [IgnoreDataMember]
        public int GoldTrophies => TrophyGoldCount;

        [DontSerialize]
        [IgnoreDataMember]
        public int SilverTrophies => TrophySilverCount;

        [DontSerialize]
        [IgnoreDataMember]
        public int BronzeTrophies => TrophyBronzeCount;

        [DontSerialize]
        [IgnoreDataMember]
        public int TotalTrophies =>
            TrophyPlatinumCount + TrophyGoldCount + TrophySilverCount + TrophyBronzeCount;

        // Theme-facing alias for CompletedGamesCount, matching the OverviewViewModel.CompletedGames name.
        [DontSerialize]
        [IgnoreDataMember]
        public int CompletedGames => CompletedGamesCount;

        private static AchievementRarityStats UnlockedOnlyStats(int unlocked) =>
            new AchievementRarityStats { Total = unlocked, Unlocked = unlocked, Locked = 0 };

        public DateTime? LastUnlockUtc
        {
            get => _lastUnlockUtc;
            set
            {
                if (SetValueAndReturn(ref _lastUnlockUtc, value))
                {
                    OnPropertyChanged(nameof(LastUnlockLocal));
                }
            }
        }

        public DateTime? LastUnlockLocal => LastUnlockUtc?.ToLocalTime();

        public DateTime? LastRefreshedUtc
        {
            get => _lastRefreshedUtc;
            set
            {
                if (SetValueAndReturn(ref _lastRefreshedUtc, value))
                {
                    OnPropertyChanged(nameof(LastRefreshedLocal));
                }
            }
        }

        public DateTime? LastRefreshedLocal => LastRefreshedUtc?.ToLocalTime();

        public long TotalPlaytimeMinutes
        {
            get => _totalPlaytimeMinutes;
            set
            {
                if (SetValueAndReturn(ref _totalPlaytimeMinutes, Math.Max(0, value)))
                {
                    OnPropertyChanged(nameof(TotalPlaytimeSeconds));
                    OnPropertyChanged(nameof(TotalPlaytimeText));
                }
            }
        }

        public ulong TotalPlaytimeSeconds => (ulong)Math.Max(0, TotalPlaytimeMinutes) * 60UL;

        public string TotalPlaytimeText => PlayniteGameMetadataFormatter.FormatPlaytime(TotalPlaytimeSeconds);

        public string CountsText => string.Format(FormattingCulture.Current, "{0:N0} / {1:N0}", UnlockedAchievementsCount, SharedGamesCount);
    }
}
