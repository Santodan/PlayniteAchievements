using System;
using System.Windows;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// A game cover tile. When the hosting widget draws from showcase pins, the tile
    /// also exposes reorder/unpin commands, which the tile context menu adds beside the
    /// Open items (see ShowcaseMosaicClickBehavior).
    /// </summary>
    public sealed class GameTileViewModel : ObservableObject
    {
        // Width-to-height bounds for a cover's own shape, so a stray banner or strip image
        // cannot stretch a row out of proportion.
        private const double MinCoverAspect = 0.5;
        private const double MaxCoverAspect = 2.0;

        private readonly Guid? _gameId;
        private readonly string _pinCollectionId;
        private readonly bool _rarityBarEnabled;
        private double _coverWidth;
        private double? _rarityPercent;

        public GameTileViewModel(
            GameSummaryItem game,
            bool pinnable,
            string pinCollectionId,
            double coverWidth,
            double coverHeight,
            int decodePixel,
            bool useCovers = true,
            bool showCompletionGlow = false,
            int spacing = 6,
            bool showRarityBar = false)
        {
            _gameId = game.PlayniteGameId;
            _pinCollectionId = pinCollectionId;
            // Icon tiles keep Uniform stretch (HasCover false) so icons are never cropped;
            // cover art fills its tile.
            var cover = game.GameCoverPath;
            var icon = game.GameLogo;
            HasCover = useCovers && !string.IsNullOrWhiteSpace(cover);
            CoverPath = useCovers
                ? (HasCover ? cover : icon)
                : (!string.IsNullOrWhiteSpace(icon) ? icon : cover);
            CoverHeight = coverHeight;
            // A cover takes its own shape at the shared row height. Until its aspect is known
            // (see NeedsAspectProbe) it holds the default portrait width.
            _coverWidth = coverWidth;
            NeedsAspectProbe = HasCover;
            if (HasCover && ImagePixelSize.TryGetCachedAspectRatio(cover, out var aspect))
            {
                ApplyAspectRatio(aspect);
            }

            DecodePixel = decodePixel;
            GameName = game.GameName;
            IsPinnable = pinnable && game.PlayniteGameId.HasValue;
            IsCompleted = game.IsCompleted;
            ShowCompletionGlow = showCompletionGlow && game.IsCompleted;
            _rarityBarEnabled = showRarityBar;
            GlowSpacing = showCompletionGlow;
            TileMargin = new Thickness(showCompletionGlow ? Math.Max(spacing, GlowClearance) : spacing);
            IsSeamless = spacing == 0 && !showCompletionGlow;

            MoveEarlierCommand = new RelayCommand(_ => Move(-1));
            MoveLaterCommand = new RelayCommand(_ => Move(1));
            UnpinCommand = new RelayCommand(_ => Unpin());
        }

        /// <summary>The tile's Playnite game, which a click opens in the library.</summary>
        public Guid? GameId => _gameId;

        public string CoverPath { get; }

        public bool HasCover { get; }

        /// <summary>The tile's width: the cover's own aspect at <see cref="CoverHeight"/>.</summary>
        public double CoverWidth
        {
            get => _coverWidth;
            private set => SetValue(ref _coverWidth, value);
        }

        /// <summary>The shared row height every tile in the mosaic is drawn at.</summary>
        public double CoverHeight { get; }

        /// <summary>True while the cover's aspect ratio has not been read yet.</summary>
        public bool NeedsAspectProbe { get; private set; }

        public int DecodePixel { get; }

        public string GameName { get; }

        public bool IsPinnable { get; }

        public bool IsCompleted { get; }

        /// <summary>True when the tile's game is completed and the widget shows the glow.</summary>
        public bool ShowCompletionGlow { get; }

        /// <summary>
        /// Global unlock percent of the game's capstone (or, with no capstone, its rarest
        /// achievement), shown by the rarity bar.
        /// </summary>
        public double? RarityPercent
        {
            get => _rarityPercent;
            private set => SetValue(ref _rarityPercent, value, nameof(RarityPercent), nameof(ShowRarityBar));
        }

        /// <summary>
        /// True when the widget's rarity bar option is on, the game is completed (the same gate as
        /// the completion glow), and a percent was found.
        /// </summary>
        public bool ShowRarityBar => _rarityBarEnabled && IsCompleted && _rarityPercent.HasValue;

        /// <summary>
        /// True on every tile while the widget's completion-glow option is on, completed or not,
        /// so the whole mosaic shares one tile size. The glow's bloom (blur 14, depth 2) and ray
        /// reach (about 0.275 of the art per side) both need roughly 14-16px of clearance; with
        /// less, neighboring tiles' art paints over the overflow and only the rays' pale inner
        /// copies survive in the gutters.
        /// </summary>
        public bool GlowSpacing { get; }

        /// <summary>
        /// Space around the tile: the widget's spacing option, raised to the glow's clearance
        /// while the completion glow is on (see <see cref="GlowSpacing"/>).
        /// </summary>
        public Thickness TileMargin { get; }

        /// <summary>Flush with its neighbours: square corners, so zero spacing leaves no gaps.</summary>
        public bool IsSeamless { get; }

        private const int GlowClearance = 14;

        public RelayCommand MoveEarlierCommand { get; }

        public RelayCommand MoveLaterCommand { get; }

        public RelayCommand UnpinCommand { get; }

        private static ShowcaseSettings Settings =>
            PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;

        /// <summary>
        /// Sizes the tile to a probed cover aspect (width over height). An unreadable cover (0)
        /// keeps the default portrait width.
        /// </summary>
        public void ApplyAspectRatio(double aspect)
        {
            NeedsAspectProbe = false;
            if (aspect > 0)
            {
                CoverWidth = Math.Round(CoverHeight * Math.Max(MinCoverAspect, Math.Min(MaxCoverAspect, aspect)));
            }
        }

        public void SetRarityPercent(double? percent) => RarityPercent = percent;

        private void Move(int direction)
        {
            if (_gameId.HasValue &&
                ShowcasePinService.MoveGame(
                    Settings,
                    _pinCollectionId,
                    _gameId.Value,
                    direction))
            {
                ShowcaseConfigurationCommit.Commit();
            }
        }

        private void Unpin()
        {
            if (!_gameId.HasValue)
            {
                return;
            }

            ShowcasePinService.ToggleGame(Settings, _pinCollectionId, _gameId.Value);
            ShowcaseConfigurationCommit.Commit();
        }
    }
}
