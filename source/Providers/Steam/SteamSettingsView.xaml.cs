using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Settings view for the Steam provider.
    /// </summary>
    public partial class SteamSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(SteamSettingsView));

        private readonly SteamSessionManager _sessionManager;
        private SteamSettings _steamSettings;

        #region DependencyProperties

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(
                nameof(AuthBusy),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }

        public static readonly DependencyProperty FullyConfiguredProperty =
            DependencyProperty.Register(
                nameof(FullyConfigured),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool FullyConfigured
        {
            get => (bool)GetValue(FullyConfiguredProperty);
            set => SetValue(FullyConfiguredProperty, value);
        }

        public static readonly DependencyProperty WebAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(WebAuthenticated),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool WebAuthenticated
        {
            get => (bool)GetValue(WebAuthenticatedProperty);
            set => SetValue(WebAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty WebAuthStatusProperty =
            DependencyProperty.Register(
                nameof(WebAuthStatus),
                typeof(string),
                typeof(SteamSettingsView),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Settings_Status_NotChecked")));

        public string WebAuthStatus
        {
            get => (string)GetValue(WebAuthStatusProperty);
            set => SetValue(WebAuthStatusProperty, value);
        }

        #endregion

        public override string ProviderKey => "Steam";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_Steam");
        public override string IconKey => "ProviderIconSteam";

        public new SteamSettings Settings => _steamSettings;

        public SteamSettingsView(SteamSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _steamSettings = settings as SteamSettings;
            base.Initialize(settings);
            RefreshAuthStatus();
        }

        public Task RefreshAuthStatusAsync()
        {
            RefreshAuthStatus();
            return Task.CompletedTask;
        }

        private void RefreshAuthStatus()
        {
            var steamId64 = _sessionManager?.GetCachedSteamId64();
            var hasWebAuth = !string.IsNullOrWhiteSpace(steamId64);
            var hasApiKey = !string.IsNullOrWhiteSpace(_steamSettings?.SteamApiKey);
            var hasUserId = !string.IsNullOrWhiteSpace(_steamSettings?.SteamUserId);

            WebAuthenticated = hasWebAuth;
            FullyConfigured = hasWebAuth && hasApiKey;

            if (hasWebAuth)
            {
                WebAuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_LoggedIn"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
            }
            else
            {
                WebAuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotLoggedIn"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
            }
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await _sessionManager.AuthenticateInteractiveAsync(CancellationToken.None);
                RefreshAuthStatus();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam web login failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                _sessionManager.ClearSession();
                RefreshAuthStatus();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam logout failed");
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
