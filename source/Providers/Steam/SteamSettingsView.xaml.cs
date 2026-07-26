using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
                new PropertyMetadata(
                    ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked")));

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
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _steamSettings = settings as SteamSettings;
            base.Initialize(settings);
            SetAuthStatusVisualState(pending: true, success: false);
            WebAuthenticated = false;
            FullyConfigured = false;
            WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                SetAuthStatusChecking();
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Checking");
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
            var probedSteamUserId = hasWebAuth && !string.IsNullOrWhiteSpace(result.UserId)
                ? result.UserId.Trim()
                : null;

            if (_steamSettings != null &&
                !string.IsNullOrWhiteSpace(probedSteamUserId) &&
                !string.Equals(_steamSettings.SteamUserId, probedSteamUserId, StringComparison.Ordinal))
            {
                _steamSettings.SteamUserId = probedSteamUserId;
            }

            WebAuthenticated = hasWebAuth;
            FullyConfigured = hasWebAuth;
            SetAuthStatusVisualState(pending: false, success: hasWebAuth);

            if (hasWebAuth)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Authenticated");
            }
            else
            {
                var localized = ResourceProvider.GetString(result.MessageKey);
                WebAuthStatus = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, result.MessageKey, StringComparison.Ordinal)
                    ? ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated")
                    : localized;
            }
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                var result = await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, ct: CancellationToken.None);
                if (result.IsSuccess)
                {
                    await RefreshAuthStatusAsync();
                    PlayniteAchievementsPlugin.NotifySettingsSaved();
                }
                else
                {
                    UpdateAuthStatusFromResult(result);
                }
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

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                _sessionManager.ClearSession();
                await RefreshAuthStatusAsync();
                PlayniteAchievementsPlugin.NotifySettingsSaved();
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

