using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Freezable resource that captures a DataContext through the resource inheritance context,
    /// so objects outside the visual tree (e.g. DataGrid columns) can bind back to it via
    /// Source={StaticResource ...}.
    /// </summary>
    public sealed class BindingProxy : Freezable
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data),
            typeof(object),
            typeof(BindingProxy));

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        protected override Freezable CreateInstanceCore() => new BindingProxy();
    }
}
