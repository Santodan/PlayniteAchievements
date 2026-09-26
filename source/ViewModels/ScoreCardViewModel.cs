using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.ViewModels.Items;

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

        /// <summary>
        /// Upper bound on the segmented bar's cell count. The shipped curve gives every rank ten
        /// levels; the clamp only keeps a hand-edited curve from drawing a hairline-wide bar.
        /// </summary>
        private const int MaxSegments = 25;

        private static readonly int MaxDisplayLevel =
            AchievementLevelCurveSettings.ModernDefault.MaxDisplayLevel;

        private static readonly Brush BronzeScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0xD6, 0x8A, 0x45));
        private static readonly Brush SilverScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0xD7, 0xE1, 0xEC));
        private static readonly Brush GoldScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xD4, 0x57));
        private static readonly Brush PlatinumScoreAccentBrush = CreateFrozenBrush(Color.FromRgb(0x84, 0xD8, 0xFF));
        private static readonly Brush BronzeScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xD6, 0x8A, 0x45));
        private static readonly Brush SilverScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0xD7, 0xE1, 0xEC));
        private static readonly Brush GoldScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0xFF, 0xD4, 0x57));
        private static readonly Brush PlatinumScoreBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0x84, 0xD8, 0xFF));

        // Unreached levels of the current rank. Heavier than the card's own background tint so the
        // bar still reads as ten cells when the card is drawn flat, without chrome behind it.
        private static readonly Brush BronzeScoreTrackBrush = CreateFrozenBrush(Color.FromArgb(0x3A, 0xD6, 0x8A, 0x45));
        private static readonly Brush SilverScoreTrackBrush = CreateFrozenBrush(Color.FromArgb(0x3A, 0xD7, 0xE1, 0xEC));
        private static readonly Brush GoldScoreTrackBrush = CreateFrozenBrush(Color.FromArgb(0x3A, 0xFF, 0xD4, 0x57));
        private static readonly Brush PlatinumScoreTrackBrush = CreateFrozenBrush(Color.FromArgb(0x3A, 0x84, 0xD8, 0xFF));

        private int _score;
        private int _level;
        private double _levelProgress;
        private string _rank = DefaultRank;
        private bool _useUniformRarityBadges;
        private AchievementLevelSnapshot _snapshot = AchievementLevelCalculator.CalculateModern(0);

        public ScoreCardViewModel(ScoreCardType scoreType)
        {
            ScoreType = scoreType;
            UpdateSegments();
        }

        public ScoreCardType ScoreType { get; }

        public int Score => _score;

        public int Level => _level;

        public double LevelProgress => _levelProgress;

        public string Rank => _rank;

        public bool UseUniformRarityBadges => _useUniformRarityBadges;

        /// <summary>
        /// One cell per level of the current rank: completed levels solid, the level in progress
        /// part-filled, the rest tinted. The bar's length is the rank's length, which is what makes
        /// it readable without a caption explaining how long a rank runs.
        /// </summary>
        public ObservableCollection<ScoreSegmentViewModel> Segments { get; } =
            new ObservableCollection<ScoreSegmentViewModel>();

        public string Label => ScoreType == ScoreCardType.Collection
            ? L("LOCPlayAch_Score_Collection")
            : L("LOCPlayAch_Score_Prestige");

        /// <summary>The score's name without "Score", for hosts that fold it into the tier line.</summary>
        public string ShortLabel => ScoreType == ScoreCardType.Collection
            ? L("LOCPlayAch_Showcase_ScoreMode_Collection")
            : L("LOCPlayAch_Showcase_ScoreMode_Prestige");

        public string ScoreText => Score.ToString("N0", FormattingCulture.Current);

        public string PointsText => string.Format(
            L("LOCPlayAch_Score_PointsFormat"),
            ScoreText);

        public string LevelText => string.Format(
            FormattingCulture.Current,
            L("LOCPlayAch_Score_LevelFormat"),
            Level);

        public string TierText => AchievementRankPresentation.FormatRank(Rank);

        /// <summary>The tier line of the compact card, which has no separate label line.</summary>
        public string CompactTierText => FormatPair(ShortLabel, TierText);

        /// <summary>Completed passes through the rank ladder; 0 until level 250.</summary>
        public int Mastery => _snapshot?.Mastery ?? 0;

        public bool HasMastery => Mastery > 0;

        public string MasteryText => FormatMastery(Mastery);

        public string TooltipTitleText => HasMastery
            ? FormatPair(TierText, MasteryText)
            : TierText;

        /// <summary>The card's single caption line: where you are, and what the bar is filling toward.</summary>
        public string CaptionText => FormatPair(LevelText, PointsUntilNextLevelText);

        public string PointsUntilNextLevelText => FormatPointsUntilNextLevel(_snapshot);

        public string TooltipLevelLabel => L("LOCPlayAch_Common_Label_Level");

        /// <summary>Level within the current mastery pass, out of the pass length.</summary>
        public string TooltipLevelValueText => FormatCounts(
            GetPassLevel(Level, Mastery),
            MaxDisplayLevel);

        public string TooltipRankPositionLabel => L("LOCPlayAch_Score_Tooltip_LevelInTier");

        public string TooltipRankPositionValueText => FormatCounts(
            GetLevelWithinRank(_snapshot),
            Math.Max(1, _snapshot.LevelsInRank));

        /// <summary>False at max level, where the next-rank row already states it.</summary>
        public bool HasNextLevel => _snapshot?.IsMaxLevel != true;

        public string TooltipNextLevelLabel => FormatNextLevel(_snapshot);

        public string TooltipNextLevelValueText => string.Format(
            L("LOCPlayAch_Score_PointsFormat"),
            Math.Max(0, _snapshot?.PointsUntilNextLevel ?? 0).ToString("N0", FormattingCulture.Current));

        public string TooltipNextRankLabel =>IsAtNextRankCeiling(_snapshot)
            ? L("LOCPlayAch_Score_Tooltip_MaxLevel")
            : FormatNextRank(_snapshot);

        public string TooltipNextRankValueText => IsAtNextRankCeiling(_snapshot)
            ? string.Empty
            : string.Format(
                L("LOCPlayAch_Score_PointsFormat"),
                Math.Max(0, _snapshot.PointsUntilNextRank).ToString("N0", FormattingCulture.Current));

        public string BadgeIconKey => AchievementRankPresentation.GetScoreCardBadgeIconKey(
            Rank,
            UseUniformRarityBadges);

        public Brush AccentBrush => GetScoreAccentBrush(Rank);

        public Brush NextTierAccentBrush => GetNextTierAccentBrush(_snapshot, Rank);

        public Brush AccentBackgroundBrush => GetScoreAccentBackgroundBrush(Rank);

        public Brush AccentTrackBrush => GetScoreAccentTrackBrush(Rank);

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
            _snapshot = AchievementLevelCalculator.CalculateModern(score);
            UpdateSegments();
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

            UpdateSegments();
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(NextTierAccentBrush));
            OnPropertyChanged(nameof(AccentBackgroundBrush));
            OnPropertyChanged(nameof(AccentTrackBrush));
        }

        /// <summary>
        /// Resizes the bar to the current rank's level span and repaints each cell. The collection
        /// is mutated in place rather than rebuilt so the item containers survive a score change.
        /// </summary>
        private void UpdateSegments()
        {
            var count = Math.Max(1, Math.Min(MaxSegments, _snapshot?.LevelsInRank ?? 1));

            while (Segments.Count > count)
            {
                Segments.RemoveAt(Segments.Count - 1);
            }

            while (Segments.Count < count)
            {
                Segments.Add(new ScoreSegmentViewModel());
            }

            var accent = AccentBrush;
            var track = AccentTrackBrush;
            var completed = Math.Max(0, Math.Min(count, _snapshot?.LevelsCompletedInRank ?? 0));
            var partial = _snapshot?.IsMaxLevel == true ? 1d : _levelProgress / 100d;

            for (var i = 0; i < count; i++)
            {
                if (i < completed)
                {
                    Segments[i].Fill = accent;
                }
                else if (i == completed)
                {
                    Segments[i].Fill = CreateSegmentFill(partial, accent, track);
                }
                else
                {
                    Segments[i].Fill = track;
                }
            }
        }

        /// <summary>
        /// The level in progress: a hard-stop gradient at the fill fraction, so one cell reads as a
        /// partly-earned level rather than a separate widget.
        /// </summary>
        private static Brush CreateSegmentFill(double fraction, Brush accent, Brush track)
        {
            if (double.IsNaN(fraction) || fraction <= 0.005d)
            {
                return track;
            }

            if (fraction >= 0.995d)
            {
                return accent;
            }

            var accentColor = (accent as SolidColorBrush)?.Color ?? Colors.Gray;
            var trackColor = (track as SolidColorBrush)?.Color ?? Colors.Transparent;
            var offset = Math.Max(0d, Math.Min(1d, fraction));
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0.5),
                EndPoint = new System.Windows.Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(accentColor, 0));
            brush.GradientStops.Add(new GradientStop(accentColor, offset));
            brush.GradientStops.Add(new GradientStop(trackColor, offset));
            brush.GradientStops.Add(new GradientStop(trackColor, 1));
            brush.Freeze();
            return brush;
        }

        private void RaiseAllPropertiesChanged()
        {
            OnPropertyChanged(nameof(Score));
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(LevelProgress));
            OnPropertyChanged(nameof(Rank));
            OnPropertyChanged(nameof(UseUniformRarityBadges));
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(ShortLabel));
            OnPropertyChanged(nameof(ScoreText));
            OnPropertyChanged(nameof(PointsText));
            OnPropertyChanged(nameof(LevelText));
            OnPropertyChanged(nameof(TierText));
            OnPropertyChanged(nameof(CompactTierText));
            OnPropertyChanged(nameof(Mastery));
            OnPropertyChanged(nameof(HasMastery));
            OnPropertyChanged(nameof(MasteryText));
            OnPropertyChanged(nameof(TooltipTitleText));
            OnPropertyChanged(nameof(CaptionText));
            OnPropertyChanged(nameof(PointsUntilNextLevelText));
            OnPropertyChanged(nameof(TooltipLevelLabel));
            OnPropertyChanged(nameof(TooltipLevelValueText));
            OnPropertyChanged(nameof(TooltipRankPositionLabel));
            OnPropertyChanged(nameof(TooltipRankPositionValueText));
            OnPropertyChanged(nameof(HasNextLevel));
            OnPropertyChanged(nameof(TooltipNextLevelLabel));
            OnPropertyChanged(nameof(TooltipNextLevelValueText));
            OnPropertyChanged(nameof(TooltipNextRankLabel));
            OnPropertyChanged(nameof(TooltipNextRankValueText));
            OnPropertyChanged(nameof(BadgeIconKey));
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(NextTierAccentBrush));
            OnPropertyChanged(nameof(AccentBackgroundBrush));
            OnPropertyChanged(nameof(AccentTrackBrush));
        }

        private static int GetDisplayLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

            return snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level;
        }

        /// <summary>Which level of the current rank the player is on, counting from one.</summary>
        private static int GetLevelWithinRank(AchievementLevelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 1;
            }

            var levelsInRank = Math.Max(1, snapshot.LevelsInRank);
            var position = snapshot.IsMaxLevel
                ? levelsInRank
                : snapshot.LevelsCompletedInRank + 1;
            return Math.Max(1, Math.Min(levelsInRank, position));
        }

        private static int GetPassLevel(int level, int mastery)
        {
            var passLevel = (long)level - ((long)mastery * MaxDisplayLevel);
            return (int)Math.Max(0, Math.Min(MaxDisplayLevel, passLevel));
        }

        /// <summary>
        /// The rank after the current one. Past the last rank of a pass the ladder restarts, so the
        /// next rank is named with the mastery it opens.
        /// </summary>
        private static string FormatNextRank(AchievementLevelSnapshot snapshot)
        {
            var next = AchievementRankPresentation.FormatRank(snapshot.NextRank);
            if (snapshot.NextRankValue.HasValue && snapshot.NextRankValue.Value < snapshot.RankValue)
            {
                return FormatPair(next, FormatMastery(snapshot.Mastery + 1));
            }

            return next;
        }

        private static string FormatMastery(int mastery)
        {
            return string.Format(
                L("LOCPlayAch_Score_MasteryFormat"),
                Math.Max(0, mastery).ToString("N0", FormattingCulture.Current));
        }

        private static bool IsAtNextRankCeiling(AchievementLevelSnapshot snapshot)
        {
            return snapshot == null ||
                snapshot.IsMaxLevel ||
                string.IsNullOrWhiteSpace(snapshot.NextRank);
        }

        private static double ClampPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }

            return Math.Max(0, Math.Min(100, value));
        }

        private static string FormatPointsUntilNextLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot?.IsMaxLevel == true)
            {
                return L("LOCPlayAch_Score_Tooltip_MaxLevel");
            }

            var points = string.Format(
                L("LOCPlayAch_Score_PointsFormat"),
                Math.Max(0, snapshot?.PointsUntilNextLevel ?? 0).ToString("N0", FormattingCulture.Current));

            return string.Format(
                L("LOCPlayAch_Score_Tooltip_NextLevelRemainingFormat"),
                points,
                FormatNextLevel(snapshot));
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

        private static string FormatPair(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(second))
            {
                return first;
            }

            return string.Format(L("LOCPlayAch_Format_Pair"), first, second);
        }

        private static string FormatCounts(int current, int total)
        {
            return string.Format(
                L("LOCPlayAch_Format_Counts"),
                current.ToString("N0", FormattingCulture.Current),
                total.ToString("N0", FormattingCulture.Current));
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

        private static Brush GetScoreAccentTrackBrush(string rank)
        {
            var tier = AchievementRankPresentation.GetRarityTier(rank);
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return PlatinumScoreTrackBrush;
                case RarityTier.Rare:
                    return GoldScoreTrackBrush;
                case RarityTier.Uncommon:
                    return SilverScoreTrackBrush;
                default:
                    return BronzeScoreTrackBrush;
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
