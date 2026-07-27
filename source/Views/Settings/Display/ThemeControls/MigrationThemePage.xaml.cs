using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Services.ThemeMigration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: theme migration page. Migrates SuccessStory-based themes to the
    /// plugin's Legacy/Modern controls. Shares state with <see cref="RevertThemePage"/> via a
    /// <see cref="ThemeMigrationController"/> bound as DataContext.
    /// </summary>
    public partial class MigrationThemePage : UserControl
    {
        private readonly ThemeMigrationController _controller;

        public MigrationThemePage()
        {
            InitializeComponent();
        }

        internal MigrationThemePage(ThemeMigrationController controller)
            : this()
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            DataContext = _controller;
            _controller.PropertyChanged += OnControllerPropertyChanged;

            Loaded += (s, e) =>
            {
                _controller.EnsureThemesLoaded();
                UpdateModeButtonState();
            };
        }

        private void OnControllerPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(ThemeMigrationController.SelectedThemePath), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(ThemeMigrationController.HasThemesToMigrate), StringComparison.Ordinal))
            {
                UpdateModeButtonState();
            }
        }

        private async void MigrateThemeLimited_Click(object sender, RoutedEventArgs e)
        {
            if (_controller == null) return;
            await _controller.MigrateAsync(MigrationMode.Limited);
        }

        private async void MigrateThemeFull_Click(object sender, RoutedEventArgs e)
        {
            if (_controller == null) return;
            await _controller.MigrateAsync(MigrationMode.Full);
        }

        private async void MigrateThemeCustom_Click(object sender, RoutedEventArgs e)
        {
            if (_controller == null) return;
            await _controller.MigrateCustomAsync();
        }

        private void ThemeMigrationCustomExpander_Expanded(object sender, RoutedEventArgs e)
        {
            UpdateModeButtonState();
        }

        private void ThemeMigrationCustomExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            UpdateModeButtonState();
        }

        private void ThemeMigrationSetAllLegacy_Click(object sender, RoutedEventArgs e)
        {
            _controller?.SetAllCustomOptions(false);
        }

        private void ThemeMigrationSetAllModern_Click(object sender, RoutedEventArgs e)
        {
            _controller?.SetAllCustomOptions(true);
        }

        private void ThemeMigrationRowSetLegacy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: ThemeMigrationElementOption option })
            {
                option.IsModern = false;
            }
        }

        private void ThemeMigrationRowSetModern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: ThemeMigrationElementOption option })
            {
                option.IsModern = true;
            }
        }

        private void UpdateModeButtonState()
        {
            var hasThemesToMigrate = _controller?.HasThemesToMigrate == true;
            var isCustomExpanded = ThemeMigrationCustomExpander?.IsExpanded == true;
            var isFullscreenTheme = ThemeMigrationService.IsFullscreenThemePath(_controller?.SelectedThemePath);

            if (isFullscreenTheme && isCustomExpanded)
            {
                ThemeMigrationCustomExpander.IsExpanded = false;
                isCustomExpanded = false;
            }

            if (ThemeMigrationPresetButtons != null)
            {
                ThemeMigrationPresetButtons.IsEnabled = hasThemesToMigrate && !isCustomExpanded;
            }

            if (ThemeMigrationFullButton != null)
            {
                ThemeMigrationFullButton.IsEnabled = hasThemesToMigrate && !isCustomExpanded && !isFullscreenTheme;
            }

            if (ThemeMigrationCustomContainer != null)
            {
                ThemeMigrationCustomContainer.IsEnabled = hasThemesToMigrate && !isFullscreenTheme;
            }
        }
    }
}
