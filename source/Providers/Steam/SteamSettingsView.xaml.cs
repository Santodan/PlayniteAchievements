using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        private readonly IPlayniteAPI _api;
        private readonly SteamSessionManager _sessionManager;
        private readonly SteamOwnedGamesImporter _ownedGamesImporter;
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

        public static readonly DependencyProperty ApiAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(ApiAuthenticated),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool ApiAuthenticated
        {
            get => (bool)GetValue(ApiAuthenticatedProperty);
            set => SetValue(ApiAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AnyAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(AnyAuthenticated),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool AnyAuthenticated
        {
            get => (bool)GetValue(AnyAuthenticatedProperty);
            set => SetValue(AnyAuthenticatedProperty, value);
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

        public SteamSettingsView(SteamSessionManager sessionManager, IPlayniteAPI api)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _ownedGamesImporter = new SteamOwnedGamesImporter(_api, Logger, _sessionManager);
            InitializeComponent();
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _steamSettings = settings as SteamSettings;
            base.Initialize(settings);

            if (_steamSettings is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged -= SteamSettings_PropertyChanged;
                notify.PropertyChanged += SteamSettings_PropertyChanged;
            }

            _ = RefreshAuthStatusAsync();
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                var apiResult = await _sessionManager.ProbeApiKeyAuthStateAsync(CancellationToken.None);
                var webResult = await _sessionManager.ProbeWebAuthStateAsync(CancellationToken.None);
                UpdateAuthStatusFromResult(apiResult, webResult);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Steam auth probe failed during settings refresh.");
                UpdateAuthStatusFromResult(AuthProbeResult.ProbeFailed(), AuthProbeResult.ProbeFailed());
            }
        }

        private void UpdateAuthStatusFromResult(AuthProbeResult apiResult, AuthProbeResult webResult)
        {
            var hasWebAuth = webResult?.IsSuccess == true;
            var hasApiAuth = apiResult?.IsSuccess == true;
            var hasApiKey = !string.IsNullOrWhiteSpace(_steamSettings?.SteamApiKey);
            var apiSteamUserId = !string.IsNullOrWhiteSpace(apiResult?.UserId)
                ? apiResult.UserId.Trim()
                : null;
            var webSteamUserId = hasWebAuth && !string.IsNullOrWhiteSpace(webResult?.UserId)
                ? webResult.UserId.Trim()
                : null;
            var probedSteamUserId = webSteamUserId ?? apiSteamUserId;

            if (_steamSettings != null && !string.Equals(_steamSettings.SteamUserId, probedSteamUserId, StringComparison.Ordinal))
            {
                _steamSettings.SteamUserId = probedSteamUserId;
            }

            WebAuthenticated = hasWebAuth;
            ApiAuthenticated = hasApiKey && hasApiAuth;
            AnyAuthenticated = hasApiAuth || hasWebAuth;
            FullyConfigured = hasApiKey && hasApiAuth;

            if (hasApiKey && hasApiAuth)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiAuthenticated");
            }
            else if (hasWebAuth)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_WebAuthOnly");
            }
            else if (hasApiKey && string.IsNullOrWhiteSpace(probedSteamUserId))
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiNeedsUserId");
            }
            else
            {
                var messageKey = apiResult?.MessageKey ?? webResult?.MessageKey;
                var localized = ResourceProvider.GetString(messageKey);
                WebAuthStatus = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, messageKey, StringComparison.Ordinal)
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
                    await ImportOwnedGamesAsync(showDialog: true, ct: CancellationToken.None);
                    PlayniteAchievementsPlugin.NotifySettingsSaved();
                }
                else
                {
                    UpdateAuthStatusFromResult(result, AuthProbeResult.NotAuthenticated());
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

        private async void ImportOwnedGames_Click(object sender, RoutedEventArgs e)
        {
            await ImportOwnedGamesAsync(showDialog: true, ct: CancellationToken.None);
        }

        private void SteamSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(SteamSettings.SteamApiKey))
            {
                UpdateConfiguredState();
            }
        }

        private void UpdateConfiguredState()
        {
            var hasApiKey = !string.IsNullOrWhiteSpace(_steamSettings?.SteamApiKey);
            var apiAuthenticated = hasApiKey && ApiAuthenticated;
            ApiAuthenticated = apiAuthenticated;
            FullyConfigured = hasApiKey && apiAuthenticated;

            if (hasApiKey && apiAuthenticated)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiAuthenticated");
            }
            else if (WebAuthenticated)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_WebAuthOnly");
            }
            else if (hasApiKey && string.IsNullOrWhiteSpace(_steamSettings?.SteamUserId))
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiNeedsUserId");
            }
            else
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
            }
        }

        private async void SteamApiKey_LostFocus(object sender, RoutedEventArgs e)
        {
            await RefreshAuthStatusAsync();
        }

        private async void SteamApiKey_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await RefreshAuthStatusAsync();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private async Task ImportOwnedGamesAsync(bool showDialog, CancellationToken ct)
        {
            try
            {
                SetAuthBusy(true);
                var result = await _ownedGamesImporter.ImportOwnedGamesAsync(ct).ConfigureAwait(true);
                if (showDialog)
                {
                    ShowOwnedGamesImportSummary(result);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam owned-games import failed");
                if (showDialog)
                {
                    _api.Dialogs.ShowMessage(
                        string.Format(
                            ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesFailed"),
                            ex.Message),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void ShowOwnedGamesImportSummary(SteamOwnedGamesImporter.ImportResult result)
        {
            string message;
            MessageBoxImage image;

            if (result == null || !result.IsAuthenticated)
            {
                message = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesNotAuthenticated");
                image = MessageBoxImage.Warning;
            }
            else if (!result.HasSteamLibraryPlugin)
            {
                message = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesMissingLibraryPlugin");
                image = MessageBoxImage.Warning;
            }
            else if (result.OwnedCount <= 0)
            {
                message = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesNoneFound");
                image = MessageBoxImage.Information;
            }
            else if (result.ImportedCount <= 0)
            {
                message = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesAlreadyPresent"),
                    result.OwnedCount);
                image = MessageBoxImage.Information;
            }
            else
            {
                message = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesSummary"),
                    result.ImportedCount,
                    result.ExistingCount,
                    result.FailedCount);
                image = MessageBoxImage.Information;
            }

            _api.Dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
        }

        private static void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
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

