using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.ThemeMigration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Shared coordinator for the theme Migration and Revert pages. Owns the discovery and
    /// migration services, the theme lists, the selection state, and the custom per-control
    /// options. Both pages bind to a single instance via their DataContext.
    /// </summary>
    internal sealed class ThemeMigrationController : ObservableObject
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ThemeDiscoveryService _themeDiscovery;
        private readonly ThemeMigrationService _themeMigration;
        private bool _themesLoaded;
        private string _selectedThemePath = string.Empty;
        private string _selectedRevertThemePath = string.Empty;
        private bool _hasThemesToMigrate;
        private bool _hasRevertableThemes;
        private bool _showNoThemesMessage = true;
        private bool _showNoRevertableThemesMessage = true;

        public ThemeMigrationController(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;

            _themeDiscovery = new ThemeDiscoveryService(_logger, _plugin.PlayniteApi);
            _themeMigration = new ThemeMigrationService(
                _logger,
                _settings,
                () => _plugin.SavePluginSettings(_settings));

            InitializeCustomOptions();
        }

        public ObservableCollection<ThemeDiscoveryService.ThemeInfo> AvailableThemes { get; } =
            new ObservableCollection<ThemeDiscoveryService.ThemeInfo>();

        public ObservableCollection<ThemeDiscoveryService.ThemeInfo> RevertableThemes { get; } =
            new ObservableCollection<ThemeDiscoveryService.ThemeInfo>();

        public ObservableCollection<ThemeMigrationElementOption> CustomOptions { get; } =
            new ObservableCollection<ThemeMigrationElementOption>();

        public string SelectedThemePath
        {
            get => _selectedThemePath;
            set => SetValue(ref _selectedThemePath, value);
        }

        public string SelectedRevertThemePath
        {
            get => _selectedRevertThemePath;
            set => SetValue(ref _selectedRevertThemePath, value);
        }

        public bool HasThemesToMigrate
        {
            get => _hasThemesToMigrate;
            private set => SetValue(ref _hasThemesToMigrate, value);
        }

        public bool HasRevertableThemes
        {
            get => _hasRevertableThemes;
            private set => SetValue(ref _hasRevertableThemes, value);
        }

        public bool ShowNoThemesMessage
        {
            get => _showNoThemesMessage;
            private set => SetValue(ref _showNoThemesMessage, value);
        }

        public bool ShowNoRevertableThemesMessage
        {
            get => _showNoRevertableThemesMessage;
            private set => SetValue(ref _showNoRevertableThemesMessage, value);
        }

        /// <summary>
        /// Loads the theme lists on first use. Called from each page's Loaded event so whichever
        /// page is shown first triggers the discovery.
        /// </summary>
        public void EnsureThemesLoaded()
        {
            if (_themesLoaded)
            {
                return;
            }

            LoadThemes();
        }

        private void LoadThemes()
        {
            try
            {
                AvailableThemes.Clear();
                RevertableThemes.Clear();

                var themesPath = _themeDiscovery.GetDefaultThemesPath();
                if (string.IsNullOrEmpty(themesPath))
                {
                    _logger?.Info("No themes path found for theme migration.");
                    _themesLoaded = true;
                    UpdateThemeMigrationState();
                    return;
                }

                var cache = _settings?.Persisted?.ThemeMigrationVersionCache;
                var themes = _themeDiscovery.DiscoverThemes(themesPath, cache);

                // Themes that need migration (no backup, has SuccessStory)
                foreach (var theme in themes.Where(t => t.NeedsMigration))
                {
                    AvailableThemes.Add(theme);
                }

                // Themes that can be reverted (has backup)
                foreach (var theme in themes.Where(t => t.HasBackup))
                {
                    RevertableThemes.Add(theme);
                }

                UpdateThemeMigrationState();
                _themesLoaded = true;

                _logger?.Info($"Loaded {AvailableThemes.Count} themes to migrate, {RevertableThemes.Count} themes to revert.");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load themes for migration.");
            }
        }

        private void UpdateThemeMigrationState()
        {
            var hasThemes = AvailableThemes.Count > 0;
            var hasRevertable = RevertableThemes.Count > 0;

            HasThemesToMigrate = hasThemes;
            HasRevertableThemes = hasRevertable;
            ShowNoThemesMessage = !hasThemes;
            ShowNoRevertableThemesMessage = !hasRevertable;
        }

        public Task MigrateCustomAsync()
        {
            return MigrateAsync(MigrationMode.Custom, BuildCustomMigrationSelection());
        }

        public async Task MigrateAsync(MigrationMode mode, CustomMigrationSelection customSelection = null)
        {
            if (string.IsNullOrWhiteSpace(SelectedThemePath))
            {
                _logger?.Warn("Migrate clicked but no theme selected.");
                return;
            }

            _logger?.Info($"User requested {mode} theme migration for: {SelectedThemePath}");

            try
            {
                var result = await _themeMigration.MigrateThemeAsync(SelectedThemePath, mode, customSelection);

                if (result.Success)
                {
                    _logger?.Info($"Theme migration ({mode}) successful: {SelectedThemePath}");

                    // Only show restart dialog if files were actually modified
                    if (result.FilesBackedUp > 0)
                    {
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            $"{result.Message}{Environment.NewLine}{Environment.NewLine}{L("LOCPlayAch_ThemeMigration_RestartRequired")}",
                            L("LOCPlayAch_ThemeMigration_Title"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        // No changes were made, just show info message
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            result.Message,
                            L("LOCPlayAch_ThemeMigration_Title"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }

                    // Reload themes to update the lists
                    LoadThemes();
                }
                else
                {
                    _logger?.Warn($"Theme migration failed: {result.Message}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to execute theme migration.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    L("LOCPlayAch_ThemeMigration_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public async Task RevertAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedRevertThemePath))
            {
                _logger?.Warn("Revert clicked but no theme selected.");
                return;
            }

            _logger?.Info($"User requested theme revert for: {SelectedRevertThemePath}");

            try
            {
                var result = await _themeMigration.RevertThemeAsync(SelectedRevertThemePath);

                if (result.Success)
                {
                    _logger?.Info($"Theme revert successful: {SelectedRevertThemePath}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Revert"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // Reload themes to update the lists
                    LoadThemes();
                }
                else
                {
                    _logger?.Warn($"Theme revert failed: {result.Message}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Revert"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to execute theme revert.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    L("LOCPlayAch_ThemeMigration_Revert"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public void SetAllCustomOptions(bool isModern)
        {
            foreach (var option in CustomOptions)
            {
                option.IsModern = isModern;
            }
        }

        private CustomMigrationSelection BuildCustomMigrationSelection()
        {
            var modernControlNames = CustomOptions
                .Where(option => option.IsModern)
                .Select(option => option.Key)
                .ToList();

            return new CustomMigrationSelection(modernControlNames, modernizeBindings: true);
        }

        private void InitializeCustomOptions()
        {
            CustomOptions.Clear();

            CustomOptions.Add(CreateControlOption(
                "PluginButton",
                "LOCPlayAch_Settings_ButtonPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginChart",
                "LOCPlayAch_Settings_BarChartPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginCompactList",
                "LOCPlayAch_Settings_CompactListPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginCompactLocked",
                "LOCPlayAch_Settings_CompactLockedListPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginCompactUnlocked",
                "LOCPlayAch_Settings_CompactUnlockedListPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginList",
                "LOCPlayAch_Settings_AchievementDataGridPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginProgressBar",
                "LOCPlayAch_Settings_ProgressBarPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginUserStats",
                "LOCPlayAch_Settings_StatsPreview"));
            CustomOptions.Add(CreateControlOption(
                "PluginViewItem",
                "LOCPlayAch_Settings_ViewItemPreview"));
        }

        private static ThemeMigrationElementOption CreateControlOption(
            string key,
            string resourceKey)
        {
            return new ThemeMigrationElementOption(
                key,
                L(resourceKey),
                isBindingOption: false,
                isModern: true);
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static string LF(string key, params object[] args)
        {
            return string.Format(L(key), args);
        }
    }
}
