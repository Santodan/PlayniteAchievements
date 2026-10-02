using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Settings view for the Guild Wars 2 provider. Key completeness is reflected live as the user
    /// types; the Check button additionally confirms the key against the API and resolves the
    /// account name, which is what turns the card from "waiting for check" into a named account.
    /// </summary>
    public partial class Gw2SettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(Gw2SettingsView));

        private readonly Func<Gw2Settings, CancellationToken, Task<string>> _validateKeyAsync;
        private Gw2Settings _gw2Settings;

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(Gw2SettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new Gw2Settings Settings => _gw2Settings;

        public Gw2SettingsView()
            : this(null)
        {
        }

        /// <param name="validateKeyAsync">
        /// Confirms the configured key against the API and returns the account's display name.
        /// Injected so the view has no direct dependency on the API client.
        /// </param>
        public Gw2SettingsView(Func<Gw2Settings, CancellationToken, Task<string>> validateKeyAsync)
        {
            _validateKeyAsync = validateKeyAsync;
            InitializeComponent();

            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_GW2"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _gw2Settings = settings as Gw2Settings;
            base.Initialize(settings);

            if (_gw2Settings is INotifyPropertyChanged notify)
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

            if (!string.Equals(e.PropertyName, nameof(Gw2Settings.ApiKey), StringComparison.Ordinal))
            {
                return;
            }

            // The cached account belongs to one key; a new key makes it stale, so drop it and let
            // the next check resolve a fresh one.
            if (_gw2Settings != null && !string.IsNullOrWhiteSpace(_gw2Settings.AccountId))
            {
                _gw2Settings.AccountId = null;
                _gw2Settings.AccountName = null;
            }

            RefreshAuthStatus();
        }

        /// <summary>
        /// Reflects what the settings alone can tell us: whether a key is present, and whether a
        /// Check has already confirmed it against the API.
        /// </summary>
        private void RefreshAuthStatus()
        {
            var hasCredentials = _gw2Settings?.HasCredentials == true;
            var verified = _gw2Settings?.IsVerified == true;

            if (verified)
            {
                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs"),
                    _gw2Settings.AccountName);
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
            if (_gw2Settings == null || _validateKeyAsync == null || !_gw2Settings.HasCredentials)
            {
                RefreshAuthStatus();
                return;
            }

            CheckButton.IsEnabled = false;
            SetAuthStatusChecking();
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Checking");

            try
            {
                var accountName = await _validateKeyAsync(_gw2Settings, CancellationToken.None).ConfigureAwait(true);

                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs"),
                    accountName);
                SetAuthStatusVisualState(pending: false, success: true);
            }
            catch (Gw2MissingPermissionException ex)
            {
                // A key can be entirely valid and still be useless here, so this reads as its own
                // state rather than as a rejected key.
                Logger.Warn(ex, "The Guild Wars 2 API key is missing the progression scope.");
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_GW2_MissingProgression");
                SetAuthStatusVisualState(pending: true, success: false);
            }
            catch (Gw2AuthorizationException ex)
            {
                Logger.Warn(ex, "The Guild Wars 2 API rejected the key during a settings check.");
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
                SetAuthStatusVisualState(pending: true, success: false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "The Guild Wars 2 key check failed.");
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
