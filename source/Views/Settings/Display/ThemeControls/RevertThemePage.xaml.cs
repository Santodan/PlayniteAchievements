using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: theme revert page. Restores themes from the backup created by a
    /// migration. Shares state with <see cref="MigrationThemePage"/> via a
    /// <see cref="ThemeMigrationController"/> bound as DataContext.
    /// </summary>
    public partial class RevertThemePage : UserControl
    {
        private readonly ThemeMigrationController _controller;

        public RevertThemePage()
        {
            InitializeComponent();
        }

        internal RevertThemePage(ThemeMigrationController controller)
            : this()
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            DataContext = _controller;

            Loaded += (s, e) => _controller.EnsureThemesLoaded();
        }

        private async void RevertTheme_Click(object sender, RoutedEventArgs e)
        {
            if (_controller == null) return;
            await _controller.RevertAsync();
        }
    }
}
