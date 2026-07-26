using System;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;

namespace PlayniteAchievements.ViewModels
{
    public enum ScoreCardType
    {
        Collection,
        Prestige
    }

    public sealed class ScoreCardViewModel : ObservableObject
    {
        private const string DefaultRank = "Bronze5";
        private static readonly Brush BronzeScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0xD6, 0x8A, 0x45));
        private static readonly Brush SilverScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0xD7, 0xE1, 0xEC));
        private static readonly Brush GoldScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xD4, 0x57));
        private static readonly Brush PlatinumScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0x84, 0xD8, 0xFF));
        private static readonly Brush BronzeScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xD6, 0x8A, 0x45));
        private static readonly Brush SilverScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0xD7, 0xE1, 0xEC));
        private static readonly Brush GoldScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0xFF, 0xD4, 0x57));
        private static readonly Brush PlatinumScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0x84, 0xD8, 0xFF));

        private int _score;
        private int _level;
        private double _levelProgress;
        private string _rank = DefaultRank;
        private bool _useUniformRarityBadges;

        public ScoreCardViewModel(ScoreCardType scoreType)
        {
            ScoreType = scoreType;
        }

        public ScoreCardType ScoreType { get; }

        public int Score => _score;

        public int Level => _level;

        public double LevelProgress => _levelProgress;

        public string Rank => _rank;

        public bool UseUniformRarityBadges => _useUniformRarityBadges;

        public string Label => ScoreType == ScoreCardType.Collection
            ? L("LOCPlayAch_Score_Collection")
            : L("LOCPlayAch_Score_Prestige");

        public string ScoreText => Score.ToString("N0", FormattingCulture.Current);

        public string PointsText => string.Format(
            L("LOCPlayAch_Score_PointsFormat"),
            ScoreText);

        public string LevelText => string.Format(
            FormattingCulture.Current,
            L("LOCPlayAch_Score_LevelFormat"),
            Level);

        public string TierText => AchievementRankPresentation.FormatRank(Rank);

        public string DetailText => string.Format(
            L("LOCPlayAch_Score_HeaderDetailFormat"),
            TierText,
            LevelText,
            PointsText);

        public string CurrentLevelPointsText => FormatCurrentLevelPoints(
            AchievementLevelCalculator.CalculateModern(Score));

        public string PointsUntilNextLevelText => FormatPointsUntilNextLevel(
            AchievementLevelCalculator.CalculateModern(Score));

        public string NextTierThresholdText => FormatNextTierThreshold(
            AchievementLevelCalculator.CalculateModern(Score));

        public string BadgeIconKey => AchievementRankPresentation.GetScoreCardBadgeIconKey(
            Rank,
            UseUniformRarityBadges);

        public Brush AccentBrush => GetScoreAccentBrush(Rank);

        public Brush NextTierAccentBrush => GetNextTierAccentBrush(
            AchievementLevelCalculator.CalculateModern(Score),
            Rank);

        public Brush AccentBackgroundBrush => GetScoreAccentBackgroundBrush(Rank);

        public void Apply(
            int score,
            int level,
            double levelProgress,
            string rank,
            bool useUniformRarityBadges)
        {
            score = Math.Max(0, score);
            level = Math.Max(0, level);
            levelProgress = ClampPercent(levelProgress);
            rank = string.IsNullOrWhiteSpace(rank) ? DefaultRank : rank;

            if (_score == score &&
                _level == level &&
                Math.Abs(_levelProgress - levelProgress) < 0.001d &&
                string.Equals(_rank, rank, StringComparison.Ordinal) &&
                _useUniformRarityBadges == useUniformRarityBadges)
            {
                return;
            }

            _score = score;
            _level = level;
            _levelProgress = levelProgress;
            _rank = rank;
            _useUniformRarityBadges = useUniformRarityBadges;
            RaiseAllPropertiesChanged();
        }

        public void ApplyFromScore(int score, bool useUniformRarityBadges)
        {
            var snapshot = AchievementLevelCalculator.CalculateModern(score);
            Apply(
                score,
                GetDisplayLevel(snapshot),
                snapshot?.LevelProgress ?? 0,
                snapshot?.Rank,
                useUniformRarityBadges);
        }

        public void RefreshBadgeStyle(bool useUniformRarityBadges)
        {
            if (_useUniformRarityBadges != useUniformRarityBadges)
            {
                _useUniformRarityBadges = useUniformRarityBadges;
                OnPropertyChanged(nameof(UseUniformRarityBadges));
                OnPropertyChanged(nameof(BadgeIconKey));
            }

            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(NextTierAccentBrush));
            OnPropertyChanged(nameof(AccentBackgroundBrush));
        }

        private void RaiseAllPropertiesChanged()
        {
            OnPropertyChanged(nameof(Score));
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(LevelProgress));
            OnPropertyChanged(nameof(Rank));
            OnPropertyChanged(nameof(UseUniformRarityBadges));
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(ScoreText));
            OnPropertyChanged(nameof(PointsText));
            OnPropertyChanged(nameof(LevelText));
            OnPropertyChanged(nameof(TierText));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(CurrentLevelPointsText));
            OnPropertyChanged(nameof(PointsUntilNextLevelText));
            OnPropertyChanged(nameof(NextTierThresholdText));
            OnPropertyChanged(nameof(BadgeIconKey));
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(NextTierAccentBrush));
            OnPropertyChanged(nameof(AccentBackgroundBrush));
        }

        private static int GetDisplayLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

            return snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level;
        }

        private static double ClampPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }

            return Math.Max(0, Math.Min(100, value));
        }

        private static string FormatCurrentLevelPoints(AchievementLevelSnapshot snapshot)
        {
            var progressLabel = L("LOCPlayAch_Progress");
            if (snapshot == null || snapshot.CurrentLevelTotalPoints <= 0)
            {
                return $"{progressLabel}: 0/0";
            }

            return string.Format(
                FormattingCulture.Current,
                "{0}: {1:N0}/{2:N0}",
                progressLabel,
                snapshot.CurrentLevelPoints,
                snapshot.CurrentLevelTotalPoints);
        }

        private static string FormatPointsUntilNextLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot?.IsMaxLevel == true)
            {
                return L("LOCPlayAch_Score_Tooltip_MaxLevel");
            }

            return string.Format(
                L("LOCPlayAch_Score_Tooltip_NextLevelRemainingFormat"),
                Math.Max(0, snapshot?.PointsUntilNextLevel ?? 0).ToString("N0", FormattingCulture.Current),
                FormatNextLevel(snapshot));
        }

        private static string FormatNextTierThreshold(AchievementLevelSnapshot snapshot)
        {
            if (snapshot?.IsMaxLevel == true || string.IsNullOrWhiteSpace(snapshot.NextRank))
            {
                return L("LOCPlayAch_Score_Tooltip_MaxLevel");
            }

            var nextTier = AchievementRankPresentation.FormatRank(snapshot.NextRank);
            return string.Format(
                L("LOCPlayAch_Score_Tooltip_NextLevelRemainingFormat"),
                Math.Max(0, snapshot.PointsUntilNextRank).ToString("N0", FormattingCulture.Current),
                nextTier);
        }

        private static string FormatNextLevel(AchievementLevelSnapshot snapshot)
        {
            var currentLevel = snapshot == null
                ? 0
                : (snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level);
            return string.Format(
                FormattingCulture.Current,
                L("LOCPlayAch_Score_LevelFormat"),
                Math.Max(0, currentLevel + 1));
        }

        private static Brush GetScoreAccentBrush(string rank)
        {
            var tier = AchievementRankPresentation.GetRarityTier(rank);
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return PlatinumScoreAccentBrush;
                case RarityTier.Rare:
                    return GoldScoreAccentBrush;
                case RarityTier.Uncommon:
                    return SilverScoreAccentBrush;
                default:
                    return BronzeScoreAccentBrush;
            }
        }

        private static Brush GetNextTierAccentBrush(AchievementLevelSnapshot snapshot, string fallbackRank)
        {
            return GetScoreAccentBrush(string.IsNullOrWhiteSpace(snapshot?.NextRank)
                ? fallbackRank
                : snapshot.NextRank);
        }

        private static Brush GetScoreAccentBackgroundBrush(string rank)
        {
            var tier = AchievementRankPresentation.GetRarityTier(rank);
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return PlatinumScoreBackgroundBrush;
                case RarityTier.Rare:
                    return GoldScoreBackgroundBrush;
                case RarityTier.Uncommon:
                    return SilverScoreBackgroundBrush;
                default:
                    return BronzeScoreBackgroundBrush;
            }
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
