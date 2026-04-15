using System;
using System.Windows.Controls;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Models;
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

        public new ExophaseSettings Settings => _exophaseSettings;

        public ExophaseSettingsView(ExophaseSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Exophase"));
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Exophase"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _exophaseSettings = settings as ExophaseSettings;
            base.Initialize(settings);
            SetAuthStatusByKey("LOCPlayAch_Auth_Checking");
            _ = RefreshAuthStatusAsync();
        }

        private void UpdateAuthStatus(AuthProbeResult result)
        {
            var isAuthenticated = result?.IsSuccess ?? false;
            Logger.Info($"[ExophaseSettings] UpdateAuthStatus: IsAuthenticated={isAuthenticated}, " +
                $"Outcome={result?.Outcome}, MessageKey='{result?.MessageKey ?? "null"}'");

            SetAuthenticated(isAuthenticated);

            if (isAuthenticated)
            {
                var status = ResourceProvider.GetString("LOCPlayAch_Auth_Authenticated");
                Logger.Info($"[ExophaseSettings] Setting authenticated status: '{status}'");
                SetAuthStatus(status);
                return;
            }

            var localized = !string.IsNullOrWhiteSpace(result?.MessageKey)
                ? ResourceProvider.GetString(result.MessageKey)
                : null;

            Logger.Debug($"[ExophaseSettings] MessageKey localization: key='{result?.MessageKey}', localized='{localized ?? "null"}'");

            var finalStatus = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, result?.MessageKey, StringComparison.Ordinal)
                ? ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated")
                : localized;

            Logger.Info($"[ExophaseSettings] Setting not-authenticated status: '{finalStatus}'");
            SetAuthStatus(finalStatus);
        }

        public async Task RefreshAuthStatusAsync()
        {
            Logger.Info("[ExophaseSettings] RefreshAuthStatusAsync START");
            AuthProbeResult result;
            try
            {
                SetAuthStatusByKey("LOCPlayAch_Auth_Checking");
                Logger.Debug("[ExophaseSettings] Calling ProbeAuthStateAsync...");
                result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
                Logger.Info($"[ExophaseSettings] ProbeAuthStateAsync result: IsSuccess={result?.IsSuccess}, " +
                    $"Outcome={result?.Outcome}, UserId='{result?.UserId ?? "null"}', MessageKey='{result?.MessageKey ?? "null"}'");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[ExophaseSettings] Auth probe failed during settings refresh");
                result = AuthProbeResult.ProbeFailed();
            }

            UpdateAuthStatus(result);
            Logger.Info("[ExophaseSettings] RefreshAuthStatusAsync COMPLETE");
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
            catch (Exception ex) { Logger.Error(ex, "Exophase login failed"); }
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
            catch (Exception ex) { Logger.Error(ex, "Exophase logout failed"); }
            finally { SetAuthBusy(false); }
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) AuthBusy = busy;
            else Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
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

        private void SetAuthStatus(string status)
        {
            var normalized = status ?? string.Empty;

            if (Dispatcher.CheckAccess())
            {
                AuthStatus = normalized;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthStatus = normalized));
            }
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

        private void ExophasePlatform_CheckboxLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox checkbox) || _exophaseSettings?.ManagedProviders == null)
            {
                return;
            }

            var token = checkbox.Tag as string;
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            checkbox.IsChecked = _exophaseSettings.ManagedProviders.Contains(token);
        }

        private void ExophasePlatform_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox checkbox) || _exophaseSettings == null)
            {
                return;
            }

            var token = checkbox.Tag as string;
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            if (_exophaseSettings.ManagedProviders == null)
            {
                _exophaseSettings.ManagedProviders = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (checkbox.IsChecked == true)
            {
                _exophaseSettings.ManagedProviders.Add(token);
            }
            else
            {
                _exophaseSettings.ManagedProviders.Remove(token);
            }
        }
    }
}

