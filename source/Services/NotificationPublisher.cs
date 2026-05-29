using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Security;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services
{
    public class NotificationPublisher
    {
        private static readonly Regex OverlayTemplateTokenPattern = new Regex("<([a-zA-Z0-9]+)>", RegexOptions.Compiled);

        public const string NotificationStyleSteam = "Steam";
        public const string NotificationStylePlayStation = "PlayStation";
        public const string NotificationStyleXbox = "Xbox";
        public const string NotificationStyleMinimal = "Minimal";
        public const string NotificationStyleCustom = "Custom";

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private static Window _persistentSettingsPreviewOverlay;

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

        public void ShowForkReleaseAvailable(string forkVersion, string releaseUrl)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
            {
                return;
            }

            var title = "Santodan Fork Update Available";
            var message = string.Format(
                "The Santodan PlayniteAchievements fork released version {0}. Click to open the fork releases page.",
                forkVersion ?? "?");

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-ForkRelease-{forkVersion}",
                    $"{title}\n{message}",
                    NotificationType.Info,
                    () => OpenUrl(releaseUrl)));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show fork release notification.");
            }
        }

        public void ShowLocalAchievementUnlocked(
            string gameName,
            IReadOnlyList<string> unlockedAchievementNames,
            string customSoundPath,
            string unlockedAchievementIconPath = null,
            Game game = null,
            string achievementDescription = null,
            int? achievementPoints = null,
            string achievementRarity = null,
            string achievementTrophy = null,
            string forcedStyle = null,
            LocalUnlockNotificationDeliveryMode? forcedDeliveryMode = null,
            LocalSettings overrideLocalSettings = null,
            string notificationProviderKey = "Local")
        {
            var names = unlockedAchievementNames?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var unlockCount = Math.Max(unlockedAchievementNames?.Count ?? 0, names.Count);
            if (unlockCount <= 0)
            {
                return;
            }

            PlayCustomSound(customSoundPath);
            var localSettings = overrideLocalSettings ?? ProviderRegistry.Settings<LocalSettings>();
            var resolvedProviderKey = string.IsNullOrWhiteSpace(notificationProviderKey) ? "Local" : notificationProviderKey.Trim();
            var enableInAppNotification = localSettings?.EnableInAppUnlockNotifications != false;

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

            if (enableInAppNotification)
            {
                try
                {
                    RunOnUiThread(() => _api.Notifications.Add(new NotificationMessage(
                        $"PlayniteAchievements-LocalUnlock-{Guid.NewGuid()}",
                        $"{title}\n{message}",
                        NotificationType.Info)));
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to show Local unlock notification.");
                }
            }

            if (names.Count > 0)
            {
                var firstAchievement = names[0];
                var soundLeadMs = Math.Max(0, localSettings?.UnlockSoundLeadMilliseconds ?? 0);
                if (soundLeadMs > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(soundLeadMs).ConfigureAwait(false);
                            SendUnlockPopup(
                                safeGameName,
                                firstAchievement,
                                unlockedAchievementIconPath,
                                providerKey: resolvedProviderKey,
                                game: game,
                                achievementDescription: achievementDescription,
                                achievementPoints: achievementPoints,
                                achievementRarity: achievementRarity,
                                achievementTrophy: achievementTrophy,
                                forcedStyle: forcedStyle,
                                forcedDeliveryMode: forcedDeliveryMode,
                                overrideLocalSettings: localSettings);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "Failed to send delayed Local unlock popup.");
                        }
                    });
                }
                else
                {
                    SendUnlockPopup(
                        safeGameName,
                        firstAchievement,
                        unlockedAchievementIconPath,
                        providerKey: resolvedProviderKey,
                        game: game,
                        achievementDescription: achievementDescription,
                        achievementPoints: achievementPoints,
                        achievementRarity: achievementRarity,
                        achievementTrophy: achievementTrophy,
                        forcedStyle: forcedStyle,
                        forcedDeliveryMode: forcedDeliveryMode,
                        overrideLocalSettings: localSettings);
                }
            }
        }

        public void SendUnlockPopup(
            string gameName,
            string achievementName,
            string achievementIconPath = null,
            string providerKey = "Local",
            string forcedStyle = null,
            LocalUnlockNotificationDeliveryMode? forcedDeliveryMode = null,
            LocalSettings overrideLocalSettings = null,
            Game game = null,
            bool togglePersistentOverlay = false,
            bool refreshPersistentOverlay = false,
            string achievementDescription = null,
            int? achievementPoints = null,
            string achievementRarity = null,
            string achievementTrophy = null)
        {
            var localSettings = overrideLocalSettings ?? ProviderRegistry.Settings<LocalSettings>();
            var mode = forcedDeliveryMode ?? localSettings?.UnlockNotificationDeliveryMode ?? LocalUnlockNotificationDeliveryMode.Hybrid;
            var style = ResolveUnlockNotificationStyle(providerKey, forcedStyle);

            if (mode == LocalUnlockNotificationDeliveryMode.Overlay || mode == LocalUnlockNotificationDeliveryMode.Hybrid)
            {
                ShowOverlayUnlockNotification(
                    gameName,
                    achievementName,
                    achievementIconPath,
                    style,
                    providerKey,
                    localSettings,
                    game,
                    togglePersistentOverlay,
                    refreshPersistentOverlay,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy);
            }

            if (mode == LocalUnlockNotificationDeliveryMode.WindowsToast || mode == LocalUnlockNotificationDeliveryMode.Hybrid)
            {
                SendWindowsToastNotification(gameName, achievementName, achievementIconPath, providerKey, forcedStyle, localSettings);
            }
        }

        public FrameworkElement CreateOverlayPreviewContent(
            string gameName,
            string achievementName,
            string forcedStyle = NotificationStyleCustom,
            string providerKey = "Local",
            LocalSettings overrideLocalSettings = null,
            string achievementIconPath = null,
            Game game = null,
            string achievementDescription = null,
            int? achievementPoints = null,
            string achievementRarity = null,
            string achievementTrophy = null)
        {
            var localSettings = overrideLocalSettings ?? ProviderRegistry.Settings<LocalSettings>() ?? new LocalSettings();
            var style = string.IsNullOrWhiteSpace(forcedStyle)
                ? ResolveUnlockNotificationStyle(providerKey, null)
                : NormalizeNotificationStyle(forcedStyle);
            var title = ResolveOverlayTitle(style);
            var safeGameName = string.IsNullOrWhiteSpace(gameName) ? "Current Game" : gameName.Trim();
            var safeAchievement = string.IsNullOrWhiteSpace(achievementName) ? "Achievement unlocked" : achievementName.Trim();
            var overlayScale = GetOverlayScale(localSettings, style);

            var content = BuildOverlayContent(title, safeGameName, safeAchievement, achievementIconPath, style, providerKey, localSettings, overlayScale, game, achievementDescription, achievementPoints, achievementRarity, achievementTrophy);
            if (!string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return content;
            }

            var width = Math.Max(280, localSettings?.OverlayCustomWidth ?? 460);
            var height = Math.Max(90, localSettings?.OverlayCustomHeight ?? 128);
            var frame = new Grid
            {
                Width = width,
                MinHeight = height
            };

            if (localSettings?.OverlayCustomAutoResizeToContent == true)
            {
                frame.MaxHeight = Math.Max(height, 520);
            }
            else
            {
                frame.Height = height;
            }

            frame.Children.Add(content);
            return frame;
        }

        public static void ClosePersistentSettingsPreview()
        {
            void CloseOverlay()
            {
                if (_persistentSettingsPreviewOverlay == null)
                {
                    return;
                }

                var existing = _persistentSettingsPreviewOverlay;
                _persistentSettingsPreviewOverlay = null;
                existing.Close();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke((Action)CloseOverlay);
                return;
            }

            CloseOverlay();
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

        public void SendWindowsToastNotification(
            string gameName,
            string achievementName,
            string achievementIconPath = null,
            string providerKey = "Local",
            string forcedStyle = null,
            LocalSettings overrideLocalSettings = null)
        {
            try
            {
                var localSettings = overrideLocalSettings ?? ProviderRegistry.Settings<LocalSettings>();
                if (localSettings?.EnableWindowsToastNotifications != true)
                {
                    _logger?.Info("[LocalToast] Skipping Windows toast because EnableWindowsToastNotifications is disabled.");
                    return;
                }

                var safeGameName = string.IsNullOrWhiteSpace(gameName) ? "Current Game" : gameName.Trim();
                var safeAchievementName = string.IsNullOrWhiteSpace(achievementName) ? "Achievement unlocked" : achievementName.Trim();
                var title = ResourceProvider.GetString("LOCPlayAch_Notification_LocalUnlockTitle");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "Local Achievement Unlocked";
                }

                var style = ResolveUnlockNotificationStyle(providerKey, forcedStyle);

                var xmlTitle = EscapeXmlText(title);
                var xmlLine1 = EscapeXmlText(safeGameName);
                var xmlLine2 = EscapeXmlText($"Unlocked: {safeAchievementName}");
                var toastIconSource = ResolveUsableNotificationImageUri(achievementIconPath);
                var visualBlock = BuildToastVisualBlock(style, xmlTitle, xmlLine1, xmlLine2, toastIconSource, providerKey);
                var audioBlock = BuildToastAudioBlock(style);

                var script = $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$toastXml = @'
<toast>
  <visual>
{visualBlock}
  </visual>
    {audioBlock}
</toast>
'@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($toastXml)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml

            LocalSettings overrideLocalSettings = null,
            Game game = null)
try {{
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.Windows.PowerShell').Show($toast)
}} catch {{
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Windows.SystemToast').Show($toast)
}}
";

                var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encodedScript}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _logger?.Info($"[LocalToast] Sending Windows toast for game='{safeGameName}', achievement='{safeAchievementName}', provider='{providerKey}', style='{style}'.");
                var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    _logger?.Warn("[LocalToast] PowerShell process did not start (Process.Start returned null).");
                    return;
                }

                _ = Task.Run(() =>
                {
                    try
                    {
                        if (!process.WaitForExit(5000))
                        {
                            _logger?.Warn($"[LocalToast] PowerShell toast process timed out. Pid={process.Id}");
                            try
                            {
                                process.Kill();
                            }
                            catch
                            {
                                // Ignore kill failures.
                            }
                            return;
                        }

                        var stdout = process.StandardOutput.ReadToEnd();
                        var stderr = process.StandardError.ReadToEnd();
                        var logStdout = string.IsNullOrWhiteSpace(stdout) ? "<empty>" : stdout.Trim();
                        var logStderr = string.IsNullOrWhiteSpace(stderr) ? "<empty>" : stderr.Trim();

                        if (process.ExitCode == 0)
                        {
                            _logger?.Info($"[LocalToast] PowerShell toast command succeeded. ExitCode=0, StdOut={logStdout}, StdErr={logStderr}");
                        }
                        else
                        {
                            _logger?.Warn($"[LocalToast] PowerShell toast command failed. ExitCode={process.ExitCode}, StdOut={logStdout}, StdErr={logStderr}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "[LocalToast] Failed while waiting for PowerShell toast command result.");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                });

                // Fallback for systems where WinRT toast is accepted but not surfaced visually.
                ShowWindowsBalloonNotification(title, safeGameName, safeAchievementName);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[LocalToast] Failed to send Windows toast notification.");
            }
        }

        private void ShowWindowsBalloonNotification(string title, string gameName, string achievementName)
        {
            try
            {
                var line1 = string.IsNullOrWhiteSpace(gameName) ? "Current Game" : gameName;
                var line2 = string.IsNullOrWhiteSpace(achievementName) ? "Achievement unlocked" : $"Unlocked: {achievementName}";
                var text = $"{line1}\n{line2}";

                _ = Task.Run(() =>
                {
                    try
                    {
                        using (var icon = new System.Windows.Forms.NotifyIcon())
                        {
                            icon.Visible = true;
                            icon.Icon = System.Drawing.SystemIcons.Information;
                            icon.BalloonTipTitle = title;
                            icon.BalloonTipText = text;
                            icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                            icon.ShowBalloonTip(5000);

                            // Keep the icon alive briefly so the balloon can render before disposal.
                            Thread.Sleep(5500);
                            icon.Visible = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[LocalToast] Tray balloon fallback failed.");
                    }
                });

                _logger?.Info("[LocalToast] Tray balloon fallback notification requested.");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LocalToast] Failed to initialize tray balloon fallback.");
            }
        }

        private static string EscapeXmlText(string value)
        {
            return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        }

        private string ResolveUnlockNotificationStyle(string providerKey, string forcedStyle)
        {
            if (!string.IsNullOrWhiteSpace(forcedStyle))
            {
                return NormalizeNotificationStyle(forcedStyle);
            }

            var persisted = _settings?.Persisted;
            var normalizedProvider = string.IsNullOrWhiteSpace(providerKey) ? "Local" : providerKey.Trim();
            if (persisted?.ProviderUnlockNotificationStyles != null &&
                persisted.ProviderUnlockNotificationStyles.TryGetValue(normalizedProvider, out var providerStyle) &&
                !string.IsNullOrWhiteSpace(providerStyle))
            {
                return NormalizeNotificationStyle(providerStyle);
            }

            return NormalizeNotificationStyle(persisted?.DefaultUnlockNotificationStyle);
        }

        private static string NormalizeNotificationStyle(string style)
        {
            if (string.Equals(style, NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return NotificationStylePlayStation;
            }

            if (string.Equals(style, NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return NotificationStyleXbox;
            }

            if (string.Equals(style, NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return NotificationStyleMinimal;
            }

            if (string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return NotificationStyleCustom;
            }

            return NotificationStyleSteam;
        }

        private static string BuildToastVisualBlock(string style, string title, string line1, string line2, string imageSource, string providerKey)
        {
            var safeProvider = EscapeXmlText(string.IsNullOrWhiteSpace(providerKey) ? "Local" : providerKey.Trim());
            var escapedImage = string.IsNullOrWhiteSpace(imageSource) ? string.Empty : EscapeXmlText(imageSource);

            if (string.Equals(style, NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                var heroImage = string.IsNullOrWhiteSpace(escapedImage)
                    ? string.Empty
                    : $"      <image placement='hero' src='{escapedImage}'/>\n";
                return
"    <binding template='ToastGeneric'>\n" +
"      <text>Trophy earned</text>\n" +
$"      <text>{line1}</text>\n" +
$"      <text>{line2}</text>\n" +
$"      <text hint-style='captionSubtle'>{safeProvider} style</text>\n" +
heroImage +
"    </binding>";
            }

            if (string.Equals(style, NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                var logoImage = string.IsNullOrWhiteSpace(escapedImage)
                    ? string.Empty
                    : $"      <image placement='appLogoOverride' hint-crop='circle' src='{escapedImage}'/>\n";
                return
"    <binding template='ToastGeneric'>\n" +
$"      <text>{line1}</text>\n" +
"      <text>Achievement unlocked</text>\n" +
$"      <text>{line2}</text>\n" +
logoImage +
"    </binding>";
            }

            if (string.Equals(style, NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return
"    <binding template='ToastGeneric'>\n" +
$"      <text>{title}</text>\n" +
$"      <text>{line2}</text>\n" +
"    </binding>";
            }

            if (string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                var customImage = string.IsNullOrWhiteSpace(escapedImage)
                    ? string.Empty
                    : $"      <image placement='appLogoOverride' hint-crop='none' src='{escapedImage}'/>\n";
                return
"    <binding template='ToastGeneric'>\n" +
$"      <text>{title}</text>\n" +
$"      <text>{line1}</text>\n" +
$"      <text>{line2}</text>\n" +
customImage +
"    </binding>";
            }

            var steamImage = string.IsNullOrWhiteSpace(escapedImage)
                ? string.Empty
                : $"      <image placement='appLogoOverride' hint-crop='none' src='{escapedImage}'/>\n";
            return
"    <binding template='ToastGeneric'>\n" +
$"      <text>{title}</text>\n" +
$"      <text>{line1}</text>\n" +
$"      <text>{line2}</text>\n" +
steamImage +
"    </binding>";
        }

        private static string BuildToastAudioBlock(string style)
        {
            if (string.Equals(style, NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return "<audio silent='true'/>";
            }

            if (string.Equals(style, NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return "<audio src='ms-winsoundevent:Notification.IM'/>";
            }

            if (string.Equals(style, NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return "<audio src='ms-winsoundevent:Notification.Reminder'/>";
            }

            return "<audio src='ms-winsoundevent:Notification.Default'/>";
        }

        private void ShowOverlayUnlockNotification(
            string gameName,
            string achievementName,
            string achievementIconPath,
            string style,
            string providerKey,
            LocalSettings overrideLocalSettings = null,
            Game game = null,
            bool togglePersistentOverlay = false,
            bool refreshPersistentOverlay = false,
            string achievementDescription = null,
            int? achievementPoints = null,
            string achievementRarity = null,
            string achievementTrophy = null)
        {
            try
            {
                var localSettings = overrideLocalSettings ?? ProviderRegistry.Settings<LocalSettings>();
                var durationMs = localSettings?.UnlockOverlayDurationMilliseconds ?? 3400;
                var fadeInMs = localSettings?.UnlockOverlayFadeInMilliseconds ?? 180;
                var fadeOutMs = localSettings?.UnlockOverlayFadeOutMilliseconds ?? 280;
                var position = localSettings?.UnlockOverlayPosition ?? LocalUnlockOverlayPosition.TopRight;
                var overlayOpacity = GetOverlayOpacity(localSettings, style);
                var overlayScale = GetOverlayScale(localSettings, style);

                RunOnUiThread(() =>
                {
                    try
                    {
                        var persistentPreviewRequested = togglePersistentOverlay || refreshPersistentOverlay;
                        if (persistentPreviewRequested)
                        {
                            if (refreshPersistentOverlay && _persistentSettingsPreviewOverlay == null)
                            {
                                return;
                            }

                            if (_persistentSettingsPreviewOverlay != null)
                            {
                                var existing = _persistentSettingsPreviewOverlay;
                                _persistentSettingsPreviewOverlay = null;
                                existing.Close();

                                if (togglePersistentOverlay && !refreshPersistentOverlay)
                                {
                                    return;
                                }
                            }
                        }

                        var title = ResolveOverlayTitle(style);
                        var safeGameName = string.IsNullOrWhiteSpace(gameName) ? "Current Game" : gameName.Trim();
                        var safeAchievement = string.IsNullOrWhiteSpace(achievementName) ? "Achievement unlocked" : achievementName.Trim();
                        var isCustomStyle = string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase);
                        var autoResizeCustom = isCustomStyle && (localSettings?.OverlayCustomAutoResizeToContent == true);
                        var transitionStyle = localSettings?.UnlockOverlayTransitionStyle ?? LocalUnlockOverlayTransitionStyle.Fade;
                        var slideDistance = localSettings?.UnlockOverlaySlideDistance ?? 72;

                        var width = isCustomStyle
                            ? Math.Max(280, localSettings?.OverlayCustomWidth ?? 460)
                            : 420 * overlayScale;
                        var height = isCustomStyle
                            ? Math.Max(90, localSettings?.OverlayCustomHeight ?? 128)
                            : 110 * overlayScale;

                        var overlayWindow = new Window
                        {
                            Width = width,
                            WindowStyle = WindowStyle.None,
                            ResizeMode = ResizeMode.NoResize,
                            AllowsTransparency = true,
                            Background = Brushes.Transparent,
                            Topmost = true,
                            ShowInTaskbar = false,
                            ShowActivated = false,
                            IsHitTestVisible = false,
                            Focusable = false,
                            Opacity = 0
                        };

                        if (autoResizeCustom)
                        {
                            overlayWindow.MinHeight = height;
                            overlayWindow.MaxHeight = Math.Max(height, 520);
                            overlayWindow.SizeToContent = SizeToContent.Height;
                        }
                        else
                        {
                            overlayWindow.Height = height;
                        }

                        PositionOverlayWindow(overlayWindow, position);
                        overlayWindow.Content = BuildOverlayContent(title, safeGameName, safeAchievement, achievementIconPath, style, providerKey, localSettings, overlayScale, game, achievementDescription, achievementPoints, achievementRarity, achievementTrophy);

                        overlayWindow.Loaded += (sender, args) =>
                        {
                            try
                            {
                                if (autoResizeCustom)
                                {
                                    PositionOverlayWindow(overlayWindow, position);
                                }

                                ApplyOverlayEnterAnimation(overlayWindow, overlayOpacity, fadeInMs, transitionStyle, slideDistance);

                                if (persistentPreviewRequested)
                                {
                                    return;
                                }

                                var closeTimer = new DispatcherTimer
                                {
                                    Interval = TimeSpan.FromMilliseconds(durationMs)
                                };

                                closeTimer.Tick += (timerSender, timerArgs) =>
                                {
                                    closeTimer.Stop();
                                    ApplyOverlayExitAnimation(overlayWindow, overlayOpacity, fadeOutMs, transitionStyle, slideDistance, () => overlayWindow.Close());
                                };

                                closeTimer.Start();
                            }
                            catch (Exception animEx)
                            {
                                _logger?.Warn(animEx, "[LocalOverlay] Failed in loaded animation pipeline.");
                                try
                                {
                                    overlayWindow.Close();
                                }
                                catch
                                {
                                }
                            }
                        };

                        overlayWindow.Closed += (_, __) =>
                        {
                            if (ReferenceEquals(_persistentSettingsPreviewOverlay, overlayWindow))
                            {
                                _persistentSettingsPreviewOverlay = null;
                            }
                        };

                        if (persistentPreviewRequested)
                        {
                            _persistentSettingsPreviewOverlay = overlayWindow;
                        }

                        overlayWindow.Show();
                    }
                    catch (Exception uiEx)
                    {
                        _logger?.Warn(uiEx, "[LocalOverlay] Failed to render overlay on UI thread.");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LocalOverlay] Failed to show overlay notification.");
            }
        }

        private static string ResolveOverlayTitle(string style)
        {
            if (string.Equals(style, NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return "Trophy earned";
            }

            if (string.Equals(style, NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return "Achievement unlocked";
            }

            return "Local Achievement Unlocked";
        }

        private static void PositionOverlayWindow(Window window, LocalUnlockOverlayPosition position)
        {
            if (window == null)
            {
                return;
            }

            const double margin = 16;
            var workArea = SystemParameters.WorkArea;
            switch (position)
            {
                case LocalUnlockOverlayPosition.TopLeft:
                    window.Left = workArea.Left + margin;
                    window.Top = workArea.Top + margin;
                    break;
                case LocalUnlockOverlayPosition.BottomLeft:
                    window.Left = workArea.Left + margin;
                    window.Top = workArea.Bottom - window.Height - margin;
                    break;
                case LocalUnlockOverlayPosition.BottomRight:
                    window.Left = workArea.Right - window.Width - margin;
                    window.Top = workArea.Bottom - window.Height - margin;
                    break;
                default:
                    window.Left = workArea.Right - window.Width - margin;
                    window.Top = workArea.Top + margin;
                    break;
            }
        }

        private static void ApplyOverlayEnterAnimation(Window overlayWindow, double targetOpacity, int fadeInMs, LocalUnlockOverlayTransitionStyle transitionStyle, int slideDistance)
        {
            try
            {
                var duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeInMs)));
                overlayWindow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, targetOpacity, duration));

                if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromRight)
                {
                    overlayWindow.BeginAnimation(Window.LeftProperty, new DoubleAnimation(overlayWindow.Left + slideDistance, overlayWindow.Left, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromLeft)
                {
                    overlayWindow.BeginAnimation(Window.LeftProperty, new DoubleAnimation(overlayWindow.Left - slideDistance, overlayWindow.Left, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromTop)
                {
                    overlayWindow.BeginAnimation(Window.TopProperty, new DoubleAnimation(overlayWindow.Top - slideDistance, overlayWindow.Top, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromBottom)
                {
                    overlayWindow.BeginAnimation(Window.TopProperty, new DoubleAnimation(overlayWindow.Top + slideDistance, overlayWindow.Top, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.Circle)
                {
                    overlayWindow.RenderTransformOrigin = new Point(0.5, 0.5);
                    if (!(overlayWindow.RenderTransform is TransformGroup group))
                    {
                        group = new TransformGroup();
                        group.Children.Add(new ScaleTransform(0.2, 0.2));
                        group.Children.Add(new RotateTransform(-14));
                        overlayWindow.RenderTransform = group;
                    }

                    var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
                    var rotate = group.Children.OfType<RotateTransform>().FirstOrDefault();
                    scale?.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.2, 1.0, duration));
                    scale?.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1.0, duration));
                    rotate?.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-14, 0, duration));
                }
            }
            catch
            {
                overlayWindow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, targetOpacity, new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeInMs)))));
            }
        }

        private static void ApplyOverlayExitAnimation(Window overlayWindow, double startOpacity, int fadeOutMs, LocalUnlockOverlayTransitionStyle transitionStyle, int slideDistance, Action onComplete)
        {
            try
            {
                var duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeOutMs)));
                var fadeOut = new DoubleAnimation(startOpacity, 0, duration);
                fadeOut.Completed += (_, __) => onComplete?.Invoke();
                overlayWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);

                if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromRight)
                {
                    overlayWindow.BeginAnimation(Window.LeftProperty, new DoubleAnimation(overlayWindow.Left, overlayWindow.Left - slideDistance, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromLeft)
                {
                    overlayWindow.BeginAnimation(Window.LeftProperty, new DoubleAnimation(overlayWindow.Left, overlayWindow.Left + slideDistance, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromTop)
                {
                    overlayWindow.BeginAnimation(Window.TopProperty, new DoubleAnimation(overlayWindow.Top, overlayWindow.Top + slideDistance, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.SlideFromBottom)
                {
                    overlayWindow.BeginAnimation(Window.TopProperty, new DoubleAnimation(overlayWindow.Top, overlayWindow.Top - slideDistance, duration));
                }
                else if (transitionStyle == LocalUnlockOverlayTransitionStyle.Circle)
                {
                    overlayWindow.RenderTransformOrigin = new Point(0.5, 0.5);
                    if (!(overlayWindow.RenderTransform is TransformGroup group))
                    {
                        group = new TransformGroup();
                        group.Children.Add(new ScaleTransform(1.0, 1.0));
                        group.Children.Add(new RotateTransform(0));
                        overlayWindow.RenderTransform = group;
                    }

                    var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
                    var rotate = group.Children.OfType<RotateTransform>().FirstOrDefault();
                    scale?.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.2, duration));
                    scale?.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.2, duration));
                    rotate?.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 14, duration));
                }
            }
            catch
            {
                var fadeOut = new DoubleAnimation(startOpacity, 0, new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeOutMs))));
                fadeOut.Completed += (_, __) => onComplete?.Invoke();
                overlayWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }

        private static double GetOverlayOpacity(LocalSettings settings, string style)
        {
            if (settings == null)
            {
                return 0.96;
            }

            if (string.Equals(style, NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayPlayStationOpacity;
            }

            if (string.Equals(style, NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayXboxOpacity;
            }

            if (string.Equals(style, NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayMinimalOpacity;
            }

            if (string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayCustomOpacity;
            }

            return settings.OverlaySteamOpacity;
        }

        private static double GetOverlayScale(LocalSettings settings, string style)
        {
            if (settings == null)
            {
                return 1.0;
            }

            if (string.Equals(style, NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayPlayStationScale;
            }

            if (string.Equals(style, NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayXboxScale;
            }

            if (string.Equals(style, NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayMinimalScale;
            }

            if (string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayCustomScale;
            }

            return settings.OverlaySteamScale;
        }

        private FrameworkElement BuildOverlayContent(string title, string gameName, string achievementName, string rawIconPath, string style, string providerKey, LocalSettings localSettings, double overlayScale, Game game = null, string achievementDescription = null, int? achievementPoints = null, string achievementRarity = null, string achievementTrophy = null)
        {
            if (string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return BuildCustomOverlayContent(title, gameName, achievementName, rawIconPath, providerKey, localSettings, overlayScale, game, achievementDescription, achievementPoints, achievementRarity, achievementTrophy);
            }

            var (backgroundBrush, borderBrush, accentBrush) = ResolveOverlayBrushes(style);
            var iconSize = Math.Max(40, 58 * overlayScale);
            var titleSize = Math.Max(12, 15 * overlayScale);
            var detailSize = Math.Max(11, 13 * overlayScale);
            var metaSize = Math.Max(10, 11 * overlayScale);
            var showBanner = localSettings?.EnableGameBannerAsBackground == true;
            var bannerSource = showBanner ? TryCreatePlayniteGameImageSource(game, useBackground: true) : null;
            var coverPosition = localSettings?.EnableGameCoverInOverlay == true
                ? localSettings.GameCoverPosition
                : LocalOverlayCoverPosition.None;
            var coverWidth = Math.Max(48, (localSettings?.GameCoverWidth ?? 80) * overlayScale);
            var coverHeight = Math.Max(iconSize + 18, 92 * overlayScale);
            var contentPadding = new Thickness(Math.Max(8, 12 * overlayScale));

            var root = new Border
            {
                Background = bannerSource == null ? backgroundBrush : Brushes.Transparent,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(0),
                ClipToBounds = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.5,
                    Color = Colors.Black
                }
            };

            var container = new Grid();
            if (bannerSource != null)
            {
                AddBannerBackground(
                    container,
                    bannerSource,
                    CreateOverlayTintBrush(backgroundBrush, 192),
                    localSettings?.GameBannerOpacity ?? 0.3,
                    localSettings?.GameBannerBlurRadius ?? 8,
                    10);
            }

            var grid = new Grid();
            grid.Margin = contentPadding;
            if (coverPosition == LocalOverlayCoverPosition.Left)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (coverPosition == LocalOverlayCoverPosition.Right)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var currentColumn = 0;

            if (coverPosition == LocalOverlayCoverPosition.Left)
            {
                var leftCover = CreateGameCoverElement(
                    game,
                    coverWidth,
                    coverHeight,
                    7,
                    new Thickness(0, 0, Math.Max(8, 10 * overlayScale), 0));
                if (leftCover != null)
                {
                    Grid.SetColumn(leftCover, currentColumn);
                    grid.Children.Add(leftCover);
                    currentColumn++;
                }
            }

            var icon = new Border
            {
                Width = iconSize,
                Height = iconSize,
                Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, Math.Max(8, 10 * overlayScale), 0)
            };

            var iconSource = TryCreateOverlayImageSource(rawIconPath);
            if (iconSource != null)
            {
                icon.Child = new Image
                {
                    Source = iconSource,
                    Stretch = Stretch.UniformToFill,
                    Width = iconSize,
                    Height = iconSize
                };
            }
            else
            {
                icon.Child = new TextBlock
                {
                    Text = "*",
                    Foreground = Brushes.White,
                    FontSize = Math.Max(14, 20 * overlayScale),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            Grid.SetColumn(icon, currentColumn);
            grid.Children.Add(icon);
            currentColumn++;

            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            textStack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = titleSize,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            textStack.Children.Add(new TextBlock
            {
                Text = gameName,
                Foreground = new SolidColorBrush(Color.FromArgb(230, 230, 230, 230)),
                FontSize = detailSize,
                Margin = new Thickness(0, Math.Max(2, 3 * overlayScale), 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            textStack.Children.Add(new TextBlock
            {
                Text = $"Unlocked: {achievementName}",
                Foreground = accentBrush,
                FontSize = detailSize,
                Margin = new Thickness(0, Math.Max(2, 2 * overlayScale), 0, 0),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            textStack.Children.Add(new TextBlock
            {
                Text = $"{providerKey} / {style}",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 210, 210, 210)),
                FontSize = metaSize,
                Margin = new Thickness(0, Math.Max(3, 5 * overlayScale), 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            Grid.SetColumn(textStack, currentColumn);
            grid.Children.Add(textStack);

            if (coverPosition == LocalOverlayCoverPosition.Right)
            {
                var rightCover = CreateGameCoverElement(
                    game,
                    coverWidth,
                    coverHeight,
                    7,
                    new Thickness(Math.Max(8, 10 * overlayScale), 0, 0, 0));
                if (rightCover != null)
                {
                    Grid.SetColumn(rightCover, currentColumn + 1);
                    grid.Children.Add(rightCover);
                }
            }

            container.Children.Add(grid);
            root.Child = container;
            return root;
        }

        private FrameworkElement BuildCustomOverlayContent(string title, string gameName, string achievementName, string rawIconPath, string providerKey, LocalSettings settings, double overlayScale, Game game = null, string achievementDescription = null, int? achievementPoints = null, string achievementRarity = null, string achievementTrophy = null)
        {
            var backgroundBrush = ResolveCustomBackgroundBrush(settings);
            var borderBrush = ParseBrushOrDefault(settings?.OverlayCustomBorderColor, Color.FromRgb(111, 163, 216));
            var accentBrush = ParseBrushOrDefault(settings?.OverlayCustomAccentColor, Color.FromRgb(167, 224, 255));
            var titleBrush = ParseBrushOrDefault(settings?.OverlayCustomTitleColor, Colors.White);
            var detailBrush = ParseBrushOrDefault(settings?.OverlayCustomDetailColor, Color.FromRgb(231, 238, 247));
            var metaBrush = ParseBrushOrDefault(settings?.OverlayCustomMetaColor, Color.FromRgb(188, 208, 229));
            var wrapAllText = settings?.OverlayCustomWrapAllText == true;

            var iconSize = Math.Max(24, (settings?.OverlayCustomIconSize ?? 58) * overlayScale);
            var titleSize = settings?.OverlayCustomTitleFontSize ?? 17;
            var detailSize = settings?.OverlayCustomDetailFontSize ?? 13;
            var metaSize = settings?.OverlayCustomMetaFontSize ?? 11;
            var cornerRadius = settings?.OverlayCustomCornerRadius ?? 18;
            var showBanner = settings?.EnableGameBannerAsBackground == true;
            var bannerSource = showBanner
                ? (TryCreateOverlayImageSource(settings?.OverlayCustomBannerImagePath) ?? TryCreatePlayniteGameImageSource(game, useBackground: true))
                : null;
            var coverPosition = settings?.EnableGameCoverInOverlay == true
                ? settings.GameCoverPosition
                : LocalOverlayCoverPosition.None;
            var timestamp = DateTime.Now;
            var sourceName = game?.Source?.Name ?? string.Empty;
            var coverWidth = Math.Max(48, (settings?.GameCoverWidth ?? 80) * overlayScale);
            var coverHeight = Math.Max(iconSize + 18, 96 * overlayScale);
            var contentPadding = new Thickness(16);

            var root = new Border
            {
                Background = bannerSource == null ? backgroundBrush : Brushes.Transparent,
                BorderBrush = borderBrush,
                BorderThickness = (settings?.OverlayCustomShowBorder != false) ? new Thickness(1.5) : new Thickness(0),
                CornerRadius = new CornerRadius(cornerRadius),
                Padding = new Thickness(0),
                ClipToBounds = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = Colors.Black
                }
            };

            var container = new Grid();
            if (bannerSource != null)
            {
                AddBannerBackground(
                    container,
                    bannerSource,
                    CreateOverlayTintBrush(ParseBrushOrDefault(settings?.OverlayCustomBackgroundColor, Color.FromRgb(30, 36, 48)), 194),
                    settings?.GameBannerOpacity ?? 0.3,
                    settings?.GameBannerBlurRadius ?? 8,
                    cornerRadius);
            }

            var grid = new Grid();
            grid.Margin = contentPadding;
            if (coverPosition == LocalOverlayCoverPosition.Left)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (coverPosition == LocalOverlayCoverPosition.Right)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var currentColumn = 0;

            if (coverPosition == LocalOverlayCoverPosition.Left)
            {
                var leftCover = CreateGameCoverElement(
                    game,
                    coverWidth,
                    coverHeight,
                    Math.Max(6, cornerRadius / 2.5),
                    new Thickness(0, 0, 14, 0),
                    settings?.OverlayCustomCoverImagePath);
                if (leftCover != null)
                {
                    Grid.SetColumn(leftCover, currentColumn);
                    grid.Children.Add(leftCover);
                    currentColumn++;
                }
            }

            var icon = new Border
            {
                Width = iconSize,
                Height = iconSize,
                Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
                CornerRadius = new CornerRadius(Math.Max(6, cornerRadius / 2.5)),
                Margin = new Thickness(0, 0, 14, 0)
            };

            var rarityKey = ResolveRarityKey(achievementRarity, achievementPoints);
            icon.Child = CreateCustomOverlayIconContent(settings, rawIconPath, providerKey, titleBrush, iconSize, titleSize, rarityKey);
            if (settings?.OverlayCustomShowIconRarityGlow == true)
            {
                var glow = CreateIconRarityGlowEffect(rarityKey);
                if (glow != null)
                {
                    icon.Effect = glow;
                }
            }

            Grid.SetColumn(icon, currentColumn);
            grid.Children.Add(icon);
            currentColumn++;

            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (settings?.OverlayCustomShowLine1 != false)
            {
                var line = CreateCustomTemplateTextBlock(
                    settings?.OverlayCustomTitleTemplate,
                    "<title>",
                    title,
                    gameName,
                    achievementName,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    providerKey,
                    NotificationStyleCustom,
                    game,
                    sourceName,
                    timestamp,
                    titleBrush,
                    titleSize,
                    settings?.OverlayCustomTitleBold == true,
                    settings?.OverlayCustomTitleItalic == true,
                    settings?.OverlayCustomTitleUnderline == true,
                    settings?.OverlayCustomTitleStrikethrough == true,
                    new Thickness(0),
                    wrapAllText,
                    suppressWhenTemplateEmpty: true);
                if (line != null)
                {
                    textStack.Children.Add(line);
                }
            }

            if (settings?.OverlayCustomShowGameName != false)
            {
                var line = CreateCustomTemplateTextBlock(
                    settings?.OverlayCustomGameNameTemplate,
                    "<gameName>",
                    title,
                    gameName,
                    achievementName,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    providerKey,
                    NotificationStyleCustom,
                    game,
                    sourceName,
                    timestamp,
                    detailBrush,
                    detailSize,
                    settings?.OverlayCustomDetailBold == true,
                    settings?.OverlayCustomDetailItalic == true,
                    settings?.OverlayCustomDetailUnderline == true,
                    settings?.OverlayCustomDetailStrikethrough == true,
                    new Thickness(0, 4, 0, 0),
                    wrapAllText,
                    suppressWhenTemplateEmpty: true);
                if (line != null)
                {
                    textStack.Children.Add(line);
                }
            }

            if (settings?.OverlayCustomShowMeta != false)
            {
                var line = CreateCustomTemplateTextBlock(
                    settings?.OverlayCustomAchievementTemplate,
                    "Unlocked: <achievementName>",
                    title,
                    gameName,
                    achievementName,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    providerKey,
                    NotificationStyleCustom,
                    game,
                    sourceName,
                    timestamp,
                    metaBrush,
                    metaSize,
                    settings?.OverlayCustomMetaBold == true,
                    settings?.OverlayCustomMetaItalic == true,
                    settings?.OverlayCustomMetaUnderline == true,
                    settings?.OverlayCustomMetaStrikethrough == true,
                    new Thickness(0, 3, 0, 0),
                    wrapAllText,
                    suppressWhenTemplateEmpty: true);
                if (line != null)
                {
                    textStack.Children.Add(line);
                }
            }

            Grid.SetColumn(textStack, currentColumn);
            grid.Children.Add(textStack);

            if (coverPosition == LocalOverlayCoverPosition.Right)
            {
                var rightCover = CreateGameCoverElement(
                    game,
                    coverWidth,
                    coverHeight,
                    Math.Max(6, cornerRadius / 2.5),
                    new Thickness(14, 0, 0, 0),
                    settings?.OverlayCustomCoverImagePath);
                if (rightCover != null)
                {
                    Grid.SetColumn(rightCover, currentColumn + 1);
                    grid.Children.Add(rightCover);
                }
            }

            container.Children.Add(grid);
            root.Child = container;
            return root;
        }

        private static string ResolveCustomTemplateLine(
            string template,
            string fallback,
            string title,
            string gameName,
            string achievementName,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            string providerKey,
            string style,
            Game game,
            string sourceName,
            DateTime timestamp,
            bool suppressWhenTemplateEmpty)
        {
            if (suppressWhenTemplateEmpty && string.IsNullOrWhiteSpace(template))
            {
                return string.Empty;
            }

            var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? fallback : template;
            if (string.IsNullOrWhiteSpace(effectiveTemplate))
            {
                return string.Empty;
            }

            var replaced = OverlayTemplateTokenPattern.Replace(effectiveTemplate, match =>
            {
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "title":
                        return title ?? string.Empty;
                    case "gamename":
                        return gameName ?? string.Empty;
                    case "achievementname":
                        return achievementName ?? string.Empty;
                    case "achievementdescription":
                        return achievementDescription ?? string.Empty;
                    case "points":
                        return achievementPoints?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                    case "rarity":
                        return achievementRarity ?? string.Empty;
                    case "trophy":
                        return FormatTrophyText(achievementTrophy);
                    case "provider":
                        return providerKey ?? string.Empty;
                    case "style":
                        return style ?? string.Empty;
                    case "date":
                        return timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    case "time":
                        return timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    case "datetime":
                        return timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    case "source":
                        return sourceName ?? string.Empty;
                    case "gameid":
                        return game?.Id.ToString() ?? string.Empty;
                    default:
                        return match.Value;
                }
            });

            return replaced?.Trim() ?? string.Empty;
        }

        private static TextBlock CreateCustomTemplateTextBlock(
            string template,
            string fallback,
            string title,
            string gameName,
            string achievementName,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            string providerKey,
            string style,
            Game game,
            string sourceName,
            DateTime timestamp,
            Brush foreground,
            double fontSize,
            bool bold,
            bool italic,
            bool underline,
            bool strikethrough,
            Thickness margin,
            bool wrapAllText,
            bool suppressWhenTemplateEmpty)
        {
            if (suppressWhenTemplateEmpty && string.IsNullOrWhiteSpace(template))
            {
                return null;
            }

            var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? fallback : template;
            if (string.IsNullOrWhiteSpace(effectiveTemplate))
            {
                return null;
            }

            var textBlock = new TextBlock
            {
                Foreground = foreground,
                FontSize = fontSize,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                Margin = margin,
                TextTrimming = wrapAllText ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = wrapAllText ? TextWrapping.Wrap : TextWrapping.NoWrap
            };

            var decorations = new TextDecorationCollection();
            if (underline)
            {
                foreach (var decoration in TextDecorations.Underline)
                {
                    decorations.Add(decoration);
                }
            }

            if (strikethrough)
            {
                foreach (var decoration in TextDecorations.Strikethrough)
                {
                    decorations.Add(decoration);
                }
            }

            if (decorations.Count > 0)
            {
                textBlock.TextDecorations = decorations;
            }

            var hasVisibleContent = false;
            var lastIndex = 0;
            foreach (Match match in OverlayTemplateTokenPattern.Matches(effectiveTemplate))
            {
                AddCustomTemplateRun(textBlock, effectiveTemplate.Substring(lastIndex, match.Index - lastIndex), ref hasVisibleContent);
                var tokenName = match.Groups[1].Value;
                if (string.Equals(tokenName, "trophyIcon", StringComparison.OrdinalIgnoreCase))
                {
                    AddCustomTemplateTrophyIcon(textBlock, achievementTrophy, achievementRarity, achievementPoints, fontSize, ref hasVisibleContent);
                }
                else
                {
                    AddCustomTemplateRun(
                        textBlock,
                        ResolveCustomTemplateToken(
                            tokenName,
                            title,
                            gameName,
                            achievementName,
                            achievementDescription,
                            achievementPoints,
                            achievementRarity,
                            achievementTrophy,
                            providerKey,
                            style,
                            game,
                            sourceName,
                            timestamp,
                            match.Value),
                        ref hasVisibleContent);
                }

                lastIndex = match.Index + match.Length;
            }

            AddCustomTemplateRun(textBlock, effectiveTemplate.Substring(lastIndex), ref hasVisibleContent);
            return hasVisibleContent ? textBlock : null;
        }

        private static void AddCustomTemplateRun(TextBlock textBlock, string text, ref bool hasVisibleContent)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                hasVisibleContent = true;
            }

            textBlock.Inlines.Add(new System.Windows.Documents.Run(text));
        }

        private static void AddCustomTemplateTrophyIcon(
            TextBlock textBlock,
            string achievementTrophy,
            string achievementRarity,
            int? achievementPoints,
            double fontSize,
            ref bool hasVisibleContent)
        {
            var resourceKey = GetTrophyResourceKeyForTrophy(achievementTrophy) ??
                              GetTrophyResourceKey(ResolveRarityKey(achievementRarity, achievementPoints));
            var trophy = TryGetResourceImageSource(resourceKey);
            if (trophy == null)
            {
                return;
            }

            var iconSize = Math.Max(10, fontSize * 1.25);
            var image = new Image
            {
                Source = trophy,
                Stretch = Stretch.Uniform,
                Width = iconSize,
                Height = iconSize,
                Margin = new Thickness(1, 0, 1, -2)
            };

            textBlock.Inlines.Add(new System.Windows.Documents.InlineUIContainer(image)
            {
                BaselineAlignment = BaselineAlignment.Center
            });
            hasVisibleContent = true;
        }

        private static string ResolveCustomTemplateToken(
            string tokenName,
            string title,
            string gameName,
            string achievementName,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            string providerKey,
            string style,
            Game game,
            string sourceName,
            DateTime timestamp,
            string fallback)
        {
            switch ((tokenName ?? string.Empty).ToLowerInvariant())
            {
                case "title":
                    return title ?? string.Empty;
                case "gamename":
                    return gameName ?? string.Empty;
                case "achievementname":
                    return achievementName ?? string.Empty;
                case "achievementdescription":
                    return achievementDescription ?? string.Empty;
                case "points":
                    return achievementPoints?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                case "rarity":
                    return achievementRarity ?? string.Empty;
                case "trophy":
                    return FormatTrophyText(achievementTrophy);
                case "provider":
                    return providerKey ?? string.Empty;
                case "style":
                    return style ?? string.Empty;
                case "date":
                    return timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "time":
                    return timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                case "datetime":
                    return timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case "source":
                    return sourceName ?? string.Empty;
                case "gameid":
                    return game?.Id.ToString() ?? string.Empty;
                default:
                    return fallback;
            }
        }

        private static string FormatTrophyText(string trophy)
        {
            if (string.IsNullOrWhiteSpace(trophy))
            {
                return string.Empty;
            }

            var normalized = trophy.Trim();
            switch (normalized.ToLowerInvariant())
            {
                case "p":
                case "platinum":
                    return "Platinum";
                case "g":
                case "gold":
                    return "Gold";
                case "s":
                case "silver":
                    return "Silver";
                case "b":
                case "bronze":
                    return "Bronze";
                default:
                    return normalized;
            }
        }

        private FrameworkElement CreateCustomOverlayIconContent(LocalSettings settings, string rawIconPath, string providerKey, Brush titleBrush, double iconSize, double titleSize, string rarityKey)
        {
            var iconSourceMode = settings?.OverlayCustomIconSource ?? LocalOverlayIconSource.AchievementIcon;

            if (iconSourceMode == LocalOverlayIconSource.AchievementIcon)
            {
                var iconSource = TryCreateOverlayImageSource(rawIconPath);
                if (iconSource != null)
                {
                    return new Image
                    {
                        Source = iconSource,
                        Stretch = Stretch.UniformToFill,
                        Width = iconSize,
                        Height = iconSize
                    };
                }
            }

            if (iconSourceMode == LocalOverlayIconSource.PentagonIcon)
            {
                var rarityBadge = TryGetResourceImageSource(GetRarityBadgeResourceKey(rarityKey));
                if (rarityBadge != null)
                {
                    return new Image
                    {
                        Source = rarityBadge,
                        Stretch = Stretch.Uniform,
                        Width = iconSize,
                        Height = iconSize
                    };
                }

                return new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(iconSize * 0.50, iconSize * 0.08),
                        new Point(iconSize * 0.90, iconSize * 0.38),
                        new Point(iconSize * 0.75, iconSize * 0.88),
                        new Point(iconSize * 0.25, iconSize * 0.88),
                        new Point(iconSize * 0.10, iconSize * 0.38)
                    },
                    Fill = titleBrush,
                    Opacity = 0.9,
                    Stretch = Stretch.Fill,
                    Width = iconSize * 0.82,
                    Height = iconSize * 0.82,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            if (iconSourceMode == LocalOverlayIconSource.TrophyIcon)
            {
                var trophy = TryGetResourceImageSource(GetTrophyResourceKey(rarityKey));
                if (trophy != null)
                {
                    return new Image
                    {
                        Source = trophy,
                        Stretch = Stretch.Uniform,
                        Width = iconSize,
                        Height = iconSize
                    };
                }
            }

            if (iconSourceMode == LocalOverlayIconSource.ProviderIcon)
            {
                var providerIcon = CreateProviderIconContent(settings, providerKey, titleBrush, iconSize, titleSize);
                if (providerIcon != null)
                {
                    return providerIcon;
                }
            }

            return new TextBlock
            {
                Text = "🏆",
                Foreground = titleBrush,
                FontSize = Math.Max(14, titleSize + 2),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private FrameworkElement CreateProviderIconContent(LocalSettings settings, string providerKey, Brush fallbackBrush, double iconSize, double titleSize)
        {
            var resolvedProviderKey = string.IsNullOrWhiteSpace(providerKey) ? "Local" : providerKey.Trim();
            if (TryResolveProviderVisuals(settings, resolvedProviderKey, out var iconKey, out var colorHex))
            {
                var iconSource = TryCreateOverlayImageSource(iconKey) ?? TryCreateProviderGeometryImageSource(iconKey, colorHex);
                if (iconSource != null)
                {
                    return new Image
                    {
                        Source = iconSource,
                        Stretch = Stretch.Uniform,
                        Width = iconSize,
                        Height = iconSize,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
            }

            var providerGlyph = resolvedProviderKey.Substring(0, 1).ToUpperInvariant();
            return new TextBlock
            {
                Text = providerGlyph,
                Foreground = fallbackBrush,
                FontSize = Math.Max(14, titleSize + 2),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static bool TryResolveProviderVisuals(LocalSettings settings, string providerKey, out string iconKey, out string colorHex)
        {
            iconKey = null;
            colorHex = null;

            if (string.Equals(providerKey, "Local", StringComparison.OrdinalIgnoreCase) && settings != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.CustomProviderIconPath) && File.Exists(settings.CustomProviderIconPath))
                {
                    iconKey = settings.CustomProviderIconPath;
                    colorHex = "#FF8A00";
                    return true;
                }

                var borrowedProviderKey = settings.BorrowedProviderIconKey?.Trim();
                if (!string.IsNullOrWhiteSpace(borrowedProviderKey) &&
                    ProviderRegistry.Instance?.TryGetProviderVisuals(borrowedProviderKey, out iconKey, out colorHex) == true)
                {
                    return true;
                }

                iconKey = "ProviderIconLocal";
                colorHex = "#FF8A00";
                return true;
            }

            return ProviderRegistry.Instance?.TryGetProviderVisuals(providerKey, out iconKey, out colorHex) == true;
        }

        private static ImageSource TryCreateProviderGeometryImageSource(string iconKey, string colorHex)
        {
            if (string.IsNullOrWhiteSpace(iconKey))
            {
                return null;
            }

            try
            {
                var geoKey = iconKey.StartsWith("Geo", StringComparison.Ordinal)
                    ? iconKey
                    : "Geo" + iconKey.Replace("ProviderIcon", string.Empty);
                var geometry = Application.Current?.TryFindResource(geoKey) as Geometry;
                if (geometry == null)
                {
                    return null;
                }

                var color = Colors.White;
                try
                {
                    if (!string.IsNullOrWhiteSpace(colorHex) && ColorConverter.ConvertFromString(colorHex) is Color parsed)
                    {
                        color = parsed;
                    }
                }
                catch
                {
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

        private static string ResolveRarityKey(string rarityText, int? points)
        {
            var value = rarityText?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value.Contains("ultra") || value.Contains("platinum")) return "UltraRare";
            if (value.Contains("uncommon")) return "Uncommon";
            if (value.Contains("rare")) return "Rare";
            if (value.Contains("common")) return "Common";

            if (points.HasValue)
            {
                if (points.Value >= 90) return "UltraRare";
                if (points.Value >= 50) return "Rare";
                if (points.Value >= 20) return "Uncommon";
            }

            return "Common";
        }

        private static string GetRarityBadgeResourceKey(string rarityKey)
        {
            switch (rarityKey)
            {
                case "UltraRare":
                    return "BadgeRarityUltraRare";
                case "Rare":
                    return "BadgeRarityRare";
                case "Uncommon":
                    return "BadgeRarityUncommon";
                default:
                    return "BadgeRarityCommon";
            }
        }

        private static string GetTrophyResourceKeyForTrophy(string trophy)
        {
            if (string.IsNullOrWhiteSpace(trophy))
            {
                return null;
            }

            switch (trophy.Trim().ToLowerInvariant())
            {
                case "p":
                case "platinum":
                    return "TrophyPlatinum";
                case "g":
                case "gold":
                    return "TrophyGold";
                case "s":
                case "silver":
                    return "TrophySilver";
                case "b":
                case "bronze":
                    return "TrophyBronze";
                default:
                    return null;
            }
        }

        private static string GetTrophyResourceKey(string rarityKey)
        {
            switch (rarityKey)
            {
                case "UltraRare":
                    return "TrophyPlatinum";
                case "Rare":
                    return "TrophyGold";
                case "Uncommon":
                    return "TrophySilver";
                default:
                    return "TrophyBronze";
            }
        }

        private static ImageSource TryGetResourceImageSource(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            try
            {
                return Application.Current?.TryFindResource(key) as ImageSource;
            }
            catch
            {
                return null;
            }
        }

        private static System.Windows.Media.Effects.Effect CreateIconRarityGlowEffect(string rarityKey)
        {
            Color glow;
            switch (rarityKey)
            {
                case "UltraRare":
                    glow = Color.FromRgb(124, 214, 255);
                    break;
                case "Rare":
                    glow = Color.FromRgb(255, 223, 133);
                    break;
                case "Uncommon":
                    glow = Color.FromRgb(194, 221, 255);
                    break;
                default:
                    glow = Color.FromRgb(230, 186, 144);
                    break;
            }

            return new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = glow,
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.68
            };
        }

        private static (System.Windows.Media.Brush Background, System.Windows.Media.Brush Border, System.Windows.Media.Brush Accent) ResolveOverlayBrushes(string style)
        {
            if (string.Equals(style, NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    new SolidColorBrush(Color.FromRgb(12, 30, 78)),
                    new SolidColorBrush(Color.FromRgb(53, 121, 246)),
                    new SolidColorBrush(Color.FromRgb(147, 201, 255)));
            }

            if (string.Equals(style, NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    new SolidColorBrush(Color.FromRgb(15, 41, 21)),
                    new SolidColorBrush(Color.FromRgb(57, 166, 84)),
                    new SolidColorBrush(Color.FromRgb(165, 238, 173)));
            }

            if (string.Equals(style, NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    new SolidColorBrush(Color.FromRgb(28, 28, 28)),
                    new SolidColorBrush(Color.FromRgb(65, 65, 65)),
                    new SolidColorBrush(Color.FromRgb(230, 230, 230)));
            }

            return (
                new SolidColorBrush(Color.FromRgb(22, 34, 48)),
                new SolidColorBrush(Color.FromRgb(72, 99, 134)),
                new SolidColorBrush(Color.FromRgb(151, 205, 255)));
        }

        private Brush ResolveCustomBackgroundBrush(LocalSettings settings)
        {
            var imagePath = settings?.OverlayCustomBackgroundImagePath?.Trim();
            var imageUri = ResolveUsableNotificationImageUri(imagePath);
            if (!string.IsNullOrWhiteSpace(imageUri))
            {
                try
                {
                    var imageBrush = new ImageBrush
                    {
                        ImageSource = new BitmapImage(new Uri(imageUri, UriKind.Absolute)),
                        Stretch = Stretch.UniformToFill,
                        Opacity = 0.92
                    };
                    imageBrush.Freeze();
                    return imageBrush;
                }
                catch
                {
                }
            }

            return ParseBrushOrDefault(settings?.OverlayCustomBackgroundColor, Color.FromRgb(30, 36, 48));
        }

        private static Brush ParseBrushOrDefault(string colorValue, Color fallback)
        {
            if (!string.IsNullOrWhiteSpace(colorValue))
            {
                try
                {
                    var parsed = (Color)ColorConverter.ConvertFromString(colorValue.Trim());
                    var brush = new SolidColorBrush(parsed);
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                }
            }

            var fallbackBrush = new SolidColorBrush(fallback);
            fallbackBrush.Freeze();
            return fallbackBrush;
        }

        private string ResolvePlayniteImagePath(string imageId)
        {
            var trimmedImageId = imageId?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedImageId))
            {
                return null;
            }

            var resolvedPath = _api?.Database?.GetFullFilePath(trimmedImageId)?.Trim();
            return string.IsNullOrWhiteSpace(resolvedPath) ? trimmedImageId : resolvedPath;
        }

        private ImageSource TryCreatePlayniteGameImageSource(Game game, bool useBackground)
        {
            if (game == null)
            {
                return null;
            }

            var imagePath = useBackground
                ? ResolvePlayniteImagePath(game.BackgroundImage)
                : ResolvePlayniteImagePath(game.CoverImage);

            return TryCreateOverlayImageSource(imagePath);
        }

        private static Brush CreateOverlayTintBrush(Brush baseBrush, byte alpha)
        {
            if (baseBrush is SolidColorBrush solidBrush)
            {
                var color = solidBrush.Color;
                var tintedBrush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
                tintedBrush.Freeze();
                return tintedBrush;
            }

            var fallbackBrush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            fallbackBrush.Freeze();
            return fallbackBrush;
        }

        private static void AddBannerBackground(Grid container, ImageSource bannerSource, Brush tintBrush, double bannerOpacity, int blurRadius, double cornerRadius)
        {
            if (container == null || bannerSource == null)
            {
                return;
            }

            var imageBrush = new ImageBrush
            {
                ImageSource = bannerSource,
                Stretch = Stretch.UniformToFill,
                Opacity = Math.Max(0.1, Math.Min(1.0, bannerOpacity))
            };

            container.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = imageBrush,
                RadiusX = Math.Max(0, cornerRadius),
                RadiusY = Math.Max(0, cornerRadius),
                Effect = blurRadius > 0
                    ? new System.Windows.Media.Effects.BlurEffect { Radius = blurRadius }
                    : null,
                IsHitTestVisible = false
            });

            if (tintBrush != null)
            {
                container.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Fill = tintBrush,
                    RadiusX = Math.Max(0, cornerRadius),
                    RadiusY = Math.Max(0, cornerRadius),
                    IsHitTestVisible = false
                });
            }
        }

        private FrameworkElement CreateGameCoverElement(Game game, double width, double height, double cornerRadius, Thickness margin, string customImagePath = null)
        {
            var coverSource = TryCreateOverlayImageSource(customImagePath) ?? TryCreatePlayniteGameImageSource(game, useBackground: false);
            if (coverSource == null)
            {
                return null;
            }

            return new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(cornerRadius),
                Margin = margin,
                Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                Child = new Image
                {
                    Source = coverSource,
                    Stretch = Stretch.UniformToFill,
                    Width = width,
                    Height = height
                }
            };
        }

        private ImageSource TryCreateOverlayImageSource(string rawIconPath)
        {
            var imageSource = ResolveUsableNotificationImageUri(rawIconPath);
            if (string.IsNullOrWhiteSpace(imageSource))
            {
                return null;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imageSource, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private string ResolveUsableNotificationImageUri(string rawIconPath)
        {
            var iconPath = NormalizeRawIconPath(rawIconPath);
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return string.Empty;
            }

            if (iconPath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (iconPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return iconPath;
            }

            if (iconPath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return iconPath;
            }

            if (Path.IsPathRooted(iconPath) && File.Exists(iconPath))
            {
                return new Uri(iconPath).AbsoluteUri;
            }

            if (iconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                iconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var cachedPath = TryCacheRemoteIcon(iconPath);
                if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                {
                    return new Uri(cachedPath).AbsoluteUri;
                }
            }

            return string.Empty;
        }

        private static string NormalizeRawIconPath(string rawIconPath)
        {
            if (string.IsNullOrWhiteSpace(rawIconPath))
            {
                return string.Empty;
            }

            var iconPath = rawIconPath.Trim();
            if (iconPath.StartsWith("cachebust|", StringComparison.OrdinalIgnoreCase))
            {
                var secondPipe = iconPath.IndexOf('|', "cachebust|".Length);
                if (secondPipe >= 0 && secondPipe + 1 < iconPath.Length)
                {
                    iconPath = iconPath.Substring(secondPipe + 1);
                }
            }

            if (iconPath.StartsWith("gray:", StringComparison.OrdinalIgnoreCase))
            {
                iconPath = iconPath.Substring("gray:".Length);
            }

            return iconPath.Trim();
        }

        private string TryCacheRemoteIcon(string url)
        {
            try
            {
                var uri = new Uri(url, UriKind.Absolute);
                var extension = Path.GetExtension(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
                {
                    extension = ".png";
                }

                var cacheDir = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "ToastIcons");
                Directory.CreateDirectory(cacheDir);

                var hash = ComputeSha1(url);
                var filePath = Path.Combine(cacheDir, hash + extension.ToLowerInvariant());
                if (File.Exists(filePath))
                {
                    return filePath;
                }

                using (var client = new System.Net.WebClient())
                {
                    client.DownloadFile(url, filePath);
                }

                return filePath;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalToast] Failed to cache remote icon: {url}");
                return string.Empty;
            }
        }

        private static string ComputeSha1(string value)
        {
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                var hash = sha1.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
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
