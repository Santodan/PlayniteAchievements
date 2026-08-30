using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Settings view for the Riot provider. Credential completeness is reflected live as the user
    /// types; the Check button additionally resolves the Riot ID to a PUUID, which both validates
    /// the key against Riot and saves a call on every later refresh.
    /// </summary>
    public partial class RiotSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(RiotSettingsView));

        private readonly Func<RiotSettings, CancellationToken, Task<string>> _resolveAccountAsync;
        private RiotSettings _riotSettings;

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(RiotSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new RiotSettings Settings => _riotSettings;

        public RiotSettingsView()
            : this(null)
        {
        }

        /// <param name="resolveAccountAsync">
        /// Resolves the configured Riot ID to a PUUID, returning the account's display name.
        /// Injected so the view has no direct dependency on the API client.
        /// </param>
        public RiotSettingsView(Func<RiotSettings, CancellationToken, Task<string>> resolveAccountAsync)
        {
            _resolveAccountAsync = resolveAccountAsync;
            InitializeComponent();

            RegionCombo.ItemsSource = RiotRegions.Choices;
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Riot"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _riotSettings = settings as RiotSettings;
            base.Initialize(settings);

            if (_riotSettings is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged -= Settings_PropertyChanged;
                notify.PropertyChanged += Settings_PropertyChanged;
            }

            RefreshAuthStatus();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                RefreshAuthStatus();
                return;
            }

            // The cached PUUID belongs to one Riot ID on one region; any change to either makes it
            // stale, so drop it and let the next check or refresh resolve a fresh one.
            if (string.Equals(e.PropertyName, nameof(RiotSettings.RiotGameName), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(RiotSettings.RiotTagLine), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(RiotSettings.PlatformRegion), StringComparison.Ordinal))
            {
                if (_riotSettings != null && !string.IsNullOrWhiteSpace(_riotSettings.Puuid))
                {
                    _riotSettings.Puuid = null;
                }
            }

            if (string.Equals(e.PropertyName, nameof(RiotSettings.RiotGameName), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(RiotSettings.RiotTagLine), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(RiotSettings.PlatformRegion), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(RiotSettings.RiotApiKey), StringComparison.Ordinal))
            {
                RefreshAuthStatus();
            }
        }

        /// <summary>
        /// Reflects what the settings alone can tell us: whether every field is filled in, and
        /// whether a Check has already confirmed the account against Riot.
        /// </summary>
        private void RefreshAuthStatus()
        {
            var hasCredentials = _riotSettings?.HasCredentials == true;
            var verified = hasCredentials && !string.IsNullOrWhiteSpace(_riotSettings.Puuid);

            if (verified)
            {
                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs"),
                    _riotSettings.RiotId);
            }
            else if (hasCredentials)
            {
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
            }
            else
            {
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
            }

            SetAuthStatusVisualState(pending: !verified, success: verified);
        }

        public Task RefreshAuthStatusAsync()
        {
            RefreshAuthStatus();
            return Task.CompletedTask;
        }

        private async void Check_Click(object sender, RoutedEventArgs e)
        {
            if (_riotSettings == null || _resolveAccountAsync == null)
            {
                RefreshAuthStatus();
                return;
            }

            if (!_riotSettings.HasCredentials)
            {
                RefreshAuthStatus();
                return;
            }

            CheckButton.IsEnabled = false;
            SetAuthStatusChecking();
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Checking");

            try
            {
                var displayName = await _resolveAccountAsync(_riotSettings, CancellationToken.None).ConfigureAwait(true);

                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs"),
                    string.IsNullOrWhiteSpace(displayName) ? _riotSettings.RiotId : displayName);
                SetAuthStatusVisualState(pending: false, success: true);
            }
            catch (RiotAccountNotFoundException)
            {
                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Riot_AccountNotFound"),
                    _riotSettings.RiotId);
                SetAuthStatusVisualState(pending: true, success: false);
            }
            catch (RiotAuthorizationException ex)
            {
                Logger.Warn(ex, "Riot rejected the API key during a settings check.");
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
                SetAuthStatusVisualState(pending: true, success: false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Riot account check failed.");
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_TemporaryFailure");
                SetAuthStatusVisualState(pending: true, success: false);
            }
            finally
            {
                CheckButton.IsEnabled = true;
            }
        }
    }
}
