using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: General section. Hosts grid defaults, achievement icon and visibility
    /// options plus the achievement visibility preview, and the reset-to-defaults action.
    /// </summary>
    public partial class DisplayGeneralSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly Action _onDisplaySettingsReset;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private ModernThemeBindings _achievementVisibilityPreviewThemeData;

        public DisplayGeneralSection()
        {
            InitializeComponent();
        }

        internal DisplayGeneralSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Action onDisplaySettingsReset)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _onDisplaySettingsReset = onDisplaySettingsReset;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                RefreshVisibilityPreview);

            RefreshVisibilityPreview();
        }

        /// <summary>
        /// Gets modern theme bindings with locked and hidden achievements for visibility preview.
        /// </summary>
        public ModernThemeBindings AchievementVisibilityPreviewThemeData
        {
            get
            {
                if (_achievementVisibilityPreviewThemeData == null)
                {
                    _achievementVisibilityPreviewThemeData = MockDataHelper.GetAchievementVisibilityPreviewThemeData();
                }
                return _achievementVisibilityPreviewThemeData;
            }
        }

        /// <summary>
        /// Refreshes the achievement visibility preview to reflect current settings.
        /// </summary>
        public void RefreshVisibilityPreview()
        {
            var settings = _settings?.Persisted;
            if (settings == null) return;

            _achievementVisibilityPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowCompactListRarityBar);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (DisplayPreviewProperties.AffectsMockPreviews(e.PropertyName))
            {
                RefreshVisibilityPreview();
            }

            if (e.PropertyName == nameof(PersistedSettings.ShowCompletedProgressColoring))
            {
                RarityAppearanceHelper.ApplyBadgeApplicationResources(_settings?.Persisted);
            }
        }

        private void ResetDisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger?.Info("Resetting Display tab settings to defaults.");

                _settings.Persisted.ResetDisplaySettingsToDefaults();
                RefreshVisibilityPreview();
                _onDisplaySettingsReset?.Invoke();

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reset Display tab settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static string LF(string key, params object[] args)
        {
            return string.Format(L(key), args);
        }
    }
}
