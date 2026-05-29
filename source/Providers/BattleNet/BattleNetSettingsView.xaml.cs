using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.BattleNet.Models;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.BattleNet
{
    public partial class BattleNetSettingsView : ProviderSettingsViewBase
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(BattleNetSettingsView));
        private readonly BattleNetApiClient _apiClient;
        private readonly ILogger _logger;
        private BattleNetSettings _battleNetSettings;

        public static readonly DependencyProperty WowConfiguredProperty =
            DependencyProperty.Register(nameof(WowConfigured), typeof(bool), typeof(BattleNetSettingsView), new PropertyMetadata(false));

        public bool WowConfigured
        {
            get => (bool)GetValue(WowConfiguredProperty);
            set => SetValue(WowConfiguredProperty, value);
        }

        public static readonly DependencyProperty Sc2ConfiguredProperty =
            DependencyProperty.Register(nameof(Sc2Configured), typeof(bool), typeof(BattleNetSettingsView), new PropertyMetadata(false));

        public bool Sc2Configured
        {
            get => (bool)GetValue(Sc2ConfiguredProperty);
            set => SetValue(Sc2ConfiguredProperty, value);
        }

        public static readonly DependencyProperty WowStatusProperty =
            DependencyProperty.Register(nameof(WowStatus), typeof(string), typeof(BattleNetSettingsView), new PropertyMetadata(string.Empty));

        public string WowStatus
        {
            get => (string)GetValue(WowStatusProperty);
            set => SetValue(WowStatusProperty, value);
        }

        public static readonly DependencyProperty Sc2StatusProperty =
            DependencyProperty.Register(nameof(Sc2Status), typeof(string), typeof(BattleNetSettingsView), new PropertyMetadata(string.Empty));

        public string Sc2Status
        {
            get => (string)GetValue(Sc2StatusProperty);
            set => SetValue(Sc2StatusProperty, value);
        }

        public new BattleNetSettings Settings => _battleNetSettings;

        public BattleNetSettingsView(BattleNetApiClient apiClient, ILogger logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger ?? Logger;
            InitializeComponent();
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
                _battleNetSettings.PropertyChanged += BattleNetSettings_PropertyChanged;
                ClientSecretBox.Password = _battleNetSettings.BattleNetClientSecret ?? string.Empty;
            }

            LoadWowRegions();
            UpdateWowStatus();
            UpdateSc2Status();
        }

        private void BattleNetSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(BattleNetSettings.BattleNetClientId):
                case nameof(BattleNetSettings.BattleNetClientSecret):
                case nameof(BattleNetSettings.Sc2RegionId):
                case nameof(BattleNetSettings.Sc2RealmId):
                case nameof(BattleNetSettings.Sc2ProfileId):
                    UpdateSc2Status();
                    break;
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

        private void UpdateSc2Status()
        {
            if (_battleNetSettings == null)
            {
                return;
            }

            Sc2Configured = BattleNetGameSupport.HasConfiguredSc2(_battleNetSettings);
            Sc2Status = ResourceProvider.GetString(Sc2Configured
                ? "LOCPlayAch_Settings_BattleNet_Status_Sc2Detected"
                : "LOCPlayAch_Settings_BattleNet_Status_Sc2Incomplete");
        }

        private void ClientSecret_Changed(object sender, RoutedEventArgs e)
        {
            if (_battleNetSettings == null)
            {
                return;
            }

            _battleNetSettings.BattleNetClientSecret = ClientSecretBox.Password;
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

        private static string Bool(bool value) => value ? "true" : "false";

        private static string MaskId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<empty>";
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= 4)
            {
                return "****";
            }

            return $"{new string('*', Math.Min(8, trimmed.Length - 4))}{trimmed.Substring(trimmed.Length - 4)}";
        }

        private static string Presence(string value) => string.IsNullOrWhiteSpace(value) ? "missing" : "set";

        private static string SettingsSummary(BattleNetSettings settings)
        {
            if (settings == null)
            {
                return "<null settings>";
            }

            return string.Format(
                "enabled={0}, apiClientId={1}, apiClientSecret={2}, sc2Region={3}, sc2Realm={4}, sc2Profile={5}, wowRegion={6}, wowRealmSlug={7}, wowCharacter={8}",
                Bool(settings.IsEnabled),
                Presence(settings.BattleNetClientId),
                Presence(settings.BattleNetClientSecret),
                settings.Sc2RegionId,
                settings.Sc2RealmId,
                settings.Sc2ProfileId > 0 ? MaskId(settings.Sc2ProfileId.ToString()) : "<none>",
                string.IsNullOrWhiteSpace(settings.WowRegion) ? "<none>" : settings.WowRegion,
                string.IsNullOrWhiteSpace(settings.WowRealmSlug) ? "<none>" : settings.WowRealmSlug,
                Presence(settings.WowCharacter));
        }
    }
}
