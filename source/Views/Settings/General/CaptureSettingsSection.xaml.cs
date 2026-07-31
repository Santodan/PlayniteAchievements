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
using PlayniteAchievements.Views.Settings;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Screenshots and recordings section. Hosts the unlock screenshot
    /// options (variants, per-variant rarity sets, suffixes, folder) and the ffmpeg-based unlock
    /// recording options. DataContext (the settings object) is inherited from the settings view.
    /// </summary>
    public partial class CaptureSettingsSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly Services.Recording.FfmpegValidationService _ffmpegValidation;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        // Rarity tiers in ascending order, paired with their display-label keys. Drives both the
        // multi-select menus and the summary text.
        private static readonly (RarityTier Tier, string LabelKey)[] RarityOptions =
        {
            (RarityTier.Common, "LOCPlayAch_Rarity_Common"),
            (RarityTier.Uncommon, "LOCPlayAch_Rarity_Uncommon"),
            (RarityTier.Rare, "LOCPlayAch_Rarity_Rare"),
            (RarityTier.UltraRare, "LOCPlayAch_Rarity_UltraRare")
        };

        public CaptureSettingsSection()
        {
            InitializeComponent();
        }

        internal CaptureSettingsSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _ffmpegValidation = new Services.Recording.FfmpegValidationService(logger);

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateRarityTexts);

            UpdateRarityTexts();
        }

        public static readonly DependencyProperty CleanRaritiesTextProperty =
            DependencyProperty.Register(nameof(CleanRaritiesText), typeof(string), typeof(CaptureSettingsSection),
                new PropertyMetadata(string.Empty));

        public string CleanRaritiesText
        {
            get => (string)GetValue(CleanRaritiesTextProperty);
            set => SetValue(CleanRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty WithToastRaritiesTextProperty =
            DependencyProperty.Register(nameof(WithToastRaritiesText), typeof(string), typeof(CaptureSettingsSection),
                new PropertyMetadata(string.Empty));

        public string WithToastRaritiesText
        {
            get => (string)GetValue(WithToastRaritiesTextProperty);
            set => SetValue(WithToastRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty FramedRaritiesTextProperty =
            DependencyProperty.Register(nameof(FramedRaritiesText), typeof(string), typeof(CaptureSettingsSection),
                new PropertyMetadata(string.Empty));

        public string FramedRaritiesText
        {
            get => (string)GetValue(FramedRaritiesTextProperty);
            set => SetValue(FramedRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingRaritiesText), typeof(string), typeof(CaptureSettingsSection),
                new PropertyMetadata(string.Empty));

        public string RecordingRaritiesText
        {
            get => (string)GetValue(RecordingRaritiesTextProperty);
            set => SetValue(RecordingRaritiesTextProperty, value);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
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

        private void FfmpegPath_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFile("ffmpeg|ffmpeg.exe|Executable|*.exe");
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.FfmpegPath = selected;
            }
        }

        /// <summary>
        /// Runs the ffmpeg validation (version + encoder probes + a 1s screen-capture smoke
        /// test) and reports the outcome in the status line. The button is disabled while the
        /// probes run; results are cached per path for the session.
        /// </summary>
        private async void FfmpegTest_Click(object sender, RoutedEventArgs e)
        {
            var path = _settings?.Persisted?.FfmpegPath;
            if (_ffmpegValidation == null || FfmpegTestButton == null || FfmpegStatusText == null)
            {
                return;
            }

            FfmpegTestButton.IsEnabled = false;
            try
            {
                var result = await _ffmpegValidation.ValidateAsync(path, runSmokeTest: true);
                if (result?.IsValid == true)
                {
                    FfmpegStatusText.Text = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_RecordingFfmpegValid"),
                        result.Version,
                        string.Join(", ", result.AvailableEncoders));
                    // Back to the muted style's own foreground for the success case.
                    FfmpegStatusText.ClearValue(TextBlock.ForegroundProperty);
                }
                else
                {
                    FfmpegStatusText.Text = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_RecordingFfmpegInvalid"),
                        result?.Error ?? string.Empty);
                    if (TryFindResource("PlayAch.Brush.ErrorText") is System.Windows.Media.Brush errorBrush)
                    {
                        FfmpegStatusText.Foreground = errorBrush;
                    }
                }

                FfmpegStatusText.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                // Validation never throws by design; guard the async-void boundary anyway.
            }
            finally
            {
                FfmpegTestButton.IsEnabled = true;
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
    }
}
