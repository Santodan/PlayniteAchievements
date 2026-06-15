using System.Collections.Generic;
using System.Collections.ObjectModel;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Legacy SuccessStory/Aniki-compatible binding surface.
    /// Runtime-only and populated from a shared theme runtime state.
    /// </summary>
    public class LegacyThemeBindings : ObservableObject
    {
        [DontSerialize]
        private bool _hasData;
        [DontSerialize]
        private int _total;
        [DontSerialize]
        private int _unlocked;
        [DontSerialize]
        private double _percent;

        [DontSerialize]
        private bool _is100Percent;
        [DontSerialize]
        private int _locked;
        [DontSerialize]
        private int _totalGamerScore;
        [DontSerialize]
        private string _estimateTimeToUnlock;
        [DontSerialize]
        private List<AchievementDetail> _listAchievements = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _listAchUnlockDateAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _listAchUnlockDateDesc = new List<AchievementDetail>();

        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _allGamesWithAchievements = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _gamesWithAchievements = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _platinumGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _platinumGamesAscending = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private bool _hasDataAllGames;
        [DontSerialize]
        private int _totalTrophies;
        [DontSerialize]
        private int _platinumTrophies;
        [DontSerialize]
        private int _goldTrophies;
        [DontSerialize]
        private int _silverTrophies;
        [DontSerialize]
        private int _bronzeTrophies;
        [DontSerialize]
        private int _level;
        [DontSerialize]
        private double _levelProgress;
        [DontSerialize]
        private string _rank = "Bronze5";
        [DontSerialize]
        private string _gsTotal = "0";
        [DontSerialize]
        private string _gsPlat = "0";
        [DontSerialize]
        private string _gs90 = "0";
        [DontSerialize]
        private string _gs30 = "0";
        [DontSerialize]
        private string _gs15 = "0";
        [DontSerialize]
        private string _gsScore = "0";
        [DontSerialize]
        private string _gsLevel = "0";
        [DontSerialize]
        private double _gsLevelProgress;
        [DontSerialize]
        private string _gsRank = "Bronze5";

        [DontSerialize]
        public bool HasData
        {
            get => _hasData;
            set => SetValue(ref _hasData, value);
        }

        [DontSerialize]
        public int Total
        {
            get => _total;
            set => SetValue(ref _total, value);
        }

        [DontSerialize]
        public int Unlocked
        {
            get => _unlocked;
            set => SetValue(ref _unlocked, value);
        }

        [DontSerialize]
        public double Percent
        {
            get => _percent;
            set => SetValue(ref _percent, value);
        }

        [DontSerialize]
        public bool Is100Percent
        {
            get => _is100Percent;
            set => SetValue(ref _is100Percent, value);
        }

        [DontSerialize]
        public int Locked
        {
            get => _locked;
            set => SetValue(ref _locked, value);
        }

        [DontSerialize]
        public int TotalGamerScore
        {
            get => _totalGamerScore;
            set => SetValue(ref _totalGamerScore, value);
        }

        [DontSerialize]
        public string EstimateTimeToUnlock
        {
            get => _estimateTimeToUnlock;
            set => SetValue(ref _estimateTimeToUnlock, value);
        }

        [DontSerialize]
        public List<AchievementDetail> ListAchievements
        {
            get => _listAchievements;
            set => SetValue(ref _listAchievements, value);
        }

        [DontSerialize]
        public List<AchievementDetail> ListAchUnlockDateAsc
        {
            get => _listAchUnlockDateAsc;
            set => SetValue(ref _listAchUnlockDateAsc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> ListAchUnlockDateDesc
        {
            get => _listAchUnlockDateDesc;
            set => SetValue(ref _listAchUnlockDateDesc, value);
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> AllGamesWithAchievements
        {
            get => _allGamesWithAchievements;
            set => ReplaceCollection(_allGamesWithAchievements, value, nameof(AllGamesWithAchievements));
        }

        [DontSerialize]
        public bool HasDataAllGames
        {
            get => _hasDataAllGames;
            set => SetValue(ref _hasDataAllGames, value);
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GamesWithAchievements
        {
            get => _gamesWithAchievements;
            set => ReplaceCollection(_gamesWithAchievements, value, nameof(GamesWithAchievements));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PlatinumGames
        {
            get => _platinumGames;
            set => ReplaceCollection(_platinumGames, value, nameof(PlatinumGames));
        }

        [DontSerialize]
        public int TotalTrophies
        {
            get => _totalTrophies;
            set => SetValue(ref _totalTrophies, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public int PlatinumTrophies
        {
            get => _platinumTrophies;
            set => SetValue(ref _platinumTrophies, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public int GoldTrophies
        {
            get => _goldTrophies;
            set => SetValue(ref _goldTrophies, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public int SilverTrophies
        {
            get => _silverTrophies;
            set => SetValue(ref _silverTrophies, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public int BronzeTrophies
        {
            get => _bronzeTrophies;
            set => SetValue(ref _bronzeTrophies, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public int Level
        {
            get => _level;
            set => SetValue(ref _level, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public double LevelProgress
        {
            get => _levelProgress;
            set => SetValue(ref _levelProgress, value);
        }

        [DontSerialize]
        public string Rank
        {
            get => _rank;
            set => SetValue(ref _rank, value ?? "Bronze5");
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PlatinumGamesAscending
        {
            get => _platinumGamesAscending;
            set => ReplaceCollection(_platinumGamesAscending, value, nameof(PlatinumGamesAscending));
        }

        [DontSerialize]
        public string GSTotal
        {
            get => _gsTotal;
            set => SetValue(ref _gsTotal, value ?? "0");
        }

        [DontSerialize]
        public string GSPlat
        {
            get => _gsPlat;
            set => SetValue(ref _gsPlat, value ?? "0");
        }

        [DontSerialize]
        public string GS90
        {
            get => _gs90;
            set => SetValue(ref _gs90, value ?? "0");
        }

        [DontSerialize]
        public string GS30
        {
            get => _gs30;
            set => SetValue(ref _gs30, value ?? "0");
        }

        [DontSerialize]
        public string GS15
        {
            get => _gs15;
            set => SetValue(ref _gs15, value ?? "0");
        }

        [DontSerialize]
        public string GSScore
        {
            get => _gsScore;
            set => SetValue(ref _gsScore, value ?? "0");
        }

        [DontSerialize]
        public string GSLevel
        {
            get => _gsLevel;
            set => SetValue(ref _gsLevel, value ?? "0");
        }

        [DontSerialize]
        public double GSLevelProgress
        {
            get => _gsLevelProgress;
            set => SetValue(ref _gsLevelProgress, value);
        }

        [DontSerialize]
        public string GSRank
        {
            get => _gsRank;
            set => SetValue(ref _gsRank, value ?? "Bronze5");
        }

        private void ReplaceCollection<T>(BulkObservableCollection<T> target, IEnumerable<T> value, string propertyName)
        {
            target.ReplaceAll(value ?? new List<T>());
            OnPropertyChanged(propertyName);
        }
    }
}
