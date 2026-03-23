using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.Epic
{
    /// <summary>
    /// Settings view for the Epic Games provider.
    /// </summary>
    public partial class EpicSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(EpicSettingsView));
        private readonly EpicSessionManager _sessionManager;
        private EpicSettings _epicSettings;

        #region DependencyProperties

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(EpicSettingsView), new PropertyMetadata(false));

        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(EpicSettingsView), new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(EpicSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        #endregion

        public new EpicSettings Settings => _epicSettings;

        public EpicSettingsView(EpicSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _epicSettings = settings as EpicSettings;
            base.Initialize(settings);
            RefreshAuthStatus();
            _ = RefreshAuthStatusAsync();
        }

        public void RefreshAuthStatus()
        {
            var isAuthenticated = _sessionManager?.IsAuthenticated ?? false;
            IsAuthenticated = isAuthenticated;
            var providerName = ResourceProvider.GetString("LOCPlayAch_Provider_Epic");

            AuthStatus = isAuthenticated
                ? string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_AlreadyAuthenticated"), providerName)
                : string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotAuthenticated"), providerName);
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Epic auth probe failed during settings refresh.");
            }

            RefreshAuthStatus();
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, CancellationToken.None);
                RefreshAuthStatus();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Epic web login failed");
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
                RefreshAuthStatus();
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Epic logout failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void LoginAlternative_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await _sessionManager.LoginAlternativeAsync(CancellationToken.None);
                await RefreshAuthStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Epic alternative login failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) AuthBusy = busy;
            else Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
        }
    }
}
