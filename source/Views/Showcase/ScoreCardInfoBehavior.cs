using System.Windows;
using System.Windows.Controls.Primitives;
using PlayniteAchievements.Views.Dialogs;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Opens the score-info dialog from a host's own info button, letting widget templates stay
    /// declarative instead of wiring Click in code-behind. The button belongs to the surface
    /// hosting the score cards rather than to each card, so two cards share one affordance.
    /// </summary>
    public static class ScoreCardInfoBehavior
    {
        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached(
                "Enabled",
                typeof(bool),
                typeof(ScoreCardInfoBehavior),
                new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject element) =>
            (bool)element.GetValue(EnabledProperty);

        public static void SetEnabled(DependencyObject element, bool value) =>
            element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is ButtonBase button))
            {
                return;
            }

            button.Click -= OnInfoClick;
            if (e.NewValue is bool enabled && enabled)
            {
                button.Click += OnInfoClick;
            }
        }

        private static void OnInfoClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ScoreInfoDialogPresenter.Show();
        }
    }
}
