using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;

using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// Notification settings: the General page. Hosts the three unlock-event features as
    /// siblings — on-screen notifications, unlock screenshots and unlock recordings — followed by
    /// the per-provider behavior override grid. Each feature has its own master switch and gates
    /// independently of the others. Appearance customization lives in
    /// <see cref="NotificationAppearanceSection"/>.
    /// </summary>
    public partial class NotificationsSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
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
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateRarityTexts);

            // The overrides grid is a DataContext island: its view model is independent of this
            // section's settings DataContext, and its ItemsSource is never reset in code-behind.
            _providerOverridesViewModel = new ProviderNotificationSettingsViewModel(
                settings,
                plugin,
                plugin.ProviderRegistry,
                logger);
            ProviderOverridesGrid.DataContext = _providerOverridesViewModel;

            UpdateRarityTexts();
        }

        public static readonly DependencyProperty CleanRaritiesTextProperty =
            DependencyProperty.Register(nameof(CleanRaritiesText), typeof(string), typeof(NotificationsSection),
                new PropertyMetadata(string.Empty));

        public string CleanRaritiesText
        {
            get => (string)GetValue(CleanRaritiesTextProperty);
            set => SetValue(CleanRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty WithToastRaritiesTextProperty =
            DependencyProperty.Register(nameof(WithToastRaritiesText), typeof(string), typeof(NotificationsSection),
                new PropertyMetadata(string.Empty));

        public string WithToastRaritiesText
        {
            get => (string)GetValue(WithToastRaritiesTextProperty);
            set => SetValue(WithToastRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty FramedRaritiesTextProperty =
            DependencyProperty.Register(nameof(FramedRaritiesText), typeof(string), typeof(NotificationsSection),
                new PropertyMetadata(string.Empty));

        public string FramedRaritiesText
        {
            get => (string)GetValue(FramedRaritiesTextProperty);
            set => SetValue(FramedRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingRaritiesText), typeof(string), typeof(NotificationsSection),
                new PropertyMetadata(string.Empty));

        public string RecordingRaritiesText
        {
            get => (string)GetValue(RecordingRaritiesTextProperty);
            set => SetValue(RecordingRaritiesTextProperty, value);
        }

        /// <summary>
        /// Pulses the controllers at the currently configured strength and duration so the settings
        /// can be felt without unlocking an achievement.
        /// </summary>
        private void TestVibration_Click(object sender, RoutedEventArgs e)
        {
            // Nothing here takes focus away from the button the way the buttons that open a window
            // do, so it would keep the theme's focused look until something else was clicked.
            Keyboard.ClearFocus();

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

            switch (e?.PropertyName)
            {
                case nameof(PersistedSettings.UnlockScreenshotCleanRarities):
                case nameof(PersistedSettings.UnlockScreenshotWithToastRarities):
                case nameof(PersistedSettings.UnlockScreenshotFramedRarities):
                case nameof(PersistedSettings.UnlockRecordingRarities):
                    UpdateRarityTexts();
                    break;
            }
        }

        private void CleanRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => _settings?.Persisted?.UnlockScreenshotCleanRarities ?? RaritySelection.All,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.UnlockScreenshotCleanRarities = value; } });
        }

        private void WithToastRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => _settings?.Persisted?.UnlockScreenshotWithToastRarities ?? RaritySelection.All,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.UnlockScreenshotWithToastRarities = value; } });
        }

        private void FramedRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => _settings?.Persisted?.UnlockScreenshotFramedRarities ?? RaritySelection.All,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.UnlockScreenshotFramedRarities = value; } });
        }

        private void RecordingRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => _settings?.Persisted?.UnlockRecordingRarities ?? RaritySelection.All,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.UnlockRecordingRarities = value; } });
        }

        private void OpenRaritySelector(Button button, Func<RaritySelection> get, Action<RaritySelection> set)
        {
            RaritySelectorMenu.Open(button, get, set, UpdateRarityTexts);
        }

        private void UpdateRarityTexts()
        {
            var persisted = _settings?.Persisted;
            CleanRaritiesText = FormatRarities(persisted?.UnlockScreenshotCleanRarities ?? RaritySelection.All);
            WithToastRaritiesText = FormatRarities(persisted?.UnlockScreenshotWithToastRarities ?? RaritySelection.All);
            FramedRaritiesText = FormatRarities(persisted?.UnlockScreenshotFramedRarities ?? RaritySelection.All);
            RecordingRaritiesText = FormatRarities(persisted?.UnlockRecordingRarities ?? RaritySelection.All);
        }

        private static string FormatRarities(RaritySelection selection)
        {
            return RaritySelectorMenu.Format(selection);
        }

        private void ScreenshotDirectory_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.UnlockScreenshotDirectory = selected;
            }
        }

        private void RecordingDirectory_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.UnlockRecordingDirectory = selected;
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
            _providerOverridesViewModel?.Dispose();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
