using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.GameJolt
{
    public partial class GameJoltSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(GameJoltSettingsView));
        private readonly GameJoltSessionManager _sessionManager;

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(GameJoltSettingsView), new PropertyMetadata(false));
        public bool AuthBusy { get => (bool)GetValue(AuthBusyProperty); set => SetValue(AuthBusyProperty, value); }

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(GameJoltSettingsView), new PropertyMetadata(false));
        public bool IsAuthenticated { get => (bool)GetValue(IsAuthenticatedProperty); set => SetValue(IsAuthenticatedProperty, value); }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(GameJoltSettingsView), new PropertyMetadata(string.Empty));
        public string AuthStatus { get => (string)GetValue(AuthStatusProperty); set => SetValue(AuthStatusProperty, value); }

        public GameJoltSettingsView(GameJoltSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_GameJolt"));
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_GameJolt"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            base.Initialize(settings);
            SetAuthStatusVisualState(pending: true, success: false);
            SetAuthStatusByKey("LOCPlayAch_Auth_NotChecked");
        }

        public async Task RefreshAuthStatusAsync()
        {
            AuthProbeResult result;
            try
            {
                SetAuthStatusChecking();
                SetAuthStatusByKey("LOCPlayAch_Auth_Checking");
                result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[GameJoltSettings] Auth probe failed during settings refresh");
                result = AuthProbeResult.ProbeFailed();
            }

            UpdateAuthStatus(result);
        }

        private void UpdateAuthStatus(AuthProbeResult result)
        {
            var isAuthenticated = result?.IsSuccess ?? false;
            SetAuthenticated(isAuthenticated);
            SetAuthStatusVisualState(pending: false, success: isAuthenticated);

            if (isAuthenticated)
            {
                SetAuthStatus(ResourceProvider.GetString("LOCPlayAch_Auth_Authenticated"));
                return;
            }

            var localized = !string.IsNullOrWhiteSpace(result?.MessageKey)
                ? ResourceProvider.GetString(result.MessageKey)
                : null;

            var finalStatus = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, result?.MessageKey, StringComparison.Ordinal)
                ? ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated")
                : localized;

            SetAuthStatus(finalStatus);
        }

        private async void Auth_Check_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await RefreshAuthStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GameJolt auth check failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                var result = await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, CancellationToken.None);
                if (result.IsSuccess)
                {
                    await RefreshAuthStatusAsync();
                    PlayniteAchievementsPlugin.NotifySettingsSaved();
                }
                else
                {
                    UpdateAuthStatus(result);
                }
            }
            catch (Exception ex) { Logger.Error(ex, "GameJolt login failed"); }
            finally { SetAuthBusy(false); }
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
            catch (Exception ex) { Logger.Error(ex, "GameJolt logout failed"); }
            finally { SetAuthBusy(false); }
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) AuthBusy = busy;
            else Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
        }

        private void SetAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess()) IsAuthenticated = authenticated;
            else Dispatcher.BeginInvoke(new Action(() => IsAuthenticated = authenticated));
        }

        private void SetAuthStatus(string status)
        {
            var normalized = status ?? string.Empty;
            if (Dispatcher.CheckAccess()) AuthStatus = normalized;
            else Dispatcher.BeginInvoke(new Action(() => AuthStatus = normalized));
        }

        private void SetAuthStatusByKey(string key)
        {
            var localized = ResourceProvider.GetString(key);
            if (string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal))
            {
                localized = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
            }

            SetAuthStatus(localized);
        }
    }
}
