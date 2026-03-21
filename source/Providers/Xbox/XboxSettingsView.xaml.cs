using System;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Providers.Xbox
{
    public partial class XboxSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(XboxSettingsView));
        private readonly XboxSessionManager _sessionManager;
        private XboxSettings _xboxSettings;

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(XboxSettingsView), new PropertyMetadata(false));
        public bool AuthBusy { get => (bool)GetValue(AuthBusyProperty); set => SetValue(AuthBusyProperty, value); }

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(XboxSettingsView), new PropertyMetadata(false));
        public bool IsAuthenticated { get => (bool)GetValue(IsAuthenticatedProperty); set => SetValue(IsAuthenticatedProperty, value); }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(XboxSettingsView), new PropertyMetadata(string.Empty));
        public string AuthStatus { get => (string)GetValue(AuthStatusProperty); set => SetValue(AuthStatusProperty, value); }

        public override string ProviderKey => "Xbox";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_Xbox");
        public override string IconKey => "ProviderIconXbox";

        public new XboxSettings Settings => _xboxSettings;

        public XboxSettingsView(XboxSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _xboxSettings = settings as XboxSettings;
            base.Initialize(settings);
            RefreshAuthStatus();
        }

        public void RefreshAuthStatus()
        {
            var isAuthenticated = _sessionManager?.IsAuthenticated ?? false;
            IsAuthenticated = isAuthenticated;
            AuthStatus = isAuthenticated
                ? string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_LoggedIn"), "Xbox")
                : string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotLoggedIn"), "Xbox");
        }

        public Task RefreshAuthStatusAsync()
        {
            RefreshAuthStatus();
            return Task.CompletedTask;
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try { SetAuthBusy(true); await _sessionManager.LoginAsync(); RefreshAuthStatus(); }
            catch (Exception ex) { Logger.Error(ex, "Xbox login failed"); }
            finally { SetAuthBusy(false); }
        }

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            try { SetAuthBusy(true); await _sessionManager.LogoutAsync(); RefreshAuthStatus(); }
            catch (Exception ex) { Logger.Error(ex, "Xbox logout failed"); }
            finally { SetAuthBusy(false); }
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) AuthBusy = busy;
            else Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
        }
    }
}
