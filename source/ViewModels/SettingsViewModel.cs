using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.UI;
using Playnite.SDK;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// ViewModel for plugin settings.
    /// Handles loading settings and providing them to the plugin.
    /// Implements ISettings to integrate with Playnite's settings system and theme PluginSettings markup extension.
    /// </summary>
    public class PlayniteAchievementsSettingsViewModel : ObservableObject, ISettings
    {
        private readonly ILogger _logger = PluginLogger.GetLogger(nameof(PlayniteAchievementsSettingsViewModel));
        private readonly PlayniteAchievementsPlugin _plugin;
        private PlayniteAchievementsSettings settings;

        public GameCustomDataStore GameCustomDataStore { get; }

        /// <summary>
        /// The settings object containing all plugin configuration.
        /// </summary>
        public PlayniteAchievementsSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public PlayniteAchievementsSettingsViewModel(PlayniteAchievementsPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            GameCustomDataStore = new GameCustomDataStore(_plugin.GetPluginUserDataPath(), _logger);

            // Load saved settings with migration support
            var savedSettings = LoadSettingsWithMigration();
            if (savedSettings != null)
            {
                Settings = savedSettings;
                // Set the plugin reference for ISettings methods
                Settings._plugin = _plugin;
                // Initialize DontSerialize properties that are not persisted
                Settings.InitializeThemeProperties();
                _logger.Info($"Settings loaded from storage. EnablePeriodicUpdates={Settings.Persisted.EnablePeriodicUpdates}");
            }
            else
            {
                Settings = new PlayniteAchievementsSettings(_plugin);
                _logger.Info($"No saved settings found. Created new settings with defaults. EnablePeriodicUpdates={Settings.Persisted.EnablePeriodicUpdates}");
            }
        }

        /// <summary>
        /// Loads settings from storage, running migration if needed.
        /// </summary>
        private PlayniteAchievementsSettings LoadSettingsWithMigration()
        {
            try
            {
                // Get the settings file path (Playnite uses config.json)
                var pluginUserDataPath = _plugin.GetPluginUserDataPath();
                var settingsFilePath = Path.Combine(pluginUserDataPath, "config.json");

                if (!File.Exists(settingsFilePath))
                {
                    // Try loading directly as fallback
                    return _plugin.LoadPluginSettings<PlayniteAchievementsSettings>();
                }

                // Read raw JSON and run migration
                var rawJson = File.ReadAllText(settingsFilePath);
                var migratedJson = ProviderSettingsMigration.MigrateFromJson(rawJson);
                var overviewMigratedJson = OverviewSettingsMigration.MigrateFromJson(migratedJson);
                var gridOptionsMigratedJson = GridOptionsSettingsMigration.MigrateFromJson(overviewMigratedJson);
                var appearanceMigratedJson = AppearanceSettingsMigration.MigrateFromJson(gridOptionsMigratedJson);
                var fullyMigratedJson = GameCustomDataStore.MigrateLegacyConfig(appearanceMigratedJson);

                // If migration changed the JSON, save the migrated version
                if (fullyMigratedJson != rawJson)
                {
                    _logger.Info("Settings migration updated config.json.");

                    try
                    {
                        var backupPath = BackupHelper.CreateBackup(
                            pluginUserDataPath,
                            "config-migration",
                            settingsFilePath);
                        _logger.Info($"Config migration backup created: {backupPath}");
                        File.WriteAllText(settingsFilePath, fullyMigratedJson);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            ex,
                            "Failed to create config migration backup or persist migrated config. Using migrated settings in memory for this session.");
                    }
                }

                // Deserialize the (potentially migrated) JSON
                return Playnite.SDK.Data.Serialization.FromJson<PlayniteAchievementsSettings>(fullyMigratedJson);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load settings with migration, falling back to direct load.");
                return _plugin.LoadPluginSettings<PlayniteAchievementsSettings>();
            }
        }

        // ============================================================
        // ISettings IMPLEMENTATION
        // These methods delegate to the nested Settings object
        // ============================================================

        public void BeginEdit()
        {
            // Only persisted settings need an edit snapshot; runtime/theme data can be large.
            _editingClone = new PlayniteAchievementsSettings(_plugin);
            _editingClone.CopyPersistedFrom(Settings);
            _plugin.ProviderRegistry?.BeginEditSession();
        }

        public void CancelEdit()
        {
            // Revert to the cloned settings
            if (_editingClone != null)
            {
                var currentProviderSettings = Settings.Persisted?.ProviderSettings != null
                    ? Settings.Persisted.Clone().ProviderSettings
                    : null;

                Settings.CopyPersistedFrom(_editingClone);

                if (currentProviderSettings != null)
                {
                    Settings.Persisted.ProviderSettings = currentProviderSettings;
                }
            }

            _plugin.ProviderRegistry?.CancelEditSession();
            _plugin.ProviderRegistry?.SyncFromSettings(Settings.Persisted);
            GameCustomDataStore?.SyncRuntimeCaches();
            ApplyThemeResources();
        }

        public void EndEdit()
        {
            _plugin.ProviderRegistry?.CommitEditSession(false);
            _plugin.ProviderRegistry?.PersistAllProviderSettings(false);

            // Save the settings via the plugin
            _plugin.SavePluginSettings(Settings);

            // Sync provider registry from the updated settings
            _plugin.ProviderRegistry?.SyncFromSettings(Settings.Persisted);
            GameCustomDataStore?.SyncRuntimeCaches();
            ApplyThemeResources();

            // Notify listeners that settings have been saved (e.g., to refresh provider status in landing page)
            PlayniteAchievementsPlugin.NotifySettingsSaved();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            var persisted = Settings?.Persisted;
            if (persisted != null)
            {
                ValidateAchievementHotkeys(persisted, errors);
            }

            return errors.Count == 0;
        }

        private static void ValidateAchievementHotkeys(PersistedSettings persisted, List<string> errors)
        {
            var viewLabel = L("LOCPlayAch_Menu_ViewAchievements");
            var manageLabel = L("LOCPlayAch_Menu_ManageAchievements");
            var overviewLabel = L("LOCPlayAch_Menu_OpenOverview");
            var openSettingsLabel = L("LOCPlayAch_Landing_OpenSettings");
            var categoryModeLabel = L("LOCPlayAch_CategorySummaries_ToggleToolTip");
            var invalidMessage = L("LOCPlayAch_Hotkeys_InvalidShortcut");
            var duplicateMessage = L("LOCPlayAch_Hotkeys_DuplicateShortcut");

            var viewValid = TryValidateHotkey(viewLabel, persisted.ViewAchievementsHotkey, invalidMessage, errors, out var viewGesture);
            var manageValid = TryValidateHotkey(manageLabel, persisted.ManageAchievementsHotkey, invalidMessage, errors, out var manageGesture);
            var overviewValid = TryValidateHotkey(overviewLabel, persisted.OverviewHotkey, invalidMessage, errors, out var overviewGesture);
            var openSettingsValid = TryValidateHotkey(openSettingsLabel, persisted.OpenSettingsHotkey, invalidMessage, errors, out var openSettingsGesture);
            var categoryModeValid = TryValidateHotkey(categoryModeLabel, persisted.CategoryModeHotkey, invalidMessage, errors, out var categoryModeGesture);

            var assignedGestures = new List<AchievementHotkeyGesture>();
            AddDuplicateHotkeyError(viewValid, viewGesture, assignedGestures, duplicateMessage, errors);
            AddDuplicateHotkeyError(manageValid, manageGesture, assignedGestures, duplicateMessage, errors);
            AddDuplicateHotkeyError(overviewValid, overviewGesture, assignedGestures, duplicateMessage, errors);
            AddDuplicateHotkeyError(openSettingsValid, openSettingsGesture, assignedGestures, duplicateMessage, errors);
            AddDuplicateHotkeyError(categoryModeValid, categoryModeGesture, assignedGestures, duplicateMessage, errors);
        }

        private void ApplyThemeResources()
        {
            var resources = Application.Current?.Resources;
            if (resources != null)
            {
                PlayAchResourceService.Apply(
                    resources,
                    Settings?.Persisted?.ResourceOverrides,
                    Settings?.Persisted);
            }
        }

        private static void AddDuplicateHotkeyError(
            bool isValid,
            AchievementHotkeyGesture gesture,
            List<AchievementHotkeyGesture> assignedGestures,
            string duplicateMessage,
            List<string> errors)
        {
            if (!isValid || gesture == null || gesture.IsEmpty)
            {
                return;
            }

            if (assignedGestures.Any(existing => existing.Equals(gesture)))
            {
                if (!errors.Contains(duplicateMessage))
                {
                    errors.Add(duplicateMessage);
                }

                return;
            }

            assignedGestures.Add(gesture);
        }

        private static bool TryValidateHotkey(
            string label,
            string text,
            string invalidMessage,
            List<string> errors,
            out AchievementHotkeyGesture gesture)
        {
            gesture = AchievementHotkeyGesture.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (AchievementHotkeyGesture.TryParse(text, out gesture) &&
                gesture != null &&
                !gesture.IsEmpty)
            {
                return true;
            }

            errors.Add($"{label}: {invalidMessage}");
            gesture = AchievementHotkeyGesture.Empty;
            return false;
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private PlayniteAchievementsSettings _editingClone;
    }
}
