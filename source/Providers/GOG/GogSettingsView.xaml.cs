using System;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Providers.GOG
{
    /// <summary>
    /// Settings view for the GOG provider.
    /// </summary>
    public partial class GogSettingsView : ProviderSettingsViewBase
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(GogSettingsView));
        private readonly GogSessionManager _sessionManager;
        private GogSettings _gogSettings;

        #region DependencyProperties

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(
                nameof(AuthBusy),
                typeof(bool),
                typeof(GogSettingsView),
                new PropertyMetadata(false));

        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(IsAuthenticated),
                typeof(bool),
                typeof(GogSettingsView),
                new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(
                nameof(AuthStatus),
                typeof(string),
                typeof(GogSettingsView),
                new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        #endregion

        public override string ProviderKey => "GOG";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_GOG");
        public override string IconKey => "ProviderIconGOG";

        public new GogSettings Settings => _gogSettings;

        public GogSettingsView(GogSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _gogSettings = settings as GogSettings;
            base.Initialize(settings);
            RefreshAuthStatus();
        }

        public void RefreshAuthStatus()
        {
            var isAuthenticated = _sessionManager?.IsAuthenticated ?? false;
            IsAuthenticated = isAuthenticated;

            if (isAuthenticated)
            {
                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_LoggedIn"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_GOG"));
            }
            else
            {
                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotLoggedIn"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_GOG"));
            }
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await _sessionManager.LoginAsync();
                RefreshAuthStatus();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GOG web login failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await _sessionManager.LogoutAsync();
                RefreshAuthStatus();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GOG logout failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                AuthBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
            }
        }
    }
}
