using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Settings view for the Final Fantasy XIV provider. The character profile URL is
    /// entered manually and verified on demand against FFXIV Collect, following the
    /// "needs to be checked" pattern used by other providers.
    /// </summary>
    public partial class FfxivSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(FfxivSettingsView));

        private FfxivSettings _ffxivSettings;

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(FfxivSettingsView), new PropertyMetadata(false));

        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(
                nameof(AuthStatus),
                typeof(string),
                typeof(FfxivSettingsView),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked")));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new FfxivSettings Settings => _ffxivSettings;

        public FfxivSettingsView()
        {
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_FFXIV"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _ffxivSettings = settings as FfxivSettings;
            base.Initialize(settings);

            if (_ffxivSettings is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged -= Settings_PropertyChanged;
                notify.PropertyChanged += Settings_PropertyChanged;
            }

            SetNotChecked();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Editing the profile invalidates any previous check and replaces the id
            // carried over from a name and world search.
            if (e == null ||
                string.Equals(e.PropertyName, nameof(FfxivSettings.CharacterProfile), StringComparison.Ordinal))
            {
                if (e != null && _ffxivSettings != null)
                {
                    _ffxivSettings.ResolvedCharacterId = 0;
                }

                SetNotChecked();
            }
        }

        private void SetNotChecked()
        {
            SetAuthStatusVisualState(pending: true, success: false);
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
        }

        /// <summary>
        /// Verifies the configured character against FFXIV Collect, updating the
        /// status display. Also invoked by the global auth-check flow.
        /// </summary>
        public async Task RefreshAuthStatusAsync()
        {
            var characterId = _ffxivSettings?.GetCharacterId();
            if (!characterId.HasValue)
            {
                SetAuthStatusVisualState(pending: false, success: false);
                AuthStatus = string.IsNullOrWhiteSpace(_ffxivSettings?.CharacterProfile)
                    ? ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated")
                    : ResourceProvider.GetString("LOCPlayAch_Settings_FFXIV_InvalidProfile");
                return;
            }

            SetAuthStatusChecking();
            AuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_FFXIV_Verifying");

            try
            {
                using (var client = new FfxivApiClient(Logger))
                {
                    var character = await client.FetchCharacterAsync(characterId.Value, CancellationToken.None).ConfigureAwait(true);

                    if (character?.Achievements?.Public == false)
                    {
                        SetAuthStatusVisualState(pending: false, success: false);
                        AuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_FFXIV_AchievementsPrivate");
                        return;
                    }

                    SetAuthStatusVisualState(pending: false, success: true);
                    var displayName = character?.Name ?? characterId.Value.ToString(CultureInfo.InvariantCulture);
                    AuthStatus = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Auth_AuthenticatedAs"),
                        string.IsNullOrWhiteSpace(character?.Server) ? displayName : $"{displayName} ({character.Server})");
                }
            }
            catch (FfxivCharacterNotIndexedException ex)
            {
                Logger.Warn($"FFXIV character {ex.LodestoneId} is not indexed on FFXIV Collect.");
                SetAuthStatusVisualState(pending: false, success: false);
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_FFXIV_NotOnCollect");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to check FFXIV character.");
                SetAuthStatusVisualState(pending: false, success: false);
                AuthStatus = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Status_Failed"),
                    ex.Message);
            }
        }

        private async void CheckCharacter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                SetAuthStatusVisualState(pending: true, success: false);
                AuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_FFXIV_Verifying");
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
