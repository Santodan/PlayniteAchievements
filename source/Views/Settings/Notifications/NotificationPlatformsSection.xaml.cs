using System;
using System.ComponentModel;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notification settings: the Platforms page. The per-provider tri-state override grid, which
    /// modulates the Behavior and Captures master switches per platform. The grid's view model
    /// debounces persistence and flushes on dispose, so this page must be disposed by its host.
    /// </summary>
    public partial class NotificationPlatformsSection : UserControl, IDisposable
    {
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private readonly ProviderNotificationSettingsViewModel _providerOverridesViewModel;

        public NotificationPlatformsSection()
        {
            InitializeComponent();
        }

        internal NotificationPlatformsSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            _persistedSubscription = new PersistedSettingsSubscription(
                settings,
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
