using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Navigation;
using Playnite.SDK;
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
        }

        public override void Initialize(IProviderSettings settings)
        {
            _psnSettings = settings as PsnSettings;
            base.Initialize(settings);
            RefreshAuthStatus();
            _ = RefreshAuthStatusAsync();
        }

        public void RefreshAuthStatus()
        {
            var isAuthenticated = _sessionManager?.IsAuthenticated ?? false;
            IsAuthenticated = isAuthenticated;
            var providerName = ResourceProvider.GetString("LOCPlayAch_Provider_PSN");
            AuthStatus = isAuthenticated
                ? string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_AlreadyAuthenticated"), providerName)
                : string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotAuthenticated"), providerName);
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                var result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None).ConfigureAwait(false);
                SetAuthenticated(result.IsSuccess);

                if (!string.IsNullOrWhiteSpace(result.MessageKey))
                {
                    SetAuthStatusByKey(result.MessageKey);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "PSN auth probe failed during settings refresh.");
            }

            RefreshAuthStatus();
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
                var result = await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, CancellationToken.None).ConfigureAwait(false);
                await CheckPsnAuthAsync().ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(result?.MessageKey))
                {
                    SetAuthStatusByKey(result.MessageKey);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PSN login failed");
                SetAuthStatusByKey("LOCPlayAch_Settings_Auth_Failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void PsnAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                _sessionManager.ClearSession();
                RefreshAuthStatus();
                SetAuthStatusByKey("LOCPlayAch_Settings_Auth_CookiesCleared");
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
                var result = await _sessionManager.ProbeAuthenticationAsync(CancellationToken.None).ConfigureAwait(false);
                var isAuthenticated = result?.IsSuccess ?? false;

                SetAuthenticated(isAuthenticated);

                if (!string.IsNullOrWhiteSpace(result?.MessageKey))
                {
                    SetAuthStatusByKey(result.MessageKey);
                }
                else
                {
                    RefreshAuthStatus();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PSN auth check failed");
                SetAuthenticated(false);
                SetAuthStatusByKey("LOCPlayAch_Settings_Auth_ProbeFailed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void SetAuthStatusByKey(string key)
        {
            var localized = ResourceProvider.GetString(key);
            if (!string.IsNullOrWhiteSpace(localized))
            {
                if (localized.Contains("{0}"))
                {
                    localized = string.Format(localized, ResourceProvider.GetString("LOCPlayAch_Provider_PSN"));
                }

                if (Dispatcher.CheckAccess())
                {
                    AuthStatus = localized;
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() => AuthStatus = localized));
                }
                return;
            }

            RefreshAuthStatus();
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
