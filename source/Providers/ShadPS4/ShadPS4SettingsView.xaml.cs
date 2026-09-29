using System;
using System.Linq;
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
            GameDataPathsEditor.Configure(
                ShadPS4PathResolver.ValidateConfiguredPath,
                () => _playniteApi?.Dialogs?.SelectFolder());
            GameDataPathsEditor.PathsChanged += GameDataPathsEditor_PathsChanged;
        }

        public override void Initialize(IProviderSettings settings)
        {
            _shadps4Settings = settings as ShadPS4Settings;
            base.Initialize(settings);
            GameDataPathsEditor.SetPaths(_shadps4Settings?.GameDataPaths);
            CheckShadPS4Auth();
        }

        public Task RefreshAuthStatusAsync()
        {
            GameDataPathsEditor.Revalidate();
            CheckShadPS4Auth();
            return Task.CompletedTask;
        }

        private void GameDataPathsEditor_PathsChanged(object sender, EventArgs e)
        {
            if (_shadps4Settings != null)
            {
                _shadps4Settings.GameDataPaths = GameDataPathsEditor.GetPaths();
            }

            CheckShadPS4Auth();
        }

        private void CheckShadPS4Auth()
        {
            var paths = ProviderPathList.Normalize(_shadps4Settings?.GameDataPaths);
            if (paths.Any(path => ShadPS4PathResolver.ValidateConfiguredPath(path).IsValid))
            {
                SetAuthenticated(true);
                SetAuthStatusByKey("LOCPlayAch_Status_Succeeded");
                return;
            }

            if (paths.Count == 0)
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

    }
}
