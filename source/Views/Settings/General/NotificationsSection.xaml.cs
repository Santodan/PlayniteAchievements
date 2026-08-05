using System;
using System.ComponentModel;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
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
