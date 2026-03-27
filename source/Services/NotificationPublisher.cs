using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services
{
    public class NotificationPublisher
    {
        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        public NotificationPublisher(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
        }

        public void ShowPeriodicStatus(string status)
        {
            if (_settings?.Persisted?.EnableNotifications != true || !_settings.Persisted.NotifyPeriodicUpdates)
                return;

            var title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
            var text = string.IsNullOrWhiteSpace(status)
                ? ResourceProvider.GetString("LOCPlayAch_Rebuild_Completed")
                : status;

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-Periodic-{Guid.NewGuid()}",
                    $"{title}\n{text}",
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show periodic notification.");
            }
        }

        public void ShowThemeAutoMigrated(string themeName)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
            {
                return;
            }

            var title = ResourceProvider.GetString("LOCPlayAch_ThemeMigration_AutoMigratedTitle");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Theme Auto-Migrated";
            }

            var displayName = string.IsNullOrWhiteSpace(themeName) ? "Theme" : themeName;

            var message = string.Format(
                ResourceProvider.GetString("LOCPlayAch_ThemeMigration_AutoMigratedMessage"),
                displayName);

            var restart = ResourceProvider.GetString("LOCPlayAch_ThemeMigration_AutoMigratedRestart");

            var text = $"{message}\n{restart}";

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-ThemeAutoMigrated-{Guid.NewGuid()}",
                    $"{title}\n{text}",
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show theme auto-migrated notification.");
            }
        }

        private static readonly string[] AllKnownProviderKeys = new[]
        {
            "Steam", "Epic", "GOG", "BattleNet", "EA", "PSN", "Xbox",
            "Xenia", "RPCS3", "ShadPS4", "RetroAchievements", "Exophase", "Manual"
        };

        private static string AuthNotificationId(string providerKey) => $"PlayAch-AuthFailed-{providerKey}";

        public void ShowProviderAuthFailed(List<string> providerKeys)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
                return;

            if (providerKeys == null || providerKeys.Count == 0)
                return;

            var pluginName = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");

            foreach (var providerKey in providerKeys)
            {
                try
                {
                    var providerName = GetLocalizedProviderName(providerKey);
                    var message = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Notification_ProviderAuthFailed"),
                        providerName);

                    var capturedKey = providerKey;
                    _api.Notifications.Add(new NotificationMessage(
                        AuthNotificationId(providerKey),
                        $"{pluginName}\n{message}",
                        NotificationType.Error,
                        () => OpenPluginSettingsForProvider(capturedKey)));
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed to show auth notification for {providerKey}.");
                }
            }
        }

        public void ClearProviderAuthNotifications(IEnumerable<string> providerKeys)
        {
            if (providerKeys == null)
                return;

            foreach (var providerKey in providerKeys)
            {
                try
                {
                    _api.Notifications.Remove(AuthNotificationId(providerKey));
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed to clear auth notification for {providerKey}.");
                }
            }
        }

        public void ClearAllProviderAuthNotifications()
        {
            ClearProviderAuthNotifications(AllKnownProviderKeys);
        }

        private static string GetLocalizedProviderName(string providerKey)
        {
            var resourceKey = $"LOCPlayAch_Provider_{providerKey}";
            var name = ResourceProvider.GetString(resourceKey);
            return !string.IsNullOrWhiteSpace(name) ? name : providerKey;
        }

        private void OpenPluginSettingsForProvider(string providerKey)
        {
            try
            {
                var plugin = PlayniteAchievementsPlugin.Instance;
                if (plugin == null)
                    return;

                Views.SettingsControl.PendingNavigationProviderKey = providerKey;
                _api.MainView.OpenPluginSettings(plugin.Id);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to open plugin settings from notification click.");
            }
        }
    }
}
