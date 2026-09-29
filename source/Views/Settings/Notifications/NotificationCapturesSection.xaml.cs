using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notification settings: the Captures page. Unlock screenshots (three independent variants)
    /// and unlock recordings, plus the shared capture delay. Each feature has its own master
    /// switch and none of them is gated on the notifications master switch, matching
    /// <see cref="PlayniteAchievements.Services.UI.ProviderNotificationPolicy"/>, which ANDs
    /// captures against their own masters only.
    /// </summary>
    public partial class NotificationCapturesSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        public NotificationCapturesSection()
        {
            InitializeComponent();
        }

        internal NotificationCapturesSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateRarityTexts);

            UpdateRarityTexts();
        }

        public static readonly DependencyProperty CleanRaritiesTextProperty =
            DependencyProperty.Register(nameof(CleanRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string CleanRaritiesText
        {
            get => (string)GetValue(CleanRaritiesTextProperty);
            set => SetValue(CleanRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty WithToastRaritiesTextProperty =
            DependencyProperty.Register(nameof(WithToastRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string WithToastRaritiesText
        {
            get => (string)GetValue(WithToastRaritiesTextProperty);
            set => SetValue(WithToastRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty FramedRaritiesTextProperty =
            DependencyProperty.Register(nameof(FramedRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string FramedRaritiesText
        {
            get => (string)GetValue(FramedRaritiesTextProperty);
            set => SetValue(FramedRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string RecordingRaritiesText
        {
            get => (string)GetValue(RecordingRaritiesTextProperty);
            set => SetValue(RecordingRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingCleanRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingCleanRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string RecordingCleanRaritiesText
        {
            get => (string)GetValue(RecordingCleanRaritiesTextProperty);
            set => SetValue(RecordingCleanRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingFramedRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingFramedRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string RecordingFramedRaritiesText
        {
            get => (string)GetValue(RecordingFramedRaritiesTextProperty);
            set => SetValue(RecordingFramedRaritiesTextProperty, value);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e?.PropertyName)
            {
                case nameof(PersistedSettings.UnlockScreenshotCleanRarities):
                case nameof(PersistedSettings.UnlockScreenshotWithToastRarities):
                case nameof(PersistedSettings.UnlockScreenshotFramedRarities):
                case nameof(PersistedSettings.UnlockRecordingRarities):
                case nameof(PersistedSettings.UnlockRecordingCleanRarities):
                case nameof(PersistedSettings.UnlockRecordingFramedRarities):
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

        private void RecordingCleanRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => _settings?.Persisted?.UnlockRecordingCleanRarities ?? RaritySelection.All,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.UnlockRecordingCleanRarities = value; } });
        }

        private void RecordingFramedRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => _settings?.Persisted?.UnlockRecordingFramedRarities ?? RaritySelection.All,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.UnlockRecordingFramedRarities = value; } });
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
            RecordingCleanRaritiesText = FormatRarities(persisted?.UnlockRecordingCleanRarities ?? RaritySelection.All);
            RecordingFramedRaritiesText = FormatRarities(persisted?.UnlockRecordingFramedRarities ?? RaritySelection.All);
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
        }
    }
}
