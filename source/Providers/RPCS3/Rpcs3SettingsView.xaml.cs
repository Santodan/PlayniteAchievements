using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.RPCS3
{
    public partial class Rpcs3SettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private readonly IPlayniteAPI _playniteApi;
        private Rpcs3Settings _rpcs3Settings;

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(Rpcs3SettingsView), new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(Rpcs3SettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new Rpcs3Settings Settings => _rpcs3Settings;

        public Rpcs3SettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_RPCS3"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _rpcs3Settings = settings as Rpcs3Settings;
            base.Initialize(settings);
            CheckRpcs3Auth();
        }

        public Task RefreshAuthStatusAsync()
        {
            CheckRpcs3Auth();
            return Task.CompletedTask;
        }

        private void Rpcs3ExecutablePath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                CheckRpcs3Auth();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private void Rpcs3ExecutablePath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CheckRpcs3Auth();
        }

        private void Rpcs3_Browse_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFile("rpcs3.exe|rpcs3.exe|Executable files|*.exe");
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _rpcs3Settings.ExecutablePath = selectedPath;
                CheckRpcs3Auth();
            }
        }

        private void CheckRpcs3Auth()
        {
            var exePath = _rpcs3Settings?.ExecutablePath;

            if (string.IsNullOrWhiteSpace(exePath))
            {
                SetAuthenticated(false);
                SetAuthStatus(string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_NotConfigured"), ResourceProvider.GetString("LOCPlayAch_Provider_RPCS3")));
                return;
            }

            var installFolder = System.IO.Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(installFolder) || !System.IO.Directory.Exists(installFolder))
            {
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_InvalidPath");
                return;
            }

            var homePath = System.IO.Path.Combine(installFolder, "dev_hdd0", "home");
            if (!System.IO.Directory.Exists(homePath))
            {
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_Rpcs3Validation_NotRpcs3");
                return;
            }

            string userId = null;
            try
            {
                foreach (var dir in System.IO.Directory.GetDirectories(homePath))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(name) && name.Length == 8 && name.All(char.IsDigit))
                    {
                        userId = name;
                        break;
                    }
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_Rpcs3Validation_NoUser");
                return;
            }

            var trophyPath = System.IO.Path.Combine(homePath, userId, "trophy");
            if (!System.IO.Directory.Exists(trophyPath))
            {
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_Rpcs3Validation_NoTrophyFolder");
                return;
            }

            SetAuthenticated(true);
            SetAuthStatusByKey("LOCPlayAch_Status_Succeeded");
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
