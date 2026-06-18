using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Implements ISettings to integrate with Playnite''s settings system and theme PluginSettings markup extension.
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
        /// Falls back to backup files if config.json is missing, empty, unreadable, or malformed:
        /// - config.json.bak (post-write mirror of latest successful config)
        /// - config.json.pre.bak (pre-write safety backup)
        /// </summary>
        private PlayniteAchievementsSettings LoadSettingsWithMigration()
        {
            try
            {
                // Get the settings file path (Playnite uses config.json)
                var pluginUserDataPath = _plugin.GetPluginUserDataPath();
                var settingsFilePath = Path.Combine(pluginUserDataPath, "config.json");
                var bakPath = settingsFilePath + ".bak";
                var preBakPath = settingsFilePath + ".pre.bak";

                if (!File.Exists(settingsFilePath))
                {
                    // No config.json – attempt recovery from backups.
                    if (!TryRecoverFromBackups(
                            reason: "config.json is missing",
                            settingsFilePath,
                            bakPath,
                            preBakPath,
                            out _))
                    {
                        // Genuinely first run (or no backup available) – try direct load.
                        return _plugin.LoadPluginSettings<PlayniteAchievementsSettings>();
                    }
                }

                // Read raw JSON, with crash-recovery fallback to the rolling backup.
                string rawJson;
                try
                {
                    rawJson = ReadRequiredJson(settingsFilePath, "config.json");
                }
                catch (Exception readEx)
                {
                    _logger.Warn(readEx, "config.json read failed; attempting backup recovery.");
                    if (!TryRecoverFromBackups(
                            reason: "config.json is unreadable/empty",
                            settingsFilePath,
                            bakPath,
                            preBakPath,
                            out rawJson))
                    {
                        _logger.Error("No usable backup available; starting with defaults.");
                        return null;
                    }
                }

                // Parse/migration failures are also recoverable from .bak.
                if (TryDeserializeAndMigrate(rawJson, settingsFilePath, pluginUserDataPath, out var loaded, out var parseEx))
                {
                    return loaded;
                }

                _logger.Warn(parseEx, "config.json parse/migration failed; attempting backup recovery.");
                if (!TryRecoverFromBackups(
                        reason: "config.json parse/migration failed",
                        settingsFilePath,
                        bakPath,
                        preBakPath,
                        out var bakJson))
                {
                    _logger.Error("No usable backup available after parse/migration failure; starting with defaults.");
                    return null;
                }

                try
                {
                    if (!TryDeserializeAndMigrate(bakJson, settingsFilePath, pluginUserDataPath, out loaded, out var bakParseEx))
                    {
                        throw bakParseEx ?? new InvalidOperationException("Recovered backup parse/migration failed.");
                    }

                    _logger.Info("Recovered settings from backup after config.json parse/migration failure.");
                    return loaded;
                }
                catch (Exception bakEx)
                {
                    _logger.Error(bakEx, "Recovery from backup failed after parse/migration failure; starting with defaults.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load settings with migration, falling back to direct load.");
                try { return _plugin.LoadPluginSettings<PlayniteAchievementsSettings>(); }
                catch { return null; }
            }
        }

        private static string ReadRequiredJson(string path, string label)
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException($"{label} is empty.");
            }

            return json;
        }

        private bool TryRecoverFromBackups(
            string reason,
            string settingsFilePath,
            string bakPath,
            string preBakPath,
            out string recoveredJson)
        {
            recoveredJson = null;

            if (TryRecoverFromSingleBackup(reason, settingsFilePath, bakPath, "config.json.bak", out recoveredJson))
            {
                return true;
            }

            return TryRecoverFromSingleBackup(reason, settingsFilePath, preBakPath, "config.json.pre.bak", out recoveredJson);
        }

        private bool TryRecoverFromSingleBackup(
            string reason,
            string settingsFilePath,
            string backupPath,
            string backupLabel,
            out string recoveredJson)
        {
            recoveredJson = null;
            if (!File.Exists(backupPath))
            {
                _logger.Debug($"Backup not found: {backupLabel} (reason: {reason}).");
                return false;
            }

            try
            {
                recoveredJson = ReadRequiredJson(backupPath, backupLabel);
                File.Copy(backupPath, settingsFilePath, overwrite: true);
                _logger.Info($"Recovered config.json from {backupLabel}. Reason: {reason}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed recovering from {backupLabel}. Reason: {reason}");
                recoveredJson = null;
                return false;
            }
        }

        private bool TryDeserializeAndMigrate(
            string rawJson,
            string settingsFilePath,
            string pluginUserDataPath,
            out PlayniteAchievementsSettings settings,
            out Exception error)
        {
            settings = null;
            error = null;

            try
            {
                var migratedJson = ProviderSettingsMigration.MigrateFromJson(rawJson);
                var overviewMigratedJson = OverviewSettingsMigration.MigrateFromJson(migratedJson);
                var fullyMigratedJson = GameCustomDataStore.MigrateLegacyConfig(overviewMigratedJson);

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
                        BackupHelper.WritePreWriteBackup(settingsFilePath);
                        File.WriteAllText(settingsFilePath, fullyMigratedJson);
                        BackupHelper.WritePostWriteMirror(settingsFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            ex,
                            "Failed to create config migration backup or persist migrated config. Using migrated settings in memory for this session.");
                    }
                }

                settings = Playnite.SDK.Data.Serialization.FromJson<PlayniteAchievementsSettings>(fullyMigratedJson);
                return settings != null;
            }
            catch (Exception ex)
            {
                error = ex;
                settings = null;
                return false;
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
                // Preserve provider settings that may have been updated by PersistSettingsForUi
                // while the dialog was open (e.g. folder overrides set from GameOptions).
                // Those are kept; only the non-provider persisted settings are reverted.
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

            // Save the reverted state to disk so that the in-memory state and the
            // on-disk state are always in sync after a cancel.  Without this, any
            // subsequent background save (column-width resize, ForkReleaseMonitor,
            // etc.) would write the reverted (pre-dialog) in-memory state over
            // whatever PersistSettingsForUi had already flushed to disk while the
            // dialog was open, effectively undoing those changes.
            try
            {
                _plugin.ProviderRegistry?.PersistAllProviderSettings(false);
                _plugin.SaveSettingsSafely(Settings);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to persist reverted settings after CancelEdit.");
            }
        }

        public void EndEdit()
        {
            _plugin.ProviderRegistry?.CommitEditSession(false);
            _plugin.ProviderRegistry?.PersistAllProviderSettings(false);

            // Save the settings via the plugin
            _plugin.SaveSettingsSafely(Settings);

            // Sync provider registry from the updated settings
            _plugin.ProviderRegistry?.SyncFromSettings(Settings.Persisted);
            GameCustomDataStore?.SyncRuntimeCaches();

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
            var viewLabel = L("LOCPlayAch_Menu_ViewAchievements", "View Achievements");
            var manageLabel = L("LOCPlayAch_Menu_ManageAchievements", "Manage Achievements");
            var overviewLabel = L("LOCPlayAch_Menu_OpenOverview", "Achievements Overview");
            var invalidMessage = L(
                "LOCPlayAch_Hotkeys_InvalidShortcut",
                "Unsupported shortcut. Press a letter, digit, function key, or a modified shortcut.");
            var duplicateMessage = L("LOCPlayAch_Hotkeys_DuplicateShortcut", "That shortcut is already assigned.");

            var viewValid = TryValidateHotkey(viewLabel, persisted.ViewAchievementsHotkey, invalidMessage, errors, out var viewGesture);
            var manageValid = TryValidateHotkey(manageLabel, persisted.ManageAchievementsHotkey, invalidMessage, errors, out var manageGesture);
            var overviewValid = TryValidateHotkey(overviewLabel, persisted.OverviewHotkey, invalidMessage, errors, out var overviewGesture);

            var assignedGestures = new List<AchievementHotkeyGesture>();
            AddDuplicateHotkeyError(viewValid, viewGesture, assignedGestures, duplicateMessage, errors);
            AddDuplicateHotkeyError(manageValid, manageGesture, assignedGestures, duplicateMessage, errors);
            AddDuplicateHotkeyError(overviewValid, overviewGesture, assignedGestures, duplicateMessage, errors);
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

        private static string L(string key, string fallback)
        {
            return ResourceProvider.GetString(key) ?? fallback;
        }

        private PlayniteAchievementsSettings _editingClone;
    }
}
