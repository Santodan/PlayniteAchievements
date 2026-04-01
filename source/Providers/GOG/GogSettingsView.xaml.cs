using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.GOG
{
    /// <summary>
    /// Settings view for the GOG provider.
    /// </summary>
    public partial class GogSettingsView : ProviderSettingsViewBase, IAuthRefreshable
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

        public new GogSettings Settings => _gogSettings;

        public GogSettingsView(GogSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_GOG"));
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Label_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_GOG"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _gogSettings = settings as GogSettings;
            base.Initialize(settings);
            _ = RefreshAuthStatusAsync();
        }

        private void UpdateAuthStatus(AuthProbeResult result)
        {
            var isAuthenticated = result?.IsSuccess ?? false;
            IsAuthenticated = isAuthenticated;

            if (isAuthenticated)
            {
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Authenticated");
            }
            else
            {
                var localized = !string.IsNullOrWhiteSpace(result?.MessageKey)
                    ? ResourceProvider.GetString(result.MessageKey)
                    : null;

                AuthStatus = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, result?.MessageKey, StringComparison.Ordinal)
                    ? ResourceProvider.GetString("LOCPlayAch_Auth_NotAuthenticated")
                    : localized;
            }
        }

        public async Task RefreshAuthStatusAsync()
        {
            AuthProbeResult result;
            try
            {
                result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "GOG auth probe failed during settings refresh.");
                result = AuthProbeResult.ProbeFailed();
            }

            UpdateAuthStatus(result);
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
                _sessionManager.ClearSession();
                await RefreshAuthStatusAsync();
                PlayniteAchievementsPlugin.NotifySettingsSaved();
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
