using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSettingsView : ProviderSettingsViewBase
    {
        private readonly IPlayniteAPI _playniteApi;
        private LocalSettings _localSettings;
        private bool _isRefreshingBundledSoundSelection;
        private bool _isRefreshingCustomSoundPathText;

        public ObservableCollection<string> ExtraLocalPathEntries { get; } = new ObservableCollection<string>();
        public ObservableCollection<BundledSoundOption> BundledUnlockSounds { get; } = new ObservableCollection<BundledSoundOption>();

        public new LocalSettings Settings => _localSettings;

        public LocalSettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            var previousSettings = _localSettings;
            if (previousSettings != null)
            {
                previousSettings.PropertyChanged -= LocalSettings_PropertyChanged;
            }

            _localSettings = settings as LocalSettings;
            base.Initialize(settings);
            ExtraLocalPathsList.ItemsSource = ExtraLocalPathEntries;
            BundledUnlockSoundComboBox.ItemsSource = BundledUnlockSounds;
            if (_localSettings != null)
            {
                _localSettings.PropertyChanged += LocalSettings_PropertyChanged;
            }

            RefreshBundledUnlockSounds();
            RefreshRealtimeMonitoringControls();
            RefreshExtraLocalPathEntries();
            UpdateExtraLocalPathButtonStates();
        }

        private void LocalSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(LocalSettings.EnableActiveGameMonitoring) ||
                e.PropertyName == nameof(LocalSettings.BundledUnlockSoundPath) ||
                e.PropertyName == nameof(LocalSettings.EffectiveBundledUnlockSoundPath) ||
                e.PropertyName == nameof(LocalSettings.CustomUnlockSoundPath) ||
                e.PropertyName == nameof(LocalSettings.UnlockSoundPath) ||
                e.PropertyName == nameof(LocalSettings.ActiveGameMonitoringIntervalSeconds))
            {
                RefreshRealtimeMonitoringControls();
            }
        }

        private void SteamUserdataPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                MoveFocusFrom(sender as TextBox);
            }
        }

        private void SteamUserdataPath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        private void SteamUserdataBrowse_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _localSettings.SteamUserdataPath = selectedPath;
            }
        }

        private void BrowseExtraLocalPath_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            PendingExtraLocalPathTextBox.Text = selectedPath;
        }

        private void BrowseUnlockSoundPath_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFile("Wave files|*.wav|All files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath) || _localSettings == null)
            {
                return;
            }

            _localSettings.CustomUnlockSoundPath = selectedPath;
            RefreshRealtimeMonitoringControls();
        }

        private void BundledUnlockSoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingBundledSoundSelection || _localSettings == null)
            {
                return;
            }

            if (!(BundledUnlockSoundComboBox.SelectedItem is BundledSoundOption option))
            {
                return;
            }

            _localSettings.BundledUnlockSoundPath = option.RelativePath ?? string.Empty;
            RefreshRealtimeMonitoringControls();
        }

        private void UnlockSoundPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_localSettings == null)
            {
                return;
            }

            if (_isRefreshingCustomSoundPathText)
            {
                return;
            }

            _localSettings.CustomUnlockSoundPath = UnlockSoundPathTextBox?.Text ?? string.Empty;
            UpdateUnlockSoundStatus();
        }

        private void PollingIntervalSecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyPollingIntervalFromTextBox(updateTextBox: false);
        }

        private void PollingIntervalSecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyPollingIntervalFromTextBox(updateTextBox: true);
        }

        private void RealtimeMonitoringSettingChanged(object sender, RoutedEventArgs e)
        {
            RefreshRealtimeMonitoringControls();
        }

        private async void TestUnlockSoundButton_Click(object sender, RoutedEventArgs e)
        {
            var validationMessage = GetUnlockSoundValidationMessage(out var canTest);
            UpdateUnlockSoundStatus(validationMessage);
            if (!canTest)
            {
                return;
            }

            var soundPath = _localSettings?.UnlockSoundPath?.Trim();
            if (string.IsNullOrWhiteSpace(soundPath))
            {
                return;
            }

            soundPath = NotificationPublisher.ResolveSoundPath(soundPath);

            try
            {
                TestUnlockSoundButton.IsEnabled = false;
                UnlockSoundStatusTextBlock.Text = "Sending test notification...";
                _playniteApi?.Notifications?.Add(new NotificationMessage(
                    $"PlayniteAchievements-LocalUnlock-Test-{Guid.NewGuid()}",
                    "Local Achievement Unlocked\nCurrent Game\nUnlocked: Test Achievement",
                    NotificationType.Info));

                await Task.Run(() =>
                {
                    using (var player = new SoundPlayer(soundPath))
                    {
                        player.PlaySync();
                    }
                });

                UnlockSoundStatusTextBlock.Text = "Test notification sent and sound played successfully.";
                if (TestUnlockSoundButton != null)
                {
                    TestUnlockSoundButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                UnlockSoundStatusTextBlock.Text = $"Failed to send test notification: {ex.Message}";
                if (TestUnlockSoundButton != null)
                {
                    TestUnlockSoundButton.IsEnabled = true;
                }
            }
        }

        private void AddExtraLocalPath_Click(object sender, RoutedEventArgs e)
        {
            var path = PendingExtraLocalPathTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!Directory.Exists(path))
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    "The selected folder does not exist.",
                    "Playnite Achievements",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (ExtraLocalPathEntries.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                PendingExtraLocalPathTextBox.Clear();
                UpdateExtraLocalPathButtonStates();
                return;
            }

            ExtraLocalPathEntries.Add(path);
            SyncExtraLocalPathsToSettings();
            PendingExtraLocalPathTextBox.Clear();
            UpdateExtraLocalPathButtonStates();
        }

        private void RemoveExtraLocalPath_Click(object sender, RoutedEventArgs e)
        {
            if (!(ExtraLocalPathsList.SelectedItem is string selectedPath))
            {
                return;
            }

            ExtraLocalPathEntries.Remove(selectedPath);
            SyncExtraLocalPathsToSettings();
            UpdateExtraLocalPathButtonStates();
        }

        private void PendingExtraLocalPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateExtraLocalPathButtonStates();
        }

        private void ExtraLocalPathsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateExtraLocalPathButtonStates();
        }

        private void RefreshExtraLocalPathEntries()
        {
            ExtraLocalPathEntries.Clear();
            if (_localSettings == null)
            {
                return;
            }

            foreach (var path in _localSettings.GetExtraLocalPathEntries())
            {
                ExtraLocalPathEntries.Add(path);
            }
        }

        private void SyncExtraLocalPathsToSettings()
        {
            _localSettings?.SetExtraLocalPathEntries(ExtraLocalPathEntries);
        }

        private void UpdateExtraLocalPathButtonStates()
        {
            if (AddExtraLocalPathButton != null)
            {
                AddExtraLocalPathButton.IsEnabled = !string.IsNullOrWhiteSpace(PendingExtraLocalPathTextBox?.Text);
            }

            if (RemoveExtraLocalPathButton != null)
            {
                RemoveExtraLocalPathButton.IsEnabled = ExtraLocalPathsList?.SelectedItem is string;
            }
        }

        private void RefreshRealtimeMonitoringControls()
        {
            if (_localSettings == null)
            {
                return;
            }

            if (PollingIntervalSecondsTextBox != null)
            {
                var normalizedInterval = _localSettings.ActiveGameMonitoringIntervalSeconds.ToString();
                if (!string.Equals(PollingIntervalSecondsTextBox.Text, normalizedInterval, StringComparison.Ordinal))
                {
                    PollingIntervalSecondsTextBox.Text = normalizedInterval;
                }
            }

            if (UnlockSoundPathTextBox != null)
            {
                var customPath = _localSettings.CustomUnlockSoundPath ?? string.Empty;
                if (!string.Equals(UnlockSoundPathTextBox.Text, customPath, StringComparison.Ordinal))
                {
                    _isRefreshingCustomSoundPathText = true;
                    try
                    {
                        UnlockSoundPathTextBox.Text = customPath;
                    }
                    finally
                    {
                        _isRefreshingCustomSoundPathText = false;
                    }
                }
            }

            RefreshBundledUnlockSoundSelection();
            UpdateUnlockSoundStatus();
        }

        private void RefreshBundledUnlockSounds()
        {
            BundledUnlockSounds.Clear();

            foreach (var soundPath in EnumerateBundledSoundPaths()
                .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = GetRelativeBundledSoundPath(soundPath);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                var displayName = Path.GetFileNameWithoutExtension(soundPath);
                BundledUnlockSounds.Add(new BundledSoundOption(displayName, relativePath));
            }

            RefreshBundledUnlockSoundSelection();
        }

        private void RefreshBundledUnlockSoundSelection()
        {
            if (BundledUnlockSoundComboBox == null)
            {
                return;
            }

            var selectedPath = (_localSettings?.EffectiveBundledUnlockSoundPath ?? string.Empty).Trim();
            _isRefreshingBundledSoundSelection = true;
            try
            {
                var match = BundledUnlockSounds.FirstOrDefault(option =>
                    string.Equals(option.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase));
                BundledUnlockSoundComboBox.SelectedItem = match ?? BundledUnlockSounds.FirstOrDefault();
            }
            finally
            {
                _isRefreshingBundledSoundSelection = false;
            }
        }

        private void ApplyPollingIntervalFromTextBox(bool updateTextBox)
        {
            if (_localSettings == null)
            {
                return;
            }

            var rawValue = PollingIntervalSecondsTextBox?.Text?.Trim();
            if (int.TryParse(rawValue, out var parsedValue))
            {
                _localSettings.ActiveGameMonitoringIntervalSeconds = parsedValue;
            }

            if (updateTextBox && PollingIntervalSecondsTextBox != null)
            {
                PollingIntervalSecondsTextBox.Text = _localSettings.ActiveGameMonitoringIntervalSeconds.ToString();
            }
        }

        private void UpdateUnlockSoundStatus(string overrideMessage = null)
        {
            if (UnlockSoundStatusTextBlock == null)
            {
                return;
            }

            var canTest = false;
            var message = overrideMessage;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = GetUnlockSoundValidationMessage(out canTest);
            }
            UnlockSoundStatusTextBlock.Text = message;

            if (TestUnlockSoundButton != null)
            {
                TestUnlockSoundButton.IsEnabled = canTest;
            }
        }

        private string GetUnlockSoundValidationMessage(out bool canTest)
        {
            canTest = false;

            if (_localSettings?.EnableActiveGameMonitoring != true)
            {
                return "Enable real-time Local monitoring to use sound alerts.";
            }

            var soundPath = _localSettings.UnlockSoundPath?.Trim();
            if (string.IsNullOrWhiteSpace(soundPath))
            {
                return "No sound file selected. Unlock notifications will stay silent.";
            }

            var resolvedSoundPath = NotificationPublisher.ResolveSoundPath(soundPath);

            if (!File.Exists(resolvedSoundPath))
            {
                return "Sound file not found.";
            }

            if (!string.Equals(Path.GetExtension(resolvedSoundPath), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return "Only .wav files are supported.";
            }

            canTest = true;
            if (!string.IsNullOrWhiteSpace(_localSettings.CustomUnlockSoundPath))
            {
                return "Using custom override sound. File is valid and ready to test.";
            }

            if (!string.IsNullOrWhiteSpace(_localSettings.EffectiveBundledUnlockSoundPath))
            {
                return "Using bundled default sound. File is valid and ready to test.";
            }

            return "Sound file is valid and ready to test.";
        }

        private static void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private static string[] EnumerateBundledSoundPaths()
        {
            try
            {
                var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrWhiteSpace(assemblyDirectory))
                {
                    return Array.Empty<string>();
                }

                var soundDirectory = Path.Combine(assemblyDirectory, "Resources", "Sounds");
                if (!Directory.Exists(soundDirectory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(soundDirectory, "*.wav", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string GetRelativeBundledSoundPath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return null;
            }

            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return null;
            }

            var relativeUri = new Uri(assemblyDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .MakeRelativeUri(new Uri(absolutePath));
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        public sealed class BundledSoundOption
        {
            public BundledSoundOption(string displayName, string relativePath)
            {
                DisplayName = displayName;
                RelativePath = relativePath;
            }

            public string DisplayName { get; }

            public string RelativePath { get; }
        }
    }
}
