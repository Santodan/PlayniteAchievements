using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Logging;
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

            // Load saved settings or create new
            var savedSettings = _plugin.LoadPluginSettings<PlayniteAchievementsSettings>();
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

        // ============================================================
        // ISettings IMPLEMENTATION
        // These methods delegate to the nested Settings object
        // ============================================================

        public void BeginEdit()
        {
            // Create a clone for editing
            _editingClone = Playnite.SDK.Data.Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            // Revert to the cloned settings
            if (_editingClone != null)
            {
                Settings.CopyPersistedFrom(_editingClone);
            }
        }

        public void EndEdit()
        {
            // Save the settings via the plugin
            _plugin.SavePluginSettings(Settings);

            // Sync provider registry from the updated settings
            _plugin.ProviderRegistry?.SyncFromSettings(Settings.Persisted);

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
