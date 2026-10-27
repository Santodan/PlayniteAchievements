using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.ViewModels
{
    public sealed class ScoreCardViewModel : ObservableObject
    {
        private const string DefaultRank = "Bronze5";

        /// <summary>
        /// Upper bound on the segmented bar's cell count. The shipped curve gives every rank ten
        /// levels; the clamp only keeps a hand-edited curve from drawing a hairline-wide bar.
        /// </summary>
        private const int MaxSegments = 25;

        // Collection and Prestige climb the Modern ladder. Points cards have no ladder: they
        // show the platform's points earned out of the points their games hold.
        private static readonly AchievementLevelCurveSettings Curve = AchievementLevelCurveSettings.ModernDefault;

        private static readonly int MaxDisplayLevel = Curve.MaxDisplayLevel;

        private const byte BackgroundAlpha = 0x24;

        // Unreached levels of the current rank. Heavier than the card's own background tint so the
        // bar still reads as ten cells when the card is drawn flat, without chrome behind it.
        private const byte TrackAlpha = 0x3A;

        // Resolved from the badge color settings on every apply and appearance change, and cached so
        // the segments can share the accent instance.
        private Brush _accentBrush;
        private Brush _nextTierAccentBrush;
        private Brush _accentBackgroundBrush;
        private Brush _accentTrackBrush;
        private Brush _accentGlossBrush;

        private int _score;
        private int _total;
        private int _level;
        private double _levelProgress;
        private string _rank = DefaultRank;
        private bool _useUniformRarityBadges;
        private bool _useCustomColors;
        private bool _showTotal = true;
        private string _platformIconKey;
        private string _platformColorHex;
        private AchievementLevelSnapshot _snapshot;
        private ScoreCardType _scoreType;
        private System.Collections.Generic.IReadOnlyList<BadgeCountItem> _rarityBadges = Array.Empty<BadgeCountItem>();
        private string _gameArtPath;

        public ScoreCardViewModel(ScoreCardType scoreType)
        {
            _scoreType = scoreType;
            _snapshot = AchievementLevelCalculator.Calculate(0, Curve);
            ResolveAccentBrushes();
            UpdateSegments();
        }

        /// <summary>The score shown. Fixed per card except through <see cref="ApplyFor"/>, which a slot uses to switch it.</summary>
        public ScoreCardType ScoreType => _scoreType;

        public int Score => _score;

        /// <summary>A points card's possible points: every achievement in its games. 0 for Collection and Prestige.</summary>
        public int Total => _total;

        /// <summary>True for a points card, which shows earned out of total and no tier, level or bar.</summary>
        public bool IsPoints => _scoreType.IsPoints;

        public int Level => _level;

        public double LevelProgress => _levelProgress;

        public string Rank => _rank;

        public bool UseUniformRarityBadges => _useUniformRarityBadges;

        /// <summary>
        /// True to draw the badge and accents in the configured rarity colors; false (the default)
        /// for the shipped colors.
        /// </summary>
        public bool UseCustomColors => _useCustomColors;

        /// <summary>
        /// True (the default) for a points card to show earned out of total; false for earned
        /// alone. The bar fills to earned out of total either way.
        /// </summary>
        public bool ShowTotal => _showTotal;

        /// <summary>A points card's platform icon key; null for Collection and Prestige.</summary>
        public string PlatformIconKey => _platformIconKey;

        /// <summary>A points card's platform color, which also colors its bar; null for Collection and Prestige.</summary>
        public string PlatformColorHex => _platformColorHex;

        /// <summary>A points card's platform icon in the platform color; null for Collection and Prestige.</summary>
        public ImageSource PlatformIcon => IsPoints
            ? ProviderIconConverter.BuildIcon(_platformIconKey, _platformColorHex)
            : null;

        /// <summary>A points card's rarity badges with unlock counts, shown under the bar; empty when off.</summary>
        public System.Collections.Generic.IReadOnlyList<BadgeCountItem> RarityBadges => _rarityBadges;

        public bool HasRarityBadges => IsPoints && _rarityBadges.Count > 0;

        /// <summary>The game's cover or icon a points card shows in place of the platform icon; null for the platform icon.</summary>
        public string GameArtPath => _gameArtPath;

        public bool HasGameArt => IsPoints && !string.IsNullOrWhiteSpace(_gameArtPath);

        public void SetRarityBadges(System.Collections.Generic.IReadOnlyList<BadgeCountItem> badges)
        {
            _rarityBadges = badges ?? Array.Empty<BadgeCountItem>();
            OnPropertyChanged(nameof(RarityBadges));
            OnPropertyChanged(nameof(HasRarityBadges));
        }

        public void SetGameArt(string path)
        {
            if (string.Equals(_gameArtPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _gameArtPath = path;
            OnPropertyChanged(nameof(GameArtPath));
            OnPropertyChanged(nameof(HasGameArt));
        }

        /// <summary>
        /// One cell per level of the current rank: completed levels solid, the level in progress
        /// part-filled, the rest tinted. The bar's length is the rank's length, which is what makes
        /// it readable without a caption explaining how long a rank runs.
        /// </summary>
        public ObservableCollection<ScoreSegmentViewModel> Segments { get; } =
            new ObservableCollection<ScoreSegmentViewModel>();

        public string Label => GetLabel(ScoreType);

        /// <summary>The score's name without "Score", for hosts that fold it into the tier line.</summary>
        public string ShortLabel => GetShortLabel(ScoreType);

        public static string GetLabel(ScoreCardType type)
        {
            if (type.IsPoints)
            {
                return PointsFamilyNames.GetLabel(type.PointsFamily);
            }

            return type.IsPrestige
                ? L("LOCPlayAch_Score_Prestige")
                : L("LOCPlayAch_Score_Collection");
        }

        /// <summary>The score's short name: the card type's name in pickers and compact tier lines.</summary>
        public static string GetShortLabel(ScoreCardType type)
        {
            if (type.IsPoints)
            {
                return PointsFamilyNames.GetShortLabel(type.PointsFamily);
            }

            return type.IsPrestige
                ? L("LOCPlayAch_Showcase_ScoreMode_Prestige")
                : L("LOCPlayAch_Showcase_ScoreMode_Collection");
        }

        public string ScoreText => Score.ToString("N0", FormattingCulture.Current);

        public string PointsText => string.Format(
            L("LOCPlayAch_Score_PointsFormat"),
            ScoreText);

        public string LevelText => string.Format(
            FormattingCulture.Current,
            L("LOCPlayAch_Score_LevelFormat"),
            Level);

        /// <summary>The card's main line: the rank, or a points card's earned points, out of total when shown.</summary>
        public string TierText => IsPoints
            ? (_showTotal ? CountsText : ScoreText)
            : AchievementRankPresentation.FormatRank(Rank);

        /// <summary>Points earned out of the points the card's games hold.</summary>
        public string CountsText => FormatCounts(Score, Total);

        /// <summary>The tier line of the compact card, which has no separate label line.</summary>
        public string CompactTierText => FormatPair(ShortLabel, TierText);

        /// <summary>Completed passes through the rank ladder; 0 until level 250.</summary>
        public int Mastery => _snapshot?.Mastery ?? 0;

        public bool HasMastery => !IsPoints && Mastery > 0;

        public string MasteryText => FormatMastery(Mastery);

        public string TooltipTitleText => IsPoints
            ? Label
            : HasMastery
            ? FormatPair(TierText, MasteryText)
            : TierText;

        /// <summary>Tooltip title for compact cards, which can collapse to the badge alone.</summary>
        public string CompactTooltipTitleText => IsPoints
            ? Label
            : HasMastery
            ? FormatPair(CompactTierText, MasteryText)
            : CompactTierText;

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
            UseUniformRarityBadges,
            UseCustomColors);

        public string MasteryIconKey => AchievementRankPresentation.GetScoreCardMasteryIconKey(UseCustomColors);

        public Brush AccentBrush => _accentBrush;

        public Brush NextTierAccentBrush => _nextTierAccentBrush;

        public Brush AccentBackgroundBrush => _accentBackgroundBrush;

        public Brush AccentTrackBrush => _accentTrackBrush;

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
            _snapshot = AchievementLevelCalculator.Calculate(score, Curve);
            ResolveAccentBrushes();
            UpdateSegments();
            RaiseAllPropertiesChanged();
        }

        public void ApplyFromScore(int score, bool useUniformRarityBadges)
        {
            var snapshot = AchievementLevelCalculator.Calculate(score, Curve);
            Apply(
                score,
                GetDisplayLevel(snapshot),
                snapshot?.LevelProgress ?? 0,
                snapshot?.Rank,
                useUniformRarityBadges);
        }

        /// <summary>
        /// Shows <paramref name="type"/>'s score from <paramref name="snapshot"/>, switching the card
        /// to that type first. Every surface applies its cards through this one switch.
        /// </summary>
        public void ApplyFor(ScoreCardType type, OverviewDataSnapshot snapshot, bool useUniformRarityBadges)
        {
            if (_scoreType != type)
            {
                _scoreType = type;

                // A new type shows a different score, so re-apply even when the number matches.
                _score = -1;
                OnPropertyChanged(nameof(ScoreType));
                OnPropertyChanged(nameof(IsPoints));
            }

            if (type.IsPoints)
            {
                ApplyPoints(snapshot?.GetScore(type) ?? 0, snapshot?.GetPossibleScore(type) ?? 0, useUniformRarityBadges);
                return;
            }

            _total = 0;
            ApplyFromScore(snapshot?.GetScore(type) ?? 0, useUniformRarityBadges);
        }

        /// <summary>Shows a points card's earned and possible points. Points cards have no level or rank.</summary>
        public void ApplyPoints(int score, int total, bool useUniformRarityBadges)
        {
            score = Math.Max(0, score);
            total = Math.Max(0, total);
            if (_score == score && _total == total && _useUniformRarityBadges == useUniformRarityBadges)
            {
                return;
            }

            _score = score;
            _total = total;
            _level = 0;
            _levelProgress = 0;
            _rank = DefaultRank;
            _useUniformRarityBadges = useUniformRarityBadges;
            _snapshot = AchievementLevelCalculator.Calculate(0, Curve);
            ResolveAccentBrushes();
            UpdateSegments();
            RaiseAllPropertiesChanged();
        }

        /// <summary>
        /// Re-resolves the badge and accents from the current appearance settings. The badge key is
        /// raised even when unchanged: the image behind it is regenerated on a recolor, and the
        /// key-to-image converter only looks it up again when the binding re-reads.
        /// </summary>
        public void RefreshBadgeStyle(bool useUniformRarityBadges)
        {
            if (_useUniformRarityBadges != useUniformRarityBadges)
            {
                _useUniformRarityBadges = useUniformRarityBadges;
                OnPropertyChanged(nameof(UseUniformRarityBadges));
            }

            RefreshColors();
        }

        /// <summary>Switches the card between the configured and the shipped rarity colors.</summary>
        public void SetUseCustomColors(bool useCustomColors)
        {
            if (_useCustomColors == useCustomColors)
            {
                return;
            }

            _useCustomColors = useCustomColors;
            OnPropertyChanged(nameof(UseCustomColors));
            RefreshColors();
        }

        /// <summary>Switches a points card between earned out of total and earned alone.</summary>
        public void SetShowTotal(bool showTotal)
        {
            if (_showTotal == showTotal)
            {
                return;
            }

            _showTotal = showTotal;
            OnPropertyChanged(nameof(ShowTotal));
            OnPropertyChanged(nameof(TierText));
            OnPropertyChanged(nameof(CompactTierText));
        }

        /// <summary>
        /// Re-resolves the colors, including a points card's platform color, after a provider color
        /// override changes.
        /// </summary>
        public void RefreshProviderAppearance()
        {
            RefreshColors();
        }

        private void RefreshColors()
        {
            OnPropertyChanged(nameof(BadgeIconKey));
            OnPropertyChanged(nameof(MasteryIconKey));
            ResolveAccentBrushes();
            UpdateSegments();
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(NextTierAccentBrush));
            OnPropertyChanged(nameof(AccentBackgroundBrush));
            OnPropertyChanged(nameof(AccentTrackBrush));
            RaisePlatformChanged();
        }

        private void RaisePlatformChanged()
        {
            OnPropertyChanged(nameof(PlatformIconKey));
            OnPropertyChanged(nameof(PlatformColorHex));
            OnPropertyChanged(nameof(PlatformIcon));
        }

        /// <summary>The settings the colors resolve from: null for the configured ones.</summary>
        private PersistedSettings ColorSettings => _useCustomColors ? null : RarityAppearanceHelper.ShippedAppearance;

        /// <summary>
        /// Resizes the bar to the current rank's level span and repaints each cell. The collection
        /// is mutated in place rather than rebuilt so the item containers survive a score change.
        /// </summary>
        private void UpdateSegments()
        {
            // A points card has no levels, so its bar is one cell filled to earned out of total.
            var count = IsPoints ? 1 : Math.Max(1, Math.Min(MaxSegments, _snapshot?.LevelsInRank ?? 1));

            while (Segments.Count > count)
            {
                Segments.RemoveAt(Segments.Count - 1);
            }

            while (Segments.Count < count)
            {
                Segments.Add(new ScoreSegmentViewModel());
            }

            var track = AccentTrackBrush;
            var completed = IsPoints ? 0 : Math.Max(0, Math.Min(count, _snapshot?.LevelsCompletedInRank ?? 0));
            var partial = IsPoints
                ? PointsFraction
                : _snapshot?.IsMaxLevel == true ? 1d : _levelProgress / 100d;

            // Master ranks sweep the completed gradient across the bar, one solid step per cell.
            var isMaster = !IsPoints && AchievementRankPresentation.IsMasterRank(Rank);
            var sweepStart = isMaster ? RarityAppearanceHelper.GetCompletedStartColor(ColorSettings) : default(Color);
            var sweepEnd = isMaster ? RarityAppearanceHelper.GetCompletedEndColor(ColorSettings) : default(Color);

            for (var i = 0; i < count; i++)
            {
                var accent = isMaster
                    ? CreateGlossBrush(Lerp(sweepStart, sweepEnd, (i + 0.5d) / count))
                    : _accentGlossBrush;

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
        /// The level in progress: the glossed fill drawn over the track up to the fill fraction, so
        /// one cell reads as a partly-earned level rather than a separate widget.
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

            var offset = Math.Max(0d, Math.Min(1d, fraction));
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(track, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 1, 1))));
            drawing.Children.Add(new GeometryDrawing(accent, null, new RectangleGeometry(new System.Windows.Rect(0, 0, offset, 1))));
            var brush = new DrawingBrush(drawing);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// A filled cell: lighter along the top edge and deeper along the bottom, around the
        /// accent at the middle.
        /// </summary>
        private static Brush CreateGlossBrush(Color color)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1)
            };
            brush.GradientStops.Add(new GradientStop(Lerp(color, Colors.White, 0.45d), 0));
            brush.GradientStops.Add(new GradientStop(Lerp(color, Colors.White, 0.15d), 0.45));
            brush.GradientStops.Add(new GradientStop(color, 0.55));
            brush.GradientStops.Add(new GradientStop(Lerp(color, Colors.Black, 0.25d), 1));
            brush.Freeze();
            return brush;
        }

        private void RaiseAllPropertiesChanged()
        {
            OnPropertyChanged(nameof(Score));
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(IsPoints));
            OnPropertyChanged(nameof(CountsText));
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
            OnPropertyChanged(nameof(CompactTooltipTitleText));
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
            RaisePlatformChanged();
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

        private void ResolveAccentBrushes()
        {
            var colorSettings = ColorSettings;
            ResolvePlatformVisuals();
            var accent = IsPoints
                ? GetPointsAccentColor(_platformColorHex, colorSettings)
                : GetAccentColor(Rank, colorSettings);
            _accentBrush = CreateFrozenBrush(accent);
            _accentBackgroundBrush = CreateFrozenBrush(WithAlpha(accent, BackgroundAlpha));
            _accentTrackBrush = CreateFrozenBrush(WithAlpha(accent, TrackAlpha));
            _accentGlossBrush = CreateGlossBrush(accent);
            _nextTierAccentBrush = IsPoints
                ? _accentBrush
                : CreateFrozenBrush(GetAccentColor(
                    string.IsNullOrWhiteSpace(_snapshot?.NextRank) ? Rank : _snapshot.NextRank,
                    colorSettings));
        }

        /// <summary>Points earned out of total, 0 to 1; 0 when the card's games hold no points.</summary>
        private double PointsFraction => _total > 0 ? Math.Min(1d, (double)_score / _total) : 0d;

        /// <summary>
        /// A points card's platform icon and color, from the family's provider key with any
        /// provider color override applied. Cleared for Collection and Prestige.
        /// </summary>
        private void ResolvePlatformVisuals()
        {
            if (!IsPoints)
            {
                _platformIconKey = null;
                _platformColorHex = null;
                return;
            }

            var visuals = ProviderRegistry.ResolveProviderVisualsOrFallback(_scoreType.PointsFamily);
            _platformIconKey = visuals.iconKey;
            _platformColorHex = visuals.colorHex;
        }

        /// <summary>
        /// A points card's color: its platform's color, complete or not. The progress fill (the
        /// theme accent) stands in when the color does not parse.
        /// </summary>
        private static Color GetPointsAccentColor(string platformColorHex, PersistedSettings colorSettings)
        {
            if (TryParseColor(platformColorHex, out var platformColor))
            {
                return platformColor;
            }

            return Application.Current?.TryFindResource("PlayAch.Brush.Progress.Fill") is SolidColorBrush fill
                ? fill.Color
                : RarityAppearanceHelper.GetBaseColor(RarityTier.Common, colorSettings);
        }

        /// <summary>
        /// The rank's color from the badge color settings: the completed-game color for Master
        /// ranks, which wear the completed badge, and the rank's rarity tier color otherwise.
        /// </summary>
        private static Color GetAccentColor(string rank, PersistedSettings colorSettings)
        {
            return AchievementRankPresentation.IsMasterRank(rank)
                ? RarityAppearanceHelper.GetCompletedColor(colorSettings)
                : RarityAppearanceHelper.GetBaseColor(AchievementRankPresentation.GetRarityTier(rank), colorSettings);
        }

        private static bool TryParseColor(string colorHex, out Color color)
        {
            color = default(Color);
            if (string.IsNullOrWhiteSpace(colorHex))
            {
                return false;
            }

            try
            {
                if (ColorConverter.ConvertFromString(colorHex.Trim()) is Color parsed)
                {
                    color = parsed;
                    return true;
                }
            }
            catch (FormatException)
            {
            }

            return false;
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static Color Lerp(Color from, Color to, double amount)
        {
            return Color.FromArgb(
                (byte)Math.Round(from.A + ((to.A - from.A) * amount)),
                (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
                (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
                (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
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
