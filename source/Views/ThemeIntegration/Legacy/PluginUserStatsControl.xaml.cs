// --SUCCESSSTORY--
using System;
using System.Collections.ObjectModel;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible user stats control for theme integration.
    /// </summary>
    public partial class PluginUserStatsControl : ThemeControlBase
    {
        private readonly UserStatsViewModel _viewModel = new UserStatsViewModel();

        protected override bool EnableAutomaticThemeDataUpdates => true;

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PlayniteAchievementsSettings.Unlocked) ||
                   propertyName == nameof(PlayniteAchievementsSettings.Locked) ||
                   propertyName == nameof(PlayniteAchievementsSettings.Common) ||
                   propertyName == nameof(PlayniteAchievementsSettings.Uncommon) ||
                   propertyName == nameof(PlayniteAchievementsSettings.Rare) ||
                   propertyName == nameof(PlayniteAchievementsSettings.UltraRare);
        }

        protected override void OnThemeDataUpdated()
        {
            UpdateFromSettings();
        }

        public PluginUserStatsControl()
        {
            InitializeComponent();

            DataContext = _viewModel;
        }

        protected void UpdateFromSettings()
        {
            var settings = Plugin?.Settings;
            var items = new ObservableCollection<UserStatsItem>();

            if (settings != null)
            {
                var unlocked = settings.Unlocked;
                var locked = settings.Locked;
                items.Add(new UserStatsItem { NameShow = "Total", ValueShow = $"{unlocked.ToString("N0", FormattingCulture.Current)}/{(unlocked + locked).ToString("N0", FormattingCulture.Current)}" });
                items.Add(new UserStatsItem { NameShow = "Unlocked", ValueShow = unlocked.ToString("N0", FormattingCulture.Current) });
                items.Add(new UserStatsItem { NameShow = "Locked", ValueShow = locked.ToString("N0", FormattingCulture.Current) });
                items.Add(new UserStatsItem { NameShow = "", ValueShow = "" });
                items.Add(new UserStatsItem { NameShow = "Common", ValueShow = settings.Common?.Stats ?? "0 / 0" });
                items.Add(new UserStatsItem { NameShow = "Uncommon", ValueShow = settings.Uncommon?.Stats ?? "0 / 0" });
                items.Add(new UserStatsItem { NameShow = "Rare", ValueShow = settings.Rare?.Stats ?? "0 / 0" });
                items.Add(new UserStatsItem { NameShow = "Ultra Rare", ValueShow = settings.UltraRare?.Stats ?? "0 / 0" });
            }

            _viewModel.ItemsSource = items;
        }
    }
}
// --END SUCCESSSTORY--
