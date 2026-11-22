using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Marks a subtree whose owner outlives Unloaded and disposes the grids in it itself, as the
    /// sidebar overview does: Playnite unloads it each time another view is shown and loads it
    /// again on return. Grids there keep their subscriptions and layout state through Unloaded
    /// instead of releasing them and setting them up again on the next Loaded. Grids anywhere
    /// else release on Unloaded.
    /// </summary>
    public static class GridLifetime
    {
        public static readonly DependencyProperty OwnerDisposesProperty = DependencyProperty.RegisterAttached(
            "OwnerDisposes",
            typeof(bool),
            typeof(GridLifetime),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetOwnerDisposes(DependencyObject element) =>
            (bool)element.GetValue(OwnerDisposesProperty);

        public static void SetOwnerDisposes(DependencyObject element, bool value) =>
            element.SetValue(OwnerDisposesProperty, value);
    }
}
