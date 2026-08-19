using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.BattleNet.Models;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.BattleNet
{
    public partial class BattleNetSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(BattleNetSettingsView));
        private readonly BattleNetApiClient _apiClient;
        private readonly BattleNetSessionManager _sessionManager;
        private readonly DataForAzerothSessionManager _dataForAzerothSession;
        private readonly ILogger _logger;
        private BattleNetSettings _battleNetSettings;

        public static readonly DependencyProperty WowConfiguredProperty =
            DependencyProperty.Register(nameof(WowConfigured), typeof(bool), typeof(BattleNetSettingsView), new PropertyMetadata(false));

        public bool WowConfigured
        {
            get => (bool)GetValue(WowConfiguredProperty);
            set => SetValue(WowConfiguredProperty, value);
        }

        public static readonly DependencyProperty WowStatusProperty =
            DependencyProperty.Register(nameof(WowStatus), typeof(string), typeof(BattleNetSettingsView), new PropertyMetadata(string.Empty));

        public string WowStatus
        {
            get => (string)GetValue(WowStatusProperty);
            set => SetValue(WowStatusProperty, value);
        }

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(BattleNetSettingsView), new PropertyMetadata(false));

        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(BattleNetSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public static readonly DependencyProperty DataForAzerothCheckedProperty =
            DependencyProperty.Register(nameof(DataForAzerothChecked), typeof(bool), typeof(BattleNetSettingsView), new PropertyMetadata(false));

        /// <summary>Whether the Data for Azeroth site check is currently cleared.</summary>
        public bool DataForAzerothChecked
        {
            get => (bool)GetValue(DataForAzerothCheckedProperty);
            set => SetValue(DataForAzerothCheckedProperty, value);
        }

        public static readonly DependencyProperty DataForAzerothStatusProperty =
            DependencyProperty.Register(nameof(DataForAzerothStatus), typeof(string), typeof(BattleNetSettingsView), new PropertyMetadata(string.Empty));

        public string DataForAzerothStatus
        {
            get => (string)GetValue(DataForAzerothStatusProperty);
            set => SetValue(DataForAzerothStatusProperty, value);
        }

        public static readonly DependencyProperty DataForAzerothPendingProperty =
            DependencyProperty.Register(nameof(DataForAzerothPending), typeof(bool), typeof(BattleNetSettingsView), new PropertyMetadata(true));

        /// <summary>Nothing probed yet, so the card claims neither success nor failure.</summary>
        public bool DataForAzerothPending
        {
            get => (bool)GetValue(DataForAzerothPendingProperty);
            set => SetValue(DataForAzerothPendingProperty, value);
        }

        public static readonly DependencyProperty DataForAzerothCheckingProperty =
            DependencyProperty.Register(nameof(DataForAzerothChecking), typeof(bool), typeof(BattleNetSettingsView), new PropertyMetadata(false));

        public bool DataForAzerothChecking
        {
            get => (bool)GetValue(DataForAzerothCheckingProperty);
            set => SetValue(DataForAzerothCheckingProperty, value);
        }

        public new BattleNetSettings Settings => _battleNetSettings;

        public BattleNetSettingsView(
            BattleNetApiClient apiClient,
            BattleNetSessionManager sessionManager,
            DataForAzerothSessionManager dataForAzerothSession,
            ILogger logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            // Null when no browser is available; the panel then stays inert rather than lying.
            _dataForAzerothSession = dataForAzerothSession;
            _logger = logger ?? Logger;
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_BattleNet"));
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_BattleNet"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            if (_battleNetSettings != null)
            {
                _battleNetSettings.PropertyChanged -= BattleNetSettings_PropertyChanged;
            }

            _battleNetSettings = settings as BattleNetSettings;
            if (_battleNetSettings == null)
            {
                _logger.Warn($"[BattleNet/Settings] Initialized with incompatible settings object: {settings?.GetType().FullName ?? "<null>"}");
            }

            base.Initialize(settings);
            if (_battleNetSettings != null)
            {
                if (BattleNetSettings.IsLegacyDefaultRedirectUri(_battleNetSettings.BattleNetRedirectUri))
                {
                    _battleNetSettings.BattleNetRedirectUri = BattleNetSettings.DefaultRedirectUri;
                }

                _battleNetSettings.PropertyChanged += BattleNetSettings_PropertyChanged;
                WowClientSecretBox.Password = _battleNetSettings.BattleNetClientSecret ?? string.Empty;
            }

            LoadWowRegions();
            UpdateWowStatus();
            SetAuthStatusVisualState(pending: true, success: false);
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
            DataForAzerothChecked = false;
            DataForAzerothChecking = false;
            DataForAzerothPending = true;
            DataForAzerothStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
        }

        private void BattleNetSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(BattleNetSettings.WowRegion):
                case nameof(BattleNetSettings.WowRealmSlug):
                case nameof(BattleNetSettings.WowCharacter):
                    UpdateWowStatus();
                    break;
            }
        }

        private void UpdateWowStatus()
        {
            if (_battleNetSettings == null)
            {
                return;
            }

            WowConfigured = !string.IsNullOrWhiteSpace(_battleNetSettings.WowRegion) &&
                !string.IsNullOrWhiteSpace(_battleNetSettings.WowRealmSlug) &&
                !string.IsNullOrWhiteSpace(_battleNetSettings.WowCharacter);
            WowStatus = ResourceProvider.GetString(WowConfigured
                ? "LOCPlayAch_Settings_BattleNet_Status_WowReady"
                : "LOCPlayAch_Settings_BattleNet_Status_WowIncomplete");
        }

        private void WowClientSecret_Changed(object sender, RoutedEventArgs e)
        {
            if (_battleNetSettings == null)
            {
                return;
            }

            _battleNetSettings.BattleNetClientSecret = WowClientSecretBox.Password;
        }

        public async Task RefreshAuthStatusAsync()
        {
            PersistCurrentSettingsForAuth();

            SetAuthStatusChecking();
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Checking");

            AuthProbeResult result;
            try
            {
                result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Battle.net auth probe failed during settings refresh.");
                result = AuthProbeResult.ProbeFailed();
            }

            UpdateAuthStatus(result);

            // Probed here rather than on a timer or during a refresh: opening this page is the one
            // moment the user is present and able to act, and the site check lapses periodically.
            await RefreshDataForAzerothStatusAsync();
        }

        private async Task RefreshDataForAzerothStatusAsync()
        {
            if (_dataForAzerothSession == null || _battleNetSettings?.UseDataForAzerothForWowRarity != true)
            {
                DataForAzerothChecked = false;
                DataForAzerothChecking = false;
                DataForAzerothPending = true;
                DataForAzerothStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
                return;
            }

            DataForAzerothChecking = true;
            DataForAzerothStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Checking");

            AuthProbeResult result;
            try
            {
                result = await _dataForAzerothSession.ProbeAuthStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Data for Azeroth site check probe failed during settings refresh.");
                result = AuthProbeResult.ProbeFailed();
            }

            UpdateDataForAzerothStatus(result);
        }

        private void UpdateDataForAzerothStatus(AuthProbeResult result)
        {
            var cleared = result?.IsSuccess ?? false;
            DataForAzerothChecked = cleared;
            DataForAzerothChecking = false;
            DataForAzerothPending = false;

            if (cleared)
            {
                // Same "Authenticated as {0}" treatment the Blizzard card gets, falling back to the
                // plain label when the site session carries no name we can show.
                var authenticatedAsFormat = ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs");
                DataForAzerothStatus = string.IsNullOrWhiteSpace(result.UserId) ||
                    string.IsNullOrWhiteSpace(authenticatedAsFormat) ||
                    string.Equals(authenticatedAsFormat, "LOCPlayAch_Auth_AuthenticatedAs", StringComparison.Ordinal)
                    ? ResourceProvider.GetString("LOCPlayAch_Auth_Authenticated")
                    : string.Format(authenticatedAsFormat, result.UserId);
                return;
            }

            // "Could not reach a verdict" is not the same as "the site is asking for a check", and
            // telling the user to go tick a box when the request merely failed wastes their time.
            var indeterminate = result == null ||
                result.Outcome == AuthOutcome.ProbeFailed ||
                result.Outcome == AuthOutcome.Cancelled ||
                result.Outcome == AuthOutcome.TimedOut;

            DataForAzerothStatus = ResourceProvider.GetString(indeterminate
                ? "LOCPlayAch_Auth_TemporaryFailure"
                : "LOCPlayAch_Settings_BattleNet_DataForAzerothSignInHint");
        }

        private async void DataForAzeroth_Login_Click(object sender, RoutedEventArgs e)
        {
            if (_dataForAzerothSession == null)
            {
                return;
            }

            try
            {
                SetAuthBusy(true);
                var result = await _dataForAzerothSession.AuthenticateInteractiveAsync(
                    forceInteractive: true,
                    CancellationToken.None);

                if (result.IsSuccess)
                {
                    await RefreshDataForAzerothStatusAsync();
                }
                else
                {
                    UpdateDataForAzerothStatus(result);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Data for Azeroth site check verification failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void DataForAzeroth_Check_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await RefreshDataForAzerothStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Data for Azeroth site check probe failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void DataForAzeroth_Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_dataForAzerothSession == null)
            {
                return;
            }

            try
            {
                SetAuthBusy(true);
                await _dataForAzerothSession.ClearSessionAsync(CancellationToken.None);
                await RefreshDataForAzerothStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Data for Azeroth site check reset failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void UpdateAuthStatus(AuthProbeResult result)
        {
            var isAuthenticated = result?.IsSuccess ?? false;
            SetAuthStatusVisualState(pending: false, success: isAuthenticated);

            if (isAuthenticated)
            {
                var settings = ProviderRegistry.Settings<BattleNetSettings>();
                var authenticatedText = ResourceProvider.GetString("LOCPlayAch_Auth_Authenticated");
                var authenticatedAsFormat = ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs");
                AuthStatus = string.IsNullOrWhiteSpace(settings.BattleNetBattleTag) ||
                    string.IsNullOrWhiteSpace(authenticatedAsFormat) ||
                    string.Equals(authenticatedAsFormat, "LOCPlayAch_Auth_AuthenticatedAs", StringComparison.Ordinal)
                    ? authenticatedText
                    : string.Format(authenticatedAsFormat, settings.BattleNetBattleTag);
                return;
            }

            var localized = !string.IsNullOrWhiteSpace(result?.MessageKey)
                ? ResourceProvider.GetString(result.MessageKey)
                : null;
            AuthStatus = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, result?.MessageKey, StringComparison.Ordinal)
                ? ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated")
                : localized;
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
                Logger.Error(ex, "Battle.net auth check failed");
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
                PersistCurrentSettingsForAuth();
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
                Logger.Error(ex, "Battle.net web login failed");
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
                Logger.Error(ex, "Battle.net logout failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void PersistCurrentSettingsForAuth()
        {
            if (_battleNetSettings != null)
            {
                ProviderRegistry.Write(_battleNetSettings, persistToDisk: true);
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

        private void LoadWowRegions()
        {
            WowRegionCombo.Items.Clear();
            WowRegionCombo.Items.Add("us");
            WowRegionCombo.Items.Add("eu");
            WowRegionCombo.Items.Add("kr");

            var settings = _battleNetSettings;
            if (!string.IsNullOrEmpty(settings?.WowRegion))
            {
                WowRegionCombo.SelectedItem = settings.WowRegion;
            }
            else
            {
                WowRegionCombo.SelectedIndex = 0;
            }
        }

        private async void WowRegion_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var region = WowRegionCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(region))
            {
                return;
            }

            var settings = _battleNetSettings;
            if (settings == null)
            {
                return;
            }

            var regionChanged = !string.Equals(settings.WowRegion, region, StringComparison.OrdinalIgnoreCase);
            settings.WowRegion = region;
            if (regionChanged)
            {
                settings.WowRealmSlug = null;
            }
            UpdateWowStatus();

            try
            {
                var realms = await _apiClient.GetWowRealmsAsync(region, CancellationToken.None);
                WowRealmCombo.Items.Clear();
                foreach (var realm in realms)
                {
                    WowRealmCombo.Items.Add(realm);
                }

                if (!string.IsNullOrEmpty(settings.WowRealmSlug))
                {
                    var selectedRealm = realms.Find(r => r.Slug == settings.WowRealmSlug);
                    WowRealmCombo.SelectedItem = selectedRealm;
                    if (selectedRealm == null)
                    {
                        settings.WowRealmSlug = null;
                        _logger.Warn($"[BattleNet/Settings] Saved WoW realm slug was not present in loaded realm list for region '{region}'. Cleared stale slug.");
                    }
                }
                UpdateWowStatus();
            }
            catch (Exception)
            {
                UpdateWowStatus();
            }
        }

        private void WowRealm_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_battleNetSettings == null)
            {
                return;
            }

            if (WowRealmCombo.SelectedItem is WowRealm realm)
            {
                _battleNetSettings.WowRealmSlug = realm.Slug;
                UpdateWowStatus();
            }
        }
    }
}
