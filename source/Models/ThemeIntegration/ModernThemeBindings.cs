using System.Collections.Generic;
using System.Collections.ObjectModel;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Modern PlayniteAchievements binding surface.
    /// Runtime-only and populated from a shared theme runtime state.
    /// </summary>
    public class ModernThemeBindings : ObservableObject
    {
        [DontSerialize]
        private bool _hasAchievements;
        [DontSerialize]
        private bool _isCompleted;
        [DontSerialize]
        private int _achievementCount;
        [DontSerialize]
        private int _unlockedCount;
        [DontSerialize]
        private int _lockedCount;
        [DontSerialize]
        private double _progressPercentage;
        [DontSerialize]
        private AchievementRarityStats _common = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _uncommon = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _rare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _ultraRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _rareAndUltraRare = new AchievementRarityStats();
        [DontSerialize]
        private List<AchievementDetail> _allAchievements = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsNewestFirst = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsOldestFirst = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsRarityAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsRarityDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDisplayItem> _allAchievementDisplayItems;

        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _completedGamesAsc = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _completedGamesDesc = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _gameSummariesAsc = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _gameSummariesDesc = new BulkObservableCollection<GameAchievementSummary>();

        [DontSerialize]
        private List<AchievementDetail> _allAchievementsUnlockAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsUnlockDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsRarityAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsRarityDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocks = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocks = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocksTop3 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocksTop5 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocksTop10 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocksTop3 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocksTop5 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocksTop10 = new List<AchievementDetail>();
        [DontSerialize]
        private AchievementRarityStats _totalCommon = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalUncommon = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalUltraRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalRareAndUltraRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalOverall = new AchievementRarityStats();

        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _steamGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _gogGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _epicGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _xboxGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _psnGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _retroAchievementsGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _rpcs3Games = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _xeniaGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _shadPS4Games = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _manualGames = new BulkObservableCollection<GameAchievementSummary>();

        [DontSerialize]
        public bool HasAchievements
        {
            get => _hasAchievements;
            set => SetValue(ref _hasAchievements, value);
        }

        [DontSerialize]
        public bool IsCompleted
        {
            get => _isCompleted;
            set => SetValue(ref _isCompleted, value);
        }

        [DontSerialize]
        public int AchievementCount
        {
            get => _achievementCount;
            set => SetValue(ref _achievementCount, value);
        }

        [DontSerialize]
        public int UnlockedCount
        {
            get => _unlockedCount;
            set => SetValue(ref _unlockedCount, value);
        }

        [DontSerialize]
        public int LockedCount
        {
            get => _lockedCount;
            set => SetValue(ref _lockedCount, value);
        }

        [DontSerialize]
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => SetValue(ref _progressPercentage, value);
        }

        [DontSerialize]
        public AchievementRarityStats Common
        {
            get => _common;
            set => SetValue(ref _common, value);
        }

        [DontSerialize]
        public AchievementRarityStats Uncommon
        {
            get => _uncommon;
            set => SetValue(ref _uncommon, value);
        }

        [DontSerialize]
        public AchievementRarityStats Rare
        {
            get => _rare;
            set => SetValue(ref _rare, value);
        }

        [DontSerialize]
        public AchievementRarityStats UltraRare
        {
            get => _ultraRare;
            set => SetValue(ref _ultraRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats RareAndUltraRare
        {
            get => _rareAndUltraRare;
            set => SetValue(ref _rareAndUltraRare, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievements
        {
            get => _allAchievements;
            set
            {
                if (EqualityComparer<List<AchievementDetail>>.Default.Equals(_allAchievements, value))
                {
                    return;
                }

                _allAchievements = value;
                OnPropertyChanged();
                _allAchievementDisplayItems = null;
                OnPropertyChanged(nameof(AllAchievementDisplayItems));
            }
        }

        [DontSerialize]
        public List<AchievementDisplayItem> AllAchievementDisplayItems
        {
            get
            {
                if (_allAchievementDisplayItems != null)
                {
                    return _allAchievementDisplayItems;
                }

                if (_allAchievements == null || _allAchievements.Count == 0)
                {
                    _allAchievementDisplayItems = new List<AchievementDisplayItem>();
                    return _allAchievementDisplayItems;
                }

                var settings = PlayniteAchievementsPlugin.Instance?.Settings;
                var hideIcon = !(settings?.Persisted?.ShowHiddenIcon ?? false);
                var hideTitle = !(settings?.Persisted?.ShowHiddenTitle ?? false);
                var hideDescription = !(settings?.Persisted?.ShowHiddenDescription ?? false);
                var showHiddenSuffix = settings?.Persisted?.ShowHiddenSuffix ?? true;
                var hideLockedIcon = !(settings?.Persisted?.ShowLockedIcon ?? true);
                var showRarityGlow = settings?.Persisted?.ShowRarityGlow ?? true;
                var showRarityBar = settings?.Persisted?.ShowCompactListRarityBar ?? true;

                var items = new List<AchievementDisplayItem>(_allAchievements.Count);
                foreach (var achievement in _allAchievements)
                {
                    var item = new AchievementDisplayItem();
                    var gameName = achievement.Game?.Name ?? "Unknown";
                    var gameId = achievement.Game?.Id;
                    item.UpdateFrom(achievement, gameName, gameId, hideIcon, hideTitle, hideDescription, hideLockedIcon, showRarityGlow, showRarityBar);
                    item.ShowHiddenSuffix = showHiddenSuffix;
                    items.Add(item);
                }

                _allAchievementDisplayItems = items;
                return _allAchievementDisplayItems;
            }
        }

        public void RefreshDisplayItems(
            bool showHiddenIcon,
            bool showHiddenTitle,
            bool showHiddenDescription,
            bool showHiddenSuffix,
            bool showLockedIcon,
            bool showRarityGlow,
            bool showRarityBar)
        {
            if (_allAchievements == null || _allAchievements.Count == 0)
            {
                _allAchievementDisplayItems = new List<AchievementDisplayItem>();
                OnPropertyChanged(nameof(AllAchievementDisplayItems));
                return;
            }

            var hideIcon = !showHiddenIcon;
            var hideTitle = !showHiddenTitle;
            var hideDescription = !showHiddenDescription;
            var hideLockedIcon = !showLockedIcon;

            var items = new List<AchievementDisplayItem>(_allAchievements.Count);
            foreach (var achievement in _allAchievements)
            {
                var item = new AchievementDisplayItem();
                var gameName = achievement.Game?.Name ?? "Unknown";
                var gameId = achievement.Game?.Id;
                item.UpdateFrom(achievement, gameName, gameId, hideIcon, hideTitle, hideDescription, hideLockedIcon, showRarityGlow, showRarityBar);
                item.ShowHiddenSuffix = showHiddenSuffix;
                items.Add(item);
            }

            _allAchievementDisplayItems = items;
            OnPropertyChanged(nameof(AllAchievementDisplayItems));
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsNewestFirst
        {
            get => _achievementsNewestFirst;
            set => SetValue(ref _achievementsNewestFirst, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsOldestFirst
        {
            get => _achievementsOldestFirst;
            set => SetValue(ref _achievementsOldestFirst, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityAsc
        {
            get => _achievementsRarityAsc;
            set => SetValue(ref _achievementsRarityAsc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityDesc
        {
            get => _achievementsRarityDesc;
            set => SetValue(ref _achievementsRarityDesc, value);
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesAsc
        {
            get => _completedGamesAsc;
            set => ReplaceCollection(_completedGamesAsc, value, nameof(CompletedGamesAsc));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesDesc
        {
            get => _completedGamesDesc;
            set => ReplaceCollection(_completedGamesDesc, value, nameof(CompletedGamesDesc));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesAsc
        {
            get => _gameSummariesAsc;
            set => ReplaceCollection(_gameSummariesAsc, value, nameof(GameSummariesAsc));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesDesc
        {
            get => _gameSummariesDesc;
            set => ReplaceCollection(_gameSummariesDesc, value, nameof(GameSummariesDesc));
        }

        [DontSerialize]
        public AchievementRarityStats TotalCommon
        {
            get => _totalCommon;
            set => SetValue(ref _totalCommon, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalUncommon
        {
            get => _totalUncommon;
            set => SetValue(ref _totalUncommon, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalRare
        {
            get => _totalRare;
            set => SetValue(ref _totalRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalUltraRare
        {
            get => _totalUltraRare;
            set => SetValue(ref _totalUltraRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalRareAndUltraRare
        {
            get => _totalRareAndUltraRare;
            set => SetValue(ref _totalRareAndUltraRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalOverall
        {
            get => _totalOverall;
            set => SetValue(ref _totalOverall, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockAsc
        {
            get => _allAchievementsUnlockAsc;
            set => SetValue(ref _allAchievementsUnlockAsc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockDesc
        {
            get => _allAchievementsUnlockDesc;
            set => SetValue(ref _allAchievementsUnlockDesc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityAsc
        {
            get => _allAchievementsRarityAsc;
            set => SetValue(ref _allAchievementsRarityAsc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityDesc
        {
            get => _allAchievementsRarityDesc;
            set => SetValue(ref _allAchievementsRarityDesc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocks
        {
            get => _mostRecentUnlocks;
            set => SetValue(ref _mostRecentUnlocks, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocks
        {
            get => _rarestRecentUnlocks;
            set => SetValue(ref _rarestRecentUnlocks, value);
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop3
        {
            get => _mostRecentUnlocksTop3;
            set => SetValue(ref _mostRecentUnlocksTop3, value);
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop5
        {
            get => _mostRecentUnlocksTop5;
            set => SetValue(ref _mostRecentUnlocksTop5, value);
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop10
        {
            get => _mostRecentUnlocksTop10;
            set => SetValue(ref _mostRecentUnlocksTop10, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop3
        {
            get => _rarestRecentUnlocksTop3;
            set => SetValue(ref _rarestRecentUnlocksTop3, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop5
        {
            get => _rarestRecentUnlocksTop5;
            set => SetValue(ref _rarestRecentUnlocksTop5, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop10
        {
            get => _rarestRecentUnlocksTop10;
            set => SetValue(ref _rarestRecentUnlocksTop10, value);
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> SteamGames
        {
            get => _steamGames;
            set => ReplaceCollection(_steamGames, value, nameof(SteamGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GOGGames
        {
            get => _gogGames;
            set => ReplaceCollection(_gogGames, value, nameof(GOGGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EpicGames
        {
            get => _epicGames;
            set => ReplaceCollection(_epicGames, value, nameof(EpicGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XboxGames
        {
            get => _xboxGames;
            set => ReplaceCollection(_xboxGames, value, nameof(XboxGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PSNGames
        {
            get => _psnGames;
            set => ReplaceCollection(_psnGames, value, nameof(PSNGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RetroAchievementsGames
        {
            get => _retroAchievementsGames;
            set => ReplaceCollection(_retroAchievementsGames, value, nameof(RetroAchievementsGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RPCS3Games
        {
            get => _rpcs3Games;
            set => ReplaceCollection(_rpcs3Games, value, nameof(RPCS3Games));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XeniaGames
        {
            get => _xeniaGames;
            set => ReplaceCollection(_xeniaGames, value, nameof(XeniaGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ShadPS4Games
        {
            get => _shadPS4Games;
            set => ReplaceCollection(_shadPS4Games, value, nameof(ShadPS4Games));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ManualGames
        {
            get => _manualGames;
            set => ReplaceCollection(_manualGames, value, nameof(ManualGames));
        }

        private void ReplaceCollection<T>(BulkObservableCollection<T> target, IEnumerable<T> value, string propertyName)
        {
            target.ReplaceAll(value ?? new List<T>());
            OnPropertyChanged(propertyName);
        }
    }
}

