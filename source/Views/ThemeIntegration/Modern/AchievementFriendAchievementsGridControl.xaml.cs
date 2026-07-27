using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.ThemeIntegration.Base;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    public partial class AchievementFriendAchievementsGridControl : ThemeControlBase
    {
        public static readonly DependencyProperty DisplayItemsProperty =
            DependencyProperty.Register(
                nameof(DisplayItems),
                typeof(IEnumerable<AchievementDisplayItem>),
                typeof(AchievementFriendAchievementsGridControl),
                new PropertyMetadata(null));

        public IEnumerable<AchievementDisplayItem> DisplayItems
        {
            get => (IEnumerable<AchievementDisplayItem>)GetValue(DisplayItemsProperty);
            private set => SetValue(DisplayItemsProperty, value);
        }

        public AchievementFriendAchievementsGridControl()
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
                   propertyName == nameof(ModernThemeBindings.DynamicFriendAchievements);
        }

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.ShowFriendsOverviewAchievementsGridColumnHeaders) ||
                   propertyName == nameof(PersistedSettings.FriendsOverviewAchievementsGridRowHeight);
        }

        private void LoadData()
        {
            DisplayItems = (EffectiveTheme?.DynamicFriendAchievements ?? new System.Collections.ObjectModel.ObservableCollection<FriendAchievementDisplayItem>())
                .Cast<AchievementDisplayItem>()
                .ToList();
        }
    }
}
