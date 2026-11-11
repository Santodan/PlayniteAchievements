using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>A single statistic tile: a formatted value over its localized label.</summary>
    public sealed class StatTileViewModel : PlayniteAchievements.Common.ObservableObject
    {
        private string _value;

        public StatTileViewModel(string key, string value, string label)
        {
            Key = key;
            _value = value;
            Label = label;
        }

        public string Key { get; }

        /// <summary>Replaced in place when the data moves, so the tile keeps its visual.</summary>
        public string Value
        {
            get => _value;
            set => SetValue(ref _value, value);
        }

        public string Label { get; }
    }

    /// <summary>
    /// The stat tiles shared by the Statistics widget and the Profile card's stat strip, drawn by
    /// ShowcaseStatBlockTemplate: full rows of up to <see cref="MaxColumns"/> tiles, with a short
    /// last row centered under them.
    /// </summary>
    public sealed class StatBlockViewModel : PlayniteAchievements.Common.ObservableObject
    {
        /// <summary>The most tiles one row holds.</summary>
        public const int MaxColumns = 4;

        private int _columns = 1;
        private TextAlignment _textAlignment = TextAlignment.Left;
        private VerticalAlignment _itemVerticalAlignment = VerticalAlignment.Top;
        private bool _stretchRows;

        public BulkObservableCollection<StatTileViewModel> Tiles { get; } =
            new BulkObservableCollection<StatTileViewModel>();

        public bool HasTiles => Tiles.Count > 0;

        public int Columns
        {
            get => _columns;
            private set => SetValue(ref _columns, value);
        }

        /// <summary>How each tile aligns its value and label.</summary>
        public TextAlignment TextAlignment
        {
            get => _textAlignment;
            set => SetValue(ref _textAlignment, value);
        }

        /// <summary>Where each tile places its value and label within its cell.</summary>
        public VerticalAlignment ItemVerticalAlignment
        {
            get => _itemVerticalAlignment;
            set => SetValue(ref _itemVerticalAlignment, value);
        }

        /// <summary>Whether the rows share out the height the block is given, like a UniformGrid.</summary>
        public bool StretchRows
        {
            get => _stretchRows;
            set => SetValue(ref _stretchRows, value);
        }

        /// <summary>
        /// Shows <paramref name="statistics"/> in order, at most <paramref name="maxColumns"/> per
        /// row (capped at <see cref="MaxColumns"/>). The same statistics in the same order only
        /// change their values; replacing the tiles would regenerate every one of them for that.
        /// </summary>
        public void SetStatistics(IReadOnlyList<ShowcaseStatistic> statistics, int maxColumns = MaxColumns)
        {
            var list = (statistics ?? Array.Empty<ShowcaseStatistic>()).Where(item => item != null).ToList();
            Columns = Math.Max(1, Math.Min(list.Count, Math.Min(MaxColumns, Math.Max(1, maxColumns))));

            if (Tiles.Count == list.Count &&
                Tiles.Select(tile => tile.Key).SequenceEqual(list.Select(item => item.Key), StringComparer.Ordinal))
            {
                for (var index = 0; index < list.Count; index++)
                {
                    Tiles[index].Value = ShowcaseStatisticFormatter.Format(list[index]);
                }

                return;
            }

            Tiles.ReplaceAll(list.Select(item => new StatTileViewModel(
                item.Key,
                ShowcaseStatisticFormatter.Format(item),
                ResourceProvider.GetString(item.LabelKey))));
            OnPropertyChanged(nameof(HasTiles));
        }
    }
}
