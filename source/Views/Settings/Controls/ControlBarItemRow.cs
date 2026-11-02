using System.ComponentModel;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// One control bar item in a grid's Display Settings: whether the grid's bar shows it. Hiding
    /// an item also clears its filter, since the Display Settings have no editor to show it.
    /// </summary>
    public sealed class ControlBarItemRow : PlayniteAchievements.Common.ObservableObject
    {
        private readonly GridCommonOptions _options;
        private readonly IGridControlBarEntry _entry;

        public ControlBarItemRow(GridCommonOptions options, IGridControlBarEntry entry)
        {
            _options = options;
            _entry = entry;
            PropertyChangedEventManager.AddHandler(options, OnOptionsChanged, nameof(GridCommonOptions.HiddenControlBarItems));
        }

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

        private void OnOptionsChanged(object sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(IsShown));
    }
}