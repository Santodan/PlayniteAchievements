using System;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.Exophase
{
    public partial class ExophaseSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(ExophaseSettingsView));
        private readonly ExophaseSessionManager _sessionManager;
        private ExophaseSettings _exophaseSettings;

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(ExophaseSettingsView), new PropertyMetadata(false));
        public bool AuthBusy { get => (bool)GetValue(AuthBusyProperty); set => SetValue(AuthBusyProperty, value); }

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(ExophaseSettingsView), new PropertyMetadata(false));
        public bool IsAuthenticated { get => (bool)GetValue(IsAuthenticatedProperty); set => SetValue(IsAuthenticatedProperty, value); }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(ExophaseSettingsView), new PropertyMetadata(string.Empty));
        public string AuthStatus { get => (string)GetValue(AuthStatusProperty); set => SetValue(AuthStatusProperty, value); }

        public override string ProviderKey => "Exophase";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_Exophase");
        public override string IconKey => "ProviderIconExophase";

        public new ExophaseSettings Settings => _exophaseSettings;

        public ExophaseSettingsView(ExophaseSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _exophaseSettings = settings as ExophaseSettings;
            base.Initialize(settings);
            RefreshAuthStatus();
        }

        public void RefreshAuthStatus()
        {
            var isAuthenticated = _sessionManager?.IsAuthenticated ?? false;
            IsAuthenticated = isAuthenticated;
            AuthStatus = isAuthenticated
                ? string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_LoggedIn"), "Exophase")
                : string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotLoggedIn"), "Exophase");
        }

        public Task RefreshAuthStatusAsync()
        {
            RefreshAuthStatus();
            return Task.CompletedTask;
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try { SetAuthBusy(true); await _sessionManager.LoginAsync(); RefreshAuthStatus(); }
            catch (Exception ex) { Logger.Error(ex, "Exophase login failed"); }
            finally { SetAuthBusy(false); }
        }

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            try { SetAuthBusy(true); await _sessionManager.LogoutAsync(); RefreshAuthStatus(); }
            catch (Exception ex) { Logger.Error(ex, "Exophase logout failed"); }
            finally { SetAuthBusy(false); }
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) AuthBusy = busy;
            else Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
        }
    }
}
