using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the GameMosaic widget: a dense wrap of game cover tiles. Tiles offer
    /// reorder/unpin context actions only when the source is showcase pins.
    /// </summary>
    public sealed class GameMosaicWidgetViewModel : ShowcaseMosaicWidgetViewModelBase
    {
        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<GameSummaryGridControlBarAdapter> _controlBarSlot;

        // Tiles are reused per game while the layout inputs stay the same, so a filter pass or an
        // unrelated refresh syncs the existing tiles instead of re-creating (and re-decoding) them.
        private readonly Dictionary<GameSummaryItem, GameTileViewModel> _tiles =
            new Dictionary<GameSummaryItem, GameTileViewModel>();
        private readonly HashSet<GameTileViewModel> _probing = new HashSet<GameTileViewModel>();
        private TileLayout _layout;

        // The rarity index is one pass over the snapshot's achievements, rebuilt only when the
        // snapshot is replaced.
        private OverviewDataSnapshot _rarityIndexSnapshot;
        private Dictionary<Guid, GameCapstoneRarityResolver.Candidates> _rarityIndex;

        public GameMosaicWidgetViewModel()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<GameSummaryGridControlBarAdapter>(RefreshTiles);
        }

        public BulkObservableCollection<GameTileViewModel> Tiles { get; } =
            new BulkObservableCollection<GameTileViewModel>();

        protected override void RefreshLayout()
        {
            var useCovers = ShowcaseWidgetOptions.GetGameMosaicUseCovers(Projection?.Instance);
            // A set width is fixed; unset follows density (standard density is the default width).
            double coverWidth = ShowcaseWidgetOptions.GetMosaicCoverWidthOverride(Projection?.Instance)
                ?? (Density == WidgetViewportDensity.Compact
                    ? 44
                    : Density == WidgetViewportDensity.Expanded ? 72 : ShowcaseWidgetOptions.DefaultMosaicCoverWidth);
            // Icon tiles are square. Cover tiles share the portrait box-art height, and each takes
            // its own cover's width at that height (see GameTileViewModel).
            var coverHeight = useCovers ? Math.Round(coverWidth * 1.4) : coverWidth;
            var layout = new TileLayout(
                ShowcaseWidgetOptions.GetGameMosaicSource(Projection?.Instance) == ShowcaseGameMosaicSource.Pinned,
                Projection?.ResolvedPinCollectionId,
                coverWidth,
                coverHeight,
                Math.Max(64, (int)Math.Ceiling(coverHeight * 2)),
                useCovers,
                ShowcaseWidgetOptions.GetGameMosaicShowCompletionGlow(Projection?.Instance),
                ShowcaseWidgetOptions.GetMosaicSpacing(Projection?.Instance),
                ShowcaseWidgetOptions.GetMosaicShowRarityBar(Projection?.Instance));

            if (!layout.Equals(_layout))
            {
                _layout = layout;
                _tiles.Clear();
                return;
            }

            // Same layout: drop only the tiles whose game left the source.
            var current = new HashSet<GameSummaryItem>(Projection?.Games ?? Array.Empty<GameSummaryItem>());
            foreach (var stale in _tiles.Keys.Where(game => !current.Contains(game)).ToList())
            {
                _tiles.Remove(stale);
            }
        }

        protected override void RefreshTiles()
        {
            if (_controlBarSlot.Bind(Projection?.Instance?.InstanceId))
            {
                ControlBar = _controlBarSlot.Adapter.ControlBar;
            }

            // The filter stays in effect while the bar is hidden. The dropdown lists the whole
            // library's platforms and rebuilds only when the library's games change.
            var adapter = _controlBarSlot.Adapter;
            var list = (Projection?.Games ?? Array.Empty<GameSummaryItem>())
                .Where(game => game != null)
                .ToList();
            adapter.UpdateOptions(Projection?.Snapshot?.GameSummaries);

            var capped = adapter.Apply(list)
                .Take(ShowcaseWidgetOptions.GetGameMosaicCount(Projection?.Instance));
            var tiles = OrderGames(capped).Select(GetTile).ToList();
            CollectionHelper.Replace(Tiles, tiles);
            ProbeCoverAspects(tiles);
        }

        private GameTileViewModel GetTile(GameSummaryItem game)
        {
            if (!_tiles.TryGetValue(game, out var tile))
            {
                tile = new GameTileViewModel(
                    game,
                    _layout.Pinnable,
                    _layout.PinCollectionId,
                    _layout.CoverWidth,
                    _layout.CoverHeight,
                    _layout.DecodePixel,
                    _layout.UseCovers,
                    _layout.ShowCompletionGlow,
                    _layout.Spacing,
                    _layout.ShowRarityBar);
                _tiles[game] = tile;
            }

            // Set on reused tiles too, so a new snapshot's percents reach them.
            if (_layout.ShowRarityBar)
            {
                tile.SetRarityPercent(ResolveRarityPercent(game));
            }

            return tile;
        }

        private double? ResolveRarityPercent(GameSummaryItem game)
        {
            if (!game.IsCompleted || !game.PlayniteGameId.HasValue)
            {
                return null;
            }

            var snapshot = Projection?.Snapshot;
            if (_rarityIndex == null || !ReferenceEquals(snapshot, _rarityIndexSnapshot))
            {
                _rarityIndexSnapshot = snapshot;
                _rarityIndex = GameCapstoneRarityResolver.Index(snapshot?.Achievements);
            }

            return _rarityIndex.TryGetValue(game.PlayniteGameId.Value, out var candidates)
                ? GameCapstoneRarityResolver.Resolve(candidates, game.CapstoneTotal > 0)
                : null;
        }

        /// <summary>
        /// Reads the aspect ratio of every cover not yet in the session cache off the UI thread,
        /// then resizes those tiles in one dispatcher pass so the mosaic lays out once.
        /// </summary>
        private void ProbeCoverAspects(IReadOnlyList<GameTileViewModel> tiles)
        {
            var pending = tiles
                .Where(tile => tile.NeedsAspectProbe && _probing.Add(tile))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            var dispatcher = Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                var aspects = pending.Select(tile => ImagePixelSize.GetAspectRatio(tile.CoverPath)).ToList();
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    for (var i = 0; i < pending.Count; i++)
                    {
                        _probing.Remove(pending[i]);
                        pending[i].ApplyAspectRatio(aspects[i]);
                    }
                }));
            });
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

        /// <summary>The inputs every tile is built from; a change to any of them rebuilds the tiles.</summary>
        private sealed class TileLayout : IEquatable<TileLayout>
        {
            public TileLayout(
                bool pinnable,
                string pinCollectionId,
                double coverWidth,
                double coverHeight,
                int decodePixel,
                bool useCovers,
                bool showCompletionGlow,
                int spacing,
                bool showRarityBar)
            {
                Pinnable = pinnable;
                PinCollectionId = pinCollectionId;
                CoverWidth = coverWidth;
                CoverHeight = coverHeight;
                DecodePixel = decodePixel;
                UseCovers = useCovers;
                ShowCompletionGlow = showCompletionGlow;
                Spacing = spacing;
                ShowRarityBar = showRarityBar;
            }

            public bool Pinnable { get; }
            public string PinCollectionId { get; }
            public double CoverWidth { get; }
            public double CoverHeight { get; }
            public int DecodePixel { get; }
            public bool UseCovers { get; }
            public bool ShowCompletionGlow { get; }
            public int Spacing { get; }
            public bool ShowRarityBar { get; }

            public bool Equals(TileLayout other) =>
                other != null &&
                Pinnable == other.Pinnable &&
                string.Equals(PinCollectionId, other.PinCollectionId, StringComparison.Ordinal) &&
                CoverWidth.Equals(other.CoverWidth) &&
                CoverHeight.Equals(other.CoverHeight) &&
                DecodePixel == other.DecodePixel &&
                UseCovers == other.UseCovers &&
                ShowCompletionGlow == other.ShowCompletionGlow &&
                Spacing == other.Spacing &&
                ShowRarityBar == other.ShowRarityBar;

            public override bool Equals(object obj) => Equals(obj as TileLayout);

            public override int GetHashCode() => DecodePixel ^ Spacing ^ CoverWidth.GetHashCode();
        }
    }
}
