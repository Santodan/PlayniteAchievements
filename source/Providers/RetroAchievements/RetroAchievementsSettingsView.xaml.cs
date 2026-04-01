using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    /// <summary>
    /// Settings view for the RetroAchievements provider.
    /// </summary>
    public partial class RetroAchievementsSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(RetroAchievementsSettingsView));
        private readonly string _pluginUserDataPath;
        private RetroAchievementsSettings _raSettings;

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(RetroAchievementsSettingsView), new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(RetroAchievementsSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new RetroAchievementsSettings Settings => _raSettings;

        public RetroAchievementsSettingsView(string pluginUserDataPath)
        {
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _raSettings = settings as RetroAchievementsSettings;
            base.Initialize(settings);

            if (_raSettings is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged -= Settings_PropertyChanged;
                notify.PropertyChanged += Settings_PropertyChanged;
            }

            RefreshAuthStatus();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null ||
                string.Equals(e.PropertyName, nameof(RetroAchievementsSettings.RaUsername), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(RetroAchievementsSettings.RaWebApiKey), StringComparison.Ordinal))
            {
                RefreshAuthStatus();
            }
        }

        private void RefreshAuthStatus()
        {
            var hasUsername = !string.IsNullOrWhiteSpace(_raSettings?.RaUsername);
            var hasApiKey = !string.IsNullOrWhiteSpace(_raSettings?.RaWebApiKey);
            var authenticated = hasUsername && hasApiKey;

            IsAuthenticated = authenticated;
            AuthStatus = authenticated
                ? ResourceProvider.GetString("LOCPlayAch_Auth_AlreadyAuthenticated")
                : ResourceProvider.GetString("LOCPlayAch_Auth_NotAuthenticated");
        }

        public Task RefreshAuthStatusAsync()
        {
            RefreshAuthStatus();
            return Task.CompletedTask;
        }

        private void ForceRebuildHashIndex_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var raCacheDir = Path.Combine(_pluginUserDataPath, "ra");

                if (!Directory.Exists(raCacheDir))
                {
                    API.Instance.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var hashIndexFiles = Directory.GetFiles(raCacheDir, "hashindex_*.json.gz");
                var deletedCount = 0;

                foreach (var file in hashIndexFiles)
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, $"Failed to delete hash index cache: {file}");
                    }
                }

                if (deletedCount > 0)
                {
                    API.Instance.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    API.Instance.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to force hash index rebuild.");
                API.Instance.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
