using System.Collections.Generic;
using System.Collections.ObjectModel;
using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Unified theme integration data for PlayniteAchievements.
    /// Contains both per-game achievement data and all-games overview data.
    /// All properties are runtime-only and should not be serialized.
    /// </summary>
    public class ThemeData : ObservableObject
    {
        #region Backing Fields - Per-Game Data

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

        #endregion

        #region Backing Fields - All-Games Overview Data

        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _completedGamesAsc = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _completedGamesDesc = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _gameSummariesAsc = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _gameSummariesDesc = new ObservableCollection<GameAchievementSummary>();

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
        private int _totalUnlockCount;
        [DontSerialize]
        private int _totalCommonUnlockCount;
        [DontSerialize]
        private int _totalUncommonUnlockCount;
        [DontSerialize]
        private int _totalRareUnlockCount;
        [DontSerialize]
        private int _totalUltraRareUnlockCount;

        #endregion

        #region Per-Game Achievement Properties

        /// <summary>
        /// Whether achievement data is available for the currently selected game.
        /// </summary>
        [DontSerialize]
        public bool HasAchievements
        {
            get => _hasAchievements;
            set => SetValue(ref _hasAchievements, value);
        }

        /// <summary>
        /// Whether the currently selected game is completed.
        /// Provider-aware: true when provider marks complete (e.g., PSN platinum) or all achievements unlocked.
        /// </summary>
        [DontSerialize]
        public bool IsCompleted
        {
            get => _isCompleted;
            set => SetValue(ref _isCompleted, value);
        }

        /// <summary>
        /// Total number of achievements for the currently selected game.
        /// </summary>
        [DontSerialize]
        public int AchievementCount
        {
            get => _achievementCount;
            set => SetValue(ref _achievementCount, value);
        }

        /// <summary>
        /// Number of unlocked achievements for the currently selected game.
        /// </summary>
        [DontSerialize]
        public int UnlockedCount
        {
            get => _unlockedCount;
            set => SetValue(ref _unlockedCount, value);
        }

        /// <summary>
        /// Number of locked achievements for the currently selected game.
        /// </summary>
        [DontSerialize]
        public int LockedCount
        {
            get => _lockedCount;
            set => SetValue(ref _lockedCount, value);
        }

        /// <summary>
        /// Percentage of achievements unlocked for the currently selected game.
        /// </summary>
        [DontSerialize]
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => SetValue(ref _progressPercentage, value);
        }

        /// <summary>
        /// Common achievement statistics for the currently selected game.
        /// </summary>
        [DontSerialize]
        public AchievementRarityStats Common
        {
            get => _common;
            set => SetValue(ref _common, value);
        }

        /// <summary>
        /// Uncommon achievement statistics for the currently selected game.
        /// </summary>
        [DontSerialize]
        public AchievementRarityStats Uncommon
        {
            get => _uncommon;
            set => SetValue(ref _uncommon, value);
        }

        /// <summary>
        /// Rare achievement statistics for the currently selected game.
        /// </summary>
        [DontSerialize]
        public AchievementRarityStats Rare
        {
            get => _rare;
            set => SetValue(ref _rare, value);
        }

        /// <summary>
        /// Ultra Rare achievement statistics for the currently selected game.
        /// </summary>
        [DontSerialize]
        public AchievementRarityStats UltraRare
        {
            get => _ultraRare;
            set => SetValue(ref _ultraRare, value);
        }

        /// <summary>
        /// All achievements for the currently selected game.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AllAchievements
        {
            get => _allAchievements;
            set
            {
                if (!EqualityComparer<List<AchievementDetail>>.Default.Equals(_allAchievements, value))
                {
                    _allAchievements = value;
                    OnPropertyChanged();
                    // Invalidate cached display items when source changes
                    _allAchievementDisplayItems = null;
                    OnPropertyChanged(nameof(AllAchievementDisplayItems));
                }
            }
        }

        /// <summary>
        /// All achievements as display items for desktop theme integration.
        /// Lazily converts AchievementDetail to AchievementDisplayItem for template binding.
        /// </summary>
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
                // Settings.ShowHiddenIcon means "show actual icon for hidden achievements" (not question mark)
                // UpdateFrom expects hideIcon meaning "should we hide the icon", so we invert
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

        /// <summary>
        /// Refreshes the cached display items using custom visibility settings.
        /// Used by settings preview to update display items when settings change.
        /// </summary>
        /// <param name="showHiddenIcon">Whether to show hidden achievement icons.</param>
        /// <param name="showHiddenTitle">Whether to show hidden achievement titles.</param>
        /// <param name="showHiddenDescription">Whether to show hidden achievement descriptions.</param>
        /// <param name="showHiddenSuffix">Whether to show the hidden achievement suffix.</param>
        /// <param name="showLockedIcon">Whether to show locked achievement icons.</param>
        /// <param name="showRarityGlow">Whether to show rarity glow effects.</param>
        /// <param name="showRarityBar">Whether to show rarity bars.</param>
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

        /// <summary>
        /// Achievements sorted by unlock date descending (newest first) for the currently selected game.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AchievementsNewestFirst
        {
            get => _achievementsNewestFirst;
            set => SetValue(ref _achievementsNewestFirst, value);
        }

        /// <summary>
        /// Achievements sorted by unlock date ascending (oldest first) for the currently selected game.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AchievementsOldestFirst
        {
            get => _achievementsOldestFirst;
            set => SetValue(ref _achievementsOldestFirst, value);
        }

        /// <summary>
        /// Achievements sorted by rarity ascending (rarest first) for the currently selected game.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityAsc
        {
            get => _achievementsRarityAsc;
            set => SetValue(ref _achievementsRarityAsc, value);
        }

        /// <summary>
        /// Achievements sorted by rarity descending (common first) for the currently selected game.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityDesc
        {
            get => _achievementsRarityDesc;
            set => SetValue(ref _achievementsRarityDesc, value);
        }

        #endregion

        #region All-Games Overview Properties

        /// <summary>
        /// Completed games sorted by last unlock date ascending (oldest first).
        /// Completion is provider-aware or all achievements unlocked.
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesAsc
        {
            get => _completedGamesAsc;
            set => SetValue(ref _completedGamesAsc, value);
        }

        /// <summary>
        /// Completed games sorted by last unlock date descending (newest first).
        /// Completion is provider-aware or all achievements unlocked.
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesDesc
        {
            get => _completedGamesDesc;
            set => SetValue(ref _completedGamesDesc, value);
        }

        /// <summary>
        /// All game summaries sorted by last unlock date ascending (oldest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesAsc
        {
            get => _gameSummariesAsc;
            set => SetValue(ref _gameSummariesAsc, value);
        }

        /// <summary>
        /// All game summaries sorted by last unlock date descending (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesDesc
        {
            get => _gameSummariesDesc;
            set => SetValue(ref _gameSummariesDesc, value);
        }

        /// <summary>
        /// Total number of achievements unlocked across all games.
        /// </summary>
        [DontSerialize]
        public int TotalUnlockCount
        {
            get => _totalUnlockCount;
            set => SetValue(ref _totalUnlockCount, value);
        }

        /// <summary>
        /// Total number of common achievements unlocked across all games.
        /// </summary>
        [DontSerialize]
        public int TotalCommonUnlockCount
        {
            get => _totalCommonUnlockCount;
            set => SetValue(ref _totalCommonUnlockCount, value);
        }

        /// <summary>
        /// Total number of uncommon achievements unlocked across all games.
        /// </summary>
        [DontSerialize]
        public int TotalUncommonUnlockCount
        {
            get => _totalUncommonUnlockCount;
            set => SetValue(ref _totalUncommonUnlockCount, value);
        }

        /// <summary>
        /// Total number of rare achievements unlocked across all games.
        /// </summary>
        [DontSerialize]
        public int TotalRareUnlockCount
        {
            get => _totalRareUnlockCount;
            set => SetValue(ref _totalRareUnlockCount, value);
        }

        /// <summary>
        /// Total number of ultra-rare achievements unlocked across all games.
        /// </summary>
        [DontSerialize]
        public int TotalUltraRareUnlockCount
        {
            get => _totalUltraRareUnlockCount;
            set => SetValue(ref _totalUltraRareUnlockCount, value);
        }

        /// <summary>
        /// All achievements from all games, sorted by unlock date ascending (oldest first).
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockAsc
        {
            get => _allAchievementsUnlockAsc;
            set => SetValue(ref _allAchievementsUnlockAsc, value);
        }

        /// <summary>
        /// All achievements from all games, sorted by unlock date descending (newest first).
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockDesc
        {
            get => _allAchievementsUnlockDesc;
            set => SetValue(ref _allAchievementsUnlockDesc, value);
        }

        /// <summary>
        /// All achievements from all games, sorted by rarity ascending (rarest first).
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityAsc
        {
            get => _allAchievementsRarityAsc;
            set => SetValue(ref _allAchievementsRarityAsc, value);
        }

        /// <summary>
        /// All achievements from all games, sorted by rarity descending (common first).
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityDesc
        {
            get => _allAchievementsRarityDesc;
            set => SetValue(ref _allAchievementsRarityDesc, value);
        }

        /// <summary>
        /// Unlocked achievements across all games, sorted by unlock date descending (newest first).
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocks
        {
            get => _mostRecentUnlocks;
            set => SetValue(ref _mostRecentUnlocks, value);
        }

        /// <summary>
        /// Unlocked achievements across all games, sorted by rarity ascending (rarest first),
        /// with recency used as a tie-breaker.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocks
        {
            get => _rarestRecentUnlocks;
            set => SetValue(ref _rarestRecentUnlocks, value);
        }

        /// <summary>
        /// Top 3 unlocked achievements across all games, newest first.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop3
        {
            get => _mostRecentUnlocksTop3;
            set => SetValue(ref _mostRecentUnlocksTop3, value);
        }

        /// <summary>
        /// Top 5 unlocked achievements across all games, newest first.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop5
        {
            get => _mostRecentUnlocksTop5;
            set => SetValue(ref _mostRecentUnlocksTop5, value);
        }

        /// <summary>
        /// Top 10 unlocked achievements across all games, newest first.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop10
        {
            get => _mostRecentUnlocksTop10;
            set => SetValue(ref _mostRecentUnlocksTop10, value);
        }

        /// <summary>
        /// Top 3 rare unlocked achievements across all games, filtered to the recent-window threshold.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop3
        {
            get => _rarestRecentUnlocksTop3;
            set => SetValue(ref _rarestRecentUnlocksTop3, value);
        }

        /// <summary>
        /// Top 5 rare unlocked achievements across all games, filtered to the recent-window threshold.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop5
        {
            get => _rarestRecentUnlocksTop5;
            set => SetValue(ref _rarestRecentUnlocksTop5, value);
        }

        /// <summary>
        /// Top 10 rare unlocked achievements across all games, filtered to the recent-window threshold.
        /// </summary>
        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop10
        {
            get => _rarestRecentUnlocksTop10;
            set => SetValue(ref _rarestRecentUnlocksTop10, value);
        }

        #endregion

        #region Per-Provider Game Lists

        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _steamGames = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _gogGames = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _epicGames = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _xboxGames = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _psnGames = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _retroAchievementsGames = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _rpcs3Games = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _shadPS4Games = new ObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private ObservableCollection<GameAchievementSummary> _manualGames = new ObservableCollection<GameAchievementSummary>();

        /// <summary>
        /// Steam games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> SteamGames
        {
            get => _steamGames;
            set => SetValue(ref _steamGames, value);
        }

        /// <summary>
        /// GOG games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GOGGames
        {
            get => _gogGames;
            set => SetValue(ref _gogGames, value);
        }

        /// <summary>
        /// Epic games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EpicGames
        {
            get => _epicGames;
            set => SetValue(ref _epicGames, value);
        }

        /// <summary>
        /// Xbox games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XboxGames
        {
            get => _xboxGames;
            set => SetValue(ref _xboxGames, value);
        }

        /// <summary>
        /// PSN games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PSNGames
        {
            get => _psnGames;
            set => SetValue(ref _psnGames, value);
        }

        /// <summary>
        /// RetroAchievements games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RetroAchievementsGames
        {
            get => _retroAchievementsGames;
            set => SetValue(ref _retroAchievementsGames, value);
        }

        /// <summary>
        /// RPCS3 games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RPCS3Games
        {
            get => _rpcs3Games;
            set => SetValue(ref _rpcs3Games, value);
        }

        /// <summary>
        /// ShadPS4 games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ShadPS4Games
        {
            get => _shadPS4Games;
            set => SetValue(ref _shadPS4Games, value);
        }

        /// <summary>
        /// Manual achievement games sorted by last unlock date (newest first).
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ManualGames
        {
            get => _manualGames;
            set => SetValue(ref _manualGames, value);
        }

        #endregion
    }
}
