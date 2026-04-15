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
        private bool _hasCustomAchievementOrder;
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
        private List<AchievementDetail> _achievementDefaultOrder = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsNewestFirst = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsOldestFirst = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsRarityAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsRarityDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _dynamicAchievements = new List<AchievementDetail>();
        [DontSerialize]
        private string _dynamicAchievementsFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicAchievementsFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicAchievementsSortKey = DynamicThemeViewKeys.Default;
        [DontSerialize]
        private string _dynamicAchievementsSortLabel = DynamicThemeViewKeys.Default;
        [DontSerialize]
        private string _dynamicAchievementsSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicAchievementsSortDirectionLabel = DynamicThemeViewKeys.Descending;
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
        private readonly BulkObservableCollection<GameAchievementSummary> _dynamicGameSummaries = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private string _dynamicGameSummariesProviderKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesProviderLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesSortKey = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicGameSummariesSortLabel = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicGameSummariesSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicGameSummariesSortDirectionLabel = DynamicThemeViewKeys.Descending;

        [DontSerialize]
        private List<AchievementDetail> _allAchievementsUnlockAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsUnlockDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsRarityAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsRarityDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _dynamicLibraryAchievements = new List<AchievementDetail>();
        [DontSerialize]
        private string _dynamicLibraryAchievementsProviderKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsProviderLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortKey = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortLabel = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortDirectionLabel = DynamicThemeViewKeys.Descending;
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
        private readonly BulkObservableCollection<GameAchievementSummary> _battleNetGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _eaGames = new BulkObservableCollection<GameAchievementSummary>();
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
        public bool HasCustomAchievementOrder
        {
            get => _hasCustomAchievementOrder;
            set => SetValue(ref _hasCustomAchievementOrder, value);
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
                var persisted = settings?.Persisted;
                var showHiddenIcon = persisted?.ShowHiddenIcon ?? false;
                var showHiddenTitle = persisted?.ShowHiddenTitle ?? false;
                var showHiddenDescription = persisted?.ShowHiddenDescription ?? false;
                var showHiddenSuffix = persisted?.ShowHiddenSuffix ?? true;
                var showLockedIcon = persisted?.ShowLockedIcon ?? true;
                var useSeparateLockedIcons = persisted?.UseSeparateLockedIconsWhenAvailable ?? false;
                var showRarityGlow = persisted?.ShowRarityGlow ?? true;
                var showRarityBar = persisted?.ShowCompactListRarityBar ?? true;

                var items = new List<AchievementDisplayItem>(_allAchievements.Count);
                foreach (var achievement in _allAchievements)
                {
                    var item = new AchievementDisplayItem();
                    var gameName = achievement.Game?.Name ?? "Unknown";
                    var gameId = achievement.Game?.Id;
                    item.UpdateFrom(
                        achievement,
                        gameName,
                        gameId,
                        showHiddenIcon,
                        showHiddenTitle,
                        showHiddenDescription,
                        showHiddenSuffix,
                        showLockedIcon,
                        ResolveUseSeparateLockedIcons(persisted, gameId, useSeparateLockedIcons),
                        showRarityGlow,
                        showRarityBar);
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
            bool useSeparateLockedIconsWhenAvailable,
            bool showRarityGlow,
            bool showRarityBar)
        {
            if (_allAchievements == null || _allAchievements.Count == 0)
            {
                _allAchievementDisplayItems = new List<AchievementDisplayItem>();
                OnPropertyChanged(nameof(AllAchievementDisplayItems));
                return;
            }

            var items = new List<AchievementDisplayItem>(_allAchievements.Count);
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            foreach (var achievement in _allAchievements)
            {
                var item = new AchievementDisplayItem();
                var gameName = achievement.Game?.Name ?? "Unknown";
                var gameId = achievement.Game?.Id;
                item.UpdateFrom(
                    achievement,
                    gameName,
                    gameId,
                    showHiddenIcon,
                    showHiddenTitle,
                    showHiddenDescription,
                    showHiddenSuffix,
                    showLockedIcon,
                    ResolveUseSeparateLockedIcons(persisted, gameId, useSeparateLockedIconsWhenAvailable),
                    showRarityGlow,
                    showRarityBar);
                items.Add(item);
            }

            _allAchievementDisplayItems = items;
            OnPropertyChanged(nameof(AllAchievementDisplayItems));
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementDefaultOrder
        {
            get => _achievementDefaultOrder;
            set => SetValue(ref _achievementDefaultOrder, value);
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
        public List<AchievementDetail> DynamicAchievements
        {
            get => _dynamicAchievements;
            set => SetValue(ref _dynamicAchievements, value);
        }

        [DontSerialize]
        public string DynamicAchievementsFilterKey
        {
            get => _dynamicAchievementsFilterKey;
            set => SetValue(ref _dynamicAchievementsFilterKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsFilterLabel
        {
            get => _dynamicAchievementsFilterLabel;
            set => SetValue(ref _dynamicAchievementsFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortKey
        {
            get => _dynamicAchievementsSortKey;
            set => SetValue(ref _dynamicAchievementsSortKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortLabel
        {
            get => _dynamicAchievementsSortLabel;
            set => SetValue(ref _dynamicAchievementsSortLabel, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortDirectionKey
        {
            get => _dynamicAchievementsSortDirectionKey;
            set => SetValue(ref _dynamicAchievementsSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortDirectionLabel
        {
            get => _dynamicAchievementsSortDirectionLabel;
            set => SetValue(ref _dynamicAchievementsSortDirectionLabel, value);
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
        public ObservableCollection<GameAchievementSummary> DynamicGameSummaries
        {
            get => _dynamicGameSummaries;
            set => ReplaceCollection(_dynamicGameSummaries, value, nameof(DynamicGameSummaries));
        }

        [DontSerialize]
        public string DynamicGameSummariesProviderKey
        {
            get => _dynamicGameSummariesProviderKey;
            set => SetValue(ref _dynamicGameSummariesProviderKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesProviderLabel
        {
            get => _dynamicGameSummariesProviderLabel;
            set => SetValue(ref _dynamicGameSummariesProviderLabel, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortKey
        {
            get => _dynamicGameSummariesSortKey;
            set => SetValue(ref _dynamicGameSummariesSortKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortLabel
        {
            get => _dynamicGameSummariesSortLabel;
            set => SetValue(ref _dynamicGameSummariesSortLabel, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortDirectionKey
        {
            get => _dynamicGameSummariesSortDirectionKey;
            set => SetValue(ref _dynamicGameSummariesSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortDirectionLabel
        {
            get => _dynamicGameSummariesSortDirectionLabel;
            set => SetValue(ref _dynamicGameSummariesSortDirectionLabel, value);
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
        public List<AchievementDetail> DynamicLibraryAchievements
        {
            get => _dynamicLibraryAchievements;
            set => SetValue(ref _dynamicLibraryAchievements, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsProviderKey
        {
            get => _dynamicLibraryAchievementsProviderKey;
            set => SetValue(ref _dynamicLibraryAchievementsProviderKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsProviderLabel
        {
            get => _dynamicLibraryAchievementsProviderLabel;
            set => SetValue(ref _dynamicLibraryAchievementsProviderLabel, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortKey
        {
            get => _dynamicLibraryAchievementsSortKey;
            set => SetValue(ref _dynamicLibraryAchievementsSortKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortLabel
        {
            get => _dynamicLibraryAchievementsSortLabel;
            set => SetValue(ref _dynamicLibraryAchievementsSortLabel, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortDirectionKey
        {
            get => _dynamicLibraryAchievementsSortDirectionKey;
            set => SetValue(ref _dynamicLibraryAchievementsSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortDirectionLabel
        {
            get => _dynamicLibraryAchievementsSortDirectionLabel;
            set => SetValue(ref _dynamicLibraryAchievementsSortDirectionLabel, value);
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
        public ObservableCollection<GameAchievementSummary> BattleNetGames
        {
            get => _battleNetGames;
            set => ReplaceCollection(_battleNetGames, value, nameof(BattleNetGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EAGames
        {
            get => _eaGames;
            set => ReplaceCollection(_eaGames, value, nameof(EAGames));
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

        private static bool ResolveUseSeparateLockedIcons(
            Models.Settings.PersistedSettings settings,
            System.Guid? playniteGameId,
            bool fallbackValue)
        {
            if (settings == null || !playniteGameId.HasValue || playniteGameId.Value == System.Guid.Empty)
            {
                return fallbackValue;
            }

            return Services.GameCustomDataLookup.ShouldUseSeparateLockedIcons(playniteGameId, settings);
        }
    }
}
