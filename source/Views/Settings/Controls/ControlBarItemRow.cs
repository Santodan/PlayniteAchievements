using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// One control bar item in a grid's Display Settings: whether the grid's bar shows it, and
    /// its place among the bar's items. Hiding an item also clears its filter, since the Display
    /// Settings have no editor to show it. An item moves among the items that share its place:
    /// the bar itself, or the Filters panel.
    /// </summary>
    public sealed class ControlBarItemRow : PlayniteAchievements.Common.ObservableObject
    {
        private readonly GridCommonOptions _options;
        private readonly IGridControlBarEntry _entry;
        private readonly Func<IReadOnlyList<ControlBarItemRow>> _rows;

        /// <param name="rows">Every row of the list this one belongs to, in the order shown.</param>
        public ControlBarItemRow(
            GridCommonOptions options,
            IGridControlBarEntry entry,
            Func<IReadOnlyList<ControlBarItemRow>> rows = null)
        {
            _options = options;
            _entry = entry;
            _rows = rows;
            MoveEarlierCommand = new PlayniteAchievements.Common.RelayCommand(_ => Move(-1));
            MoveLaterCommand = new PlayniteAchievements.Common.RelayCommand(_ => Move(1));
            PropertyChangedEventManager.AddHandler(options, OnOptionsChanged, nameof(GridCommonOptions.HiddenControlBarItems));
        }

        public string Id => _entry.Id;

        public string Label => ResourceProvider.GetString(_entry.LabelKey);

        public bool IsShown
        {
            get => !_options.IsControlBarItemHidden(_entry.Id);
            set
            {
                if (value == IsShown)
                {
                    return;
                }

                if (!value)
                {
                    _entry.ClearSelection();
                }

                _options.SetControlBarItemHidden(_entry.Id, !value);
            }
        }

        /// <summary>False for the search box, which keeps its own place on the bar.</summary>
        public bool CanReorder => _entry is GridControlBarItem;

        public bool CanMoveEarlier => Moved(-1) != null;

        public bool CanMoveLater => Moved(1) != null;

        public ICommand MoveEarlierCommand { get; }

        public ICommand MoveLaterCommand { get; }

        // The dropdown filters sit in the Filters panel unless its button is hidden.
        private bool InPanel =>
            _entry is GridControlBarItem item &&
            item.IsPanelFilter &&
            !_options.IsControlBarItemHidden(ShowcaseControlBarItems.Filters);

        private List<string> Moved(int direction)
        {
            var rows = _rows?.Invoke();
            if (!CanReorder || rows == null)
            {
                return null;
            }

            var inPanel = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var ids = new List<string>();
            foreach (var row in rows.Where(row => row.CanReorder && !string.IsNullOrWhiteSpace(row.Id)))
            {
                if (!inPanel.ContainsKey(row.Id))
                {
                    inPanel[row.Id] = row.InPanel;
                    ids.Add(row.Id);
                }
            }

            var mine = InPanel;
            return GridControlBarOrder.Move(
                ids,
                Id,
                direction,
                id => inPanel.TryGetValue(id, out var other) && other == mine);
        }

        private void Move(int direction)
        {
            var order = Moved(direction);
            if (order != null)
            {
                _options.ControlBarItemOrder = order;
            }
        }

        private void OnOptionsChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsShown));
            OnPropertyChanged(nameof(CanMoveEarlier));
            OnPropertyChanged(nameof(CanMoveLater));
        }
    }
}
