using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
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
        private const string LocalProviderIconResourceKey = "GeoLocal";
        private const string LocalProviderColorHex = "#FF8A00";
        private const string SuccessStoryExtensionId = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private const string SuccessStoryFolderName = "SuccessStory";
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _pluginSettings;
        private readonly ILogger _logger;
        private LocalSettings _localSettings;
        private CancellationTokenSource _localImportCts;

        public static readonly DependencyProperty SuccessStoryImportPathProperty =
            DependencyProperty.Register(
                nameof(SuccessStoryImportPath),
                typeof(string),
                typeof(LocalSettingsView),
                new PropertyMetadata(string.Empty));

        public string SuccessStoryImportPath
        {
            get => (string)GetValue(SuccessStoryImportPathProperty);
            set => SetValue(SuccessStoryImportPathProperty, value);
        }

        public ObservableCollection<string> ExtraLocalPathEntries { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> ExcludedLocalPathEntries { get; } = new ObservableCollection<string>();
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
            ExcludedLocalPathsList.ItemsSource = ExcludedLocalPathEntries;
            ImportedGameCustomSourceComboBox.ItemsSource = AvailableSourceNames;
            if (_localSettings != null)
            {
                _localSettings.PropertyChanged += LocalSettings_PropertyChanged;
            }

            RefreshAvailableSourceNames();
            RefreshAvailableMetadataSources();
            RefreshAvailableSteamAppCacheUsers();
            RefreshLocalProviderIconControls();
            RefreshExtraLocalPathEntries();
            RefreshExcludedLocalPathEntries();
            RefreshImportedGameTargetControls();
            UpdateExtraLocalPathButtonStates();
            EnsureSuccessStoryImportPathDefault();
            UpdateSuccessStoryImportStatus("Ready.");
        }

        private void LocalSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
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

        private void SteamUserdataAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            if (_localSettings == null)
            {
                return;
            }

            if (TryAutoDetectSteamBasePath(out var detectedSteamBasePath))
            {
                _localSettings.SteamUserdataPath = detectedSteamBasePath;
                RefreshAvailableSteamAppCacheUsers();
                _playniteApi?.Dialogs?.ShowMessage(
                    $"Detected Steam path: {detectedSteamBasePath}",
                    "Playnite Achievements",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _playniteApi?.Dialogs?.ShowMessage(
                "Steam path was not detected automatically. Please set it manually using Browse.",
                "Playnite Achievements",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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

        private void BrowseExcludedLocalPath_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            PendingExcludedLocalPathTextBox.Text = selectedPath;
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

        private static ImageSource CreatePreviewImageSource(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
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

            return CreateDefaultLocalProviderImageSource();
        }

        private static ImageSource CreateDefaultLocalProviderImageSource()
        {
            try
            {
                var geometry = Application.Current?.TryFindResource(LocalProviderIconResourceKey) as Geometry;
                if (geometry == null)
                {
                    return null;
                }

                if (!(ColorConverter.ConvertFromString(LocalProviderColorHex) is Color color))
                {
                    return null;
                }

                var drawing = new GeometryDrawing
                {
                    Geometry = geometry,
                    Brush = new SolidColorBrush(color)
                };
                drawing.Freeze();

                var drawingImage = new DrawingImage(drawing);
                drawingImage.Freeze();
                return drawingImage;
            }
            catch
            {
                return null;
            }
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

        private void AddExcludedLocalPath_Click(object sender, RoutedEventArgs e)
        {
            var path = PendingExcludedLocalPathTextBox.Text?.Trim();
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

            if (ExcludedLocalPathEntries.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                PendingExcludedLocalPathTextBox.Clear();
                UpdateExtraLocalPathButtonStates();
                return;
            }

            ExcludedLocalPathEntries.Add(path);
            SyncExcludedLocalPathsToSettings();
            PendingExcludedLocalPathTextBox.Clear();
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

        private void RemoveExcludedLocalPath_Click(object sender, RoutedEventArgs e)
        {
            if (!(ExcludedLocalPathsList.SelectedItem is string selectedPath))
            {
                return;
            }

            ExcludedLocalPathEntries.Remove(selectedPath);
            SyncExcludedLocalPathsToSettings();
            UpdateExtraLocalPathButtonStates();
        }

        private void ImportExtraLocalGamesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pluginSettings == null)
            {
                return;
            }

            if (!TryShowImportTargetDialog(
                out var selectedTarget,
                out var customSourceName,
                out var metadataSourceId,
                out var steamAppCacheUserId,
                out var existingGameBehavior,
                out var includeFoldersWithoutAchievementFiles,
                out var iconRateLimitRetryMode,
                out var iconRateLimitRetryRounds))
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
                _localSettings.IncludeFoldersWithoutAchievementFilesOnImport = includeFoldersWithoutAchievementFiles;
                _localSettings.IconRateLimitRetryMode = iconRateLimitRetryMode;
                _localSettings.IconRateLimitRetryRounds = iconRateLimitRetryRounds;
                RefreshImportedGameTargetControls();
            }

            var roots = ExtraLocalPathEntries
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            StartLocalImport(
                roots,
                selectedTarget,
                customSourceName,
                metadataSourceId,
                existingGameBehavior,
                steamAppCacheUserId,
                includeFoldersWithoutAchievementFiles,
                iconRateLimitRetryMode,
                iconRateLimitRetryRounds);
        }

        private void BrowseSuccessStoryImportPath_Click(object sender, RoutedEventArgs e)
        {
            EnsureSuccessStoryImportPathDefault();

            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            SuccessStoryImportPath = selectedPath;
            UpdateSuccessStoryImportStatus("Path selected.");
        }

        private void ImportSuccessStoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_localImportCts != null)
            {
                return;
            }

            EnsureSuccessStoryImportPathDefault();
            var importPath = (SuccessStoryImportPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(importPath) || !Directory.Exists(importPath))
            {
                var message = string.IsNullOrWhiteSpace(importPath)
                    ? "SuccessStory folder path is empty."
                    : $"SuccessStory folder does not exist: {importPath}";

                _playniteApi?.Dialogs?.ShowMessage(
                    message,
                    "Playnite Achievements",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateSuccessStoryImportStatus(message);
                return;
            }

            StartSuccessStoryImport(importPath);
        }

        private void PendingExtraLocalPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateExtraLocalPathButtonStates();
        }

        private void PendingExcludedLocalPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateExtraLocalPathButtonStates();
        }

        private void ExtraLocalPathsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateExtraLocalPathButtonStates();
        }

        private void ExcludedLocalPathsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        private void RefreshExcludedLocalPathEntries()
        {
            ExcludedLocalPathEntries.Clear();
            if (_localSettings == null)
            {
                return;
            }

            foreach (var path in _localSettings.GetExcludedLocalPathEntries())
            {
                ExcludedLocalPathEntries.Add(path);
            }
        }

        private void SyncExtraLocalPathsToSettings()
        {
            _localSettings?.SetExtraLocalPathEntries(ExtraLocalPathEntries);
        }

        private void SyncExcludedLocalPathsToSettings()
        {
            _localSettings?.SetExcludedLocalPathEntries(ExcludedLocalPathEntries);
        }

        private void UpdateExtraLocalPathButtonStates()
        {
            if (AddExtraLocalPathButton != null)
            {
                AddExtraLocalPathButton.IsEnabled = !string.IsNullOrWhiteSpace(PendingExtraLocalPathTextBox?.Text);
            }

            if (AddExcludedLocalPathButton != null)
            {
                AddExcludedLocalPathButton.IsEnabled = !string.IsNullOrWhiteSpace(PendingExcludedLocalPathTextBox?.Text);
            }

            if (RemoveExtraLocalPathButton != null)
            {
                RemoveExtraLocalPathButton.IsEnabled = ExtraLocalPathsList?.SelectedItem is string;
            }

            if (RemoveExcludedLocalPathButton != null)
            {
                RemoveExcludedLocalPathButton.IsEnabled = ExcludedLocalPathsList?.SelectedItem is string;
            }

            if (ImportExtraLocalGamesButton != null)
            {
                ImportExtraLocalGamesButton.IsEnabled = _localImportCts == null;
            }

            if (ImportSuccessStoryButton != null)
            {
                ImportSuccessStoryButton.IsEnabled = _localImportCts == null;
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

            var normalizedSelectedId = ImportedGameMetadataSourceCatalog.NormalizeMetadataSourceId(
                _playniteApi,
                _logger,
                _localSettings.ImportedGameMetadataSourceId);
            if (!string.Equals(_localSettings.ImportedGameMetadataSourceId, normalizedSelectedId, StringComparison.OrdinalIgnoreCase))
            {
                _localSettings.ImportedGameMetadataSourceId = normalizedSelectedId;
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
            foreach (var registryPath in GetSteamRegistryCandidatePaths())
            {
                AddCandidate(registryPath);
            }

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

        private bool TryAutoDetectSteamBasePath(out string detectedSteamBasePath)
        {
            detectedSteamBasePath = null;

            var rankedCandidates = new List<(string Path, int Score)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in GetSteamBaseCandidatePaths())
            {
                var normalized = NormalizeSteamBasePath(candidate);
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                var score = 0;
                if (Directory.Exists(Path.Combine(normalized, "userdata")))
                {
                    score += 2;
                }

                if (Directory.Exists(Path.Combine(normalized, "appcache", "stats")))
                {
                    score += 2;
                }

                if (File.Exists(Path.Combine(normalized, "steam.exe")))
                {
                    score += 1;
                }

                rankedCandidates.Add((normalized, score));
            }

            var best = rankedCandidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(best.Path))
            {
                return false;
            }

            detectedSteamBasePath = best.Path;
            return true;
        }

        private static string NormalizeSteamBasePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            if (string.IsNullOrWhiteSpace(expanded))
            {
                return null;
            }

            if (string.Equals(Path.GetFileName(expanded), "steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                expanded = Directory.GetParent(expanded)?.FullName;
            }

            if (string.Equals(Path.GetFileName(expanded), "userdata", StringComparison.OrdinalIgnoreCase))
            {
                expanded = Directory.GetParent(expanded)?.FullName;
            }

            if (string.Equals(Path.GetFileName(expanded), "stats", StringComparison.OrdinalIgnoreCase))
            {
                var appcachePath = Directory.GetParent(expanded);
                if (string.Equals(appcachePath?.Name, "appcache", StringComparison.OrdinalIgnoreCase))
                {
                    expanded = appcachePath?.Parent?.FullName;
                }
            }

            if (string.Equals(Path.GetFileName(expanded), "appcache", StringComparison.OrdinalIgnoreCase))
            {
                expanded = Directory.GetParent(expanded)?.FullName;
            }

            if (string.IsNullOrWhiteSpace(expanded))
            {
                return null;
            }

            try
            {
                var fullPath = Path.GetFullPath(expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return Directory.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> GetSteamRegistryCandidatePaths()
        {
            var results = new List<string>();
            var candidateValueNames = new[]
            {
                "SteamPath",
                "InstallPath",
                "InstallLocation"
            };

            var keyPaths = new[]
            {
                @"Software\Valve\Steam",
                @"Software\WOW6432Node\Valve\Steam",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1"
            };

            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var keyPath in keyPaths)
                {
                    RegistryKey key = null;
                    try
                    {
                        key = hive.OpenSubKey(keyPath);
                        if (key == null)
                        {
                            continue;
                        }

                        foreach (var valueName in candidateValueNames)
                        {
                            var raw = key.GetValue(valueName) as string;
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                results.Add(raw.Trim());
                            }
                        }

                        var steamExe = key.GetValue("SteamExe") as string;
                        if (!string.IsNullOrWhiteSpace(steamExe))
                        {
                            var parent = Directory.GetParent(steamExe.Trim())?.FullName;
                            if (!string.IsNullOrWhiteSpace(parent))
                            {
                                results.Add(parent);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore registry access failures and continue with filesystem candidates.
                    }
                    finally
                    {
                        key?.Dispose();
                    }
                }
            }

            return results;
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
            out LocalExistingGameImportBehavior existingGameBehavior,
            out bool includeFoldersWithoutAchievementFiles,
            out LocalIconRateLimitRetryMode iconRateLimitRetryMode,
            out int iconRateLimitRetryRounds)
        {
            selectedTarget = _localSettings?.ImportedGameLibraryTarget ?? LocalImportedGameLibraryTarget.None;
            customSourceName = _localSettings?.ImportedGameCustomSourceName ?? string.Empty;
            metadataSourceId = _localSettings?.ImportedGameMetadataSourceId ?? string.Empty;
            steamAppCacheUserId = _localSettings?.SteamAppCacheUserId ?? string.Empty;
            existingGameBehavior = _localSettings?.ExistingGameImportBehavior ?? LocalExistingGameImportBehavior.OverwriteExisting;
            includeFoldersWithoutAchievementFiles = _localSettings?.IncludeFoldersWithoutAchievementFilesOnImport == true;
            iconRateLimitRetryMode = _localSettings?.IconRateLimitRetryMode ?? LocalIconRateLimitRetryMode.FixedRounds;
            iconRateLimitRetryRounds = Math.Max(1, _localSettings?.IconRateLimitRetryRounds ?? 2);

            var dialog = new LocalImportTargetDialog(
                selectedTarget,
                customSourceName,
                metadataSourceId,
                steamAppCacheUserId,
                includeFoldersWithoutAchievementFiles,
                existingGameBehavior,
                iconRateLimitRetryMode,
                iconRateLimitRetryRounds,
                AvailableSourceNames,
                AvailableMetadataSources,
                AvailableSteamAppCacheUsers);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                "Import Local Games",
                dialog,
                new WindowOptions
                {
                    Width = 560,
                    Height = 450,
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
            includeFoldersWithoutAchievementFiles = dialog.IncludeFoldersWithoutAchievementFiles;
            iconRateLimitRetryMode = dialog.IconRateLimitRetryMode;
            iconRateLimitRetryRounds = Math.Max(1, dialog.IconRateLimitRetryRounds);
            return true;
        }

        private void StartLocalImport(
            System.Collections.Generic.IReadOnlyCollection<string> roots,
            LocalImportedGameLibraryTarget selectedTarget,
            string customSourceName,
            string metadataSourceId,
            LocalExistingGameImportBehavior existingGameBehavior,
            string steamAppCacheUserId = null,
            bool includeFoldersWithoutAchievementFiles = false,
            LocalIconRateLimitRetryMode iconRateLimitRetryMode = LocalIconRateLimitRetryMode.FixedRounds,
            int iconRateLimitRetryRounds = 2)
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
                    Height = 360,
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
                    var importer = new LocalFolderGamesImporter(_playniteApi, _pluginSettings, _logger, _localSettings);
                    var result = await importer.ImportFromRootsAsync(
                        roots,
                        selectedTarget,
                        customSourceName,
                        metadataSourceId,
                        existingGameBehavior,
                        includeFoldersWithoutAchievementFiles,
                        _localImportCts.Token,
                        progress,
                        steamAppCacheUserIdOverride: steamAppCacheUserId).ConfigureAwait(false);

                    var targetLabel = selectedTarget == LocalImportedGameLibraryTarget.CustomSource
                        ? $"custom source '{customSourceName?.Trim()}'"
                        : (selectedTarget == LocalImportedGameLibraryTarget.Steam ? "Steam library" : "None/manual library");
                    var metadataLabel = AvailableMetadataSources.FirstOrDefault(option => string.Equals(option.Id, metadataSourceId ?? string.Empty, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "Automatic";
                    var existingBehaviorLabel = existingGameBehavior == LocalExistingGameImportBehavior.SkipExisting ? "skip existing" : "overwrite existing";
                    var folderModeLabel = includeFoldersWithoutAchievementFiles ? "including schema-only folders" : "achievement files required";

                    var pendingRateLimitedIconAppIds = new HashSet<int>(result.RateLimitedMissingIconAppIds ?? new List<int>());
                    var recoveredOnRetryCount = 0;

                    var retryRoundsPerformed = 0;
                    var maxRetryRounds = iconRateLimitRetryMode == LocalIconRateLimitRetryMode.FixedRounds
                        ? Math.Max(1, iconRateLimitRetryRounds)
                        : int.MaxValue;

                    while (pendingRateLimitedIconAppIds.Count > 0 && retryRoundsPerformed < maxRetryRounds)
                    {
                        if (iconRateLimitRetryMode == LocalIconRateLimitRetryMode.None)
                        {
                            break;
                        }

                        retryRoundsPerformed++;

                        Dispatcher.Invoke(() =>
                        {
                            progressControl.Update(
                                0d,
                                $"Retrying missing icons - round {retryRoundsPerformed}",
                                $"Retry queue contains {pendingRateLimitedIconAppIds.Count} game(s). Cancel to stop retries.");
                            UpdateImportStatus($"Retrying missing icons - round {retryRoundsPerformed}");
                        });

                        var retryProgress = new Progress<LocalFolderGamesImporter.LocalImportProgressInfo>(report =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                progressControl.Update(report?.Percent ?? 0d, report?.Message, report?.Detail);
                                UpdateImportStatus(report?.Message);
                            });
                        });

                        var retryResult = await importer.RetryMissingIconsFromRateLimitAsync(
                            pendingRateLimitedIconAppIds,
                            selectedTarget,
                            customSourceName,
                            maxAttemptsPerApp: 3,
                            waitDuringBackoff: true,
                            _localImportCts.Token,
                            retryProgress).ConfigureAwait(false);

                        recoveredOnRetryCount += retryResult.RecoveredCount;
                        pendingRateLimitedIconAppIds = new HashSet<int>(retryResult.RemainingRateLimitedAppIds ?? new List<int>());
                    }

                    var summary = $"Imported {result.ImportedCount} new games, reused {result.LinkedExistingCount} existing games, skipped {result.SkippedCount}, failed {result.FailedCount} across {result.UniqueAppIdCount} detected App IDs for {targetLabel} using metadata source '{metadataLabel}' with existing-game behavior '{existingBehaviorLabel}' and folder mode '{folderModeLabel}'.";
                    if (result.RejectedSteamAppCount > 0)
                    {
                        summary += $" Rejected {result.RejectedSteamAppCount} non-importable Steam App IDs.";
                    }

                    if (result.RateLimitedMissingIconAppIds.Count > 0)
                    {
                        summary += $" Steam rate-limited icon requests left {result.RateLimitedMissingIconAppIds.Count} icon(s) missing during the initial import.";
                    }

                    if (recoveredOnRetryCount > 0)
                    {
                        summary += $" Recovered {recoveredOnRetryCount} icon(s) via retry.";
                    }

                    if (pendingRateLimitedIconAppIds.Count > 0)
                    {
                        summary += $" {pendingRateLimitedIconAppIds.Count} icon(s) are still pending rate-limit recovery.";
                    }

                    var finalMissingIconCount = _playniteApi.Database.Games
                        .Where(game => game != null && game.PluginId == Guid.Empty && !string.IsNullOrWhiteSpace(game.GameId) && int.TryParse(game.GameId, out _))
                        .Count(game => string.IsNullOrWhiteSpace(game.Icon));

                    Dispatcher.Invoke(() =>
                    {
                        if (result.RateLimitedMissingIconAppIds.Count > 0)
                        {
                            var retryModeLabel = iconRateLimitRetryMode == LocalIconRateLimitRetryMode.None
                                ? "None"
                                : (iconRateLimitRetryMode == LocalIconRateLimitRetryMode.Infinite ? "Infinite" : $"Fixed ({Math.Max(1, iconRateLimitRetryRounds)})");
                            _playniteApi.Dialogs.ShowMessage(
                                $"Rate-limit icon summary:{Environment.NewLine}" +
                                $"- Missed in initial import: {result.RateLimitedMissingIconAppIds.Count}{Environment.NewLine}" +
                                $"- Recovered by retry: {recoveredOnRetryCount}{Environment.NewLine}" +
                                $"- Still missing from rate-limit list: {pendingRateLimitedIconAppIds.Count}{Environment.NewLine}" +
                                $"- Total imported games still without icon: {finalMissingIconCount}{Environment.NewLine}" +
                                $"- Retry mode used: {retryModeLabel}",
                                "Local Import Icon Summary",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    });

                    Dispatcher.Invoke(() =>
                    {
                        progressControl.SetCopyableReport(result.RejectedSteamAppReport);
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

        private void EnsureSuccessStoryImportPathDefault()
        {
            if (!string.IsNullOrWhiteSpace(SuccessStoryImportPath))
            {
                return;
            }

            var extensionsDataPath = _playniteApi?.Paths?.ExtensionsDataPath;
            if (string.IsNullOrWhiteSpace(extensionsDataPath))
            {
                return;
            }

            SuccessStoryImportPath = Path.Combine(
                extensionsDataPath,
                SuccessStoryExtensionId,
                SuccessStoryFolderName);
        }

        private void StartSuccessStoryImport(string importPath)
        {
            _localImportCts?.Dispose();
            _localImportCts = new CancellationTokenSource();
            UpdateExtraLocalPathButtonStates();

            var progressControl = new LocalImportProgressControl
            {
                DialogTitle = "Importing SuccessStory unlocks"
            };

            var window = PlayniteUiProvider.CreateExtensionWindow(
                "Import SuccessStory unlocks",
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

            UpdateSuccessStoryImportStatus("Starting SuccessStory import...");
            window.Show();

            var progress = new Progress<LocalSuccessStoryImportService.SuccessStoryImportProgressInfo>(report =>
            {
                progressControl.Update(report?.Percent ?? 0d, report?.Message, report?.Detail);
                UpdateSuccessStoryImportStatus(report?.Message);
            });

            Task.Run(async () =>
            {
                try
                {
                    var plugin = _pluginSettings?._plugin;
                    var cacheManager = plugin?.RefreshRuntime?.Cache;
                    var achievementDataService = plugin?.AchievementDataService;

                    if (cacheManager == null || achievementDataService == null)
                    {
                        throw new InvalidOperationException("PlayniteAchievements services are not ready yet.");
                    }

                    var importer = new LocalSuccessStoryImportService(
                        _playniteApi,
                        cacheManager,
                        achievementDataService,
                        _logger,
                        _localSettings?.ImportedGameMetadataSourceId);

                    var result = await importer.ImportFromFolderAsync(
                        importPath,
                        _localImportCts.Token,
                        progress).ConfigureAwait(false);

                    var summary = result.BuildSummary();
                    Dispatcher.Invoke(() =>
                    {
                        progressControl.SetCopyableReport(summary);
                        progressControl.MarkCompleted(summary);
                        UpdateSuccessStoryImportStatus(summary);
                    });
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.Invoke(() =>
                    {
                        const string message = "SuccessStory import cancelled.";
                        progressControl.MarkCancelled(message);
                        UpdateSuccessStoryImportStatus(message);
                    });
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed importing unlock states from SuccessStory files.");
                    Dispatcher.Invoke(() =>
                    {
                        var message = $"SuccessStory import failed: {ex.Message}";
                        progressControl.MarkFailed(message);
                        UpdateSuccessStoryImportStatus(message);
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

        private void UpdateSuccessStoryImportStatus(string message)
        {
            if (ImportSuccessStoryStatusTextBlock != null)
            {
                ImportSuccessStoryStatusTextBlock.Text = message ?? string.Empty;
            }
        }

        private static void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
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
