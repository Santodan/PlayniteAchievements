using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// Settings view for the Hypixel provider. The username is entered manually and verified on
    /// demand against the player's hypixel.net profile page.
    /// </summary>
    public partial class HypixelSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(HypixelSettingsView));

        private HypixelSettings _hypixelSettings;

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(HypixelSettingsView), new PropertyMetadata(false));

        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(
                nameof(AuthStatus),
                typeof(string),
                typeof(HypixelSettingsView),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked")));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new HypixelSettings Settings => _hypixelSettings;

        public HypixelSettingsView()
        {
            InitializeComponent();
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Hypixel"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _hypixelSettings = settings as HypixelSettings;
            base.Initialize(settings);

            if (_hypixelSettings is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged -= Settings_PropertyChanged;
                notify.PropertyChanged += Settings_PropertyChanged;
            }

            SetNotChecked();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Editing the username invalidates any previous check.
            if (e == null || string.Equals(e.PropertyName, nameof(HypixelSettings.Username), StringComparison.Ordinal))
            {
                SetNotChecked();
            }
        }

        private void SetNotChecked()
        {
            SetAuthStatusVisualState(pending: true, success: false);
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
        }

        /// <summary>
        /// Verifies that the configured player has a Hypixel profile, updating the status display.
        /// Also invoked by the global auth-check flow.
        /// </summary>
        public async Task RefreshAuthStatusAsync()
        {
            var username = _hypixelSettings?.Username?.Trim();
            if (string.IsNullOrEmpty(username))
            {
                SetAuthStatusVisualState(pending: false, success: false);
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
                return;
            }

            SetAuthStatusChecking();
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Checking");

            try
            {
                using (var client = new HypixelApiClient(Logger))
                {
                    await client.FetchProfileAsync(username, CancellationToken.None).ConfigureAwait(true);

                    SetAuthStatusVisualState(pending: false, success: true);
                    AuthStatus = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs"),
                        username);
                }
            }
            catch (HypixelPlayerNotFoundException)
            {
                SetAuthStatusVisualState(pending: false, success: false);
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Hypixel_PlayerNotFound");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to check the Hypixel player.");
                SetAuthStatusVisualState(pending: false, success: false);
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Hypixel_Unavailable");
            }
        }

        private async void CheckPlayer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                SetAuthStatusVisualState(pending: true, success: false);
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Checking");
                await RefreshAuthStatusAsync();
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
