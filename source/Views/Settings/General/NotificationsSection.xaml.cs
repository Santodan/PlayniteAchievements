using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Notifications section. Hosts the notification enable toggles, toast
    /// behavior options, and the per-provider behavior override grid. Appearance customization
    /// lives in <see cref="NotificationAppearanceSection"/>; screenshots and recordings live in
    /// <see cref="CaptureSettingsSection"/>.
    /// </summary>
    public partial class NotificationsSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private readonly ProviderNotificationSettingsViewModel _providerOverridesViewModel;
        private readonly ILogger _logger;

        public NotificationsSection()
        {
            InitializeComponent();
        }

        internal NotificationsSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged);

            // The overrides grid is a DataContext island: its view model is independent of this
            // section's settings DataContext, and its ItemsSource is never reset in code-behind.
            _providerOverridesViewModel = new ProviderNotificationSettingsViewModel(
                settings,
                plugin,
                plugin.ProviderRegistry,
                logger);
            ProviderOverridesGrid.DataContext = _providerOverridesViewModel;
        }

        /// <summary>
        /// Pulses the controllers at the currently configured strength and duration so the settings
        /// can be felt without unlocking an achievement.
        /// </summary>
        private void TestVibration_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            try
            {
                ControllerVibrationService.Pulse(
                    persisted.ControllerVibrationStrengthPercent,
                    persisted.ControllerVibrationDurationMs,
                    _logger);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Test controller vibration failed.");
            }
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) ||
                e.PropertyName == nameof(PersistedSettings.ProviderColorOverrides))
            {
                _providerOverridesViewModel?.RefreshProviderAppearance();
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
            _providerOverridesViewModel?.Dispose();
        }
    }
}
