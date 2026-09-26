using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the GameMosaic widget: a dense wrap of game cover tiles. Tiles offer
    /// reorder/unpin context actions only when the source is showcase pins.
    /// </summary>
    public sealed class GameMosaicWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private bool _isSeamless;

        public BulkObservableCollection<GameTileViewModel> Tiles { get; } =
            new BulkObservableCollection<GameTileViewModel>();

        /// <summary>
        /// Zero spacing without the glow: the wrap panel stops spreading leftover row width
        /// between tiles and the covers lose their rounded corners, so the tiles touch.
        /// </summary>
        public bool IsSeamless { get => _isSeamless; private set => SetValue(ref _isSeamless, value); }

        protected override void Refresh()
        {
            var games = Projection?.Games ?? Array.Empty<GameSummaryItem>();
            var useCovers = ShowcaseWidgetOptions.GetGameMosaicUseCovers(Projection?.Instance);
            var showCompletionGlow =
                ShowcaseWidgetOptions.GetGameMosaicShowCompletionGlow(Projection?.Instance);
            // A set width is fixed; unset follows density (standard density is the default width).
            double coverWidth = ShowcaseWidgetOptions.GetMosaicCoverWidthOverride(Projection?.Instance)
                ?? (Density == WidgetViewportDensity.Compact
                    ? 44
                    : Density == WidgetViewportDensity.Expanded ? 72 : ShowcaseWidgetOptions.DefaultMosaicCoverWidth);
            var spacing = ShowcaseWidgetOptions.GetMosaicSpacing(Projection?.Instance);
            // The glow needs its clearance, so glowing tiles never sit flush.
            IsSeamless = spacing == 0 && !showCompletionGlow;
            // Icon tiles are square; cover tiles keep the portrait box-art ratio.
            var coverHeight = useCovers ? Math.Round(coverWidth * 1.4) : coverWidth;
            var decodePixel = Math.Max(64, (int)Math.Ceiling(coverHeight * 2));
            var pinnable = ShowcaseWidgetOptions.GetGameMosaicSource(Projection?.Instance) ==
                ShowcaseGameMosaicSource.Pinned;
            CollectionHelper.Replace(Tiles, OrderGames(games)
                .Select(game => new GameTileViewModel(
                    game,
                    pinnable,
                    Projection?.ResolvedPinCollectionId,
                    coverWidth,
                    coverHeight,
                    decodePixel,
                    useCovers,
                    showCompletionGlow,
                    spacing)));
        }

        /// <summary>
        /// Applies the widget's configured sort over the projected tiles. PinOrder carries no
        /// sort member path, so it leaves the order the mosaic's Source produced in place.
        /// </summary>
        private IEnumerable<GameSummaryItem> OrderGames(IEnumerable<GameSummaryItem> games)
        {
            var list = games.Where(game => game != null).ToList();
            GameSummariesSortHelper.Sort(
                list,
                ShowcaseWidgetOptions.GetGameMosaicSort(Projection?.Instance),
                ShowcaseWidgetOptions.GetMosaicSortDescending(Projection?.Instance)
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);
            return list;
        }
    }
}
