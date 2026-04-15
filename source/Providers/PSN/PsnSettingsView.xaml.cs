using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Navigation;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.PSN
{
    public partial class PsnSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(PsnSettingsView));
        private readonly PsnSessionManager _sessionManager;
        private PsnSettings _psnSettings;

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(PsnSettingsView), new PropertyMetadata(false));
        public bool AuthBusy { get => (bool)GetValue(AuthBusyProperty); set => SetValue(AuthBusyProperty, value); }

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(PsnSettingsView), new PropertyMetadata(false));
        public bool IsAuthenticated { get => (bool)GetValue(IsAuthenticatedProperty); set => SetValue(IsAuthenticatedProperty, value); }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(PsnSettingsView), new PropertyMetadata(string.Empty));
        public string AuthStatus { get => (string)GetValue(AuthStatusProperty); set => SetValue(AuthStatusProperty, value); }

        public new PsnSettings Settings => _psnSettings;

        public PsnSettingsView(PsnSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_PSN"));
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_PSN"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _psnSettings = settings as PsnSettings;
            base.Initialize(settings);
            _ = RefreshAuthStatusAsync();
        }

        private void UpdateAuthStatus(AuthProbeResult result)
        {
            var isAuthenticated = result?.IsSuccess ?? false;
            SetAuthenticated(isAuthenticated);

            if (isAuthenticated)
            {
                SetAuthStatusByKey("LOCPlayAch_Auth_Authenticated");
                return;
            }

            if (!string.IsNullOrWhiteSpace(result?.MessageKey))
            {
                SetAuthStatusByKey(result.MessageKey);
                return;
            }

            SetAuthStatusByKey("LOCPlayAch_Common_NotAuthenticated");
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                SyncCanonicalAuthInputs();
                var result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
                UpdateAuthStatus(result);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "PSN auth probe failed during settings refresh.");
                UpdateAuthStatus(AuthProbeResult.ProbeFailed());
            }
        }

        private async void PsnNpsso_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await CheckPsnAuthAsync().ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(new Action(() => MoveFocusFrom((TextBox)sender)));
            }
        }

        private async void PsnNpsso_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            await CheckPsnAuthAsync().ConfigureAwait(false);
        }

        private async void PsnAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            await CheckPsnAuthAsync().ConfigureAwait(false);
        }

        private async void PsnAuth_Login_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                SyncCanonicalAuthInputs();
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
                Logger.Error(ex, "PSN login failed");
                SetAuthStatusByKey("LOCPlayAch_Common_NotAuthenticated");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void PsnAuth_Clear_Click(object sender, RoutedEventArgs e)
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
                Logger.Error(ex, "PSN logout failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async Task CheckPsnAuthAsync()
        {
            SetAuthBusy(true);

            try
            {
                await RefreshAuthStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PSN auth check failed");
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_Common_NotAuthenticated");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void SyncCanonicalAuthInputs()
        {
            if (_psnSettings == null)
            {
                return;
            }

            var liveSettings = ProviderRegistry.Settings<PsnSettings>();
            if (liveSettings == null)
            {
                return;
            }

            var npsso = _psnSettings.Npsso ?? string.Empty;
            if (string.Equals(liveSettings.Npsso ?? string.Empty, npsso, StringComparison.Ordinal))
            {
                return;
            }

            liveSettings.Npsso = npsso;
            ProviderRegistry.Write(liveSettings, persistToDisk: true);
        }

        private void SetAuthStatusByKey(string key)
        {
            var localized = ResourceProvider.GetString(key);
            if (string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal))
            {
                localized = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
            }

            if (Dispatcher.CheckAccess())
            {
                AuthStatus = localized;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthStatus = localized));
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

        private void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) AuthBusy = busy;
            else Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
        }
    }
}

