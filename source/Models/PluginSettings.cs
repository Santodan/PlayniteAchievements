using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Main settings class for PlayniteAchievements plugin.
    /// This class contains two kinds of members:
    /// 1) Persisted configuration (via PersistedSettings property, saved via Playnite's plugin settings JSON).
    /// 2) Runtime/theme integration surfaces (updated live, never serialized).
    ///
    /// Theme integration data is organized into:
    /// - Persisted: User-configurable settings
    /// - Theme: Unified modern theme integration data (per-game + all-games overview)
    /// - LegacyTheme: Legacy compatibility data (SuccessStory, Aniki ReMake, old inline properties)
    ///
    /// Keep runtime/theme members clearly marked with [DontSerialize] on both the backing field
    /// and the public property to prevent settings bloat and improve performance.
    /// </summary>
    public class PlayniteAchievementsSettings : ObservableObject
    {
        private static readonly List<AchievementDetail> EmptyAchievementList = new List<AchievementDetail>();
        private static readonly AchievementRarityStats EmptyRarityStats = new AchievementRarityStats();

        #region Composition Properties

        /// <summary>
        /// Persisted user settings (serialized to plugin settings JSON).
        /// </summary>
        public PersistedSettings Persisted { get; private set; }

        /// <summary>
        /// Unified modern theme integration data (runtime, not serialized).
        /// Contains both per-game achievement data and all-games overview data.
        /// </summary>
        [DontSerialize]
        private ThemeData _theme;

        public ThemeData Theme
        {
            get => _theme ?? (_theme = new ThemeData());
            private set => _theme = value;
        }

        /// <summary>
        /// Legacy theme compatibility data (runtime, not serialized).
        /// Contains SuccessStory compatibility, Aniki ReMake compatibility, and old inline properties.
        /// </summary>
        [DontSerialize]
        private LegacyThemeData _legacyTheme;

        public LegacyThemeData LegacyTheme
        {
            get => _legacyTheme ?? (_legacyTheme = new LegacyThemeData());
            private set => _legacyTheme = value;
        }

        #endregion

        #region Commands

        [DontSerialize]
        private ICommand _openFullscreenAchievementWindow;
        [DontSerialize]
        private ICommand _singleGameRefreshCommand;
        [DontSerialize]
        private ICommand _recentRefreshCommand;
        [DontSerialize]
        private ICommand _favoritesRefreshCommand;
        [DontSerialize]
        private ICommand _fullRefreshCommand;
        [DontSerialize]
        private ICommand _installedRefreshCommand;
        [DontSerialize]
        private ICommand _openAchievementWindow;
        [DontSerialize]
        private ICommand _openGameAchievementWindow;

        [DontSerialize]
        public ICommand OpenFullscreenAchievementWindow
        {
            get => _openFullscreenAchievementWindow;
            set => SetValue(ref _openFullscreenAchievementWindow, value);
        }

        [DontSerialize]
        public ICommand SingleGameRefreshCommand
        {
            get => _singleGameRefreshCommand;
            set => SetValue(ref _singleGameRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand RecentRefreshCommand
        {
            get => _recentRefreshCommand;
            set => SetValue(ref _recentRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand FavoritesRefreshCommand
        {
            get => _favoritesRefreshCommand;
            set => SetValue(ref _favoritesRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand FullRefreshCommand
        {
            get => _fullRefreshCommand;
            set => SetValue(ref _fullRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand InstalledRefreshCommand
        {
            get => _installedRefreshCommand;
            set => SetValue(ref _installedRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand OpenAchievementWindow
        {
            get => _openAchievementWindow;
            set => SetValue(ref _openAchievementWindow, value);
        }

        [DontSerialize]
        public ICommand OpenGameAchievementWindow
        {
            get => _openGameAchievementWindow;
            set => SetValue(ref _openGameAchievementWindow, value);
        }



        #endregion

        #region Modern Theme Integration

        // === Per-Game Achievement Data ===

        [DontSerialize]
        public bool HasAchievements => Theme.HasAchievements;

        [DontSerialize]
        public int AchievementCount => Theme.AchievementCount;

        [DontSerialize]
        public int UnlockedCount => Theme.UnlockedCount;

        [DontSerialize]
        public int LockedCount => Theme.LockedCount;

        [DontSerialize]
        public double ProgressPercentage => Theme.ProgressPercentage;

        [DontSerialize]
        public bool IsCompleted => Theme.IsCompleted;

        [DontSerialize]
        public List<AchievementDetail> Achievements => Theme.AllAchievements ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsNewestFirst => Theme.AchievementsNewestFirst ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsOldestFirst => Theme.AchievementsOldestFirst ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityAsc => Theme.AchievementsRarityAsc ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityDesc => Theme.AchievementsRarityDesc ?? EmptyAchievementList;

        [DontSerialize]
        public AchievementRarityStats Common => Theme.Common ?? EmptyRarityStats;

        [DontSerialize]
        public AchievementRarityStats Uncommon => Theme.Uncommon ?? EmptyRarityStats;

        [DontSerialize]
        public AchievementRarityStats Rare => Theme.Rare ?? EmptyRarityStats;

        [DontSerialize]
        public AchievementRarityStats UltraRare => Theme.UltraRare ?? EmptyRarityStats;

        // === All-Games Overview Data ===

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesAsc
        {
            get => Theme.CompletedGamesAsc;
            set => Theme.CompletedGamesAsc = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesDesc
        {
            get => Theme.CompletedGamesDesc;
            set => Theme.CompletedGamesDesc = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesAsc
        {
            get => Theme.GameSummariesAsc;
            set => Theme.GameSummariesAsc = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesDesc
        {
            get => Theme.GameSummariesDesc;
            set => Theme.GameSummariesDesc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockAsc
        {
            get => Theme.AllAchievementsUnlockAsc ?? EmptyAchievementList;
            set => Theme.AllAchievementsUnlockAsc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockDesc
        {
            get => Theme.AllAchievementsUnlockDesc ?? EmptyAchievementList;
            set => Theme.AllAchievementsUnlockDesc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityAsc
        {
            get => Theme.AllAchievementsRarityAsc ?? EmptyAchievementList;
            set => Theme.AllAchievementsRarityAsc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityDesc
        {
            get => Theme.AllAchievementsRarityDesc ?? EmptyAchievementList;
            set => Theme.AllAchievementsRarityDesc = value;
        }

        // All recent unlocks across all games (newest first).
        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocks
        {
            get => Theme.MostRecentUnlocks ?? EmptyAchievementList;
            set => Theme.MostRecentUnlocks = value;
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop3
        {
            get => Theme.MostRecentUnlocksTop3 ?? EmptyAchievementList;
            set => Theme.MostRecentUnlocksTop3 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop5
        {
            get => Theme.MostRecentUnlocksTop5 ?? EmptyAchievementList;
            set => Theme.MostRecentUnlocksTop5 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop10
        {
            get => Theme.MostRecentUnlocksTop10 ?? EmptyAchievementList;
            set => Theme.MostRecentUnlocksTop10 = value;
        }

        // All rare recent unlocks across all games (rarest first), limited to the last 180 days.
        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocks
        {
            get => Theme.RarestRecentUnlocks ?? EmptyAchievementList;
            set => Theme.RarestRecentUnlocks = value;
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop3
        {
            get => Theme.RarestRecentUnlocksTop3 ?? EmptyAchievementList;
            set => Theme.RarestRecentUnlocksTop3 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop5
        {
            get => Theme.RarestRecentUnlocksTop5 ?? EmptyAchievementList;
            set => Theme.RarestRecentUnlocksTop5 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop10
        {
            get => Theme.RarestRecentUnlocksTop10 ?? EmptyAchievementList;
            set => Theme.RarestRecentUnlocksTop10 = value;
        }

        // Per-provider game lists
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> SteamGames
        {
            get => Theme.SteamGames;
            set => Theme.SteamGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GOGGames
        {
            get => Theme.GOGGames;
            set => Theme.GOGGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EpicGames
        {
            get => Theme.EpicGames;
            set => Theme.EpicGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XboxGames
        {
            get => Theme.XboxGames;
            set => Theme.XboxGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PSNGames
        {
            get => Theme.PSNGames;
            set => Theme.PSNGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RetroAchievementsGames
        {
            get => Theme.RetroAchievementsGames;
            set => Theme.RetroAchievementsGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RPCS3Games
        {
            get => Theme.RPCS3Games;
            set => Theme.RPCS3Games = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ShadPS4Games
        {
            get => Theme.ShadPS4Games;
            set => Theme.ShadPS4Games = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ManualGames
        {
            get => Theme.ManualGames;
            set => Theme.ManualGames = value;
        }

        #endregion

        #region Legacy Theme Compatibility

        // === SuccessStory Compatibility ===
        private ICommand _refreshSelectedGameCommand;

        [DontSerialize]
        public bool HasData
        {
            // Legacy themes (including migrated SuccessStory themes) use HasData as a
            // selected-game flag, so keep this bound to per-game achievement state.
            get => Theme.HasAchievements;
            set => Theme.HasAchievements = value;
        }

        [DontSerialize]
        public bool HasDataAllGames
        {
            get => LegacyTheme.HasDataAllGames;
            set => LegacyTheme.HasDataAllGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GamesWithAchievements
        {
            get => LegacyTheme.GamesWithAchievements;
            set => LegacyTheme.GamesWithAchievements = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PlatinumGames
        {
            get => LegacyTheme.PlatinumGames;
            set => LegacyTheme.PlatinumGames = value;
        }

        [DontSerialize]
        public int TotalTrophies
        {
            get => LegacyTheme.TotalTrophies;
            set => LegacyTheme.TotalTrophies = value;
        }

        [DontSerialize]
        public int PlatinumTrophies
        {
            get => LegacyTheme.PlatinumTrophies;
            set => LegacyTheme.PlatinumTrophies = value;
        }

        [DontSerialize]
        public int GoldTrophies
        {
            get => LegacyTheme.GoldTrophies;
            set => LegacyTheme.GoldTrophies = value;
        }

        [DontSerialize]
        public int SilverTrophies
        {
            get => LegacyTheme.SilverTrophies;
            set => LegacyTheme.SilverTrophies = value;
        }

        [DontSerialize]
        public int BronzeTrophies
        {
            get => LegacyTheme.BronzeTrophies;
            set => LegacyTheme.BronzeTrophies = value;
        }

        [DontSerialize]
        public int TotalUnlockCount
        {
            get => Theme.TotalUnlockCount;
            set => Theme.TotalUnlockCount = value;
        }

        [DontSerialize]
        public int TotalCommonUnlockCount
        {
            get => Theme.TotalCommonUnlockCount;
            set => Theme.TotalCommonUnlockCount = value;
        }

        [DontSerialize]
        public int TotalUncommonUnlockCount
        {
            get => Theme.TotalUncommonUnlockCount;
            set => Theme.TotalUncommonUnlockCount = value;
        }

        [DontSerialize]
        public int TotalRareUnlockCount
        {
            get => Theme.TotalRareUnlockCount;
            set => Theme.TotalRareUnlockCount = value;
        }

        [DontSerialize]
        public int TotalUltraRareUnlockCount
        {
            get => Theme.TotalUltraRareUnlockCount;
            set => Theme.TotalUltraRareUnlockCount = value;
        }

        [DontSerialize]
        public int Level
        {
            get => LegacyTheme.Level;
            set => LegacyTheme.Level = value;
        }

        [DontSerialize]
        public double LevelProgress
        {
            get => LegacyTheme.LevelProgress;
            set => LegacyTheme.LevelProgress = value;
        }

        [DontSerialize]
        public string Rank
        {
            get => LegacyTheme.Rank;
            set => LegacyTheme.Rank = value;
        }

        [DontSerialize]
        public ICommand RefreshSelectedGameCommand
        {
            get => _refreshSelectedGameCommand;
            set => SetValue(ref _refreshSelectedGameCommand, value);
        }

        [DontSerialize]
        public bool EnableIntegrationCompact => true;

        [DontSerialize]
        public bool EnableIntegrationButton => true;

        [DontSerialize]
        public bool EnableIntegrationViewItem => true;

        [DontSerialize]
        public bool EnableIntegrationCompactUnlocked => true;

        [DontSerialize]
        public bool EnableIntegrationCompactLocked => true;

        [DontSerialize]
        public bool Is100Percent => LegacyTheme.Is100Percent;

        [DontSerialize]
        public int Locked => LegacyTheme.Locked;

        [DontSerialize]
        public List<AchievementDetail> ListAchievements => LegacyTheme.ListAchievements ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> ListAchUnlockDateAsc => LegacyTheme.ListAchUnlockDateAsc ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> ListAchUnlockDateDesc => LegacyTheme.ListAchUnlockDateDesc ?? EmptyAchievementList;

        // === Old Inline Properties ===

        [DontSerialize]
        public bool HasDataLegacy
        {
            get => LegacyTheme.HasData;
            set => LegacyTheme.HasData = value;
        }

        [DontSerialize]
        public int Total
        {
            get => LegacyTheme.Total;
            set => LegacyTheme.Total = value;
        }

        [DontSerialize]
        public int Unlocked
        {
            get => LegacyTheme.Unlocked;
            set => LegacyTheme.Unlocked = value;
        }

        [DontSerialize]
        public double Percent
        {
            get => LegacyTheme.Percent;
            set => LegacyTheme.Percent = value;
        }

        // === Aniki ReMake Compatibility ===

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> AllGamesWithAchievements
        {
            get => LegacyTheme.AllGamesWithAchievements;
            set => LegacyTheme.AllGamesWithAchievements = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PlatinumGamesAscending
        {
            get => LegacyTheme.PlatinumGamesAscending;
            set => LegacyTheme.PlatinumGamesAscending = value;
        }

        [DontSerialize]
        public string GSTotal
        {
            get => LegacyTheme.GSTotal;
            set => LegacyTheme.GSTotal = value;
        }

        [DontSerialize]
        public string GSPlat
        {
            get => LegacyTheme.GSPlat;
            set => LegacyTheme.GSPlat = value;
        }

        [DontSerialize]
        public string GS90
        {
            get => LegacyTheme.GS90;
            set => LegacyTheme.GS90 = value;
        }

        [DontSerialize]
        public string GS30
        {
            get => LegacyTheme.GS30;
            set => LegacyTheme.GS30 = value;
        }

        [DontSerialize]
        public string GS15
        {
            get => LegacyTheme.GS15;
            set => LegacyTheme.GS15 = value;
        }

        [DontSerialize]
        public string GSScore
        {
            get => LegacyTheme.GSScore;
            set => LegacyTheme.GSScore = value;
        }

        [DontSerialize]
        public string GSLevel
        {
            get => LegacyTheme.GSLevel;
            set => LegacyTheme.GSLevel = value;
        }

        [DontSerialize]
        public double GSLevelProgress
        {
            get => LegacyTheme.GSLevelProgress;
            set => LegacyTheme.GSLevelProgress = value;
        }

        [DontSerialize]
        public string GSRank
        {
            get => LegacyTheme.GSRank;
            set => LegacyTheme.GSRank = value;
        }

        #endregion

        #region Internal Fields and Methods

        [DontSerialize]
        internal PlayniteAchievementsPlugin _plugin;

        /// <summary>
        /// Copies persisted settings from another settings instance.
        /// Used by the ViewModel when applying settings changes.
        /// </summary>
        internal void CopyPersistedFrom(PlayniteAchievementsSettings other)
        {
            if (other == null)
            {
                return;
            }

            // Copy the entire PersistedSettings object
            Persisted = other.Persisted?.Clone() ?? new PersistedSettings();
        }

        /// <summary>
        /// Initializes theme properties that are not persisted.
        /// Called when settings are loaded from storage to ensure DontSerialize
        /// properties are properly initialized.
        /// </summary>
        internal void InitializeThemeProperties()
        {
            if (Theme == null)
            {
                Theme = new ThemeData();
            }
            if (LegacyTheme == null)
            {
                LegacyTheme = new LegacyThemeData();
            }
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor for deserialization.
        /// Initializes all composition properties with default instances.
        /// </summary>
        public PlayniteAchievementsSettings()
        {
            Persisted = new PersistedSettings();
            Theme = new ThemeData();
            LegacyTheme = new LegacyThemeData();
        }

        /// <summary>
        /// Constructor for initialization with plugin reference.
        /// Initializes all composition properties with default instances.
        /// </summary>
        public PlayniteAchievementsSettings(PlayniteAchievementsPlugin plugin) : this()
        {
            _plugin = plugin;
        }

        #endregion

        #region Game Context

        [DontSerialize]
        private Game _selectedGame;

        /// <summary>
        /// The currently selected game in Playnite's main view.
        /// Exposed for fullscreen themes to bind backgrounds, covers, logos, etc.
        /// Similar to SuccessStory's GameContext property.
        ///
        /// IMPORTANT: This is a stored property that gets set when OnGameSelected is called,
        /// not a computed property that queries MainView.SelectedGames. This ensures that
        /// fullscreen themes receive the correct game context even when MainView.SelectedGames
        /// may not be synchronized with the game context passed to GameContextChanged.
        /// </summary>
        [DontSerialize]
        public Game SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (value != _selectedGame)
                {
                    _selectedGame = value;
                    OnPropertyChanged(nameof(SelectedGame));
                    OnPropertyChanged(nameof(SelectedGameCoverPath));
                    OnPropertyChanged(nameof(SelectedGameBackgroundPath));
                }
            }
        }

        /// <summary>
        /// Full file path to the selected game's cover image.
        /// Themes can bind to this property to get the cover image path.
        /// </summary>
        [DontSerialize]
        public string SelectedGameCoverPath => _selectedGame != null && !string.IsNullOrWhiteSpace(_selectedGame.CoverImage)
            ? _plugin?.PlayniteApi?.Database?.GetFullFilePath(_selectedGame.CoverImage)
            : null;

        [DontSerialize]
        public string SelectedGameBackgroundPath => _selectedGame != null && !string.IsNullOrWhiteSpace(_selectedGame.BackgroundImage)
            ? _plugin?.PlayniteApi?.Database?.GetFullFilePath(_selectedGame.BackgroundImage)
            : null;

        #endregion
    }
}
