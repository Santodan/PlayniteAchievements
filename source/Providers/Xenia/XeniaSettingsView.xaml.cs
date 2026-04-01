using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xenia
{
    public partial class XeniaSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private readonly IPlayniteAPI _playniteApi;
        private XeniaSettings _xeniaSettings;

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(XeniaSettingsView), new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(XeniaSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new XeniaSettings Settings => _xeniaSettings;

        public XeniaSettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Xenia"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _xeniaSettings = settings as XeniaSettings;
            base.Initialize(settings);
            CheckXeniaAuth();
        }

        public Task RefreshAuthStatusAsync()
        {
            CheckXeniaAuth();
            return Task.CompletedTask;
        }

        private void XeniaAccountPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                CheckXeniaAuth();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private void XeniaAccountPath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CheckXeniaAuth();
        }

        private void Xenia_Browse_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _xeniaSettings.AccountPath = selectedPath;
                CheckXeniaAuth();
            }
        }

        private void CheckXeniaAuth()
        {
            var accountPath = (_xeniaSettings?.AccountPath ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(accountPath))
            {
                SetAuthenticated(false);
                SetAuthStatus(string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_NotConfigured"), ResourceProvider.GetString("LOCPlayAch_Provider_Xenia")));
            }
            else if (Directory.Exists(accountPath))
            {
                if (File.Exists(Path.Combine(accountPath, "Account")))
                {
                    SetAuthenticated(true);
                    SetAuthStatusByKey("LOCPlayAch_XeniaValidation_Success");
                }
                else
                {
                    SetAuthenticated(false);
                    SetAuthStatusByKey("LOCPlayAch_XeniaValidation_NoAccount");
                }
            }
            else
            {
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_XeniaValidation_InvalidPath");
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
