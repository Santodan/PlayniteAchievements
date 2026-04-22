using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Security;
using System.Text;
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
        public const string NotificationStyleSteam = "Steam";
        public const string NotificationStylePlayStation = "PlayStation";
        public const string NotificationStyleXbox = "Xbox";
        public const string NotificationStyleMinimal = "Minimal";
        public const string NotificationStyleCustom = "Custom";

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

        public void ShowLocalAchievementUnlocked(string gameName, IReadOnlyList<string> unlockedAchievementNames, string customSoundPath, string unlockedAchievementIconPath = null)
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
            var localSettings = ProviderRegistry.Settings<LocalSettings>();
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

            // Send Windows native toast notification
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
                                providerKey: "Local");
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
                        providerKey: "Local");
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
            LocalSettings overrideLocalSettings = null)
        {
            var localSettings = overrideLocalSettings ?? ProviderRegistry.Settings<LocalSettings>();
            var mode = forcedDeliveryMode ?? localSettings?.UnlockNotificationDeliveryMode ?? LocalUnlockNotificationDeliveryMode.Hybrid;
            var style = ResolveUnlockNotificationStyle(providerKey, forcedStyle);

            if (mode == LocalUnlockNotificationDeliveryMode.Overlay || mode == LocalUnlockNotificationDeliveryMode.Hybrid)
            {
                ShowOverlayUnlockNotification(gameName, achievementName, achievementIconPath, style, providerKey, localSettings);
            }

            if (mode == LocalUnlockNotificationDeliveryMode.WindowsToast || mode == LocalUnlockNotificationDeliveryMode.Hybrid)
            {
                SendWindowsToastNotification(gameName, achievementName, achievementIconPath, providerKey, forcedStyle, localSettings);
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

        private void ShowOverlayUnlockNotification(string gameName, string achievementName, string achievementIconPath, string style, string providerKey, LocalSettings overrideLocalSettings = null)
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
                    var title = ResolveOverlayTitle(style);
                    var safeGameName = string.IsNullOrWhiteSpace(gameName) ? "Current Game" : gameName.Trim();
                    var safeAchievement = string.IsNullOrWhiteSpace(achievementName) ? "Achievement unlocked" : achievementName.Trim();
                    var isCustomStyle = string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase);
                    var autoResizeCustom = isCustomStyle && (localSettings?.OverlayCustomAutoResizeToContent == true);

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
                    overlayWindow.Content = BuildOverlayContent(title, safeGameName, safeAchievement, achievementIconPath, style, providerKey, localSettings, overlayScale);

                    overlayWindow.Loaded += (sender, args) =>
                    {
                        if (autoResizeCustom)
                        {
                            PositionOverlayWindow(overlayWindow, position);
                        }

                        var fadeIn = new DoubleAnimation(0, overlayOpacity, new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeInMs))));
                        overlayWindow.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                        var closeTimer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(durationMs)
                        };

                        closeTimer.Tick += (timerSender, timerArgs) =>
                        {
                            closeTimer.Stop();
                            var fadeOut = new DoubleAnimation(overlayOpacity, 0, new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeOutMs))));
                            fadeOut.Completed += (_, __) => overlayWindow.Close();
                            overlayWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                        };

                        closeTimer.Start();
                    };

                    overlayWindow.Show();
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

        private FrameworkElement BuildOverlayContent(string title, string gameName, string achievementName, string rawIconPath, string style, string providerKey, LocalSettings localSettings, double overlayScale)
        {
            if (string.Equals(style, NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return BuildCustomOverlayContent(title, gameName, achievementName, rawIconPath, providerKey, localSettings, overlayScale);
            }

            var (backgroundBrush, borderBrush, accentBrush) = ResolveOverlayBrushes(style);
            var iconSize = Math.Max(40, 58 * overlayScale);
            var titleSize = Math.Max(12, 15 * overlayScale);
            var detailSize = Math.Max(11, 13 * overlayScale);
            var metaSize = Math.Max(10, 11 * overlayScale);

            var root = new Border
            {
                Background = backgroundBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(Math.Max(8, 12 * overlayScale)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.5,
                    Color = Colors.Black
                }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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

            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

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

            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            root.Child = grid;
            return root;
        }

        private FrameworkElement BuildCustomOverlayContent(string title, string gameName, string achievementName, string rawIconPath, string providerKey, LocalSettings settings, double overlayScale)
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

            var root = new Border
            {
                Background = backgroundBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(cornerRadius),
                Padding = new Thickness(16),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = Colors.Black
                }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new Border
            {
                Width = iconSize,
                Height = iconSize,
                Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
                CornerRadius = new CornerRadius(Math.Max(6, cornerRadius / 2.5)),
                Margin = new Thickness(0, 0, 14, 0)
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
                    Foreground = titleBrush,
                    FontSize = Math.Max(14, titleSize + 2),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            textStack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = titleBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = titleSize,
                TextTrimming = wrapAllText ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = wrapAllText ? TextWrapping.Wrap : TextWrapping.NoWrap
            });

            textStack.Children.Add(new TextBlock
            {
                Text = gameName,
                Foreground = detailBrush,
                FontSize = detailSize,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = wrapAllText ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = wrapAllText ? TextWrapping.Wrap : TextWrapping.NoWrap
            });

            textStack.Children.Add(new TextBlock
            {
                Text = $"Unlocked: {achievementName}",
                Foreground = accentBrush,
                FontSize = detailSize,
                Margin = new Thickness(0, 3, 0, 0),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = wrapAllText ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = wrapAllText ? TextWrapping.Wrap : TextWrapping.NoWrap
            });

            textStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(providerKey) ? "Local / Custom" : $"{providerKey} / Custom",
                Foreground = metaBrush,
                FontSize = metaSize,
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = wrapAllText ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = wrapAllText ? TextWrapping.Wrap : TextWrapping.NoWrap
            });

            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            root.Child = grid;
            return root;
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
