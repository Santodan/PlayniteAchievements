using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.ThemeIntegration.Base;
using System.Collections.Generic;
using System.Windows;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    public partial class AchievementFriendSummariesGridControl : ThemeControlBase
    {
        public static readonly DependencyProperty DisplayItemsProperty =
            DependencyProperty.Register(
                nameof(DisplayItems),
                typeof(IEnumerable<FriendSummaryItem>),
                typeof(AchievementFriendSummariesGridControl),
                new PropertyMetadata(null));

        public IEnumerable<FriendSummaryItem> DisplayItems
        {
            get => (IEnumerable<FriendSummaryItem>)GetValue(DisplayItemsProperty);
            private set => SetValue(DisplayItemsProperty, value);
        }

        public AchievementFriendSummariesGridControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        protected override bool EnableAutomaticThemeDataUpdates => true;
        protected override bool UsesThemeBindings => true;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadData();
        }

        protected override void OnThemeDataUpdated()
        {
            LoadData();
        }

        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return string.IsNullOrWhiteSpace(propertyName) ||
                   propertyName == nameof(ModernThemeBindings.DynamicFriendSummaries);
        }

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.ShowFriendsOverviewFriendSummariesGridColumnHeaders) ||
                   propertyName == nameof(PersistedSettings.FriendsOverviewFriendSummariesGridRowHeight);
        }

        private void LoadData()
        {
            DisplayItems = EffectiveTheme?.DynamicFriendSummaries ?? new System.Collections.ObjectModel.ObservableCollection<FriendSummaryItem>();
        }
    }
}
