using System;
using System.Linq;
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
            AccountPathsEditor.Configure(
                XeniaAccountResolver.ValidateAccountPath,
                () => _playniteApi?.Dialogs?.SelectFolder());
            AccountPathsEditor.PathsChanged += AccountPathsEditor_PathsChanged;
        }

        public override void Initialize(IProviderSettings settings)
        {
            _xeniaSettings = settings as XeniaSettings;
            base.Initialize(settings);
            AccountPathsEditor.SetPaths(_xeniaSettings?.AccountPaths);
            CheckXeniaAuth();
        }

        public Task RefreshAuthStatusAsync()
        {
            AccountPathsEditor.Revalidate();
            CheckXeniaAuth();
            return Task.CompletedTask;
        }

        private void AccountPathsEditor_PathsChanged(object sender, EventArgs e)
        {
            if (_xeniaSettings != null)
            {
                _xeniaSettings.AccountPaths = AccountPathsEditor.GetPaths();
            }

            CheckXeniaAuth();
        }

        private void CheckXeniaAuth()
        {
            var paths = ProviderPathList.Normalize(_xeniaSettings?.AccountPaths);
            if (paths.Count == 0)
            {
                SetAuthenticated(false);
                SetAuthStatus(string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_NotConfigured"), ResourceProvider.GetString("LOCPlayAch_Provider_Xenia")));
                return;
            }

            var results = paths.Select(XeniaAccountResolver.ValidateAccountPath).ToList();
            if (results.Any(result => result.IsValid))
            {
                SetAuthenticated(true);
                SetAuthStatusByKey("LOCPlayAch_Status_Succeeded");
                return;
            }

            SetAuthenticated(false);
            SetAuthStatusByKey(results[0].MessageKey);
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

    }
}
