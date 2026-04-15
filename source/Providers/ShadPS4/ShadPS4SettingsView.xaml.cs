using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.ShadPS4
{
    public partial class ShadPS4SettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private readonly IPlayniteAPI _playniteApi;
        private ShadPS4Settings _shadps4Settings;

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(ShadPS4SettingsView), new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(ShadPS4SettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new ShadPS4Settings Settings => _shadps4Settings;

        public ShadPS4SettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_ShadPS4"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _shadps4Settings = settings as ShadPS4Settings;
            base.Initialize(settings);
            CheckShadPS4Auth();
        }

        public Task RefreshAuthStatusAsync()
        {
            CheckShadPS4Auth();
            return Task.CompletedTask;
        }

        private void ShadPS4GameDataPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                CheckShadPS4Auth();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private void ShadPS4GameDataPath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CheckShadPS4Auth();
        }

        private void ShadPS4_Browse_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _shadps4Settings.GameDataPath = selectedPath;
                CheckShadPS4Auth();
            }
        }

        private void CheckShadPS4Auth()
        {
            var configuredPath = _shadps4Settings?.GameDataPath;
            var gameDataPath = ShadPS4PathResolver.ResolveConfiguredLegacyGameDataPath(configuredPath);
            if (!string.IsNullOrWhiteSpace(gameDataPath))
            {
                SetAuthenticated(true);
                SetAuthStatusByKey("LOCPlayAch_Status_Succeeded");
                return;
            }

            if (ShadPS4PathResolver.HasConfiguredAppDataTrophyData(configuredPath))
            {
                SetAuthenticated(true);
                SetAuthStatusByKey("LOCPlayAch_Status_Succeeded");
                return;
            }

            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                SetAuthenticated(false);
                SetAuthStatus(string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_NotConfigured"), ResourceProvider.GetString("LOCPlayAch_Provider_ShadPS4")));
            }
            else
            {
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_InvalidPath");
            }
        }

        private void SetAuthStatusByKey(string key)
        {
            var value = ResourceProvider.GetString(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetAuthStatus(value);
            }
        }

        private void SetAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                AuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthStatus = status));
            }
        }

        private void SetAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                IsAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => IsAuthenticated = authenticated));
            }
        }

        private static void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }
}
