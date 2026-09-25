using System;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// A game cover tile. When the hosting widget draws from showcase pins, the tile
    /// also exposes reorder/unpin commands, which the tile context menu adds beside the
    /// Open items (see ShowcaseMosaicClickBehavior).
    /// </summary>
    public sealed class GameTileViewModel
    {
        private readonly Guid? _gameId;
        private readonly string _pinCollectionId;

        public GameTileViewModel(
            GameSummaryItem game,
            bool pinnable,
            string pinCollectionId,
            double coverWidth,
            double coverHeight,
            int decodePixel,
            bool useCovers = true,
            bool showCompletionGlow = false)
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
            CoverWidth = coverWidth;
            CoverHeight = coverHeight;
            DecodePixel = decodePixel;
            GameName = game.GameName;
            IsPinnable = pinnable && game.PlayniteGameId.HasValue;
            ShowCompletionGlow = showCompletionGlow && game.IsCompleted;
            GlowSpacing = showCompletionGlow;

            MoveEarlierCommand = new RelayCommand(_ => Move(-1));
            MoveLaterCommand = new RelayCommand(_ => Move(1));
            UnpinCommand = new RelayCommand(_ => Unpin());
        }

        /// <summary>The tile's Playnite game, which a click opens in the library.</summary>
        public Guid? GameId => _gameId;

        public string CoverPath { get; }

        public bool HasCover { get; }

        public double CoverWidth { get; }

        public double CoverHeight { get; }

        public int DecodePixel { get; }

        public string GameName { get; }

        public bool IsPinnable { get; }

        /// <summary>True when the tile's game is completed and the widget shows the glow.</summary>
        public bool ShowCompletionGlow { get; }

        /// <summary>
        /// True on every tile while the widget's completion-glow option is on, completed or not,
        /// so the whole mosaic shares one tile size. The glow's bloom (blur 14, depth 2) and ray
        /// reach (about 0.275 of the art per side) both need roughly 14-16px of clearance; with
        /// less, neighboring tiles' art paints over the overflow and only the rays' pale inner
        /// copies survive in the gutters.
        /// </summary>
        public bool GlowSpacing { get; }

        public RelayCommand MoveEarlierCommand { get; }

        public RelayCommand MoveLaterCommand { get; }

        public RelayCommand UnpinCommand { get; }

        private static ShowcaseSettings Settings =>
            PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;

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
