using System;
using System.Linq;
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
            ExecutablePathsEditor.Configure(
                Rpcs3InstallationResolver.ValidateExecutablePath,
                () => _playniteApi?.Dialogs?.SelectFile("rpcs3.exe|rpcs3.exe|Executable files|*.exe"));
            ExecutablePathsEditor.PathsChanged += ExecutablePathsEditor_PathsChanged;
        }

        public override void Initialize(IProviderSettings settings)
        {
            _rpcs3Settings = settings as Rpcs3Settings;
            base.Initialize(settings);
            ExecutablePathsEditor.SetPaths(_rpcs3Settings?.ExecutablePaths);
            CheckRpcs3Auth();
        }

        public Task RefreshAuthStatusAsync()
        {
            ExecutablePathsEditor.Revalidate();
            CheckRpcs3Auth();
            return Task.CompletedTask;
        }

        private void ExecutablePathsEditor_PathsChanged(object sender, EventArgs e)
        {
            if (_rpcs3Settings != null)
            {
                _rpcs3Settings.ExecutablePaths = ExecutablePathsEditor.GetPaths();
            }

            CheckRpcs3Auth();
        }

        private void CheckRpcs3Auth()
        {
            var paths = ProviderPathList.Normalize(_rpcs3Settings?.ExecutablePaths);
            if (paths.Count == 0)
            {
                SetAuthenticated(false);
                SetAuthStatus(string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_NotConfigured"), ResourceProvider.GetString("LOCPlayAch_Provider_RPCS3")));
                return;
            }

            var results = paths.Select(Rpcs3InstallationResolver.ValidateExecutablePath).ToList();
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
