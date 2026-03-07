using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop theme integration control displaying achievements in a horizontal scrolling row.
    /// Shows compact achievement icons with progress bars and rarity glow effects.
    /// </summary>
    public partial class AchievementCompactListControl : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        /// <summary>
        /// Identifies the IconSize dependency property.
        /// </summary>
        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(AchievementCompactListControl),
                new PropertyMetadata(56.0));

        /// <summary>
        /// Gets or sets the size of each achievement icon.
        /// Default is 56 to allow space for glow effect around the icon.
        /// </summary>
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public AchievementCompactListControl()
        {
            InitializeComponent();
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        /// <summary>
        /// Handles mouse wheel for horizontal scrolling.
        /// </summary>
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0)
            {
                e.Handled = true;
                var scrollViewer = FindScrollViewer(AchievementsList);
                if (scrollViewer != null)
                {
                    scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
                }
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            if (parent == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
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
        /// Forces the ItemsControl to refresh its ItemsSource binding.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            if (AchievementsList != null)
            {
                var binding = AchievementsList.GetBindingExpression(ItemsControl.ItemsSourceProperty);
                binding?.UpdateTarget();
            }
        }
    }
}
