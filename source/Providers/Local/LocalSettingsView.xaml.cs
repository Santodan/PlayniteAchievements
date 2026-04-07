using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSettingsView : ProviderSettingsViewBase
    {
        private readonly IPlayniteAPI _playniteApi;
        private LocalSettings _localSettings;

        public new LocalSettings Settings => _localSettings;

        public LocalSettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _localSettings = settings as LocalSettings;
            base.Initialize(settings);
        }

        private void SteamUserdataPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                MoveFocusFrom(sender as TextBox);
            }
        }

        private void SteamUserdataPath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        private void SteamUserdataBrowse_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _localSettings.SteamUserdataPath = selectedPath;
            }
        }

        private static void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }
}
