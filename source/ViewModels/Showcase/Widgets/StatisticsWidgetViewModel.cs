using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

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
    /// Backs the Statistics widget: a grid of stat tiles, every statistic or the ones chosen in
    /// its settings, over the games its collection and control bar keep. A Tall viewport
    /// arranges them in a single column.
    /// </summary>
    public sealed class StatisticsWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly ShowcaseStatisticsScope _scope;
        private GridControlBarViewModel _controlBar;
        private int _columns = 2;

        public StatisticsWidgetViewModel()
        {
            _scope = new ShowcaseStatisticsScope(Refresh);
        }

        /// <summary>The platform, game, tag and progress filters its settings show; null when none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            private set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

        public BulkObservableCollection<StatTileViewModel> Tiles { get; } =
            new BulkObservableCollection<StatTileViewModel>();

        public int Columns
        {
            get => _columns;
            private set => SetValue(ref _columns, value);
        }

        protected override void Refresh()
        {
            _scope.Apply(Projection);
            ControlBar = _scope.ShownBar;
            IEnumerable<ShowcaseStatistic> items = _scope.Statistics;
            // The widget's chosen statistics, in the chosen order; every one when none were chosen.
            var keys = ShowcaseWidgetOptions.GetStatisticsKeys(Projection?.Instance);
            if (keys != null)
            {
                var byKey = items.Where(item => item?.Key != null)
                    .GroupBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                items = keys.Where(byKey.ContainsKey).Select(key => byKey[key]).ToList();
            }

            var list = items.Where(item => item != null).ToList();
            Columns = Orientation == WidgetViewportOrientation.Tall ? 1 : 2;

            // The same statistics in the same order only change their values; replacing the
            // tiles would regenerate every one of them for that.
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
        }
    }
}
