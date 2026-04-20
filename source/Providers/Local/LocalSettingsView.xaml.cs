using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.ImportedGameMetadata;
using PlayniteAchievements.Services;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSettingsView : ProviderSettingsViewBase
    {
        private const string LocalProviderIconFileName = "local.png";
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _pluginSettings;
        private readonly ILogger _logger;
        private LocalSettings _localSettings;
        private CancellationTokenSource _localImportCts;
        private bool _isRefreshingBundledSoundSelection;
        private bool _isRefreshingCustomSoundPathText;

        public ObservableCollection<string> ExtraLocalPathEntries { get; } = new ObservableCollection<string>();
        public ObservableCollection<BundledSoundOption> BundledUnlockSounds { get; } = new ObservableCollection<BundledSoundOption>();
        public ObservableCollection<string> AvailableSourceNames { get; } = new ObservableCollection<string>();
        public ObservableCollection<ImportedGameMetadataSourceOption> AvailableMetadataSources { get; } = new ObservableCollection<ImportedGameMetadataSourceOption>();
        public ObservableCollection<LocalSteamAppCacheUserOption> AvailableSteamAppCacheUsers { get; } = new ObservableCollection<LocalSteamAppCacheUserOption>();

        public new LocalSettings Settings => _localSettings;

        public LocalSettingsView(IPlayniteAPI playniteApi, PlayniteAchievementsSettings pluginSettings, ILogger logger)
        {
            _playniteApi = playniteApi;
            _pluginSettings = pluginSettings;
            _logger = logger;
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
            ImportedGameCustomSourceComboBox.ItemsSource = AvailableSourceNames;
            if (_localSettings != null)
            {
                _localSettings.PropertyChanged += LocalSettings_PropertyChanged;
            }

            RefreshAvailableSourceNames();
            RefreshAvailableMetadataSources();
            RefreshAvailableSteamAppCacheUsers();
            RefreshBundledUnlockSounds();
            RefreshRealtimeMonitoringControls();
            RefreshLocalProviderIconControls();
            RefreshExtraLocalPathEntries();
            RefreshImportedGameTargetControls();
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

            if (e.PropertyName == nameof(LocalSettings.CustomProviderIconPath))
            {
                RefreshLocalProviderIconControls();
            }

            if (e.PropertyName == nameof(LocalSettings.SteamUserdataPath))
            {
                RefreshAvailableSteamAppCacheUsers();
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
                RefreshAvailableSteamAppCacheUsers();
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

        private async void BrowseLocalProviderIcon_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFile("Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All Files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath) || _localSettings == null)
            {
                return;
            }

            try
            {
                var storedPath = await SaveLocalProviderIconAsync(selectedPath).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(storedPath))
                {
                    RefreshLocalProviderIconControls("Failed to import the selected Local provider icon.");
                    return;
                }

                _localSettings.CustomProviderIconPath = storedPath;
                RefreshLocalProviderIconControls("Custom Local provider icon saved.");
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed importing custom Local provider icon.");
                RefreshLocalProviderIconControls($"Failed to import the selected icon: {ex.Message}");
            }
        }

        private void ClearLocalProviderIcon_Click(object sender, RoutedEventArgs e)
        {
            if (_localSettings == null)
            {
                return;
            }

            TryDeleteManagedLocalProviderIcon(_localSettings.CustomProviderIconPath);
            _localSettings.CustomProviderIconPath = string.Empty;
            RefreshLocalProviderIconControls("Using the built-in Local provider icon.");
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

        private void RefreshLocalProviderIconControls(string statusMessage = null)
        {
            if (LocalProviderIconPathTextBox == null)
            {
                return;
            }

            var customPath = _localSettings?.CustomProviderIconPath?.Trim() ?? string.Empty;
            var hasCustomIcon = !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath);

            LocalProviderIconPathTextBox.Text = hasCustomIcon ? customPath : string.Empty;
            if (ClearLocalProviderIconButton != null)
            {
                ClearLocalProviderIconButton.IsEnabled = hasCustomIcon;
            }

            if (LocalProviderIconPreviewImage != null)
            {
                LocalProviderIconPreviewImage.Source = CreatePreviewImageSource(customPath);
            }

            if (LocalProviderIconStatusTextBlock != null)
            {
                LocalProviderIconStatusTextBlock.Text = statusMessage ??
                    (hasCustomIcon
                        ? "Custom icon is active for the Local provider."
                        : "No custom Local icon is set. The built-in Local icon will be used.");
            }
        }

        private async Task<string> SaveLocalProviderIconAsync(string sourcePath)
        {
            var diskImageService = _pluginSettings?._plugin?.DiskImageService;
            if (diskImageService == null || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            var targetPath = Path.Combine(
                diskImageService.GetCacheDirectoryPath(),
                "provider_icons",
                LocalProviderIconFileName);

            return await diskImageService
                .GetOrCopyLocalIconToPathAsync(
                    sourcePath,
                    targetPath,
                    LocalSettings.ProviderIconMaxPixelSize,
                    CancellationToken.None,
                    overwriteExistingTarget: true)
                .ConfigureAwait(false);
        }

        private static BitmapImage CreatePreviewImageSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = 48;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void TryDeleteManagedLocalProviderIcon(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                var targetDirectory = Path.GetDirectoryName(GetManagedLocalProviderIconPath());
                var normalizedPath = Path.GetFullPath(path);
                var normalizedDirectory = Path.GetFullPath(targetDirectory ?? string.Empty)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(normalizedPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed deleting managed Local provider icon.");
            }
        }

        private string GetManagedLocalProviderIconPath()
        {
            return Path.Combine(
                _pluginSettings?._plugin?.DiskImageService?.GetCacheDirectoryPath() ?? string.Empty,
                "provider_icons",
                LocalProviderIconFileName);
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

        private void ImportExtraLocalGamesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pluginSettings == null)
            {
                return;
            }

            if (!TryShowImportTargetDialog(out var selectedTarget, out var customSourceName, out var metadataSourceId, out var steamAppCacheUserId, out var existingGameBehavior))
            {
                return;
            }

            if (_localSettings != null)
            {
                _localSettings.ImportedGameLibraryTarget = selectedTarget;
                _localSettings.ImportedGameCustomSourceName = customSourceName ?? string.Empty;
                _localSettings.ImportedGameMetadataSourceId = metadataSourceId ?? string.Empty;
                _localSettings.SteamAppCacheUserId = steamAppCacheUserId ?? string.Empty;
                _localSettings.ExistingGameImportBehavior = existingGameBehavior;
                RefreshImportedGameTargetControls();
            }

            var roots = ExtraLocalPathEntries
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            StartLocalImport(roots, selectedTarget, customSourceName, metadataSourceId, existingGameBehavior, steamAppCacheUserId);
        }

        private void PendingExtraLocalPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateExtraLocalPathButtonStates();
        }

        private void ExtraLocalPathsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateExtraLocalPathButtonStates();
        }

        private void ImportedGameLibraryTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshImportedGameTargetControls();
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

            if (ImportExtraLocalGamesButton != null)
            {
                ImportExtraLocalGamesButton.IsEnabled = _localImportCts == null;
            }
        }

        private void RefreshImportedGameTargetControls()
        {
            if (ImportedGameCustomSourcePanel == null)
            {
                return;
            }

            ImportedGameCustomSourcePanel.Visibility = _localSettings?.ImportedGameLibraryTarget == LocalImportedGameLibraryTarget.CustomSource
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void RefreshAvailableSourceNames()
        {
            AvailableSourceNames.Clear();

            try
            {
                foreach (var sourceName in _playniteApi?.Database?.Sources?
                    .Where(source => source != null && !string.IsNullOrWhiteSpace(source.Name))
                    .Select(source => source.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>())
                {
                    AvailableSourceNames.Add(sourceName);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed loading Playnite source names for Local import settings.");
            }

            if (_localSettings != null &&
                _localSettings.ImportedGameLibraryTarget == LocalImportedGameLibraryTarget.CustomSource &&
                !string.IsNullOrWhiteSpace(_localSettings.ImportedGameCustomSourceName))
            {
                var match = AvailableSourceNames.FirstOrDefault(name =>
                    string.Equals(name, _localSettings.ImportedGameCustomSourceName, StringComparison.OrdinalIgnoreCase));
                _localSettings.ImportedGameCustomSourceName = match ?? AvailableSourceNames.FirstOrDefault() ?? string.Empty;
            }
        }

        private void RefreshAvailableMetadataSources()
        {
            AvailableMetadataSources.Clear();

            try
            {
                foreach (var option in ImportedGameMetadataSourceCatalog.GetAvailableOptions(_playniteApi, _logger))
                {
                    AvailableMetadataSources.Add(option);
                }

                _logger?.Info($"[LocalImport] Available metadata providers for Local import: {string.Join(", ", AvailableMetadataSources.Select(option => option.DisplayName))}");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed loading Playnite metadata plugins for Local import settings.");
            }

            if (_localSettings == null)
            {
                return;
            }

            var selectedId = (_localSettings.ImportedGameMetadataSourceId ?? string.Empty).Trim();
            if (!AvailableMetadataSources.Any(option => string.Equals(option.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            {
                _localSettings.ImportedGameMetadataSourceId = string.Empty;
            }
        }

        private void RefreshAvailableSteamAppCacheUsers()
        {
            AvailableSteamAppCacheUsers.Clear();
            AvailableSteamAppCacheUsers.Add(new LocalSteamAppCacheUserOption(LocalSettings.SteamAppCacheUserNone, "None (skip Steam appcache imports)"));
            AvailableSteamAppCacheUsers.Add(new LocalSteamAppCacheUserOption(string.Empty, "Automatic (all detected users)"));

            var discoveredUsers = DiscoverSteamAppCacheUsers().ToList();
            foreach (var user in discoveredUsers)
            {
                AvailableSteamAppCacheUsers.Add(user);
            }

            if (_localSettings == null)
            {
                return;
            }

            var selectedUserId = (_localSettings.SteamAppCacheUserId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(selectedUserId))
            {
                return;
            }

            if (!AvailableSteamAppCacheUsers.Any(option => string.Equals(option.UserId, selectedUserId, StringComparison.OrdinalIgnoreCase)))
            {
                AvailableSteamAppCacheUsers.Add(new LocalSteamAppCacheUserOption(selectedUserId, selectedUserId));
            }
        }

        private IReadOnlyList<LocalSteamAppCacheUserOption> DiscoverSteamAppCacheUsers()
        {
            var personaNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var discoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var steamBasePath in GetSteamBaseCandidatePaths())
            {
                TryReadSteamLoginUsers(steamBasePath, personaNamesById);

                var userdataRoot = GetSteamUserdataRoot(steamBasePath);
                if (!string.IsNullOrWhiteSpace(userdataRoot) && Directory.Exists(userdataRoot))
                {
                    try
                    {
                        foreach (var userDir in Directory.EnumerateDirectories(userdataRoot))
                        {
                            var userId = Path.GetFileName(userDir)?.Trim();
                            if (!string.IsNullOrWhiteSpace(userId) && Regex.IsMatch(userId, @"^\d+$"))
                            {
                                discoveredIds.Add(userId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Failed discovering Steam userdata users from '{userdataRoot}'.");
                    }
                }

                var statsRoot = GetSteamAppCacheStatsRoot(steamBasePath);
                if (!string.IsNullOrWhiteSpace(statsRoot) && Directory.Exists(statsRoot))
                {
                    try
                    {
                        foreach (var statsPath in Directory.EnumerateFiles(statsRoot, "UserGameStats_*_*.bin", SearchOption.TopDirectoryOnly))
                        {
                            var parts = Path.GetFileNameWithoutExtension(statsPath)?.Split('_');
                            if (parts == null || parts.Length < 3)
                            {
                                continue;
                            }

                            var userId = parts[1]?.Trim();
                            if (!string.IsNullOrWhiteSpace(userId) && Regex.IsMatch(userId, @"^\d+$"))
                            {
                                discoveredIds.Add(userId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Failed discovering Steam appcache users from '{statsRoot}'.");
                    }
                }
            }

            return discoveredIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Select(id => new LocalSteamAppCacheUserOption(id, BuildSteamAppCacheUserDisplayName(id, personaNamesById)))
                .ToList();
        }

        private IEnumerable<string> GetSteamBaseCandidatePaths()
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
                if (string.IsNullOrWhiteSpace(expanded))
                {
                    return;
                }

                if (string.Equals(Path.GetFileName(expanded), "userdata", StringComparison.OrdinalIgnoreCase))
                {
                    expanded = Directory.GetParent(expanded)?.FullName;
                }

                if (!string.IsNullOrWhiteSpace(expanded) && Directory.Exists(expanded))
                {
                    candidates.Add(expanded);
                }
            }

            AddCandidate(_localSettings?.SteamUserdataPath);
            AddCandidate(Environment.GetEnvironmentVariable("SteamPath"));
            AddCandidate(@"%ProgramFiles(x86)%\Steam");
            AddCandidate(@"%ProgramFiles%\Steam");

            foreach (var drive in Environment.GetLogicalDrives())
            {
                AddCandidate(Path.Combine(drive, "Program Files (x86)", "Steam"));
                AddCandidate(Path.Combine(drive, "Program Files", "Steam"));
                AddCandidate(Path.Combine(drive, "Programs", "Steam"));
                AddCandidate(Path.Combine(drive, "Steam"));
            }

            return candidates;
        }

        private static string GetSteamUserdataRoot(string steamBasePath)
        {
            if (string.IsNullOrWhiteSpace(steamBasePath))
            {
                return null;
            }

            var userdataRoot = Path.Combine(steamBasePath, "userdata");
            return Directory.Exists(userdataRoot) ? userdataRoot : null;
        }

        private static string GetSteamAppCacheStatsRoot(string steamBasePath)
        {
            if (string.IsNullOrWhiteSpace(steamBasePath))
            {
                return null;
            }

            var statsRoot = Path.Combine(steamBasePath, "appcache", "stats");
            return Directory.Exists(statsRoot) ? statsRoot : null;
        }

        private void TryReadSteamLoginUsers(string steamBasePath, IDictionary<string, string> personaNamesById)
        {
            if (string.IsNullOrWhiteSpace(steamBasePath) || personaNamesById == null)
            {
                return;
            }

            var loginUsersPath = Path.Combine(steamBasePath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersPath))
            {
                return;
            }

            try
            {
                var lines = File.ReadAllLines(loginUsersPath);
                string currentSteamId = null;

                foreach (var rawLine in lines)
                {
                    var line = rawLine?.Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var steamIdMatch = Regex.Match(line, "^\"(?<id>\\d{5,})\"$");
                    if (steamIdMatch.Success)
                    {
                        currentSteamId = steamIdMatch.Groups["id"].Value;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(currentSteamId))
                    {
                        continue;
                    }

                    var personaMatch = Regex.Match(line, @"^""PersonaName""\s+""(?<name>.*)""$");
                    if (!personaMatch.Success)
                    {
                        continue;
                    }

                    var accountId = TryConvertSteamId64ToAccountId(currentSteamId);
                    if (!string.IsNullOrWhiteSpace(accountId) && !personaNamesById.ContainsKey(accountId))
                    {
                        personaNamesById[accountId] = personaMatch.Groups["name"].Value.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed reading Steam login users from '{loginUsersPath}'.");
            }
        }

        private static string TryConvertSteamId64ToAccountId(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return null;
            }

            if (!ulong.TryParse(steamId.Trim(), out var steamIdValue) || steamIdValue < 76561197960265728UL)
            {
                return null;
            }

            var accountId = steamIdValue - 76561197960265728UL;
            return accountId <= uint.MaxValue
                ? accountId.ToString()
                : null;
        }

        private static string BuildSteamAppCacheUserDisplayName(string userId, IReadOnlyDictionary<string, string> personaNamesById)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return string.Empty;
            }

            if (personaNamesById != null && personaNamesById.TryGetValue(userId, out var personaName) && !string.IsNullOrWhiteSpace(personaName))
            {
                return $"{personaName} ({userId})";
            }

            return userId;
        }

        private bool TryShowImportTargetDialog(
            out LocalImportedGameLibraryTarget selectedTarget,
            out string customSourceName,
            out string metadataSourceId,
            out string steamAppCacheUserId,
            out LocalExistingGameImportBehavior existingGameBehavior)
        {
            selectedTarget = _localSettings?.ImportedGameLibraryTarget ?? LocalImportedGameLibraryTarget.None;
            customSourceName = _localSettings?.ImportedGameCustomSourceName ?? string.Empty;
            metadataSourceId = _localSettings?.ImportedGameMetadataSourceId ?? string.Empty;
            steamAppCacheUserId = _localSettings?.SteamAppCacheUserId ?? string.Empty;
            existingGameBehavior = _localSettings?.ExistingGameImportBehavior ?? LocalExistingGameImportBehavior.OverwriteExisting;

            var dialog = new LocalImportTargetDialog(selectedTarget, customSourceName, metadataSourceId, steamAppCacheUserId, existingGameBehavior, AvailableSourceNames, AvailableMetadataSources, AvailableSteamAppCacheUsers);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                "Import Local Games",
                dialog,
                new WindowOptions
                {
                    Width = 560,
                    Height = 400,
                    CanBeResizable = false,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            dialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (dialog.DialogResult != true)
            {
                return false;
            }

            selectedTarget = dialog.SelectedTarget;
            customSourceName = dialog.CustomSourceName?.Trim() ?? string.Empty;
            metadataSourceId = dialog.MetadataSourceId?.Trim() ?? string.Empty;
            steamAppCacheUserId = dialog.SteamAppCacheUserId?.Trim() ?? string.Empty;
            existingGameBehavior = dialog.ExistingGameBehavior;
            return true;
        }

        private void StartLocalImport(
            System.Collections.Generic.IReadOnlyCollection<string> roots,
            LocalImportedGameLibraryTarget selectedTarget,
            string customSourceName,
            string metadataSourceId,
            LocalExistingGameImportBehavior existingGameBehavior,
            string steamAppCacheUserId = null)
        {
            _localImportCts?.Dispose();
            _localImportCts = new CancellationTokenSource();
            UpdateExtraLocalPathButtonStates();

            var progressControl = new LocalImportProgressControl
            {
                DialogTitle = "Importing Local Games"
            };
            var window = PlayniteUiProvider.CreateExtensionWindow(
                "Import Local Games",
                progressControl,
                new WindowOptions
                {
                    Width = 430,
                    Height = 250,
                    CanBeResizable = false,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            progressControl.RequestClose += (s, e) => window.Close();
            progressControl.CancelRequested += (s, e) => _localImportCts?.Cancel();
            window.Closed += (s, e) =>
            {
                if (_localImportCts != null && !_localImportCts.IsCancellationRequested && progressControl.ShowCancelButton)
                {
                    _localImportCts.Cancel();
                }
            };

            UpdateImportStatus("Starting Local import...");
            window.Show();

            var progress = new Progress<LocalFolderGamesImporter.LocalImportProgressInfo>(report =>
            {
                progressControl.Update(report?.Percent ?? 0d, report?.Message, report?.Detail);
                UpdateImportStatus(report?.Message);
            });

            Task.Run(async () =>
            {
                try
                {
                    var importer = new LocalFolderGamesImporter(_playniteApi, _pluginSettings, _logger);
                    var result = await importer.ImportFromRootsAsync(
                        roots,
                        selectedTarget,
                        customSourceName,
                        metadataSourceId,
                        existingGameBehavior,
                        _localImportCts.Token,
                        progress,
                        steamAppCacheUserIdOverride: steamAppCacheUserId).ConfigureAwait(false);

                    var targetLabel = selectedTarget == LocalImportedGameLibraryTarget.CustomSource
                        ? $"custom source '{customSourceName?.Trim()}'"
                        : (selectedTarget == LocalImportedGameLibraryTarget.Steam ? "Steam library" : "None/manual library");
                    var metadataLabel = AvailableMetadataSources.FirstOrDefault(option => string.Equals(option.Id, metadataSourceId ?? string.Empty, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "Automatic";
                    var existingBehaviorLabel = existingGameBehavior == LocalExistingGameImportBehavior.SkipExisting ? "skip existing" : "overwrite existing";
                    var summary = $"Imported {result.ImportedCount} new games, reused {result.LinkedExistingCount} existing games, skipped {result.SkippedCount}, failed {result.FailedCount} across {result.UniqueAppIdCount} detected App IDs for {targetLabel} using metadata source '{metadataLabel}' with existing-game behavior '{existingBehaviorLabel}'.";

                    Dispatcher.Invoke(() =>
                    {
                        progressControl.MarkCompleted(summary);
                        UpdateImportStatus(summary);
                    });
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.Invoke(() =>
                    {
                        const string message = "Local import cancelled.";
                        progressControl.MarkCancelled(message);
                        UpdateImportStatus(message);
                    });
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed importing games from Local folders.");
                    Dispatcher.Invoke(() =>
                    {
                        var message = $"Import failed: {ex.Message}";
                        progressControl.MarkFailed(message);
                        UpdateImportStatus(message);
                    });
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        _localImportCts?.Dispose();
                        _localImportCts = null;
                        UpdateExtraLocalPathButtonStates();
                    });
                }
            });
        }

        private void UpdateImportStatus(string message)
        {
            if (ImportExtraLocalGamesStatusTextBlock != null)
            {
                ImportExtraLocalGamesStatusTextBlock.Text = message ?? string.Empty;
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
