using System;
using System.Collections.Generic;
using System.IO;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services;
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
                var fullyMigratedJson = GameCustomDataStore.MigrateLegacyConfig(migratedJson);

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
            // Create a clone for editing
            _editingClone = Playnite.SDK.Data.Serialization.GetClone(Settings);
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

            // Notify listeners that settings have been saved (e.g., to refresh provider status in landing page)
            PlayniteAchievementsPlugin.NotifySettingsSaved();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return errors.Count == 0;
        }

        private PlayniteAchievementsSettings _editingClone;
    }
}
