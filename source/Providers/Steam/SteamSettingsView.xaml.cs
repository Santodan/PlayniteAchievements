using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Diagnostics;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Models;

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
            _ = RefreshAuthStatusAsync();
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                var result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
                UpdateAuthStatusFromResult(result);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Steam auth probe failed during settings refresh.");
                UpdateAuthStatusFromResult(AuthProbeResult.ProbeFailed());
            }
        }

        private void UpdateAuthStatusFromResult(AuthProbeResult result)
        {
            var hasWebAuth = result.IsSuccess;
            var hasApiKey = !string.IsNullOrWhiteSpace(_steamSettings?.SteamApiKey);

            WebAuthenticated = hasWebAuth;
            FullyConfigured = hasWebAuth && hasApiKey;

            if (hasWebAuth)
            {
                WebAuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_AlreadyAuthenticated"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
            }
            else
            {
                WebAuthStatus = ResourceProvider.GetString(result.MessageKey) ?? result.MessageKey;
            }
        }

        private void RefreshAuthStatus()
        {
            var cachedId = _sessionManager.GetCachedSteamId64();
            var hasWebAuth = !string.IsNullOrWhiteSpace(cachedId);
            var hasApiKey = !string.IsNullOrWhiteSpace(_steamSettings?.SteamApiKey);

            WebAuthenticated = hasWebAuth;
            FullyConfigured = hasWebAuth && hasApiKey;

            if (hasWebAuth)
            {
                WebAuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_AlreadyAuthenticated"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
            }
            else
            {
                WebAuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotAuthenticated"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
            }
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                var result = await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, ct: CancellationToken.None);
                UpdateAuthStatusFromResult(result);
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

        private async void SteamAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await RefreshAuthStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam auth check failed");
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

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
