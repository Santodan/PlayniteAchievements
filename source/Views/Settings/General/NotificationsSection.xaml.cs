using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;

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

        // Rarity tiers in ascending order, paired with their display-label keys. Drives both the
        // multi-select menus and the summary text.
        private static readonly (RarityTier Tier, string LabelKey)[] RarityOptions =
        {
            (RarityTier.Common, "LOCPlayAch_Rarity_Common"),
            (RarityTier.Uncommon, "LOCPlayAch_Rarity_Uncommon"),
            (RarityTier.Rare, "LOCPlayAch_Rarity_Rare"),
            (RarityTier.UltraRare, "LOCPlayAch_Rarity_UltraRare")
        };

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

        /// <summary>
        /// Builds and opens a checkable rarity menu under <paramref name="button"/>. Each toggle
        /// reads the current selection fresh (the menu stays open across clicks), flips the tier's
        /// bit, writes it back, and refreshes the summary text.
        /// </summary>
        private void OpenRaritySelector(Button button, Func<RaritySelection> get, Action<RaritySelection> set)
        {
            var menu = button?.ContextMenu;
            if (menu == null || get == null || set == null)
            {
                return;
            }

            menu.Items.Clear();
            foreach (var option in RarityOptions)
            {
                var flag = option.Tier.ToFlag();
                var item = CreateMenuItem(
                    button,
                    L(option.LabelKey),
                    get().Contains(option.Tier),
                    isChecked =>
                    {
                        var current = get();
                        set(isChecked ? current | flag : current & ~flag);
                        UpdateRarityTexts();
                    });
                menu.Items.Add(item);
            }

            OpenContextMenu(button, menu);
        }

        private static MenuItem CreateMenuItem(Button button, string header, bool isChecked, Action<bool> onToggle)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = isChecked
            };

            if (button?.TryFindResource("AchievementMultiSelectMenuItemStyle") is Style itemStyle)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, __) => onToggle?.Invoke(item.IsChecked);
            return item;
        }

        private static void OpenContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null || menu.Items.Count == 0)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
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
            if (selection == RaritySelection.All)
            {
                return L("LOCPlayAch_Common_All");
            }

            if (selection == RaritySelection.None)
            {
                return L("LOCPlayAch_Common_None");
            }

            var labels = new List<string>();
            foreach (var option in RarityOptions)
            {
                if (selection.Contains(option.Tier))
                {
                    labels.Add(L(option.LabelKey));
                }
            }

            return labels.Count > 0 ? string.Join(", ", labels) : L("LOCPlayAch_Common_None");
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
