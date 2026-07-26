using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace PlayniteAchievements.Providers.Hoyoverse
{
    public partial class HoyoverseSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private readonly IPlayniteAPI _playniteApi;
        private HoyoverseSettings _hoyoverseSettings;

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(HoyoverseSettingsView), new PropertyMetadata(true));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(HoyoverseSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new HoyoverseSettings Settings => _hoyoverseSettings;

        public HoyoverseSettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Hoyoverse"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            if (_hoyoverseSettings != null)
            {
                _hoyoverseSettings.PropertyChanged -= HoyoverseSettings_PropertyChanged;
            }

            _hoyoverseSettings = settings as HoyoverseSettings;
            if (_hoyoverseSettings != null)
            {
                _hoyoverseSettings.PropertyChanged += HoyoverseSettings_PropertyChanged;
            }

            base.Initialize(settings);
            CheckConfiguration();
        }

        private void HoyoverseSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e?.PropertyName) ||
                e.PropertyName == nameof(HoyoverseSettings.IsEnabled) ||
                e.PropertyName == nameof(HoyoverseSettings.EnableGenshinImpact) ||
                e.PropertyName == nameof(HoyoverseSettings.EnableHonkaiStarRail) ||
                e.PropertyName == nameof(HoyoverseSettings.EnableZenlessZoneZero) ||
                e.PropertyName == nameof(HoyoverseSettings.GenshinExportPath) ||
                e.PropertyName == nameof(HoyoverseSettings.HonkaiStarRailExportPath) ||
                e.PropertyName == nameof(HoyoverseSettings.ZenlessZoneZeroExportPath))
            {
                CheckConfiguration();
            }
        }

        public Task RefreshAuthStatusAsync()
        {
            CheckConfiguration();
            return Task.CompletedTask;
        }

        private void ExportPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CheckConfiguration();
            MoveFocusFrom(sender as TextBox);
        }

        private void ExportPath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CheckConfiguration();
        }

        private void BrowseExport_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFile("Achievement exports (*.json;*.dat)|*.json;*.dat|JSON files (*.json)|*.json|StarRailStation files (*.dat)|*.dat");
            if (string.IsNullOrWhiteSpace(selectedPath) || _hoyoverseSettings == null)
            {
                return;
            }

            switch ((sender as Button)?.Tag as string)
            {
                case "Genshin":
                    _hoyoverseSettings.GenshinExportPath = selectedPath;
                    break;
                case "Hsr":
                    _hoyoverseSettings.HonkaiStarRailExportPath = selectedPath;
                    break;
                case "Zzz":
                    _hoyoverseSettings.ZenlessZoneZeroExportPath = selectedPath;
                    break;
            }

            CheckConfiguration();
        }

        private void CheckConfiguration()
        {
            if (_hoyoverseSettings == null)
            {
                SetAuthenticated(true);
                SetAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Hoyoverse_NoExportConfigured"));
                return;
            }

            var missing = false;
            missing |= IsConfiguredMissing(_hoyoverseSettings.EnableGenshinImpact, _hoyoverseSettings.GenshinExportPath);
            missing |= IsConfiguredMissing(_hoyoverseSettings.EnableHonkaiStarRail, _hoyoverseSettings.HonkaiStarRailExportPath);
            missing |= IsConfiguredMissing(_hoyoverseSettings.EnableZenlessZoneZero, _hoyoverseSettings.ZenlessZoneZeroExportPath);

            if (missing)
            {
                SetAuthenticated(false);
                SetAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Hoyoverse_InvalidExportPath"));
                return;
            }

            var hasAnyPath =
                HasConfiguredExistingFile(_hoyoverseSettings.EnableGenshinImpact, _hoyoverseSettings.GenshinExportPath) ||
                HasConfiguredExistingFile(_hoyoverseSettings.EnableHonkaiStarRail, _hoyoverseSettings.HonkaiStarRailExportPath) ||
                HasConfiguredExistingFile(_hoyoverseSettings.EnableZenlessZoneZero, _hoyoverseSettings.ZenlessZoneZeroExportPath);

            SetAuthenticated(true);
            SetAuthStatus(ResourceProvider.GetString(hasAnyPath
                ? "LOCPlayAch_Status_Succeeded"
                : "LOCPlayAch_Settings_Hoyoverse_NoExportConfigured"));
        }

        private static bool IsConfiguredMissing(bool enabled, string path)
        {
            return enabled && !string.IsNullOrWhiteSpace(path) && !File.Exists(path);
        }

        private static bool HasConfiguredExistingFile(bool enabled, string path)
        {
            return enabled && !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }

        private void SetAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                AuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthStatus = status));
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

        private static void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

    }
}
