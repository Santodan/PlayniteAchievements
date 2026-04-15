using System;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.ViewModels
{
    public class GameOverviewItem : ObservableObject
    {
        private string _gameName;
        public string GameName { get => _gameName; set => SetValue(ref _gameName, value); }

        private string _sortingName;
        public string SortingName { get => _sortingName; set => SetValue(ref _sortingName, value); }

        private string _gameLogo;
        public string GameLogo { get => _gameLogo; set => SetValue(ref _gameLogo, value); }

        private string _gameCoverPath;
        public string GameCoverPath { get => _gameCoverPath; set => SetValue(ref _gameCoverPath, value); }

        private string _platformText;
        public string PlatformText
        {
            get => _platformText;
            set
            {
                if (SetValueAndReturn(ref _platformText, value))
                {
                    OnPropertyChanged(nameof(SecondaryMetadataText));
                }
            }
        }

        private string _regionText;
        public string RegionText
        {
            get => _regionText;
            set
            {
                if (SetValueAndReturn(ref _regionText, value))
                {
                    OnPropertyChanged(nameof(SecondaryMetadataText));
                }
            }
        }

        private ulong _playtimeSeconds;
        public ulong PlaytimeSeconds
        {
            get => _playtimeSeconds;
            set
            {
                if (SetValueAndReturn(ref _playtimeSeconds, value))
                {
                    OnPropertyChanged(nameof(PlaytimeText));
                    OnPropertyChanged(nameof(SecondaryMetadataText));
                }
            }
        }

        public int AppId { get; set; } // Stays as AppId is immutable ID
        public Guid? PlayniteGameId { get; set; }

        private int _totalAchievements;
        public int TotalAchievements 
        { 
            get => _totalAchievements; 
            set 
            { 
                if (_totalAchievements != value)
                {
                    SetValue(ref _totalAchievements, value); 
                    OnPropertyChanged(nameof(Progression)); 
                    OnPropertyChanged(nameof(ProgressionText)); 
                }
            } 
        }

        private int _unlockedAchievements;
        public int UnlockedAchievements 
        { 
            get => _unlockedAchievements; 
            set 
            { 
                if (_unlockedAchievements != value)
                {
                    SetValue(ref _unlockedAchievements, value); 
                    OnPropertyChanged(nameof(Progression)); 
                    OnPropertyChanged(nameof(ProgressionText)); 
                }
            } 
        }

        private int _commonCount;
        public int CommonCount { get => _commonCount; set => SetValue(ref _commonCount, value); }

        private int _uncommonCount;
        public int UncommonCount { get => _uncommonCount; set => SetValue(ref _uncommonCount, value); }

        private int _rareCount;
        public int RareCount { get => _rareCount; set => SetValue(ref _rareCount, value); }

        private int _ultraRareCount;
        public int UltraRareCount { get => _ultraRareCount; set => SetValue(ref _ultraRareCount, value); }

        // Total rarity counts (including locked achievements)
        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }

        // Trophy counts for PlayStation games
        private int _trophyPlatinumCount;
        public int TrophyPlatinumCount { get => _trophyPlatinumCount; set => SetValue(ref _trophyPlatinumCount, value); }

        private int _trophyGoldCount;
        public int TrophyGoldCount { get => _trophyGoldCount; set => SetValue(ref _trophyGoldCount, value); }

        private int _trophySilverCount;
        public int TrophySilverCount { get => _trophySilverCount; set => SetValue(ref _trophySilverCount, value); }

        private int _trophyBronzeCount;
        public int TrophyBronzeCount { get => _trophyBronzeCount; set => SetValue(ref _trophyBronzeCount, value); }

        public int TrophyPlatinumTotal { get; set; }
        public int TrophyGoldTotal { get; set; }
        public int TrophySilverTotal { get; set; }
        public int TrophyBronzeTotal { get; set; }

        /// <summary>
        /// True if this game has PlayStation trophy type data.
        /// </summary>
        public bool HasTrophyTypes => TrophyPlatinumCount > 0 || TrophyGoldCount > 0 || TrophySilverCount > 0 || TrophyBronzeCount > 0;

        public bool HasRarityPieChartData =>
            TotalCommonPossible > 0 ||
            TotalUncommonPossible > 0 ||
            TotalRarePossible > 0 ||
            TotalUltraRarePossible > 0;

        public bool HasTrophyPieChartData =>
            TrophyPlatinumTotal > 0 ||
            TrophyGoldTotal > 0 ||
            TrophySilverTotal > 0 ||
            TrophyBronzeTotal > 0;

        private DateTime? _lastPlayed;
        public DateTime? LastPlayed 
        { 
            get => _lastPlayed; 
            set 
            { 
                if (_lastPlayed != value)
                {
                    SetValue(ref _lastPlayed, value); 
                    OnPropertyChanged(nameof(LastPlayedText)); 
                }
            } 
        }

        private bool _isCompleted;
        public bool IsCompleted { get => _isCompleted; set => SetValue(ref _isCompleted, value); }

        private string _provider;
        public string Provider { get => _provider; set => SetValue(ref _provider, value); }

        private string _providerKey;
        public string ProviderKey { get => _providerKey; set => SetValue(ref _providerKey, value); }

        private string _providerIconKey;
        public string ProviderIconKey { get => _providerIconKey; set => SetValue(ref _providerIconKey, value); }

        private string _providerColorHex;
        public string ProviderColorHex { get => _providerColorHex; set => SetValue(ref _providerColorHex, value); }


        public int Progression => AchievementCompletionPercentCalculator.ComputeRoundedPercent(UnlockedAchievements, TotalAchievements);

        public string ProgressionText => $"{Progression}%";

        public string PlaytimeText => PlayniteGameMetadataFormatter.FormatPlaytime(PlaytimeSeconds);

        public string SecondaryMetadataText => PlayniteGameMetadataFormatter.BuildSidebarMetadataText(
            PlatformText,
            PlaytimeText,
            RegionText);

        public string LastPlayedText => LastPlayed.HasValue
            ? LastPlayed.Value.ToLocalTime().ToString("g")
            : "";
    }
}
