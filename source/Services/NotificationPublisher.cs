using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
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
                ? ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete")
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

        public void ShowUpstreamReleaseAvailable(string upstreamVersion, string releaseUrl)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
            {
                return;
            }

            var title = ResourceProvider.GetString("LOCPlayAch_Notification_UpstreamReleaseTitle");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Original Fork Update Available";
            }

            var messageFormat = ResourceProvider.GetString("LOCPlayAch_Notification_UpstreamReleaseMessage");
            if (string.IsNullOrWhiteSpace(messageFormat))
            {
                messageFormat = "The original PlayniteAchievements fork released version {0}. Click to open the upstream releases page.";
            }

            var message = string.Format(messageFormat, upstreamVersion ?? "?");

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-UpstreamRelease-{upstreamVersion}",
                    $"{title}\n{message}",
                    NotificationType.Info,
                    () => OpenUrl(releaseUrl)));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show upstream release notification.");
            }
        }

        public void ShowLocalAchievementUnlocked(string gameName, IReadOnlyList<string> unlockedAchievementNames, string customSoundPath)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
            {
                return;
            }

            var names = unlockedAchievementNames?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var unlockCount = Math.Max(unlockedAchievementNames?.Count ?? 0, names.Count);
            if (unlockCount <= 0)
            {
                return;
            }

            var title = ResourceProvider.GetString("LOCPlayAch_Notification_LocalUnlockTitle");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Local Achievement Unlocked";
            }

            var safeGameName = string.IsNullOrWhiteSpace(gameName) ? "Current Game" : gameName.Trim();
            string message;
            if (unlockCount == 1 && names.Count == 1)
            {
                var singleFormat = ResourceProvider.GetString("LOCPlayAch_Notification_LocalUnlockSingle");
                if (string.IsNullOrWhiteSpace(singleFormat))
                {
                    singleFormat = "{0}\nUnlocked: {1}";
                }

                message = string.Format(singleFormat, safeGameName, names[0]);
            }
            else
            {
                var multiFormat = ResourceProvider.GetString("LOCPlayAch_Notification_LocalUnlockMultiple");
                if (string.IsNullOrWhiteSpace(multiFormat))
                {
                    multiFormat = "{0}\n{1} new Local achievements unlocked.";
                }

                message = string.Format(multiFormat, safeGameName, unlockCount);
                if (names.Count > 0)
                {
                    message = $"{message}\n{string.Join(", ", names.Take(3))}";
                    if (names.Count > 3)
                    {
                        message = $"{message}...";
                    }
                }
            }

            try
            {
                RunOnUiThread(() => _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-LocalUnlock-{Guid.NewGuid()}",
                    $"{title}\n{message}",
                    NotificationType.Info)));

                PlayCustomSound(customSoundPath);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show Local unlock notification.");
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

        private void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to open URL: {url}");
            }
        }

        private void PlayCustomSound(string soundPath)
        {
            if (string.IsNullOrWhiteSpace(soundPath))
            {
                return;
            }

            try
            {
                soundPath = ResolveSoundPath(soundPath);
                if (!File.Exists(soundPath))
                {
                    _logger?.Warn($"Configured Local unlock sound file was not found: {soundPath}");
                    return;
                }

                _ = Task.Run(() =>
                {
                    try
                    {
                        using (var player = new SoundPlayer(soundPath))
                        {
                            player.PlaySync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Failed to play Local unlock sound: {soundPath}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to play Local unlock sound: {soundPath}");
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        public static string ResolveSoundPath(string soundPath)
        {
            if (string.IsNullOrWhiteSpace(soundPath))
            {
                return string.Empty;
            }

            var trimmedPath = soundPath.Trim();
            if (Path.IsPathRooted(trimmedPath))
            {
                return trimmedPath;
            }

            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return trimmedPath;
            }

            return Path.GetFullPath(Path.Combine(assemblyDirectory, trimmedPath));
        }
    }
}
