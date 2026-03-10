using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK.Models;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements list control for theme integration.
    /// Displays achievements in a DataGrid with sorting and virtualization.
    /// Clones items to maintain independent reveal state per control instance.
    /// </summary>
    public partial class AchievementListControl : ThemeControlBase
    {
        /// <summary>
        /// Identifies the DisplayItems dependency property.
        /// </summary>
        public static readonly DependencyProperty DisplayItemsProperty =
            DependencyProperty.Register(nameof(DisplayItems), typeof(List<AchievementDisplayItem>),
                typeof(AchievementListControl), new PropertyMetadata(new List<AchievementDisplayItem>()));

        /// <summary>
        /// Gets the display items for the list.
        /// </summary>
        public List<AchievementDisplayItem> DisplayItems
        {
            get => (List<AchievementDisplayItem>)GetValue(DisplayItemsProperty);
            private set => SetValue(DisplayItemsProperty, value);
        }

        public AchievementListControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadData();
        }

        private void LoadData()
        {
            var sourceItems = Plugin?.Settings?.Theme?.AllAchievementDisplayItems;
            if (sourceItems == null)
            {
                DisplayItems = new List<AchievementDisplayItem>();
                return;
            }

            DisplayItems = sourceItems.Select(item => item.Clone()).ToList();
        }

        /// <summary>
        /// Determines whether a change raised from ThemeData should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return propertyName == nameof(Models.ThemeIntegration.ThemeData.AllAchievementDisplayItems);
        }

        /// <summary>
        /// Called when theme data changes and the list should be refreshed.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            LoadData();
        }

        /// <summary>
        /// Called when the game context changes for this control.
        /// </summary>
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            if (IsLoaded)
            {
                LoadData();
            }
        }
    }
}
