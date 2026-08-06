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
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Services
{
    public class NotificationPublisher
    {
        private static readonly Regex OverlayTemplateTokenPattern = new Regex("<([a-zA-Z0-9]+)>", RegexOptions.Compiled);
        private static readonly object NotificationScoreCacheLock = new object();
        private static string _notificationScoreCacheKey;
        private static DateTime _notificationScoreCacheUtc;
        private static NotificationScoreContext _notificationScoreCache;

        public const string NotificationStyleSteam = "Steam";
        public const string NotificationStylePlayStation = "PlayStation";
        public const string NotificationStyleXbox = "Xbox";
        public const string NotificationStyleMinimal = "Minimal";
        public const string NotificationStyleCustom = "Custom";

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private static Window _persistentSettingsPreviewOverlay;
        private static readonly List<OverlayWindowState> ActiveOverlayWindows = new List<OverlayWindowState>();
        private static readonly List<IWebView> ActiveSanOverlayWebViews = new List<IWebView>();
        private static readonly object WebView2LoaderSync = new object();
        private static bool _webView2LoaderConfigured;
        private static bool _sanWebViewWarmupStarted;
        private static Task<CoreWebView2Environment> _sanWebViewEnvironmentTask;
        private static Window _sanWebViewWarmupWindow;
        private static WebView2 _sanWebViewWarmupControl;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        public NotificationPublisher(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            WarmUpSanWebView2();
        }

        private void LogAchievementNotificationDebug(string message)
        {
            AchievementNotificationDebugLog.Info(message);
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
            var achievementItems = unlockedAchievementNames?
                .Select((name, index) => new AchievementUnlockNotificationItem(
                    name,
                    index == 0 ? unlockedAchievementIconPath : null,
                    index == 0 ? achievementDescription : null,
                    index == 0 ? achievementPoints : null,
                    index == 0 ? achievementRarity : null,
                    index == 0 ? achievementTrophy : null))
                .ToList();

            ShowLocalAchievementUnlocked(
                gameName,
                achievementItems,
                customSoundPath,
                forcedStyle,
                forcedDeliveryMode,
                overrideLocalSettings,
                notificationProviderKey,
                game);
        }

        public void ShowLocalAchievementUnlocked(
            string gameName,
            IReadOnlyList<AchievementUnlockNotificationItem> unlockedAchievements,
            string customSoundPath,
            string forcedStyle = null,
            LocalUnlockNotificationDeliveryMode? forcedDeliveryMode = null,
            LocalSettings overrideLocalSettings = null,
            string notificationProviderKey = "Local",
            Game game = null)
        {
            var achievements = unlockedAchievements?
                .Where(item => !string.IsNullOrWhiteSpace(item?.Name))
                .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList() ?? new List<AchievementUnlockNotificationItem>();
            var names = achievements.Select(item => item.Name.Trim()).ToList();
            var unlockCount = Math.Max(unlockedAchievements?.Count ?? 0, achievements.Count);
            if (unlockCount <= 0)
            {
                return;
            }

            var localSettings = overrideLocalSettings ?? ProviderRegistry.Settings<LocalSettings>();
            var resolvedProviderKey = string.IsNullOrWhiteSpace(notificationProviderKey) ? "Local" : notificationProviderKey.Trim();
            var enableInAppNotification = localSettings?.EnableInAppUnlockNotifications != false;
            if (localSettings?.EnableOverlayDebugLogging == true)
            {
                AchievementNotificationDebugLog.LogSettingsSnapshot(
                    localSettings,
                    _settings?.Persisted,
                    "notification-dispatch");
                LogAchievementNotificationDebug(
                    $"Unlock batch received game='{gameName}', gameId='{game?.Id}', provider='{resolvedProviderKey}', " +
                    $"inputCount='{unlockedAchievements?.Count ?? 0}', usableCount='{achievements.Count}', " +
                    $"deliveryMode='{localSettings.UnlockNotificationDeliveryMode}', inAppNotification='{enableInAppNotification}', " +
                    $"soundConfigured='{!string.IsNullOrWhiteSpace(customSoundPath)}', soundLeadMs='{localSettings.UnlockSoundLeadMilliseconds}'.");
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

            if (achievements.Count > 0)
            {
                var soundLeadMs = localSettings?.UnlockSoundLeadMilliseconds ?? 0;
                void PlayUnlockSound()
                {
                    PlayCustomSound(customSoundPath);
                }

                void SendAllUnlockPopups()
                {
                    foreach (var achievement in achievements)
                    {
                        SendUnlockPopup(
                            safeGameName,
                            achievement.Name,
                            achievement.IconPath,
                            providerKey: resolvedProviderKey,
                            game: game,
                            achievementDescription: achievement.Description,
                            achievementPoints: achievement.Points,
                            achievementRarity: achievement.Rarity,
                            achievementTrophy: achievement.Trophy,
                            forcedStyle: forcedStyle,
                            forcedDeliveryMode: forcedDeliveryMode,
                            overrideLocalSettings: localSettings);
                    }
                }

                if (soundLeadMs > 0)
                {
                    PlayUnlockSound();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(soundLeadMs).ConfigureAwait(false);
                            SendAllUnlockPopups();
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "Failed to send delayed Local unlock popup.");
                        }
                    });
                }
                else if (soundLeadMs < 0)
                {
                    SendAllUnlockPopups();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(Math.Abs((long)soundLeadMs))).ConfigureAwait(false);
                            PlayUnlockSound();
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "Failed to play delayed Local unlock sound.");
                        }
                    });
                }
                else
                {
                    PlayUnlockSound();
                    SendAllUnlockPopups();
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
            // Achievement notifications use the custom renderer by default. The provider key
            // supplies provider-specific icons and wildcard values; it does not select a style.
            // A forced style is reserved for the explicit built-in style preview actions.
            var style = string.IsNullOrWhiteSpace(forcedStyle)
                ? NotificationStyleCustom
                : ResolveUnlockNotificationStyle(providerKey, forcedStyle);

            _logger?.Info(
                $"[LocalOverlay] Dispatching unlock popup provider='{providerKey}', mode='{mode}', style='{style}', " +
                $"transition='{localSettings?.UnlockOverlayTransitionStyle}', sanElement='{localSettings?.OverlayCustomSanElementPresetId ?? string.Empty}'.");
            if (localSettings?.EnableOverlayDebugLogging == true)
            {
                LogAchievementNotificationDebug(
                    $"[LocalOverlayDebug] Settings position='{localSettings.UnlockOverlayPosition}', " +
                    $"followActiveMonitor='{localSettings.ShowOverlayOnActiveGameMonitor}', " +
                    $"customSize='{localSettings.OverlayCustomWidth:0.###}x{localSettings.OverlayCustomHeight:0.###}', " +
                    $"customOpacity='{localSettings.OverlayCustomOpacity:0.###}', gameId='{game?.Id}', " +
                    $"iconPath='{achievementIconPath ?? string.Empty}', iconExists='{(!string.IsNullOrWhiteSpace(achievementIconPath) && File.Exists(achievementIconPath))}', " +
                    $"descriptionPresent='{!string.IsNullOrWhiteSpace(achievementDescription)}', points='{achievementPoints}', rarity='{achievementRarity}', trophy='{achievementTrophy}'.");
            }

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
                SendWindowsToastNotification(gameName, achievementName, achievementIconPath, providerKey, style, localSettings);
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
            var height = Math.Max(LocalSettings.MinCustomOverlayHeight, localSettings?.OverlayCustomHeight ?? 128);
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
                    AchievementNotificationDebugLog.Warn($"Configured unlock sound was not found: '{soundPath}'.");
                    return;
                }

                AchievementNotificationDebugLog.Info($"Playing unlock sound from '{soundPath}'.");

                _ = Task.Run(() =>
                {
                    try
                    {
                        using (var player = new SoundPlayer(soundPath))
                        {
                            player.PlaySync();
                        }
                        AchievementNotificationDebugLog.Info("Unlock sound playback completed.");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Failed to play Local unlock sound: {soundPath}");
                        AchievementNotificationDebugLog.Error(ex, $"Unlock sound playback failed for '{soundPath}'.");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to play Local unlock sound: {soundPath}");
                AchievementNotificationDebugLog.Error(ex, $"Failed to resolve or start the unlock sound '{soundPath}'.");
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
                    AchievementNotificationDebugLog.Info("Windows toast skipped because EnableWindowsToastNotifications is disabled.");
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
                AchievementNotificationDebugLog.Info(
                    $"Starting Windows toast provider='{providerKey}', style='{style}', imageResolved='{!string.IsNullOrWhiteSpace(toastIconSource)}'.");
                var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    _logger?.Warn("[LocalToast] PowerShell process did not start (Process.Start returned null).");
                    AchievementNotificationDebugLog.Warn("Windows toast PowerShell process did not start (Process.Start returned null).");
                    return;
                }

                _ = Task.Run(() =>
                {
                    try
                    {
                        if (!process.WaitForExit(5000))
                        {
                            _logger?.Warn($"[LocalToast] PowerShell toast process timed out. Pid={process.Id}");
                            AchievementNotificationDebugLog.Warn($"Windows toast PowerShell process timed out; pid='{process.Id}'.");
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
                            AchievementNotificationDebugLog.Info(
                                $"Windows toast command completed exitCode='0', stdout='{logStdout}', stderr='{logStderr}'.");
                        }
                        else
                        {
                            _logger?.Warn($"[LocalToast] PowerShell toast command failed. ExitCode={process.ExitCode}, StdOut={logStdout}, StdErr={logStderr}");
                            AchievementNotificationDebugLog.Warn(
                                $"Windows toast command failed exitCode='{process.ExitCode}', stdout='{logStdout}', stderr='{logStderr}'.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "[LocalToast] Failed while waiting for PowerShell toast command result.");
                        AchievementNotificationDebugLog.Error(ex, "Failed while waiting for the Windows toast command result.");
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
                AchievementNotificationDebugLog.Error(ex, "Failed to construct or send the Windows toast notification.");
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
                var debugLoggingEnabled = localSettings?.EnableOverlayDebugLogging == true;

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
                            ? Math.Max(LocalSettings.MinCustomOverlayHeight, localSettings?.OverlayCustomHeight ?? 128)
                            : 110 * overlayScale;

                        if (debugLoggingEnabled)
                        {
                            LogAchievementNotificationDebug(
                                $"Renderer selection style='{style}', custom='{isCustomStyle}', sanSelected='{(localSettings != null && (IsSanTransitionStyle(localSettings.UnlockOverlayTransitionStyle) || !string.IsNullOrWhiteSpace(localSettings.OverlayCustomSanElementPresetId)))}', " +
                                $"sizeDip='{width:0.###}x{height:0.###}', autoResize='{autoResizeCustom}', opacity='{overlayOpacity:0.###}', " +
                                $"durationMs='{durationMs}', fadeInMs='{fadeInMs}', fadeOutMs='{fadeOutMs}'.");
                        }

                        if (isCustomStyle &&
                            TryShowSanHtmlOverlayNotification(
                                safeGameName,
                                safeAchievement,
                                achievementIconPath,
                                providerKey,
                                localSettings,
                                game,
                                achievementDescription,
                                achievementPoints,
                                achievementRarity,
                                achievementTrophy,
                                durationMs,
                                position,
                                width,
                                height,
                                persistentPreviewRequested))
                        {
                            return;
                        }

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

                        overlayWindow.Content = BuildOverlayContent(title, safeGameName, safeAchievement, achievementIconPath, style, providerKey, localSettings, overlayScale, game, achievementDescription, achievementPoints, achievementRarity, achievementTrophy);
                        AttachOverlayTopmostGuard(overlayWindow, debugLoggingEnabled);
                        var overlayState = persistentPreviewRequested
                            ? null
                            : RegisterOverlayWindow(overlayWindow, position);
                        PositionOverlayWindow(overlayWindow, position, GetOverlayStackIndex(overlayState), localSettings?.ShowOverlayOnActiveGameMonitor == true, debugLoggingEnabled);
                        LogOverlayWindowLifecycle(overlayWindow, "WPF", "positioned-before-show", overlayOpacity, debugLoggingEnabled);

                        overlayWindow.Loaded += (sender, args) =>
                        {
                            try
                            {
                                if (autoResizeCustom)
                                {
                                    PositionOverlayWindow(overlayWindow, position, GetOverlayStackIndex(overlayState), localSettings?.ShowOverlayOnActiveGameMonitor == true, debugLoggingEnabled);
                                }

                                LogOverlayWindowLifecycle(overlayWindow, "WPF", "loaded-before-animation", overlayOpacity, debugLoggingEnabled);
                                ApplyOverlayEnterAnimation(overlayWindow, overlayOpacity, fadeInMs, transitionStyle, slideDistance);
                                if (debugLoggingEnabled)
                                {
                                    LogAchievementNotificationDebug(
                                        $"[LocalOverlayDebug] WPF enter animation started targetOpacity='{overlayOpacity:0.###}', " +
                                        $"fadeInMs='{fadeInMs}', transition='{transitionStyle}', slideDistance='{slideDistance}'.");
                                    ScheduleOverlayWindowLifecycleSample(
                                        overlayWindow,
                                        "WPF",
                                        "post-enter-animation",
                                        Math.Max(50, fadeInMs + 100),
                                        overlayOpacity,
                                        debugLoggingEnabled);
                                }

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
                                    if (overlayState != null)
                                    {
                                        overlayState.IsClosing = true;
                                    }

                                    ApplyOverlayExitAnimation(overlayWindow, overlayOpacity, fadeOutMs, transitionStyle, slideDistance, () => overlayWindow.Close());
                                };

                                closeTimer.Start();
                            }
                            catch (Exception animEx)
                            {
                                _logger?.Warn(animEx, "[LocalOverlay] Failed in loaded animation pipeline.");
                                AchievementNotificationDebugLog.Error(animEx, "The WPF overlay loaded-animation pipeline failed.");
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
                            LogOverlayWindowLifecycle(overlayWindow, "WPF", "closed", overlayOpacity, debugLoggingEnabled);
                            if (ReferenceEquals(_persistentSettingsPreviewOverlay, overlayWindow))
                            {
                                _persistentSettingsPreviewOverlay = null;
                            }

                            if (overlayState != null)
                            {
                                UnregisterOverlayWindow(overlayState);
                            }
                        };

                        if (persistentPreviewRequested)
                        {
                            _persistentSettingsPreviewOverlay = overlayWindow;
                        }

                        overlayWindow.Show();
                        LogOverlayWindowLifecycle(overlayWindow, "WPF", "show-returned", overlayOpacity, debugLoggingEnabled);
                    }
                    catch (Exception uiEx)
                    {
                        _logger?.Warn(uiEx, "[LocalOverlay] Failed to render overlay on UI thread.");
                        AchievementNotificationDebugLog.Error(uiEx, "The WPF overlay failed while rendering on the UI thread.");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LocalOverlay] Failed to show overlay notification.");
                AchievementNotificationDebugLog.Error(ex, "The overlay notification failed before or during UI dispatch.");
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

        private void PositionOverlayWindow(
            Window window,
            LocalUnlockOverlayPosition position,
            int stackIndex = 0,
            bool followActiveGameMonitor = true,
            bool debugLoggingEnabled = false)
        {
            if (window == null)
            {
                return;
            }

            const double margin = 16;
            const double spacing = 8;
            var systemWorkArea = SystemParameters.WorkArea;
            var monitorDiagnostics = new OverlayMonitorDiagnostics();
            var foregroundWorkArea = systemWorkArea;
            if (followActiveGameMonitor || debugLoggingEnabled)
            {
                foregroundWorkArea = GetForegroundMonitorWorkArea(out monitorDiagnostics);
            }

            var workArea = followActiveGameMonitor ? foregroundWorkArea : systemWorkArea;
            var stackOffset = Math.Max(0, stackIndex) * (GetOverlayWindowHeight(window) + spacing);
            switch (position)
            {
                case LocalUnlockOverlayPosition.TopLeft:
                    window.Left = workArea.Left + margin;
                    window.Top = workArea.Top + margin + stackOffset;
                    break;
                case LocalUnlockOverlayPosition.TopCenter:
                    window.Left = workArea.Left + ((workArea.Width - window.Width) / 2);
                    window.Top = workArea.Top + margin + stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomLeft:
                    window.Left = workArea.Left + margin;
                    window.Top = workArea.Bottom - GetOverlayWindowHeight(window) - margin - stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomCenter:
                    window.Left = workArea.Left + ((workArea.Width - window.Width) / 2);
                    window.Top = workArea.Bottom - GetOverlayWindowHeight(window) - margin - stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomRight:
                    window.Left = workArea.Right - window.Width - margin;
                    window.Top = workArea.Bottom - GetOverlayWindowHeight(window) - margin - stackOffset;
                    break;
                default:
                    window.Left = workArea.Right - window.Width - margin;
                    window.Top = workArea.Top + margin + stackOffset;
                    break;
            }

            if (debugLoggingEnabled)
            {
                var wpfDpi = VisualTreeHelper.GetDpi(window);
                LogAchievementNotificationDebug(
                    $"[LocalOverlayDebug] Positioned renderer='{ResolveOverlayRendererName(window)}', position='{position}', stackIndex='{stackIndex}', " +
                    $"followActiveMonitor='{followActiveGameMonitor}', foregroundHwnd='{FormatHandle(monitorDiagnostics.ForegroundWindow)}', " +
                    $"monitorHandle='{FormatHandle(monitorDiagnostics.Monitor)}', monitorInfo='{monitorDiagnostics.HasMonitorInfo}', " +
                    $"monitorBoundsRaw='{FormatNativeRect(monitorDiagnostics.MonitorBounds)}', monitorWorkRaw='{FormatNativeRect(monitorDiagnostics.WorkArea)}', " +
                    $"foregroundDpi='{monitorDiagnostics.ForegroundDpi}', wpfScale='{wpfDpi.DpiScaleX:0.###}x{wpfDpi.DpiScaleY:0.###}', " +
                    $"selectedWorkArea='{FormatRect(workArea)}', systemWorkAreaDip='{FormatRect(systemWorkArea)}', " +
                    $"windowBoundsDip='{window.Left:0.###},{window.Top:0.###},{window.Width:0.###},{GetOverlayWindowHeight(window):0.###}'.");
            }
        }

        private static double GetOverlayWindowHeight(Window window)
        {
            if (window == null)
            {
                return 0;
            }

            if (window.ActualHeight > 0)
            {
                return window.ActualHeight;
            }

            if (!double.IsNaN(window.Height) && window.Height > 0)
            {
                return window.Height;
            }

            if (window.MinHeight > 0)
            {
                return window.MinHeight;
            }

            return 110;
        }

        private static OverlayWindowState RegisterOverlayWindow(Window window, LocalUnlockOverlayPosition position)
        {
            if (window == null)
            {
                return null;
            }

            var state = new OverlayWindowState(window, position);
            ActiveOverlayWindows.RemoveAll(item => item?.Window == null || !item.Window.IsVisible);
            ActiveOverlayWindows.Add(state);
            return state;
        }

        private void UnregisterOverlayWindow(OverlayWindowState state)
        {
            if (state == null)
            {
                return;
            }

            ActiveOverlayWindows.Remove(state);
            RepositionActiveOverlayWindows(state.Position);
        }

        private static int GetOverlayStackIndex(OverlayWindowState state)
        {
            if (state == null)
            {
                return 0;
            }

            return ActiveOverlayWindows
                .Where(item => item != null && item.Position == state.Position)
                .TakeWhile(item => !ReferenceEquals(item, state))
                .Count();
        }

        private void RepositionActiveOverlayWindows(LocalUnlockOverlayPosition position)
        {
            var overlays = ActiveOverlayWindows
                .Where(item => item != null && item.Position == position && !item.IsClosing && item.Window?.IsVisible == true)
                .ToList();

            for (var i = 0; i < overlays.Count; i++)
            {
                var settings = ProviderRegistry.Settings<LocalSettings>();
                PositionOverlayWindow(
                    overlays[i].Window,
                    position,
                    i,
                    settings?.ShowOverlayOnActiveGameMonitor == true,
                    settings?.EnableOverlayDebugLogging == true);
            }
        }

        private sealed class OverlayWindowState
        {
            public OverlayWindowState(Window window, LocalUnlockOverlayPosition position)
            {
                Window = window;
                Position = position;
            }

            public Window Window { get; }
            public LocalUnlockOverlayPosition Position { get; }
            public bool IsClosing { get; set; }
        }

        private bool TryShowSanWebViewOverlayNotification(
            string gameName,
            string achievementName,
            string achievementIconPath,
            string providerKey,
            LocalSettings settings,
            Game game,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            int durationMs,
            LocalUnlockOverlayPosition position,
            double width,
            double height,
            bool persistentPreviewRequested = false)
        {
            return TryShowSanHtmlOverlayNotification(
                gameName,
                achievementName,
                achievementIconPath,
                providerKey,
                settings,
                game,
                achievementDescription,
                achievementPoints,
                achievementRarity,
                achievementTrophy,
                durationMs,
                position,
                width,
                height,
                persistentPreviewRequested);
        }

        private bool TryShowSanHtmlOverlayNotification(
            string gameName,
            string achievementName,
            string achievementIconPath,
            string providerKey,
            LocalSettings settings,
            Game game,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            int durationMs,
            LocalUnlockOverlayPosition position,
            double width,
            double height,
            bool persistentPreviewRequested = false)
        {
            var hasSanSelection = settings != null &&
                (IsSanTransitionStyle(settings.UnlockOverlayTransitionStyle) ||
                 !string.IsNullOrWhiteSpace(settings.OverlayCustomSanElementPresetId));
            if (settings == null || !hasSanSelection)
            {
                return false;
            }

            var debugLoggingEnabled = settings.EnableOverlayDebugLogging;

            try
            {
                var canvasWidth = (int)Math.Ceiling(Math.Max(1, width));
                var canvasHeight = (int)Math.Ceiling(Math.Max(1, height));
                var autoResizeToContent = settings.OverlayCustomAutoResizeToContent;
                var isSanTransition = IsSanTransitionStyle(settings.UnlockOverlayTransitionStyle);
                var overlayOpacity = Math.Max(0.35, Math.Min(1.0, settings.OverlayCustomOpacity));
                var fadeInMs = settings.UnlockOverlayFadeInMilliseconds;
                var fadeOutMs = settings.UnlockOverlayFadeOutMilliseconds;
                var slideDistance = settings.UnlockOverlaySlideDistance;
                _logger?.Info($"[LocalOverlay] Rendering SAN WebView overlay preset='{settings.OverlayCustomSanPresetId}', themeDir='{settings.OverlayCustomSanThemeDirectory}'.");
                var html = BuildSanWebViewDocument(
                    gameName,
                    achievementName,
                    achievementIconPath,
                    providerKey,
                    settings,
                    game,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    durationMs,
                    width,
                    height);
                var htmlPath = WriteSanWebViewDocumentToTempFile(html);

                var webView = new WebView2
                {
                    DefaultBackgroundColor = System.Drawing.Color.Transparent
                };

                var window = new Window
                {
                    Content = webView
                };
                window.Width = canvasWidth;
                window.Height = canvasHeight;
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                window.AllowsTransparency = true;
                window.Background = Brushes.Transparent;
                window.Topmost = true;
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.IsHitTestVisible = false;
                window.Focusable = false;
                window.Opacity = isSanTransition || persistentPreviewRequested ? 1 : 0;
                AttachOverlayTopmostGuard(window, debugLoggingEnabled);

                var overlayState = persistentPreviewRequested ? null : RegisterOverlayWindow(window, position);
                PositionOverlayWindow(window, position, GetOverlayStackIndex(overlayState), settings.ShowOverlayOnActiveGameMonitor, debugLoggingEnabled);
                LogOverlayWindowLifecycle(window, "SAN-WebView2", "positioned-before-show", overlayOpacity, debugLoggingEnabled);

                window.Closed += (_, __) =>
                {
                    LogOverlayWindowLifecycle(window, "SAN-WebView2", "closed", overlayOpacity, debugLoggingEnabled);
                    if (ReferenceEquals(_persistentSettingsPreviewOverlay, window))
                    {
                        _persistentSettingsPreviewOverlay = null;
                    }

                    if (overlayState != null)
                    {
                        UnregisterOverlayWindow(overlayState);
                    }

                    try
                    {
                        webView.Dispose();
                    }
                    catch
                    {
                    }

                    TryDeleteSanWebViewDocument(htmlPath);
                };

                window.Loaded += async (_, __) =>
                {
                    try
                    {
                        LogOverlayWindowLifecycle(window, "SAN-WebView2", "loaded-before-webview-init", overlayOpacity, debugLoggingEnabled);
                        var environment = await GetSanWebView2EnvironmentAsync();
                        await webView.EnsureCoreWebView2Async(environment);
                        LogOverlayWindowLifecycle(window, "SAN-WebView2", "webview-controller-ready", overlayOpacity, debugLoggingEnabled);
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        webView.CoreWebView2.NavigationCompleted += (_, navigationArgs) =>
                        {
                            if (debugLoggingEnabled)
                            {
                                LogAchievementNotificationDebug(
                                    $"[LocalOverlayDebug] SAN navigation completed success='{navigationArgs.IsSuccess}', " +
                                    $"webErrorStatus='{navigationArgs.WebErrorStatus}', source='{webView.Source}'.");
                            }

                            LogOverlayWindowLifecycle(window, "SAN-WebView2", "navigation-completed", overlayOpacity, debugLoggingEnabled);
                        };
                        if (autoResizeToContent)
                        {
                            webView.CoreWebView2.WebMessageReceived += (_, messageArgs) =>
                            {
                                const string prefix = "san-height:";
                                var message = messageArgs.TryGetWebMessageAsString();
                                if (string.IsNullOrWhiteSpace(message) || !message.StartsWith(prefix, StringComparison.Ordinal))
                                {
                                    return;
                                }

                                if (!double.TryParse(message.Substring(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var measuredHeight))
                                {
                                    return;
                                }

                                var resizedHeight = Math.Max(canvasHeight, Math.Min(2000, Math.Ceiling(measuredHeight)));
                                if (Math.Abs(window.Height - resizedHeight) < 1)
                                {
                                    return;
                                }

                                window.Height = resizedHeight;
                                PositionOverlayWindow(window, position, GetOverlayStackIndex(overlayState), settings.ShowOverlayOnActiveGameMonitor, debugLoggingEnabled);
                                LogOverlayWindowLifecycle(window, "SAN-WebView2", "content-height-resized", overlayOpacity, debugLoggingEnabled);
                            };
                        }
                        webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                        if (debugLoggingEnabled)
                        {
                            LogAchievementNotificationDebug($"[LocalOverlayDebug] SAN navigation started source='{new Uri(htmlPath).AbsoluteUri}'.");
                        }

                        if (!isSanTransition && !persistentPreviewRequested)
                        {
                            ApplyOverlayEnterAnimation(window, overlayOpacity, fadeInMs, settings.UnlockOverlayTransitionStyle, slideDistance);
                            ScheduleOverlayWindowLifecycleSample(
                                window,
                                "SAN-WebView2",
                                "post-enter-animation",
                                Math.Max(50, fadeInMs + 100),
                                overlayOpacity,
                                debugLoggingEnabled);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "[LocalOverlay] Failed to initialize SAN WebView2 overlay.");
                        AchievementNotificationDebugLog.Error(ex, "SAN WebView2 initialization failed.");
                        try
                        {
                            window.Close();
                        }
                        catch
                        {
                        }
                    }
                };

                if (persistentPreviewRequested)
                {
                    _persistentSettingsPreviewOverlay = window;
                    window.Show();
                    LogOverlayWindowLifecycle(window, "SAN-WebView2", "show-returned-persistent", overlayOpacity, debugLoggingEnabled);
                    return true;
                }

                var sanViewDurationMs = 0;
                if ((settings?.OverlayCustomSanView1DurationMilliseconds ?? 0) > 0 ||
                    (settings?.OverlayCustomSanView2DurationMilliseconds ?? 0) > 0)
                {
                    sanViewDurationMs =
                        Math.Max(0, settings?.OverlayCustomSanView1DurationMilliseconds ?? 0) +
                        Math.Max(0, settings?.OverlayCustomSanView2DurationMilliseconds ?? 0);
                }

                var closeDelayMs = Math.Max(Math.Max(1000, durationMs), sanViewDurationMs) + 900;
                var closeTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(closeDelayMs)
                };
                closeTimer.Tick += (_, __) =>
                {
                    closeTimer.Stop();
                    if (overlayState != null)
                    {
                        overlayState.IsClosing = true;
                    }

                    try
                    {
                        if (isSanTransition)
                        {
                            window.Close();
                        }
                        else
                        {
                            ApplyOverlayExitAnimation(window, overlayOpacity, fadeOutMs, settings.UnlockOverlayTransitionStyle, slideDistance, () => window.Close());
                        }
                    }
                    catch
                    {
                    }
                };
                closeTimer.Start();
                window.Show();
                LogOverlayWindowLifecycle(window, "SAN-WebView2", "show-returned", overlayOpacity, debugLoggingEnabled);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[LocalOverlay] Failed to render SAN WebView2 overlay; falling back to WPF renderer.");
                AchievementNotificationDebugLog.Error(ex, "SAN WebView2 rendering failed; falling back to the WPF renderer.");
                return false;
            }
        }

        private void ConfigureWebView2LoaderDirectory()
        {
            try
            {
                lock (WebView2LoaderSync)
                {
                    if (_webView2LoaderConfigured)
                    {
                        return;
                    }

                    var pluginDirectory = GetPluginAssemblyDirectory();
                    var architectureFolder = Environment.Is64BitProcess ? "x64" : "x86";
                    var loaderDirectory = Path.Combine(pluginDirectory, architectureFolder);
                    var loaderPath = Path.Combine(loaderDirectory, "WebView2Loader.dll");
                    if (!File.Exists(loaderPath))
                    {
                        _logger?.Warn($"[LocalOverlay] WebView2 loader was not found at '{loaderPath}'.");
                        return;
                    }

                    TryCopyWebView2LoaderToPluginRoot(loaderPath);
                    SetDllDirectory(loaderDirectory);
                    var module = LoadLibrary(loaderPath);
                    if (module == IntPtr.Zero)
                    {
                        _logger?.Warn($"[LocalOverlay] Failed to preload WebView2 loader from '{loaderPath}'.");
                    }

                    if (!SetDllDirectory(loaderDirectory))
                    {
                        _logger?.Warn($"[LocalOverlay] Failed to set WebView2 loader directory to '{loaderDirectory}'.");
                    }

                    _webView2LoaderConfigured = true;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[LocalOverlay] Failed to configure WebView2 loader directory.");
            }
        }

        private static Rect GetForegroundMonitorWorkArea(out OverlayMonitorDiagnostics diagnostics)
        {
            diagnostics = new OverlayMonitorDiagnostics();
            try
            {
                var foregroundWindow = GetForegroundWindow();
                diagnostics.ForegroundWindow = foregroundWindow;
                diagnostics.ForegroundDpi = TryGetWindowDpi(foregroundWindow);
                var monitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToPrimary);
                diagnostics.Monitor = monitor;
                if (monitor != IntPtr.Zero)
                {
                    var monitorInfo = new MonitorInfo
                    {
                        Size = Marshal.SizeOf(typeof(MonitorInfo))
                    };

                    if (GetMonitorInfo(monitor, ref monitorInfo))
                    {
                        diagnostics.HasMonitorInfo = true;
                        diagnostics.MonitorBounds = monitorInfo.Monitor;
                        diagnostics.WorkArea = monitorInfo.Work;
                        return new Rect(
                            monitorInfo.Work.Left,
                            monitorInfo.Work.Top,
                            monitorInfo.Work.Right - monitorInfo.Work.Left,
                            monitorInfo.Work.Bottom - monitorInfo.Work.Top);
                    }
                }
            }
            catch
            {
            }

            return SystemParameters.WorkArea;
        }

        private void LogOverlayWindowLifecycle(
            Window window,
            string renderer,
            string phase,
            double targetOpacity,
            bool debugLoggingEnabled)
        {
            if (window == null || !debugLoggingEnabled)
            {
                return;
            }

            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                var nativeBounds = new NativeRect();
                var hasNativeBounds = handle != IntPtr.Zero && GetWindowRect(handle, out nativeBounds);
                var dpi = VisualTreeHelper.GetDpi(window);
                var source = PresentationSource.FromVisual(window);
                var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;

                LogAchievementNotificationDebug(
                    $"[LocalOverlayDebug] Window lifecycle renderer='{renderer}', phase='{phase}', hwnd='{FormatHandle(handle)}', " +
                    $"hasPresentationSource='{source != null}', isLoaded='{window.IsLoaded}', isVisible='{window.IsVisible}', " +
                    $"windowState='{window.WindowState}', topmost='{window.Topmost}', allowsTransparency='{window.AllowsTransparency}', " +
                    $"opacity='{window.Opacity:0.###}', targetOpacity='{targetOpacity:0.###}', " +
                    $"boundsDip='{window.Left:0.###},{window.Top:0.###},{window.Width:0.###},{GetOverlayWindowHeight(window):0.###}', " +
                    $"actualSizeDip='{window.ActualWidth:0.###}x{window.ActualHeight:0.###}', " +
                    $"wpfScale='{dpi.DpiScaleX:0.###}x{dpi.DpiScaleY:0.###}', " +
                    $"sourceTransform='{transform.M11:0.###}x{transform.M22:0.###}', " +
                    $"nativeDpi='{TryGetWindowDpi(handle)}', nativeBoundsPx='{(hasNativeBounds ? FormatNativeRect(nativeBounds) : "unavailable")}'.");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalOverlay] Failed to collect window diagnostics for renderer='{renderer}', phase='{phase}'.");
            }
        }

        private void ScheduleOverlayWindowLifecycleSample(
            Window window,
            string renderer,
            string phase,
            int delayMilliseconds,
            double targetOpacity,
            bool debugLoggingEnabled)
        {
            if (window == null || !debugLoggingEnabled)
            {
                return;
            }

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMilliseconds))
            };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                LogOverlayWindowLifecycle(window, renderer, phase, targetOpacity, debugLoggingEnabled);
            };
            timer.Start();
        }

        private static string ResolveOverlayRendererName(Window window)
        {
            return window?.Content is WebView2 ? "SAN-WebView2" : "WPF";
        }

        private static string FormatHandle(IntPtr handle)
        {
            return $"0x{handle.ToInt64():X}";
        }

        private static string FormatRect(Rect rect)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.###},{1:0.###},{2:0.###},{3:0.###}",
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height);
        }

        private static string FormatNativeRect(NativeRect rect)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},{3}",
                rect.Left,
                rect.Top,
                rect.Right,
                rect.Bottom);
        }

        private static uint TryGetWindowDpi(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return GetDpiForWindow(window);
            }
            catch
            {
                return 0;
            }
        }

        private const uint MonitorDefaultToPrimary = 1;

        private sealed class OverlayMonitorDiagnostics
        {
            public IntPtr ForegroundWindow { get; set; }
            public IntPtr Monitor { get; set; }
            public bool HasMonitorInfo { get; set; }
            public NativeRect MonitorBounds { get; set; }
            public NativeRect WorkArea { get; set; }
            public uint ForegroundDpi { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        private void AttachOverlayTopmostGuard(Window window, bool debugLoggingEnabled)
        {
            if (window == null)
            {
                return;
            }

            DispatcherTimer topmostTimer = null;
            var loggedSuccess = false;
            var loggedFailure = false;
            Action enforceTopmost = () =>
            {
                try
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    if (handle != IntPtr.Zero)
                    {
                        var succeeded = SetWindowPos(
                            handle,
                            HwndTopmost,
                            0,
                            0,
                            0,
                            0,
                            SetWindowPosNoMove |
                            SetWindowPosNoSize |
                            SetWindowPosNoActivate |
                            SetWindowPosShowWindow);

                        if (succeeded && !loggedSuccess && debugLoggingEnabled)
                        {
                            loggedSuccess = true;
                            LogAchievementNotificationDebug(
                                $"[LocalOverlayDebug] Topmost guard succeeded renderer='{ResolveOverlayRendererName(window)}', " +
                                $"hwnd='{FormatHandle(handle)}'.");
                        }
                        else if (!succeeded && !loggedFailure)
                        {
                            loggedFailure = true;
                            _logger?.Warn(
                                $"[LocalOverlay] Topmost guard failed renderer='{ResolveOverlayRendererName(window)}', " +
                                $"hwnd='{FormatHandle(handle)}', win32Error='{Marshal.GetLastWin32Error()}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!loggedFailure)
                    {
                        loggedFailure = true;
                        _logger?.Warn(ex, "[LocalOverlay] Topmost guard threw an exception.");
                    }
                }
            };

            window.SourceInitialized += (_, __) => enforceTopmost();
            window.Loaded += (_, __) =>
            {
                enforceTopmost();
                topmostTimer = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                topmostTimer.Tick += (_, __) => enforceTopmost();
                topmostTimer.Start();
            };
            window.Closed += (_, __) =>
            {
                topmostTimer?.Stop();
                topmostTimer = null;
            };
        }

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SetWindowPosNoSize = 0x0001;
        private const uint SetWindowPosNoMove = 0x0002;
        private const uint SetWindowPosNoActivate = 0x0010;
        private const uint SetWindowPosShowWindow = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        public void SendScoreProgressNotification(
            string scoreName,
            string changeName,
            int level,
            string tier,
            ScoreProgressNotificationSettings notification,
            LocalSettings baseSettings)
        {
            if (notification == null || baseSettings == null) return;
            var localSettings = baseSettings.CreateProgressNotificationCopy(notification);
            var detail = string.Equals(changeName, "tier", StringComparison.OrdinalIgnoreCase)
                ? $"{scoreName} tier reached: {tier}"
                : $"{scoreName} level reached: {level}";
            var soundPath = string.IsNullOrWhiteSpace(notification.SoundPath)
                ? baseSettings.UnlockSoundPath
                : notification.SoundPath;
            Action showPopup = () => SendUnlockPopup(
                    "Achievement Collection",
                    detail,
                    providerKey: "Local",
                    forcedStyle: NotificationStyleCustom,
                    overrideLocalSettings: localSettings,
                    achievementDescription: $"{scoreName} score progress",
                    achievementPoints: level,
                    achievementRarity: tier,
                    achievementTrophy: tier);

            var lead = notification.SoundLeadMilliseconds;
            if (lead > 0)
            {
                PlayCustomSound(soundPath);
                Task.Delay(lead).ContinueWith(_ => showPopup());
            }
            else if (lead < 0)
            {
                showPopup();
                Task.Delay(Math.Abs(lead)).ContinueWith(_ => PlayCustomSound(soundPath));
            }
            else
            {
                showPopup();
                PlayCustomSound(soundPath);
            }
        }

        private void WarmUpSanWebView2()
        {
            if (_sanWebViewWarmupStarted)
            {
                return;
            }

            _sanWebViewWarmupStarted = true;

            try
            {
                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        var environment = await GetSanWebView2EnvironmentAsync();
                        var webView = new WebView2
                        {
                            Width = 1,
                            Height = 1,
                            DefaultBackgroundColor = System.Drawing.Color.Transparent
                        };

                        var window = new Window
                        {
                            Content = webView,
                            Width = 1,
                            Height = 1,
                            WindowStyle = WindowStyle.ToolWindow,
                            ResizeMode = ResizeMode.NoResize,
                            AllowsTransparency = false,
                            Background = Brushes.Black,
                            ShowInTaskbar = false,
                            ShowActivated = false,
                            IsHitTestVisible = false,
                            Focusable = false,
                            Opacity = 0,
                            Left = -32000,
                            Top = -32000
                        };

                        _sanWebViewWarmupWindow = window;
                        _sanWebViewWarmupControl = webView;
                        window.Show();

                        await webView.EnsureCoreWebView2Async(environment);
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        webView.NavigateToString("<!doctype html><html><head><meta charset=\"utf-8\"></head><body></body></html>");
                        // Keep the initialized controller alive without leaving a visible WPF window in
                        // Application.Current.Windows. Fullscreen themes can treat any visible secondary
                        // window as an active overlay and stop updating their focus/navigation state.
                        window.Hide();
                        _logger?.Debug("[LocalOverlay] SAN WebView2 controller warm-up completed.");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[LocalOverlay] SAN WebView2 controller warm-up failed.");
                        try
                        {
                            _sanWebViewWarmupWindow?.Close();
                            _sanWebViewWarmupControl?.Dispose();
                        }
                        catch
                        {
                        }

                        _sanWebViewWarmupWindow = null;
                        _sanWebViewWarmupControl = null;
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LocalOverlay] Failed to schedule SAN WebView2 warm-up.");
            }
        }

        private Task<CoreWebView2Environment> GetSanWebView2EnvironmentAsync()
        {
            lock (WebView2LoaderSync)
            {
                if (_sanWebViewEnvironmentTask == null)
                {
                    ConfigureWebView2LoaderDirectory();
                    _sanWebViewEnvironmentTask = CoreWebView2Environment.CreateAsync();
                }

                return _sanWebViewEnvironmentTask;
            }
        }

        private void TryCopyWebView2LoaderToPluginRoot(string loaderPath)
        {
            try
            {
                var rootLoaderPath = Path.Combine(GetPluginAssemblyDirectory(), "WebView2Loader.dll");
                var source = new FileInfo(loaderPath);
                var destination = new FileInfo(rootLoaderPath);
                if (destination.Exists && destination.Length == source.Length)
                {
                    return;
                }

                File.Copy(loaderPath, rootLoaderPath, true);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[LocalOverlay] Failed to stage WebView2 loader in the plugin root.");
            }
        }

        private string WriteSanWebViewDocumentToTempFile(string html)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "SanOverlay");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"san-overlay-{Guid.NewGuid():N}.html");
            File.WriteAllText(path, html ?? string.Empty, Encoding.UTF8);
            return path;
        }

        private void TryDeleteSanWebViewDocument(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string GetPluginAssemblyDirectory()
        {
            var assemblyLocation = typeof(NotificationPublisher).Assembly.Location;
            return string.IsNullOrWhiteSpace(assemblyLocation)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyLocation) ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        private string BuildSanWebViewDocument(
            string gameName,
            string achievementName,
            string achievementIconPath,
            string providerKey,
            LocalSettings settings,
            Game game,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            int durationMs,
            double width,
            double height)
        {
            var assetRoot = settings.OverlayCustomSanAssetRootPath;
            if (string.IsNullOrWhiteSpace(assetRoot))
            {
                var bundledNotifyDir = ResolveBundledSanNotifyDirectory();
                if (!string.IsNullOrWhiteSpace(bundledNotifyDir))
                {
                    assetRoot = Directory.GetParent(bundledNotifyDir)?.FullName ?? assetRoot;
                }
            }
            var notifyDir = !string.IsNullOrWhiteSpace(assetRoot) ? Path.Combine(assetRoot, "notify") : string.Empty;
            var globalCssDir = !string.IsNullOrWhiteSpace(assetRoot) ? Path.Combine(assetRoot, "dist", "app") : string.Empty;
            var preset = NormalizeSanPresetId(settings?.OverlayCustomSanPresetId);
            var animationPreset = ResolveSanAnimationPresetId(settings, preset);
            if (string.IsNullOrWhiteSpace(animationPreset))
            {
                animationPreset = "default";
            }
            var isSanTransition = IsSanTransitionStyle(settings?.UnlockOverlayTransitionStyle ?? LocalUnlockOverlayTransitionStyle.Fade);
            var presetDir = !string.IsNullOrWhiteSpace(notifyDir) && !string.IsNullOrWhiteSpace(animationPreset)
                ? Path.Combine(notifyDir, "presets", animationPreset)
                : string.Empty;

            var globalCss = RewriteCssUrls(ReadSanAssetText(settings, "dist", "app", "global.css"), globalCssDir);
            var baseCss = RewriteCssUrls(
                string.IsNullOrWhiteSpace(settings.OverlayCustomSanBaseCss) ? ReadSanAssetText(settings, "notify", "base.css") : settings.OverlayCustomSanBaseCss,
                notifyDir);
            var baseAnimCss = RewriteCssUrls(
                string.IsNullOrWhiteSpace(settings.OverlayCustomSanBaseAnimCss) ? ReadSanAssetText(settings, "notify", "baseanim.css") : settings.OverlayCustomSanBaseAnimCss,
                notifyDir);
            var configuredElementPreset = NormalizeSanPresetId(settings?.OverlayCustomSanElementPresetId);
            var followsTransitionElement = string.IsNullOrWhiteSpace(configuredElementPreset);
            var elementPreset = configuredElementPreset;
            if (followsTransitionElement)
            {
                elementPreset = animationPreset;
            }
            else if (isSanTransition && !AreSanElementAndTransitionCompatible(animationPreset, configuredElementPreset))
            {
                _logger?.Warn($"[LocalOverlay] SAN element preset '{configuredElementPreset}' is not compatible with transition preset '{animationPreset}'. Using the transition element instead.");
                followsTransitionElement = true;
                elementPreset = animationPreset;
            }
            var usesSanTimeline = isSanTransition;
            var elementPresetDir = !string.IsNullOrWhiteSpace(notifyDir) && !string.IsNullOrWhiteSpace(elementPreset)
                ? Path.Combine(notifyDir, "presets", elementPreset)
                : string.Empty;

            var selectedPresetHtml = string.Equals(elementPreset, preset, StringComparison.OrdinalIgnoreCase)
                ? settings.OverlayCustomSanPresetHtml
                : ReadSanAssetText(settings, "notify", "presets", elementPreset, "index.html");
            var selectedElementCss = string.Equals(elementPreset, preset, StringComparison.OrdinalIgnoreCase)
                ? settings.OverlayCustomSanPresetCss
                : ReadSanAssetText(settings, "notify", "presets", elementPreset, "styles.css");
            var selectedPresetCss = string.Equals(animationPreset, preset, StringComparison.OrdinalIgnoreCase)
                ? settings.OverlayCustomSanPresetCss
                : ReadSanAssetText(settings, "notify", "presets", animationPreset, "styles.css");
            if (!isSanTransition && !string.IsNullOrWhiteSpace(configuredElementPreset))
            {
                selectedPresetCss = selectedElementCss;
                presetDir = elementPresetDir;
            }
            if (string.IsNullOrWhiteSpace(selectedPresetHtml))
            {
                selectedPresetHtml = settings.OverlayCustomSanPresetHtml;
            }

            if (string.IsNullOrWhiteSpace(selectedPresetHtml))
            {
                selectedPresetHtml = ReadSanAssetText(settings, "notify", "presets", string.IsNullOrWhiteSpace(elementPreset) ? "default" : elementPreset, "index.html");
            }

            if (string.IsNullOrWhiteSpace(selectedPresetCss))
            {
                selectedPresetCss = settings.OverlayCustomSanPresetCss;
                presetDir = !string.IsNullOrWhiteSpace(notifyDir) && !string.IsNullOrWhiteSpace(preset)
                    ? Path.Combine(notifyDir, "presets", preset)
                    : presetDir;
            }

            if (string.IsNullOrWhiteSpace(selectedPresetCss))
            {
                selectedPresetCss = ReadSanAssetText(settings, "notify", "presets", animationPreset, "styles.css");
                presetDir = !string.IsNullOrWhiteSpace(notifyDir)
                    ? Path.Combine(notifyDir, "presets", animationPreset)
                    : presetDir;
            }

            var elementCss = string.Equals(elementPreset, animationPreset, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : RewriteCssUrls(selectedElementCss, elementPresetDir);
            var presetCss = RewriteCssUrls(selectedPresetCss, presetDir);
            var theme = TryParseSanTheme(settings);
            var customisation = theme?["customisation"] as JObject;
            var primaryIconUri = ResolveSanDisplayIconUri(achievementIconPath, settings, game, providerKey, achievementRarity, achievementTrophy, achievementPoints);
            var secondaryIconUri = ResolveSanSecondaryIconUri(settings, game, achievementIconPath, providerKey, achievementRarity, achievementTrophy, achievementPoints);
            var iconUri = primaryIconUri;
            var logoSourceUri = settings?.OverlayCustomShowSecondaryIcon == true ? secondaryIconUri : primaryIconUri;
            var logoUri = ResolveSanLogoUri(settings, customisation, achievementTrophy, achievementRarity, achievementPoints, logoSourceUri, elementPreset);
            var decorationUri = ResolveSanDecorationUri(settings, customisation, achievementTrophy, achievementRarity, achievementPoints);
            var hiddenIconUri = ResolveSanAssetUri(settings, "icon", "lock.svg");
            var base64Uri = ResolveSanAssetUri(settings, "img", "base64.png");
            var ssImageUri = ResolveSanAssetUri(settings, "img", "santextlogobg.png");
            var sanLogoSymbolUri = ResolveSanAssetUri(settings, "img", "s.svg");
            var backgroundImageUri = ResolveSanBackgroundImageUri(settings, customisation, achievementIconPath, game);
            var coverImageUri = ResolveSanCoverImageUri(settings, game);
            var percentBadgeImageUri = ResolveSanPercentBadgeImageUri(settings, customisation, achievementRarity);
            var percentValue = ResolveSanPercentValue(achievementRarity);
            var rarityLimit = customisation?.Value<double?>("rarity") ?? 10;
            var semiRarityLimit = customisation?.Value<double?>("semirarity") ?? 50;
            var percentTier = ResolveSanPercentTier(percentValue, rarityLimit, semiRarityLimit);
            var achievementScore = customisation?.Value<bool?>("usepercent") == true
                ? percentValue.ToString("0.0", CultureInfo.InvariantCulture)
                : ResolveSanGamerscoreValue(percentValue, rarityLimit, semiRarityLimit);
            var achievementUnit = customisation?.Value<bool?>("usepercent") == true
                ? "%"
                : ResolveSanScoreUnit(settings);
            var rarityColor = percentTier == "bronze" ? "#a05526" : percentTier == "silver" ? "#828282" : "#b4904a";
            var provider = string.IsNullOrWhiteSpace(providerKey) ? "Local" : providerKey.Trim();
            var scoreLineFallback = UsesSanScorePrefix(elementPreset) ? "G <points> - <achievementName>" : "<gameName>";
            var titleTemplate = ResolveSanRuntimeTitleTemplate(settings.OverlayCustomTitleTemplate, preset);
            var gameTemplate = ResolveSanRuntimeGameTemplate(settings.OverlayCustomGameNameTemplate, preset);
            var line1 = settings.OverlayCustomShowLine1 != false
                ? ResolveSanTemplateLineHtml(settings, titleTemplate, "Achievement unlocked", "Achievement unlocked", gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, provider, NotificationStyleCustom, game, game?.Source?.Name, DateTime.Now, false)
                : string.Empty;
            var line2 = settings.OverlayCustomShowGameName != false
                ? ResolveSanTemplateLineHtml(settings, gameTemplate, scoreLineFallback, "Achievement unlocked", gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, provider, NotificationStyleCustom, game, game?.Source?.Name, DateTime.Now, true)
                : string.Empty;
            var line3 = settings.OverlayCustomShowMeta != false
                ? ResolveSanTemplateLineHtml(settings, settings.OverlayCustomAchievementTemplate, "Unlocked: <achievementName>", "Achievement unlocked", gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, provider, NotificationStyleCustom, game, game?.Source?.Name, DateTime.Now, true)
                : string.Empty;
            var line4 = settings.OverlayCustomShowLine4
                ? ResolveSanTemplateLineHtml(settings, settings.OverlayCustomLine4Template, string.Empty, "Achievement unlocked", gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, provider, NotificationStyleCustom, game, game?.Source?.Name, DateTime.Now, true)
                : string.Empty;
            var line5 = settings.OverlayCustomShowLine5
                ? ResolveSanTemplateLineHtml(settings, settings.OverlayCustomLine5Template, string.Empty, "Achievement unlocked", gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, provider, NotificationStyleCustom, game, game?.Source?.Name, DateTime.Now, true)
                : string.Empty;
            var line6 = settings.OverlayCustomShowLine6
                ? ResolveSanTemplateLineHtml(settings, settings.OverlayCustomLine6Template, string.Empty, "Achievement unlocked", gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, provider, NotificationStyleCustom, game, game?.Source?.Name, DateTime.Now, true)
                : string.Empty;
            var sanElems = ResolveSanTextElements(settings, line1, line2, line3, out var unlockMessage, out var title, out var desc);
            var sanLineDefinitionsJson = BuildSanLineDefinitionsJson(settings, line1, line2, line3, line4, line5, line6);

            var displaySeconds = Math.Max(1, Math.Max(1, durationMs) / 1000.0);
            var view1Seconds = Math.Max(0.5, (settings?.OverlayCustomSanView1DurationMilliseconds > 0 ? settings.OverlayCustomSanView1DurationMilliseconds : 5000) / 1000.0);
            var view2Seconds = Math.Max(0.5, (settings?.OverlayCustomSanView2DurationMilliseconds > 0 ? settings.OverlayCustomSanView2DurationMilliseconds : 5000) / 1000.0);
            var timelineScale = Math.Max(0.1, displaySeconds / 10.0);
            var transitionSeconds = Math.Max(0.05, ((settings.UnlockOverlayFadeInMilliseconds > 0 ? settings.UnlockOverlayFadeInMilliseconds : 180) / 1000.0) * timelineScale);
            var bodyAttrs = BuildSanBodyAttributes(settings, customisation, sanElems.Length >= 3, animationPreset, elementPreset);
            var themeScale = Math.Max(0.1, (customisation?.Value<double?>("scale") ?? 100) / 100.0);
            var sanScale = Math.Max(0.1, settings.OverlayCustomScale * themeScale);
            var useCustomFontSizes = customisation?.Value<bool?>("usecustomfontsizes") == true;
            var themeFontScale = Math.Max(0.1, (customisation?.Value<double?>("fontsize") ?? 100) / 100.0);
            var themeUnlockFontScale = Math.Max(0.1, ((useCustomFontSizes ? customisation?.Value<double?>("unlockmsgfontsize") : customisation?.Value<double?>("fontsize")) ?? 100) / 100.0);
            var themeTitleFontScale = Math.Max(0.1, ((useCustomFontSizes ? customisation?.Value<double?>("titlefontsize") : customisation?.Value<double?>("fontsize")) ?? 100) / 100.0);
            var themeDescFontScale = Math.Max(0.1, ((useCustomFontSizes ? customisation?.Value<double?>("descfontsize") : customisation?.Value<double?>("fontsize")) ?? 100) / 100.0);
            var fontScale = Math.Max(0.1, ((settings?.OverlayCustomDetailFontSize ?? 11) / 11.0) * themeFontScale);
            var unlockFontScale = Math.Max(0.1, ((settings?.OverlayCustomTitleFontSize ?? 13) / 13.0) * themeUnlockFontScale);
            var titleFontScale = Math.Max(0.1, ((settings?.OverlayCustomDetailFontSize ?? 11) / 11.0) * themeTitleFontScale);
            var descFontScale = Math.Max(0.1, ((settings?.OverlayCustomMetaFontSize ?? 9) / 9.0) * themeDescFontScale);
            var opacity = Math.Max(0, Math.Min(1, customisation?.Value<double?>("opacity") / 100.0 ?? settings.OverlayCustomOpacity));
            var roundness = Math.Max(0, (settings?.OverlayCustomCornerRadius ?? ((customisation?.Value<double?>("roundness") ?? 0) / 4.0)) * Math.Max(0.1, settings?.OverlayCustomScale ?? 1.0));
            var iconRoundness = customisation?.Value<double?>("iconroundness") == 100
                ? "50%"
                : $"{Math.Max(0, (customisation?.Value<double?>("iconroundness") ?? 0) / 6.0 * sanScale).ToString("0.###", CultureInfo.InvariantCulture)}px";
            var badgeFontSize = customisation?.Value<double?>("percentbadgefontsize") ?? 10;
            var badgeRoundness = customisation?.Value<double?>("percentbadgeroundness") ?? 50;
            var badgePosition = ResolveSanBadgePosition(
                customisation?.Value<string>("percentbadgepos"),
                (customisation?.Value<double?>("percentbadgex") ?? 0) * sanScale,
                (customisation?.Value<double?>("percentbadgey") ?? 0) * sanScale);
            var glowEnabled = customisation?.Value<bool?>("glow") == true;
            var glowRarity = customisation?.Value<bool?>("glowrarity") == true;
            var glowColor = glowRarity
                ? CssColor(customisation?.Value<string>($"glowcolor{percentTier}"), "#8a2be2")
                : CssColor(customisation?.Value<string>("glowcolor"), "#8a2be2");
            var glowAnim = glowEnabled && !string.Equals(customisation?.Value<string>("glowanim"), "off", StringComparison.OrdinalIgnoreCase)
                ? $"{customisation?.Value<string>("glowanim") ?? "pulse"} calc(var(--transition) * var(--glowspeed)) linear infinite"
                : "none";

            var variables = new StringBuilder();
            variables.AppendLine(":root {");
            variables.AppendLine($"  --notifywidth: {Math.Max(1, width).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --notifyheight: {Math.Max(1, height).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --san-icon-size: {Math.Max(1, settings?.OverlayCustomIconSize ?? 58).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --san-secondary-icon-size: {Math.Max(1, settings?.OverlayCustomSecondaryIconSize ?? settings?.OverlayCustomIconSize ?? 58).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --san-icon-corner-radius: {Math.Max(0, settings?.OverlayCustomIconCornerRadius ?? 10).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --san-secondary-icon-corner-radius: {Math.Max(0, settings?.OverlayCustomSecondaryIconCornerRadius ?? 10).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --iconsize: var(--san-icon-size);");
            variables.AppendLine($"  --icon-size: var(--san-icon-size);");
            variables.AppendLine($"  --achicon-size: var(--san-icon-size);");
            variables.AppendLine($"  --logo-size: var(--san-secondary-icon-size);");
            variables.AppendLine($"  --displaytime: {displaySeconds.ToString("0.###", CultureInfo.InvariantCulture)}s;");
            variables.AppendLine($"  --san-view1-displaytime: {view1Seconds.ToString("0.###", CultureInfo.InvariantCulture)}s;");
            variables.AppendLine($"  --san-view2-displaytime: {view2Seconds.ToString("0.###", CultureInfo.InvariantCulture)}s;");
            variables.AppendLine($"  --transition: {transitionSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s;");
            variables.AppendLine($"  --scale: {sanScale.ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --gradientangle: {(customisation?.Value<double?>("gradientangle") ?? 90).ToString("0.###", CultureInfo.InvariantCulture)}deg;");
            variables.AppendLine($"  --bgimg: {(string.IsNullOrWhiteSpace(backgroundImageUri) ? "none" : $"url('{CssUrl(backgroundImageUri)}')")};");
            variables.AppendLine($"  --gameart: {(string.IsNullOrWhiteSpace(backgroundImageUri) ? "none" : $"url('{CssUrl(backgroundImageUri)}')")};");
            variables.AppendLine($"  --gameicon: url('{CssUrl(iconUri)}');");
            variables.AppendLine($"  --bgimgbrightness: {(customisation?.Value<double?>("bgimgbrightness") ?? 100).ToString("0.###", CultureInfo.InvariantCulture)}%;");
            variables.AppendLine($"  --brightness: {(customisation?.Value<double?>("brightness") ?? 100).ToString("0.###", CultureInfo.InvariantCulture)}%;");
            variables.AppendLine($"  --primarycolor: {CssColor(settings.OverlayCustomBackgroundColor, CssColor(customisation?.Value<string>("primarycolor"), "#203e7a"))};");
            variables.AppendLine($"  --secondarycolor: {CssColor(settings.OverlayCustomBorderColor, CssColor(customisation?.Value<string>("secondarycolor"), "#0c2a66"))};");
            variables.AppendLine($"  --tertiarycolor: {CssColor(settings.OverlayCustomAccentColor, CssColor(customisation?.Value<string>("tertiarycolor"), "#ffffff"))};");
            variables.AppendLine($"  --fontcolor: {CssColor(settings.OverlayCustomTitleColor, CssColor(customisation?.Value<string>("fontcolor"), "#ffffff"))};");
            variables.AppendLine($"  --unlockmsgfontcolor: {CssColor(settings.OverlayCustomTitleColor, CssColor(customisation?.Value<bool?>("usecustomfontcolors") == true ? customisation?.Value<string>("unlockmsgfontcolor") : customisation?.Value<string>("fontcolor"), "#ffffff"))};");
            variables.AppendLine($"  --titlefontcolor: {CssColor(settings.OverlayCustomDetailColor, CssColor(customisation?.Value<bool?>("usecustomfontcolors") == true ? customisation?.Value<string>("titlefontcolor") : customisation?.Value<string>("fontcolor"), "#ffffff"))};");
            variables.AppendLine($"  --descfontcolor: {CssColor(settings.OverlayCustomMetaColor, CssColor(customisation?.Value<bool?>("usecustomfontcolors") == true ? customisation?.Value<string>("descfontcolor") : customisation?.Value<string>("fontcolor"), "#ffffff"))};");
            variables.AppendLine($"  --opacity: {opacity.ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --roundness: {roundness.ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --fontsize: {fontScale.ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --unlockmsgfontsize: {unlockFontScale.ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --titlefontsize: {titleFontScale.ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --descfontsize: {descFontScale.ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --iconroundness: {iconRoundness};");
            variables.AppendLine($"  --fontoutline: {(customisation?.Value<bool?>("fontoutline") == true ? $"{(sanScale * (customisation?.Value<double?>("fontoutlinescale") ?? 1)).ToString("0.###", CultureInfo.InvariantCulture)}px {CssColor(customisation?.Value<string>("fontoutlinecolor"), "#000000")}" : "none")};");
            variables.AppendLine($"  --fontshadow: {ResolveSanFontShadow(customisation, sanScale)};");
            variables.AppendLine($"  --logo: url('{CssUrl(logoUri)}');");
            variables.AppendLine($"  --decoration: {(string.IsNullOrWhiteSpace(decorationUri) ? "none" : $"url('{CssUrl(decorationUri)}')")};");
            variables.AppendLine($"  --hiddenicon: url('{CssUrl(hiddenIconUri)}');");
            variables.AppendLine($"  --base64: url('{CssUrl(base64Uri)}');");
            variables.AppendLine($"  --ssimg: url('{CssUrl(ssImageUri)}');");
            variables.AppendLine($"  --s: url('{CssUrl(sanLogoSymbolUri)}');");
            variables.AppendLine("  --darkgrey: #101010;");
            variables.AppendLine("  --mediumgrey: #2b2b2b;");
            variables.AppendLine("  --lightgrey: #3d3d3d;");
            variables.AppendLine($"  --gs: {achievementScore};");
            variables.AppendLine($"  --unit: {achievementUnit};");
            variables.AppendLine($"  --raritycolor: {rarityColor};");
            variables.AppendLine($"  --glow: {(glowEnabled ? $"drop-shadow({(((customisation?.Value<double?>("glowx") ?? 0) * sanScale) / 10.0).ToString("0.###", CultureInfo.InvariantCulture)}px {(((customisation?.Value<double?>("glowy") ?? 0) * sanScale) / 10.0).ToString("0.###", CultureInfo.InvariantCulture)}px var(--glowsize) var(--glowcolor))" : "none")};");
            variables.AppendLine($"  --glowsize: {(((customisation?.Value<double?>("glowsize") ?? 100) / 100.0) * 0.6).ToString("0.###", CultureInfo.InvariantCulture)}rem;");
            variables.AppendLine($"  --glowcolor: {glowColor};");
            variables.AppendLine($"  --glowanim: {glowAnim};");
            variables.AppendLine($"  --glowspeed: {(customisation?.Value<double?>("glowspeed") ?? 5).ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --blur: {(((customisation?.Value<double?>("blur") ?? 0) * sanScale) / 50.0).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --mask: {ResolveSanMask(settings, customisation)};");
            variables.AppendLine($"  --outline: {(settings?.OverlayCustomShowBorder != false ? (customisation?.Value<string>("outline") ?? "solid") : "none")};");
            variables.AppendLine($"  --outlinewidth: {(((customisation?.Value<double?>("outlinewidth") ?? 25) / 25.0) * Math.Max(0.1, settings?.OverlayCustomScale ?? 1.0)).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --outlinecolor: {CssColor(settings?.OverlayCustomBorderColor, CssColor(customisation?.Value<string>("outlinecolor"), "transparent"))};");
            variables.AppendLine($"  --iconborder: {ResolveSanIconBorder(settings, customisation, achievementRarity)};");
            variables.AppendLine($"  --iconborderpos: {(string.Equals(customisation?.Value<string>("iconborderpos"), "back", StringComparison.OrdinalIgnoreCase) ? "-1" : "99")};");
            variables.AppendLine($"  --iconborderscale: {((customisation?.Value<double?>("iconborderscale") ?? 100) / 100.0).ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --iconborderx: {customisation?.Value<int?>("iconborderx") ?? 0};");
            variables.AppendLine($"  --iconbordery: {customisation?.Value<int?>("iconbordery") ?? 0};");
            variables.AppendLine($"  --textvspace: {customisation?.Value<int?>("textvspace") ?? 0};");
            variables.AppendLine($"  --badgeposx: {badgePosition.X};");
            variables.AppendLine($"  --badgeposy: {badgePosition.Y};");
            variables.AppendLine($"  --badgecolor: {CssColor(customisation?.Value<string>("percentbadgecolor"), "#203e7a")};");
            variables.AppendLine($"  --badgefontcolor: {CssColor(customisation?.Value<string>("percentbadgefontcolor"), "#ffffff")};");
            variables.AppendLine($"  --badgesize: {((badgeFontSize / 10.0) * sanScale).ToString("0.###", CultureInfo.InvariantCulture)}px;");
            variables.AppendLine($"  --badgeroundness: {(badgeRoundness >= 100 ? "50%" : $"{((badgeFontSize / 4.0) / Math.Max(0.1, badgeRoundness / 10.0)).ToString("0.###", CultureInfo.InvariantCulture)}px")};");
            variables.AppendLine($"  --badgeimg: {(string.IsNullOrWhiteSpace(percentBadgeImageUri) ? "none" : $"url('{CssUrl(percentBadgeImageUri)}')")};");
            variables.AppendLine($"  --bodyopacity: 1;");
            variables.AppendLine($"  --elemopacity: 1;");
            variables.AppendLine($"  --logoscale: 1;");
            variables.AppendLine($"  --decorationscale: {((customisation?.Value<double?>("decorationscale") ?? 100) / 100.0).ToString("0.###", CultureInfo.InvariantCulture)};");
            variables.AppendLine($"  --iconscale: 1;");
            variables.AppendLine($"  --iconshadowcolor: {CssColor(customisation?.Value<string>("iconshadowcolor"), "#ffb84e99")};");
            variables.AppendLine($"  --iconanimcolor: {CssColor(customisation?.Value<string>("iconanimcolor"), "#ffb84e")};");
            variables.AppendLine($"  --decorationdisplaytype: {((customisation?.Value<int?>("decorationpos") ?? 0) > 0 ? "block" : "none")};");
            variables.AppendLine("  --hiddenicondisplaytype: none;");
            variables.AppendLine($"  --percentdisplaytype: {(customisation?.Value<bool?>("usepercent") == true ? "block" : "none")};");
            variables.AppendLine("}");
            variables.AppendLine("html, body { overflow: hidden; background: transparent !important; }");
            variables.AppendLine("body { opacity: 1 !important; }");
            variables.AppendLine(".san-inline-token-icon { display: inline-block; width: 1.2em; height: 1.2em; margin: 0 0.12em -0.18em; background: center / contain no-repeat; }");
            variables.AppendLine(".wrapper#achcontent > #unlockmsg, .wrapper#achcontent > #title, .wrapper#achcontent > #desc { display: none !important; }");
            variables.AppendLine(".wrapper#achcontent > .san-line-stack { grid-column: 1 / -1; grid-row: 1 / -1; }");
            variables.AppendLine(".san-line-stack { display: flex !important; flex-direction: column !important; align-items: flex-start !important; justify-content: center !important; gap: 0 !important; width: 100% !important; min-width: 0 !important; height: 100% !important; overflow: hidden !important; }");
            variables.AppendLine(".san-line-stack .san-generated-line { position: static !important; inset: auto !important; transform: none !important; translate: 0 0 !important; display: block !important; opacity: 1 !important; scale: 1 !important; animation: none !important; transition: none !important; width: 100% !important; min-width: 0 !important; white-space: normal !important; overflow: visible !important; text-overflow: clip !important; }");
            variables.AppendLine(".wrapper#achcontent:has(.san-line-stack), .san-line-stack, .san-line-stack * { opacity: 1 !important; }");
            variables.AppendLine(".san-line-inner { display: inline-block; line-height: 1.15; vertical-align: middle; max-width: 100%; }");
            variables.AppendLine(".san-line-inner, .san-line-inner * { font-weight: inherit !important; font-style: inherit !important; text-decoration: inherit !important; }");
            variables.AppendLine(".wrapper#achiconwrapper { width: var(--san-icon-size) !important; height: var(--san-icon-size) !important; min-width: var(--san-icon-size) !important; min-height: var(--san-icon-size) !important; max-width: var(--san-icon-size) !important; max-height: var(--san-icon-size) !important; }");
            variables.AppendLine(".wrapper#logo { width: var(--san-secondary-icon-size) !important; height: var(--san-secondary-icon-size) !important; min-width: var(--san-secondary-icon-size) !important; min-height: var(--san-secondary-icon-size) !important; max-width: var(--san-secondary-icon-size) !important; max-height: var(--san-secondary-icon-size) !important; }");
            variables.AppendLine(".wrapper#achiconwrapper, .wrapper#achiconinnerwrapper, #achicon, #iconbg { border-radius: var(--san-icon-corner-radius) !important; }");
            variables.AppendLine(".wrapper#achiconinnerwrapper, #achicon, #iconbg { overflow: hidden !important; }");
            variables.AppendLine(".wrapper#logo, #logo, .san-secondary-icon { border-radius: var(--san-secondary-icon-corner-radius) !important; }");
            variables.AppendLine("#logo, .san-secondary-icon { overflow: hidden !important; }");
            variables.AppendLine(".wrapper#achiconwrapper > *, .wrapper#logo > *, .wrapper#achiconwrapper img, .wrapper#logo img, .wrapper#achiconwrapper svg, .wrapper#logo svg { width: 100% !important; height: 100% !important; max-width: 100% !important; max-height: 100% !important; object-fit: contain !important; }");
            variables.AppendLine("body[data-san-elements] #iconbg, body[data-san-elements] #achicon, body[data-san-elements] .icon, body[data-san-elements] .achicon { width: var(--san-icon-size) !important; height: var(--san-icon-size) !important; min-width: var(--san-icon-size) !important; min-height: var(--san-icon-size) !important; max-width: var(--san-icon-size) !important; max-height: var(--san-icon-size) !important; background-size: contain !important; }");
            variables.AppendLine("body[data-san-elements] #logo { width: var(--san-secondary-icon-size) !important; height: var(--san-secondary-icon-size) !important; min-width: var(--san-secondary-icon-size) !important; min-height: var(--san-secondary-icon-size) !important; max-width: var(--san-secondary-icon-size) !important; max-height: var(--san-secondary-icon-size) !important; background-size: contain !important; }");
            variables.AppendLine("body[data-san-elements] .wrapper#achiconwrapper::before, body[data-san-elements] .wrapper#achiconwrapper::after, body[data-san-elements] .wrapper#achcontent::before, body[data-san-elements] .wrapper#achcontent::after { width: var(--san-icon-size) !important; height: var(--san-icon-size) !important; min-width: var(--san-icon-size) !important; min-height: var(--san-icon-size) !important; max-width: var(--san-icon-size) !important; max-height: var(--san-icon-size) !important; background-size: contain !important; }");
            variables.AppendLine("body[data-san-elements] .wrapper#logo::before, body[data-san-elements] .wrapper#logo::after { width: var(--san-secondary-icon-size) !important; height: var(--san-secondary-icon-size) !important; min-width: var(--san-secondary-icon-size) !important; min-height: var(--san-secondary-icon-size) !important; max-width: var(--san-secondary-icon-size) !important; max-height: var(--san-secondary-icon-size) !important; background-size: contain !important; }");
            variables.AppendLine(".wrapper#achiconinnerwrapper, #achicon, #iconborder { width: 100% !important; height: 100% !important; max-width: 100% !important; max-height: 100% !important; }");
            if (UsesSanScorePrefix(elementPreset))
            {
                variables.AppendLine("#title::before, #title::after, #desc::before, #desc::after { content: none !important; display: none !important; }");
            }
            variables.AppendLine("#xpwrapper { display: none !important; }");
            variables.AppendLine(".wrapper#achcont, .wrapper#bg { border-radius: var(--roundness) !important; overflow: hidden; }");
            variables.AppendLine(".wrapper#achcont { position: relative !important; border: var(--outlinewidth) var(--outline) var(--outlinecolor) !important; }");
            variables.AppendLine("body.san-webview-fast-start .wrapper#achcontent { opacity: 1 !important; animation: none !important; animation-delay: 0ms !important; transition: none !important; }");
            variables.AppendLine("body.san-webview-fast-start .wrapper#achcontent > span { opacity: 1 !important; animation: none !important; animation-delay: 0ms !important; transition: none !important; }");
            variables.AppendLine("body.san-webview-hide-icon-border #iconborder { display: none !important; }");
            variables.AppendLine("body.san-webview-force-visible .wrapper#achcontent > span { opacity: 1 !important; animation: none !important; }");
            variables.AppendLine("body.san-webview-force-visible #achicon { opacity: 1 !important; scale: 1 !important; animation: none !important; display: grid !important; }");
            variables.AppendLine("body.san-webview-force-visible .wrapper#achiconwrapper { opacity: 1 !important; scale: 1 !important; animation: none !important; }");
            variables.AppendLine("body.san-webview-no-secondary-icon .wrapper#logo, body.san-webview-no-secondary-icon #logo { display: none !important; opacity: 0 !important; animation: none !important; }");
            variables.AppendLine(".san-game-cover { position: absolute; top: 0; bottom: 0; width: var(--san-cover-width); background: center / cover no-repeat var(--san-cover-image); opacity: 1; pointer-events: none; z-index: 2; }");
            variables.AppendLine(".san-game-cover.left { left: 0; }");
            variables.AppendLine(".san-game-cover.right { right: 0; }");
            variables.AppendLine("body.san-webview-has-cover .wrapper#achcont { overflow: hidden; }");
            variables.AppendLine("body.san-webview-has-cover.san-cover-left .san-line-stack { margin-left: var(--san-cover-width) !important; max-width: calc(100% - var(--san-cover-width)) !important; }");
            variables.AppendLine("body.san-webview-has-cover.san-cover-right .san-line-stack { margin-right: var(--san-cover-width) !important; max-width: calc(100% - var(--san-cover-width)) !important; }");
            variables.AppendLine($"body {{ --san-cover-image: {(string.IsNullOrWhiteSpace(coverImageUri) ? "none" : $"url('{CssUrl(coverImageUri)}')")}; --san-cover-width: {Math.Max(24, settings?.GameCoverWidth ?? 80).ToString("0.###", CultureInfo.InvariantCulture)}px; }}");
            variables.AppendLine("body[gameart] .wrapper#bg, body[bgimg] .wrapper#bg { background-color: var(--primarycolor) !important; }");
            variables.AppendLine($"body[gameart] .wrapper#bg::after, body[bgimg] .wrapper#bg::after {{ opacity: {Math.Max(0, Math.Min(1, settings?.GameBannerOpacity ?? 0.3)).ToString("0.###", CultureInfo.InvariantCulture)} !important; }}");
            variables.AppendLine("body[data-san-elements=\"xbox360\"] .wrapper#achcontent .san-line-stack, body[data-san-elements=\"xboxone\"] .wrapper#achcontent .san-line-stack, body[data-san-elements=\"ps5\"] .wrapper#achcontent .san-line-stack, body[data-san-elements=\"ps4\"] .wrapper#achcontent .san-line-stack { grid-column: 2 !important; }");
            variables.AppendLine("body[data-san-preset=\"epicgames\"] .wrapper#achcont, body[data-san-elements=\"epicgames\"] .wrapper#achcont { width: 100% !important; scale: 1 !important; opacity: var(--opacity) !important; }");
            variables.AppendLine("body[data-san-preset=\"epicgames\"] .wrapper#achcontent, body[data-san-elements=\"epicgames\"] .wrapper#achcontent { position: absolute !important; inset: 0 !important; width: auto !important; display: grid !important; grid-template-columns: calc(var(--san-icon-size) + 10px) minmax(0, 1fr) calc(var(--san-icon-size) + 10px) !important; align-items: center !important; justify-content: stretch !important; padding: calc(8px * var(--scale)) calc(12px * var(--scale)) !important; opacity: 1 !important; visibility: visible !important; overflow: hidden !important; }");
            variables.AppendLine("body[data-san-preset=\"epicgames\"] .wrapper#achcontent .san-line-stack, body[data-san-elements=\"epicgames\"] .wrapper#achcontent .san-line-stack { grid-column: 2 !important; position: relative !important; z-index: 50 !important; opacity: 1 !important; visibility: visible !important; display: flex !important; width: 100% !important; height: 100% !important; align-items: center !important; text-align: center !important; }");
            variables.AppendLine("body[data-san-preset=\"epicgames\"] #iconbg, body[data-san-elements=\"epicgames\"] #iconbg { display: none !important; opacity: 0 !important; background: none !important; }");
            if (settings?.EnableGameBannerAsBackground == true)
            {
                variables.AppendLine("body[data-san-preset=\"epicgames\"] .wrapper#achcont::before, body[data-san-elements=\"epicgames\"] .wrapper#achcont::before { display: none !important; opacity: 0 !important; }");
                variables.AppendLine("body[data-san-preset=\"epicgames\"] .shadow, body[data-san-elements=\"epicgames\"] .shadow { display: none !important; opacity: 0 !important; }");
                variables.AppendLine("body[data-san-preset=\"epicgames\"] .wrapper#bg, body[data-san-elements=\"epicgames\"] .wrapper#bg { background-color: var(--primarycolor) !important; }");
            }
            variables.AppendLine("body[data-san-preset=\"default\"] #bg, body[data-san-elements=\"default\"] #bg { background-color: var(--primarycolor) !important; }");
            variables.AppendLine("body[data-san-preset=\"default\"] .wrapper#achcontent .san-line-stack, body[data-san-elements=\"default\"] .wrapper#achcontent .san-line-stack { box-sizing: border-box !important; padding-left: calc(12px * var(--scale)) !important; padding-right: calc(12px * var(--scale)) !important; }");
            variables.AppendLine("body[data-san-preset=\"gfwl\"] #iconbg, body[data-san-elements=\"gfwl\"] #iconbg { background: var(--tertiarycolor) !important; background-image: none !important; }");
            variables.AppendLine("body[data-san-preset=\"gfwl\"] .wrapper#logo, body[data-san-elements=\"gfwl\"] .wrapper#logo { background-image: none !important; }");
            variables.AppendLine("body[data-san-preset=\"gfwl\"] .wrapper#achiconwrapper::before, body[data-san-preset=\"gfwl\"] .wrapper#logo::before, body[data-san-elements=\"gfwl\"] .wrapper#achiconwrapper::before, body[data-san-elements=\"gfwl\"] .wrapper#logo::before { background: var(--tertiarycolor) !important; background-image: none !important; }");
            variables.AppendLine("body[data-san-preset=\"gfwl\"] .wrapper#logo, body[data-san-elements=\"gfwl\"] .wrapper#logo { width: var(--san-icon-size) !important; height: var(--san-icon-size) !important; min-width: var(--san-icon-size) !important; min-height: var(--san-icon-size) !important; max-width: var(--san-icon-size) !important; max-height: var(--san-icon-size) !important; }");
            variables.AppendLine("body[data-san-elements=\"steamdeck\"] .wrapper#achcontent .san-line-stack, body[data-san-elements=\"ps3\"] .wrapper#achcontent .san-line-stack { grid-column: 1 / -1 !important; }");
            variables.AppendLine("body[data-san-elements=\"xboxone\"] #iconbg { display: none !important; }");
            variables.AppendLine("body[data-san-preset=\"windows\"] .wrapper#content, body[data-san-elements=\"windows\"] .wrapper#content { gap: calc(0.12rem * var(--scale)) calc(0.35rem * var(--scale)) !important; padding: calc(0.25rem * var(--scale)) calc(0.45rem * var(--scale)) !important; align-content: center !important; }");
            variables.AppendLine("body[data-san-preset=\"windows\"] .wrapper#header, body[data-san-elements=\"windows\"] .wrapper#header { padding: calc(0.12rem * var(--scale)) calc(0.45rem * var(--scale)) !important; gap: calc(0.35rem * var(--scale)) !important; align-self: end !important; }");
            variables.AppendLine("body[data-san-preset=\"windows\"] .wrapper#header > .wrapper#logo, body[data-san-elements=\"windows\"] .wrapper#header > .wrapper#logo { width: calc(0.95rem * var(--unlockmsgfontsize)) !important; height: calc(0.95rem * var(--unlockmsgfontsize)) !important; min-width: calc(0.95rem * var(--unlockmsgfontsize)) !important; min-height: calc(0.95rem * var(--unlockmsgfontsize)) !important; max-width: calc(0.95rem * var(--unlockmsgfontsize)) !important; max-height: calc(0.95rem * var(--unlockmsgfontsize)) !important; }");
            variables.AppendLine("body[data-san-preset=\"windows\"] .wrapper#achcontent, body[data-san-elements=\"windows\"] .wrapper#achcontent { row-gap: calc(0.08rem * var(--scale)) !important; align-self: start !important; }");
            variables.AppendLine("body[data-san-preset=\"windows\"] .wrapper#achcontent > .san-line-stack, body[data-san-elements=\"windows\"] .wrapper#achcontent > .san-line-stack { justify-content: flex-start !important; }");
            variables.AppendLine("body[data-san-elements=\"ps5\"] .wrapper#logo, body[data-san-elements=\"ps4\"] .wrapper#logo { margin-inline: calc(0.35rem * var(--scale)) !important; margin-left: calc(0.35rem * var(--scale)) !important; margin-right: calc(0.35rem * var(--scale)) !important; }");
            variables.AppendLine("body[data-san-elements=\"ps5\"] .wrapper#achcontent, body[data-san-elements=\"ps4\"] .wrapper#achcontent { column-gap: calc(0.2rem * var(--fontsize)) !important; }");
            if (settings?.OverlayCustomShowIconRarityGlow == true)
            {
                variables.AppendLine($".wrapper#achiconwrapper {{ filter: drop-shadow(0 0 {(Math.Max(2, settings.OverlayCustomIconSize * 0.16)).ToString("0.###", CultureInfo.InvariantCulture)}px {CssColor(glowColor, rarityColor)}) !important; }}");
            }
            if (settings?.OverlayCustomShowSecondaryIconRarityGlow == true && settings?.OverlayCustomShowSecondaryIcon == true)
            {
                variables.AppendLine($".wrapper#logo, .wrapper#achiconwrapper.secondary, .wrapper#logo.secondary, .san-secondary-icon {{ filter: drop-shadow(0 0 {(Math.Max(2, settings.OverlayCustomSecondaryIconSize * 0.16)).ToString("0.###", CultureInfo.InvariantCulture)}px {CssColor(glowColor, rarityColor)}) !important; }}");
            }
            if (!followsTransitionElement && (settings?.OverlayCustomSanElementPosition ?? LocalSanElementPosition.Left) == LocalSanElementPosition.Right)
            {
                variables.AppendLine("body[data-san-elements] { --achiconstart: 3; --logostart: 3; }");
                variables.AppendLine("body[data-san-elements] .wrapper#achiconwrapper, body[data-san-elements] .wrapper#logo, body[data-san-elements] #iconbg { grid-column: 3 !important; grid-column-start: 3 !important; justify-self: end !important; }");
                variables.AppendLine("body[data-san-elements] #iconbg { left: auto !important; right: 0 !important; }");
                variables.AppendLine("body[data-san-elements] .wrapper#achcontent::before { grid-column-start: 3 !important; justify-self: end !important; }");
                variables.AppendLine("body[data-san-elements] .wrapper#achcontent > .san-line-stack { grid-column: 1 / 3 !important; }");
            }
            else if (!followsTransitionElement)
            {
                variables.AppendLine("body[data-san-elements] { --achiconstart: 1; --logostart: 1; }");
                variables.AppendLine("body[data-san-elements] .wrapper#achiconwrapper, body[data-san-elements] .wrapper#logo, body[data-san-elements] #iconbg { grid-column: 1 !important; grid-column-start: 1 !important; justify-self: start !important; }");
                variables.AppendLine("body[data-san-elements] #iconbg { left: 0 !important; right: auto !important; }");
                variables.AppendLine("body[data-san-elements] .wrapper#achcontent::before { grid-column-start: 1 !important; justify-self: start !important; }");
                variables.AppendLine("body[data-san-elements] .wrapper#achcontent > .san-line-stack { grid-column: 2 / -1 !important; }");
            }
            if (!followsTransitionElement && (settings?.OverlayCustomSanElementPosition ?? LocalSanElementPosition.Left) == LocalSanElementPosition.Right)
            {
                variables.AppendLine("body[data-san-elements=\"default\"] .wrapper#achiconwrapper { grid-column-start: 3 !important; justify-self: end !important; }");
                variables.AppendLine("body[data-san-elements=\"xbox360\"] .wrapper#achiconwrapper { justify-self: end !important; }");
                variables.AppendLine("body[data-san-elements=\"xbox360\"] .wrapper#achcontent { grid-template-columns: 1fr calc(var(--notifyheight) + (5px * var(--scale))) !important; padding-left: calc(8px * var(--scale)) !important; padding-right: calc(8px * var(--scale)) !important; }");
                variables.AppendLine("body[data-san-elements=\"xbox360\"] .wrapper#achcontent .san-line-stack { grid-column: 1 !important; }");
            }
            else if (!followsTransitionElement)
            {
                variables.AppendLine("body[data-san-elements=\"default\"] .wrapper#achiconwrapper { grid-column-start: 1 !important; justify-self: start !important; }");
                variables.AppendLine("body[data-san-elements=\"xbox360\"] .wrapper#achiconwrapper { justify-self: start !important; }");
                variables.AppendLine("body[data-san-elements=\"xbox360\"] .wrapper#achcontent { grid-template-columns: calc(var(--notifyheight) + (5px * var(--scale))) 1fr !important; padding-left: calc(8px * var(--scale)) !important; padding-right: calc(8px * var(--scale)) !important; }");
                variables.AppendLine("body[data-san-elements=\"xbox360\"] .wrapper#achcontent .san-line-stack { grid-column: 2 !important; }");
            }
            variables.AppendLine("body[data-san-elements=\"ps5\"] .wrapper#achiconwrapper, body[data-san-elements=\"ps4\"] .wrapper#achiconwrapper { grid-column: 1 !important; grid-column-start: 1 !important; justify-self: start !important; z-index: 4 !important; }");
            variables.AppendLine("body[data-san-elements=\"ps5\"] .wrapper#logo, body[data-san-elements=\"ps4\"] .wrapper#logo { grid-column: 3 !important; grid-column-start: 3 !important; justify-self: end !important; z-index: 4 !important; }");
            variables.AppendLine("body[data-san-elements=\"ps5\"] .wrapper#achcontent, body[data-san-elements=\"ps4\"] .wrapper#achcontent { grid-column: 2 !important; min-width: 0 !important; }");
            variables.AppendLine("body[data-san-elements=\"default\"] .wrapper#achiconwrapper { z-index: 4 !important; }");
            variables.AppendLine("body[data-san-elements=\"default\"] .wrapper#achcontent .san-line-stack { box-sizing: border-box !important; }");
            variables.AppendLine("body[data-san-elements=\"default\"] #achcont[data-san-view-index=\"1\"] .wrapper#achcontent .san-line-stack { margin-left: calc(var(--san-icon-size) + (12px * var(--scale))) !important; max-width: calc(100% - var(--san-icon-size) - (12px * var(--scale))) !important; }");
            variables.AppendLine("body[data-san-elements=\"default\"] #achcont[data-san-view-index=\"2\"] .wrapper#achcontent .san-line-stack { margin-right: calc(var(--san-secondary-icon-size) + (12px * var(--scale))) !important; max-width: calc(100% - var(--san-secondary-icon-size) - (12px * var(--scale))) !important; }");
            variables.AppendLine("body.san-webview-disable-san-transition .wrapper#achcont, body.san-webview-disable-san-transition .wrapper#bg, body.san-webview-disable-san-transition .wrapper#achiconwrapper, body.san-webview-disable-san-transition .wrapper#achcontent, body.san-webview-disable-san-transition .wrapper#achcontent > span { animation: none !important; transition: none !important; opacity: 1 !important; scale: 1 !important; translate: 0 0 !important; }");
            variables.AppendLine("body.san-webview-disable-san-transition .wrapper#achcont { width: var(--notifywidth) !important; }");

            var script = $@"
<script>
const sanStrings = {{
  unlockmsg: {JsString(unlockMessage)},
  title: {JsString(title)},
  desc: {JsString(desc)}
}};
const sanLineDefinitions = {sanLineDefinitionsJson};
const sanElems = {JsStringArray(sanElems)};
const sanEscape = value => String(value || '').replace(/[&<>'""]/g, ch => {{
  switch (ch) {{
    case '&': return '&amp;';
    case '<': return '&lt;';
    case '>': return '&gt;';
    case ""'"": return '&#39;';
    case '""': return '&quot;';
    default: return ch;
  }}
}});
const sanAddElem = (type, pos) => {{
  if (type === 'decoration' && {JsBool(customisation?.Value<bool?>("showdecoration") == true)} && {JsInt(customisation?.Value<int?>("decorationpos") ?? 0)} === pos) return '<span id=""decoration""></span>';
  if (type === 'hiddenicon') return '';
  if (type === 'percent') return '';
  return '';
}};
const sanApplyElems = () => {{
  const allContainers = [...document.querySelectorAll('#achcont')];
  const activeContainers = {JsBool(!usesSanTimeline)} && allContainers.length > 1 ? [allContainers[allContainers.length - 1]] : allContainers;
  if ({JsBool(!usesSanTimeline)} && allContainers.length > 1) {{
    allContainers.slice(0, -1).forEach(el => el.style.display = 'none');
  }}
  const views = activeContainers.length > 0 ? activeContainers : [document];
  views.forEach((root, index) => {{
    if (root && root.dataset) {{
      root.dataset.sanViewIndex = String(index + 1);
    }}
  }});
  const getTextTargets = root => {{
    const content = root.querySelector ? root.querySelector('#achcontent') : null;
    if (!content) {{
      return [...(root.querySelectorAll ? root.querySelectorAll('#unlockmsg,#title,#desc') : [])].slice(0, 3);
    }}
    [...content.querySelectorAll('#unlockmsg,#title,#desc')].forEach(el => {{
      el.innerHTML = '';
      el.style.display = 'none';
    }});
    let stack = content.querySelector('.san-line-stack');
    if (!stack) {{
      stack = document.createElement('div');
      stack.className = 'san-line-stack';
      content.appendChild(stack);
    }}
    const visibleCounts = [0, 1].map(viewIndex => sanLineDefinitions.filter(line => line.html && (views.length < 2 || line.view === 2 || line.view === viewIndex)).length);
    const desiredCount = Math.max(3, ...visibleCounts);
    let targets = [...stack.querySelectorAll('.san-generated-line')];
    while (targets.length < desiredCount) {{
      const span = document.createElement('span');
      span.className = 'san-generated-line';
      stack.appendChild(span);
      targets.push(span);
    }}
    return targets.slice(0, desiredCount);
  }};
  const fillView = (root, viewIndex) => {{
    const lines = sanLineDefinitions
      .filter(line => line.html && (line.view === 2 || line.view === viewIndex))
      .sort((a, b) => a.order - b.order || a.index - b.index);
    const targets = getTextTargets(root);
    targets.forEach((el, slotIndex) => {{
        const line = lines[slotIndex];
        if (!line) {{
          el.innerHTML = '';
          el.style.display = 'none';
          return;
        }}
        const pos = slotIndex + 1;
        el.style.display = '';
        el.dataset.sanLineId = 'line' + line.index;
        el.innerHTML = sanAddElem('decoration', pos) + sanAddElem('hiddenicon', pos) + '<span class=""san-line-inner"">' + line.html + '</span>' + sanAddElem('percent', pos);
        el.style.color = line.color || '';
        el.style.fontSize = line.size ? line.size + 'px' : '';
        el.style.marginBottom = line.spacing ? line.spacing + 'px' : '0';
        el.style.paddingBottom = line.spacing ? line.spacing + 'px' : '0';
        el.style.lineHeight = '1.15';
        const textDecoration = [];
        if (line.underline) textDecoration.push('underline');
        if (line.strike) textDecoration.push('line-through');
        el.style.setProperty('font-weight', line.bold ? '700' : '400', 'important');
        el.style.setProperty('font-style', line.italic ? 'italic' : 'normal', 'important');
        el.style.setProperty('text-decoration', textDecoration.join(' ') || 'none', 'important');
        const inner = el.querySelector('.san-line-inner');
        if (inner) {{
          inner.style.setProperty('font-weight', line.bold ? '700' : '400', 'important');
          inner.style.setProperty('font-style', line.italic ? 'italic' : 'normal', 'important');
          inner.style.setProperty('text-decoration', textDecoration.join(' ') || 'none', 'important');
          inner.style.setProperty('-webkit-text-stroke', line.outlineColor ? (line.outlineSize || 0.6) + 'px ' + line.outlineColor : '', 'important');
          const shadowSize = line.shadowSize || 2;
          inner.style.setProperty('text-shadow', line.shadowColor ? '0 ' + Math.max(1, shadowSize / 2) + 'px ' + shadowSize + 'px ' + line.shadowColor : '', 'important');
        }}
    }});
  }};
  views.forEach((root, index) => fillView(root, index));
  if (views.length === 1 && sanLineDefinitions.some(line => line.view === 1)) {{
    window.setTimeout(() => {{
      const root = views[0];
      if (root && root.dataset) {{
        root.dataset.sanViewIndex = '2';
      }}
      fillView(root, 1);
    }}, {Math.Max(500, (int)Math.Round(view1Seconds * 1000)).ToString(CultureInfo.InvariantCulture)});
  }}
  document.body.toggleAttribute('alldetails', sanLineDefinitions.filter(line => line.html).length >= 3);
}};
document.body.dataset.sanAnimationPreset = {JsString(animationPreset)};
document.body.dataset.sanElements = {JsString(elementPreset)};
sanApplyElems();
if (document.body.dataset.sanAnimationPreset === 'epicgames' || document.body.dataset.sanElements === 'epicgames') document.body.classList.add('san-webview-force-visible');
document.body.classList.add('san-webview-fast-start');
document.body.classList.toggle('san-webview-disable-san-transition', {JsBool(!usesSanTimeline)});
document.body.classList.toggle('san-webview-no-secondary-icon', {JsBool(settings?.OverlayCustomShowSecondaryIcon != true)});
document.body.classList.toggle('san-webview-has-cover', {JsBool(settings?.EnableGameCoverInOverlay == true && !string.IsNullOrWhiteSpace(coverImageUri))});
document.body.classList.toggle('san-cover-left', {JsBool(settings?.GameCoverPosition == LocalOverlayCoverPosition.Left)});
document.body.classList.toggle('san-cover-right', {JsBool(settings?.GameCoverPosition != LocalOverlayCoverPosition.Left)});
if ({JsBool(settings?.EnableGameCoverInOverlay == true && !string.IsNullOrWhiteSpace(coverImageUri))}) {{
  const cover = document.createElement('div');
  cover.className = 'san-game-cover ' + ({JsBool(settings?.GameCoverPosition == LocalOverlayCoverPosition.Left)} ? 'left' : 'right');
  const container = document.getElementById('achcont') || document.body;
  container.appendChild(cover);
}}
document.querySelectorAll('#achicon').forEach(img => img.src = {JsString(iconUri)});
document.querySelectorAll('#logo').forEach(el => {{
  if (el.tagName && el.tagName.toLowerCase() === 'img') {{
    el.src = {JsString(logoUri)};
  }} else {{
    el.style.backgroundImage = 'url(' + {JsString(logoUri)} + ')';
  }}
}});
document.querySelectorAll('#achicon').forEach(img => {{
  const verify = () => {{
    if (!img.getAttribute('src') || (img.complete && img.naturalWidth === 0)) document.body.classList.add('san-webview-hide-icon-border');
  }};
  img.addEventListener('error', () => document.body.classList.add('san-webview-hide-icon-border'));
  window.setTimeout(verify, 120);
}});
document.body.toggleAttribute('nodecoration', {JsBool(customisation?.Value<bool?>("showdecoration") != true)});
document.body.toggleAttribute('noiconanim', {JsBool(customisation?.Value<bool?>("iconanim") != true)});
document.documentElement.style.setProperty('--decorationindex', {JsString((customisation?.Value<int?>("decorationpos") ?? 1).ToString(CultureInfo.InvariantCulture))});
const sanScoreTargets = document.getElementById('xpwrapper')
  ? [document.getElementById('xpwrapper')]
  : ['title', 'desc'].map(id => document.getElementById(id)).filter(Boolean);
sanScoreTargets.forEach(el => {{
  el.removeAttribute('gs');
  el.removeAttribute('unit');
}});
window.setTimeout(() => {{
  const visibleText = [...document.querySelectorAll('.san-line-stack .san-generated-line')].some(el => {{
    const style = getComputedStyle(el);
    return el.textContent.trim() && style.display !== 'none' && Number(style.opacity || '0') > 0.05;
  }});
if (!visibleText) document.body.classList.add('san-webview-force-visible');
}}, 250);
if ({JsBool(settings?.OverlayCustomAutoResizeToContent == true)}) {{
  let lastSanHeight = 0;
  const reportSanContentHeight = () => {{
    let bottom = Math.max(document.documentElement.scrollHeight, document.body.scrollHeight);
    document.querySelectorAll('body *').forEach(el => {{
      const style = getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden') return;
      const rect = el.getBoundingClientRect();
      if (Number.isFinite(rect.bottom)) bottom = Math.max(bottom, rect.bottom);
    }});
    const measured = Math.ceil(bottom + 2);
    if (measured > lastSanHeight) {{
      lastSanHeight = measured;
      document.documentElement.style.setProperty('--notifyheight', measured + 'px');
      if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('san-height:' + measured);
    }}
  }};
  window.addEventListener('load', reportSanContentHeight);
  window.setTimeout(reportSanContentHeight, 50);
  window.setTimeout(reportSanContentHeight, 300);
  window.setTimeout(reportSanContentHeight, 800);
}}
</script>";

            var manualCss = SanitizeInlineCss(settings?.OverlayCustomManualElementCss);
            return $@"<!doctype html>
<html>
<head>
<meta charset=""utf-8"">
<meta http-equiv=""X-UA-Compatible"" content=""IE=edge"">
<style>{globalCss}</style>
<style>{baseCss}</style>
<style>{baseAnimCss}</style>
<style>{elementCss}</style>
<style>{presetCss}</style>
<style>{variables}</style>
<style>{manualCss}</style>
</head>
<body {bodyAttrs} style=""background-color: transparent;"">
<audio src=""""></audio>
<div class=""wrapper"" id=""ssdisplay""></div>
{selectedPresetHtml}
{script}
</body>
</html>";
        }

        private static JObject TryParseSanTheme(LocalSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings?.OverlayCustomSanThemeJson))
            {
                return null;
            }

            try
            {
                return JObject.Parse(settings.OverlayCustomSanThemeJson);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildSanBodyAttributes(LocalSettings settings, JObject customisation, bool hasAllDetails, string animationPreset = null, string elementPreset = null)
        {
            var attrs = new List<string> { "main" };
            var backgroundStyle = settings?.EnableGameBannerAsBackground == true
                ? "gameart"
                : customisation?.Value<string>("bgstyle");
            attrs.Add(string.IsNullOrWhiteSpace(backgroundStyle) ? "solid" : backgroundStyle.Trim().ToLowerInvariant());
            attrs.Add((customisation?.Value<string>("pos") ?? "bottomright").Trim().ToLowerInvariant());
            if (customisation?.Value<bool?>("bgonly") == true)
            {
                attrs.Add("bgonly");
            }

            if (hasAllDetails)
            {
                attrs.Add("alldetails");
            }

            var preset = NormalizeSanPresetId(animationPreset ?? settings?.OverlayCustomSanPresetId);
            if (!string.IsNullOrWhiteSpace(preset))
            {
                attrs.Add($"data-san-preset=\"{SecurityElement.Escape(preset)}\"");
            }

            var elements = NormalizeSanPresetId(elementPreset);
            if (!string.IsNullOrWhiteSpace(elements))
            {
                attrs.Add($"data-san-elements=\"{SecurityElement.Escape(elements)}\"");
            }

            return string.Join(" ", attrs);
        }

        private string ResolveSanBackgroundImageUri(LocalSettings settings, JObject customisation, string achievementIconPath, Game game)
        {
            var backgroundStyle = (customisation?.Value<string>("bgstyle") ?? string.Empty).Trim().ToLowerInvariant();
            if (settings?.EnableGameBannerAsBackground == true)
            {
                var forcedGameArt = ResolveSanConfiguredGameArtUri(settings, game, includeCoverFallback: false);
                if (!string.IsNullOrWhiteSpace(forcedGameArt))
                {
                    return forcedGameArt;
                }
            }

            if (backgroundStyle == "bgimg")
            {
                if (customisation?.Value<bool?>("bgachicon") == true)
                {
                    return ResolveSanIconUri(achievementIconPath, settings, game);
                }

                var customBackground = ResolveSanThemeFileUri(settings, customisation?.Value<string>("bgimg"));
                if (!string.IsNullOrWhiteSpace(customBackground))
                {
                    return ToDataUri(customBackground);
                }

                return ToDataUri(ResolveSanAssetUri(settings, "img", "sanimgbg.png"));
            }

            if (backgroundStyle != "gameart")
            {
                return string.Empty;
            }

            var customGameArt = ResolveSanThemeFileUri(settings, customisation?.Value<string>("gameart"));
            if (!string.IsNullOrWhiteSpace(customGameArt))
            {
                return ToDataUri(customGameArt);
            }

            var configuredGameArt = ResolveSanConfiguredGameArtUri(settings, game, includeCoverFallback: true);
            if (!string.IsNullOrWhiteSpace(configuredGameArt))
            {
                return configuredGameArt;
            }

            return string.Empty;
        }

        private string ResolveSanConfiguredGameArtUri(LocalSettings settings, Game game, bool includeCoverFallback)
        {
            var candidates = new List<string>();
            if (settings?.EnableGameBannerAsBackground == true)
            {
                candidates.Add(settings.OverlayCustomBannerImagePath);
                candidates.Add(settings.OverlayCustomBackgroundImagePath);
                candidates.Add(game?.BackgroundImage);
            }

            if (includeCoverFallback && settings?.EnableGameCoverInOverlay == true)
            {
                candidates.Add(settings.OverlayCustomCoverImagePath);
                candidates.Add(game?.CoverImage);
            }

            foreach (var candidate in candidates)
            {
                var image = ResolvePlayniteImagePath(candidate);
                if (!string.IsNullOrWhiteSpace(image) && File.Exists(image))
                {
                    return ToDataUri(new Uri(image).AbsoluteUri);
                }
            }

            return string.Empty;
        }

        private string ResolveSanCoverImageUri(LocalSettings settings, Game game)
        {
            if (settings?.EnableGameCoverInOverlay != true)
            {
                return string.Empty;
            }

            var candidates = new[]
            {
                settings.OverlayCustomCoverImagePath,
                game?.CoverImage,
                game?.Icon
            };

            foreach (var candidate in candidates)
            {
                var image = ResolvePlayniteImagePath(candidate);
                if (!string.IsNullOrWhiteSpace(image) && File.Exists(image))
                {
                    return ToDataUri(new Uri(image).AbsoluteUri);
                }
            }

            return string.Empty;
        }

        private static string ResolveSanPercentBadgeImageUri(LocalSettings settings, JObject customisation, string achievementRarity)
        {
            if (customisation?.Value<bool?>("percentbadgeimg") != true)
            {
                return string.Empty;
            }

            var percent = ResolveSanPercentValue(achievementRarity);
            var tier = ResolveSanPercentTier(
                percent,
                customisation?.Value<double?>("rarity") ?? 10,
                customisation?.Value<double?>("semirarity") ?? 50);

            return ToDataUri(ResolveSanThemeFileUri(settings, customisation?.Value<string>($"percentbadgeimg{tier}")));
        }

        private static double ResolveSanPercentValue(string achievementRarity)
        {
            if (string.IsNullOrWhiteSpace(achievementRarity))
            {
                return 100;
            }

            var match = Regex.Match(achievementRarity, @"[\d]+(?:[.,][\d]+)?");
            if (!match.Success)
            {
                return 100;
            }

            return double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? Math.Max(0, Math.Min(100, value))
                : 100;
        }

        private static string ResolveSanPercentTier(double percent, double rareLimit, double semiRareLimit)
        {
            return percent > semiRareLimit
                ? "bronze"
                : percent > rareLimit
                    ? "silver"
                    : "gold";
        }

        private static string ResolveSanGamerscoreValue(double percent, double rareLimit, double semiRareLimit)
        {
            var value = 100 - (Math.Round(percent / 5.0, MidpointRounding.AwayFromZero) * 5);
            return Math.Max(0, Math.Min(100, value)).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string ResolveSanScoreUnit(LocalSettings settings)
        {
            var preset = NormalizeSanPresetId(settings?.OverlayCustomSanPresetId);
            switch (preset)
            {
                case "epicgames":
                    return " XP";
                case "xbox360":
                case "gfwl":
                    return "G";
                default:
                    return string.Empty;
            }
        }

        private static string ResolveSanDefaultDescription(LocalSettings settings, string gameName, string achievementDescription)
        {
            var preset = NormalizeSanPresetId(settings?.OverlayCustomSanPresetId);
            switch (preset)
            {
                case "xbox360":
                case "gfwl":
                    return string.IsNullOrWhiteSpace(gameName) ? "Steam Achievement Notifier" : gameName;
                case "steamdeck":
                case "xqjan":
                    return achievementDescription ?? string.Empty;
                default:
                    return string.Empty;
            }
        }

        private static SanBadgePosition ResolveSanBadgePosition(string position, double xOffset, double yOffset)
        {
            var key = (position ?? "bottomcenter").Trim().ToLowerInvariant();
            int x;
            int y;
            switch (key)
            {
                case "topleft":
                    x = 20;
                    y = 10;
                    break;
                case "topcenter":
                    x = 50;
                    y = 10;
                    break;
                case "topright":
                    x = 80;
                    y = 10;
                    break;
                case "bottomleft":
                    x = 20;
                    y = 90;
                    break;
                case "bottomright":
                    x = 80;
                    y = 90;
                    break;
                case "bottomcenter":
                default:
                    x = 50;
                    y = 90;
                    break;
            }

            return new SanBadgePosition
            {
                X = $"calc({x}% + {xOffset.ToString("0.###", CultureInfo.InvariantCulture)}px) calc({100 - x}% - {xOffset.ToString("0.###", CultureInfo.InvariantCulture)}px)",
                Y = $"calc({y}% + {yOffset.ToString("0.###", CultureInfo.InvariantCulture)}px) 0"
            };
        }

        private sealed class SanBadgePosition
        {
            public string X { get; set; }
            public string Y { get; set; }
        }

        private static string ResolveSanFontShadow(JObject customisation, double sanScale)
        {
            if (customisation?.Value<bool?>("fontshadow") != true)
            {
                return "none";
            }

            var x = customisation.Value<double?>("fontshadowx") ?? 0;
            var y = customisation.Value<double?>("fontshadowy") ?? 0;
            var blur = sanScale * (customisation.Value<double?>("fontshadowscale") ?? 1);
            var color = CssColor(customisation.Value<string>("fontshadowcolor"), "#000000");
            var shadow = $"drop-shadow({x.ToString("0.###", CultureInfo.InvariantCulture)}px {y.ToString("0.###", CultureInfo.InvariantCulture)}px {blur.ToString("0.###", CultureInfo.InvariantCulture)}px {color})";
            return $"{shadow} {shadow} {shadow}";
        }

        private static string ResolveSanMask(LocalSettings settings, JObject customisation)
        {
            if (customisation?.Value<bool?>("mask") != true)
            {
                return "none";
            }

            var maskUri = ResolveSanThemeFileUri(settings, customisation.Value<string>("maskimg"));
            return string.IsNullOrWhiteSpace(maskUri)
                ? "none"
                : $"url('{CssUrl(ToDataUri(maskUri))}') center / cover no-repeat";
        }

        private static string RewriteCssUrls(string css, string originDirectory)
        {
            if (string.IsNullOrWhiteSpace(css) || string.IsNullOrWhiteSpace(originDirectory))
            {
                return css ?? string.Empty;
            }

            return Regex.Replace(css, "url\\((?<quote>['\"]?)(?<path>[^)'\"]+)(\\k<quote>)\\)", match =>
            {
                var raw = match.Groups["path"].Value.Trim();
                if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("#", StringComparison.Ordinal))
                {
                    return match.Value;
                }

                var absolute = Path.GetFullPath(Path.Combine(originDirectory, raw.Replace('/', Path.DirectorySeparatorChar)));
                return $"url('{CssUrl(new Uri(absolute).AbsoluteUri)}')";
            }, RegexOptions.IgnoreCase);
        }

        private static string ReadSanAssetText(LocalSettings settings, params string[] relativeParts)
        {
            var roots = new[]
            {
                settings?.OverlayCustomSanAssetRootPath,
                Path.Combine(GetPluginAssemblyDirectory(), "Resources", "Tools", "SAN-source"),
                @"E:\Programs\Playnite\CustomExtension\PlayniteAchievements\tools\SAN-source"
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var path = relativeParts.Aggregate(root, Path.Combine);
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }

            return string.Empty;
        }

        private static string ResolveBundledSanNotifyDirectory()
        {
            var roots = new[]
            {
                Path.Combine(GetPluginAssemblyDirectory(), "Resources", "Tools", "SAN-source"),
                @"E:\Programs\Playnite\CustomExtension\PlayniteAchievements\tools\SAN-source"
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var notifyDir = Path.Combine(root, "notify");
                if (File.Exists(Path.Combine(notifyDir, "base.css")) &&
                    File.Exists(Path.Combine(notifyDir, "baseanim.css")) &&
                    Directory.Exists(Path.Combine(notifyDir, "presets")))
                {
                    return notifyDir;
                }
            }

            return string.Empty;
        }

        private static string ResolveSanIconUri(string achievementIconPath, LocalSettings settings, Game game)
        {
            if (!string.IsNullOrWhiteSpace(achievementIconPath) && File.Exists(achievementIconPath))
            {
                return ToDataUri(achievementIconPath);
            }

            var fallback = ResolveSanAssetUri(settings, "img", "achicon.png");
            return string.IsNullOrWhiteSpace(fallback) ? string.Empty : ToDataUri(fallback);
        }

        private static string ResolveSanDisplayIconUri(string achievementIconPath, LocalSettings settings, Game game, string providerKey, string achievementRarity, string achievementTrophy, int? achievementPoints)
        {
            switch (settings?.OverlayCustomIconSource ?? LocalOverlayIconSource.AchievementIcon)
            {
                case LocalOverlayIconSource.TrophyIcon:
                    return ResolveSanTrophyIconUri(settings, achievementTrophy, achievementRarity, achievementPoints);
                case LocalOverlayIconSource.PentagonIcon:
                    return ResolveSanRarityIconUri(achievementRarity, achievementPoints);
                case LocalOverlayIconSource.ProviderIcon:
                    return ResolveSanProviderIconUri(settings, providerKey);
                case LocalOverlayIconSource.CustomIcon:
                    return ResolveSanCustomIconUri(settings?.OverlayCustomIconPath);
                case LocalOverlayIconSource.AchievementIcon:
                default:
                    return ResolveSanIconUri(achievementIconPath, settings, game);
            }
        }

        private string ResolveSanSecondaryIconUri(LocalSettings settings, Game game, string achievementIconPath, string providerKey, string achievementRarity, string achievementTrophy, int? achievementPoints)
        {
            switch (settings?.OverlayCustomSecondaryIconSource ?? LocalOverlayIconSource.AchievementIcon)
            {
                case LocalOverlayIconSource.None:
                    return string.Empty;
                case LocalOverlayIconSource.TrophyIcon:
                    return ResolveSanTrophyIconUri(settings, achievementTrophy, achievementRarity, achievementPoints);
                case LocalOverlayIconSource.PentagonIcon:
                    return ResolveSanRarityIconUri(achievementRarity, achievementPoints);
                case LocalOverlayIconSource.ProviderIcon:
                    return ResolveSanProviderIconUri(settings, providerKey);
                case LocalOverlayIconSource.CustomIcon:
                    return ResolveSanCustomIconUri(settings?.OverlayCustomSecondaryIconPath);
                case LocalOverlayIconSource.AchievementIcon:
                default:
                    return ResolveSanIconUri(achievementIconPath, settings, game);
            }
        }

        private static string ResolveSanCustomIconUri(string customIconPath)
        {
            if (string.IsNullOrWhiteSpace(customIconPath))
            {
                return string.Empty;
            }

            return ToDataUri(customIconPath);
        }

        private string ResolveSanGameIconUri(LocalSettings settings, Game game, string achievementIconPath)
        {
            var candidates = new[]
            {
                settings?.OverlayCustomCoverImagePath,
                game?.Icon,
                game?.CoverImage,
                game?.BackgroundImage
            };

            foreach (var candidate in candidates)
            {
                var image = ResolvePlayniteImagePath(candidate);
                if (!string.IsNullOrWhiteSpace(image) && File.Exists(image))
                {
                    return ToDataUri(new Uri(image).AbsoluteUri);
                }
            }

            return ResolveSanIconUri(achievementIconPath, settings, game);
        }

        private static string ResolveSanTrophyIconUri(LocalSettings settings, string achievementTrophy, string achievementRarity, int? achievementPoints)
        {
            var trophyKey = GetTrophyResourceKeyForTrophy(achievementTrophy) ?? GetTrophyResourceKey(ResolveRarityKey(achievementRarity, achievementPoints));
            string fileName;
            switch (trophyKey)
            {
                case "TrophyPlatinum":
                    fileName = "sanlogotrophy.svg";
                    break;
                case "TrophyGold":
                    fileName = "sanlogotrophy_gold.svg";
                    break;
                case "TrophySilver":
                    fileName = "sanlogotrophy_silver.svg";
                    break;
                default:
                    fileName = "sanlogotrophy_bronze.svg";
                    break;
            }

            var uri = ResolveSanAssetUri(settings, "img", fileName);
            return string.IsNullOrWhiteSpace(uri) ? string.Empty : ToDataUri(uri);
        }

        private static string ResolveSanProviderIconUri(LocalSettings settings, string providerKey)
        {
            var safeProvider = string.IsNullOrWhiteSpace(providerKey) ? "Local" : providerKey.Trim();
            if (string.Equals(safeProvider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                var localImage = ResolveSanLocalProviderIconUri(settings);
                if (!string.IsNullOrWhiteSpace(localImage))
                {
                    return localImage;
                }
            }

            if (TryResolveProviderVisuals(settings, safeProvider, out var iconKey, out var colorHex))
            {
                var customImage = ToDataUri(iconKey);
                if (!string.IsNullOrWhiteSpace(customImage) && !string.Equals(customImage, iconKey, StringComparison.OrdinalIgnoreCase))
                {
                    return customImage;
                }

                var geometryIcon = BuildProviderGeometryDataUri(iconKey, colorHex);
                if (!string.IsNullOrWhiteSpace(geometryIcon))
                {
                    return geometryIcon;
                }
            }

            return BuildSanProviderBadgeDataUri(safeProvider);
        }

        private static string ResolveSanLocalProviderIconUri(LocalSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings?.CustomProviderIconPath) && File.Exists(settings.CustomProviderIconPath))
            {
                return ToDataUri(settings.CustomProviderIconPath);
            }

            var borrowedProviderKey = settings?.BorrowedProviderIconKey?.Trim();
            if (!string.IsNullOrWhiteSpace(borrowedProviderKey) &&
                !string.Equals(borrowedProviderKey, "Local", StringComparison.OrdinalIgnoreCase))
            {
                var borrowed = ResolveSanProviderIconUri(settings, borrowedProviderKey);
                if (!string.IsNullOrWhiteSpace(borrowed))
                {
                    return borrowed;
                }
            }

            if (TryResolveProviderVisuals(settings, "Local", out var iconKey, out var colorHex))
            {
                var customImage = ToDataUri(iconKey);
                if (!string.IsNullOrWhiteSpace(customImage) && !string.Equals(customImage, iconKey, StringComparison.OrdinalIgnoreCase))
                {
                    return customImage;
                }

                var geometryIcon = BuildProviderGeometryDataUri(iconKey, colorHex);
                if (!string.IsNullOrWhiteSpace(geometryIcon))
                {
                    return geometryIcon;
                }
            }

            return BuildSanLocalProviderBadgeDataUri();
        }

        private static string BuildProviderGeometryDataUri(string iconKey, string colorHex)
        {
            if (string.IsNullOrWhiteSpace(iconKey))
            {
                return string.Empty;
            }

            try
            {
                if (string.Equals(iconKey, "ProviderIconLocal", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(iconKey, "GeoLocal", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildLocalProviderIconDataUri(colorHex);
                }

                var geoKey = iconKey.StartsWith("Geo", StringComparison.Ordinal)
                    ? iconKey
                    : "Geo" + iconKey.Replace("ProviderIcon", string.Empty);
                var geometry = Application.Current?.TryFindResource(geoKey) as Geometry;
                if (geometry == null)
                {
                    return string.Empty;
                }

                var fill = CssColor(colorHex, "#FF8A00");
                var data = SecurityElement.Escape(SanitizeSvgPathData(geometry.ToString(CultureInfo.InvariantCulture)));
                var svg = $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 32 32""><path d=""{data}"" fill=""{fill}""/></svg>";
                return BuildSvgDataUri(svg);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SanitizeSvgPathData(string data)
        {
            var sanitized = (data ?? string.Empty).Trim();
            if (sanitized.StartsWith("F1 ", StringComparison.OrdinalIgnoreCase) ||
                sanitized.StartsWith("F0 ", StringComparison.OrdinalIgnoreCase))
            {
                sanitized = sanitized.Substring(3).TrimStart();
            }

            return sanitized;
        }

        private static string BuildLocalProviderIconDataUri(string colorHex)
        {
            var fill = CssColor(colorHex, "#FF8A00");
            var path = "M8 9C8 6.791 9.791 5 12 5h8c2.209 0 4 1.791 4 4 1.521 0 2.891.922 3.472 2.328l1.709 4.14c1.33 3.223-1.038 6.782-4.525 6.782-1.433 0-2.819-.518-3.902-1.459l-1.838-1.598c-.546-.474-1.245-.735-1.968-.735h-1.896c-.723 0-1.422.261-1.968.735l-1.838 1.598c-1.083.941-2.469 1.459-3.902 1.459-3.487 0-5.855-3.559-4.525-6.782l1.709-4.14C5.109 9.922 6.479 9 8 9zm3.25.25v2.5h-2.5v1.5h2.5v2.5h1.5v-2.5h2.5v-1.5h-2.5v-2.5h-1.5zm9.25 2a1.5 1.5 0 100 3 1.5 1.5 0 000-3zm3 3a1.5 1.5 0 100 3 1.5 1.5 0 000-3z";
            var svg = $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 32 32""><path d=""{path}"" fill=""{fill}""/></svg>";
            return BuildSvgDataUri(svg);
        }

        private static string BuildSanRarityBadgeDataUri(string rarityKey)
        {
            string color;
            string stroke;
            switch (rarityKey)
            {
                case "UltraRare":
                    color = "#b490ff";
                    stroke = "#f0dcff";
                    break;
                case "Rare":
                    color = "#4aa3ff";
                    stroke = "#cde9ff";
                    break;
                case "Uncommon":
                    color = "#75c878";
                    stroke = "#ddf7df";
                    break;
                default:
                    color = "#b8b8b8";
                    stroke = "#ffffff";
                    break;
            }

            var svg = $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 64 64""><path d=""M32 5 58 24 48 56H16L6 24Z"" fill=""{color}"" stroke=""{stroke}"" stroke-width=""5""/><path d=""M32 13 49 26 43 48H21L15 26Z"" fill=""#111"" opacity="".22""/></svg>";
            return BuildSvgDataUri(svg);
        }

        private static string BuildSanProviderBadgeDataUri(string providerKey)
        {
            var label = string.IsNullOrWhiteSpace(providerKey) ? "P" : providerKey.Trim().Substring(0, 1).ToUpperInvariant();
            var svg = $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 64 64""><rect x=""5"" y=""5"" width=""54"" height=""54"" rx=""12"" fill=""#1f3f7d"" stroke=""#ffffff"" stroke-width=""4""/><text x=""32"" y=""40"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""28"" font-weight=""700"" fill=""#ffffff"">{SecurityElement.Escape(label)}</text></svg>";
            return BuildSvgDataUri(svg);
        }

        private static string BuildSanLocalProviderBadgeDataUri()
        {
            var svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 64 64""><rect x=""5"" y=""5"" width=""54"" height=""54"" rx=""12"" fill=""#1f3f7d"" stroke=""#ffffff"" stroke-width=""4""/><text x=""32"" y=""42"" text-anchor=""middle"" font-family=""Arial Black,Arial,sans-serif"" font-size=""30"" font-weight=""900"" fill=""#ffffff"">L</text></svg>";
            return BuildSvgDataUri(svg);
        }

        private static string BuildSvgDataUri(string svg)
        {
            return $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg ?? string.Empty))}";
        }

        private static string ToDataUri(string pathOrUri)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathOrUri))
                {
                    return string.Empty;
                }

                if (pathOrUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    pathOrUri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    pathOrUri.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                {
                    return pathOrUri;
                }

                var path = pathOrUri;
                if (pathOrUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri))
                {
                    path = uri.LocalPath;
                }

                if (!File.Exists(path))
                {
                    return pathOrUri;
                }

                var mimeType = ResolveImageMimeType(path);
                return $"data:{mimeType};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
            }
            catch
            {
                return pathOrUri;
            }
        }

        private static string ResolveImageMimeType(string path)
        {
            switch ((Path.GetExtension(path) ?? string.Empty).Trim().ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".gif":
                    return "image/gif";
                case ".webp":
                    return "image/webp";
                case ".svg":
                    return "image/svg+xml";
                case ".bmp":
                    return "image/bmp";
                case ".ico":
                    return "image/x-icon";
                default:
                    return "image/png";
            }
        }

        private static string[] ResolveSanTextElements(LocalSettings settings, string line1, string line2, string line3, out string unlockMessage, out string title, out string desc)
        {
            unlockMessage = line1 ?? string.Empty;
            title = line2 ?? string.Empty;
            desc = line3 ?? string.Empty;

            var elems = new List<string>();
            if (!string.IsNullOrWhiteSpace(unlockMessage))
            {
                elems.Add("unlockmsg");
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                elems.Add("title");
            }

            if (!string.IsNullOrWhiteSpace(desc))
            {
                elems.Add("desc");
            }

            if (elems.Count == 0)
            {
                unlockMessage = "Achievement unlocked";
                title = "Current Game";
                elems.Add("unlockmsg");
                elems.Add("title");
            }

            return elems.ToArray();
        }

        private static string BuildSanLineDefinitionsJson(LocalSettings settings, params string[] lines)
        {
            if (settings == null || lines == null)
            {
                return "[]";
            }

            var views = new[]
            {
                settings.OverlayCustomLine1View,
                settings.OverlayCustomLine2View,
                settings.OverlayCustomLine3View,
                settings.OverlayCustomLine4View,
                settings.OverlayCustomLine5View,
                settings.OverlayCustomLine6View
            };
            var orders = new[]
            {
                settings.OverlayCustomLine1Order,
                settings.OverlayCustomLine2Order,
                settings.OverlayCustomLine3Order,
                settings.OverlayCustomLine4Order,
                settings.OverlayCustomLine5Order,
                settings.OverlayCustomLine6Order
            };
            var colors = new[]
            {
                settings.OverlayCustomTitleColor,
                settings.OverlayCustomDetailColor,
                settings.OverlayCustomMetaColor,
                settings.OverlayCustomLine4Color,
                settings.OverlayCustomLine5Color,
                settings.OverlayCustomLine6Color
            };
            var outlineColors = new[]
            {
                settings.OverlayCustomLine1OutlineColor,
                settings.OverlayCustomLine2OutlineColor,
                settings.OverlayCustomLine3OutlineColor,
                settings.OverlayCustomLine4OutlineColor,
                settings.OverlayCustomLine5OutlineColor,
                settings.OverlayCustomLine6OutlineColor
            };
            var shadowColors = new[]
            {
                settings.OverlayCustomLine1ShadowColor,
                settings.OverlayCustomLine2ShadowColor,
                settings.OverlayCustomLine3ShadowColor,
                settings.OverlayCustomLine4ShadowColor,
                settings.OverlayCustomLine5ShadowColor,
                settings.OverlayCustomLine6ShadowColor
            };
            var outlineSizes = new[]
            {
                settings.OverlayCustomLine1OutlineSize,
                settings.OverlayCustomLine2OutlineSize,
                settings.OverlayCustomLine3OutlineSize,
                settings.OverlayCustomLine4OutlineSize,
                settings.OverlayCustomLine5OutlineSize,
                settings.OverlayCustomLine6OutlineSize
            };
            var shadowSizes = new[]
            {
                settings.OverlayCustomLine1ShadowSize,
                settings.OverlayCustomLine2ShadowSize,
                settings.OverlayCustomLine3ShadowSize,
                settings.OverlayCustomLine4ShadowSize,
                settings.OverlayCustomLine5ShadowSize,
                settings.OverlayCustomLine6ShadowSize
            };
            var outlineEnabled = new[]
            {
                settings.OverlayCustomLine1OutlineEnabled,
                settings.OverlayCustomLine2OutlineEnabled,
                settings.OverlayCustomLine3OutlineEnabled,
                settings.OverlayCustomLine4OutlineEnabled,
                settings.OverlayCustomLine5OutlineEnabled,
                settings.OverlayCustomLine6OutlineEnabled
            };
            var shadowEnabled = new[]
            {
                settings.OverlayCustomLine1ShadowEnabled,
                settings.OverlayCustomLine2ShadowEnabled,
                settings.OverlayCustomLine3ShadowEnabled,
                settings.OverlayCustomLine4ShadowEnabled,
                settings.OverlayCustomLine5ShadowEnabled,
                settings.OverlayCustomLine6ShadowEnabled
            };
            var sizes = new[]
            {
                settings.OverlayCustomTitleFontSize,
                settings.OverlayCustomDetailFontSize,
                settings.OverlayCustomMetaFontSize,
                settings.OverlayCustomLine4FontSize,
                settings.OverlayCustomLine5FontSize,
                settings.OverlayCustomLine6FontSize
            };
            var spacing = new[]
            {
                settings.OverlayCustomLine1Spacing,
                settings.OverlayCustomLine2Spacing,
                settings.OverlayCustomLine3Spacing,
                settings.OverlayCustomLine4Spacing,
                settings.OverlayCustomLine5Spacing,
                settings.OverlayCustomLine6Spacing
            };
            var bold = new[]
            {
                settings.OverlayCustomTitleBold,
                settings.OverlayCustomDetailBold,
                settings.OverlayCustomMetaBold,
                settings.OverlayCustomLine4Bold,
                settings.OverlayCustomLine5Bold,
                settings.OverlayCustomLine6Bold
            };
            var italic = new[]
            {
                settings.OverlayCustomTitleItalic,
                settings.OverlayCustomDetailItalic,
                settings.OverlayCustomMetaItalic,
                settings.OverlayCustomLine4Italic,
                settings.OverlayCustomLine5Italic,
                settings.OverlayCustomLine6Italic
            };
            var underline = new[]
            {
                settings.OverlayCustomTitleUnderline,
                settings.OverlayCustomDetailUnderline,
                settings.OverlayCustomMetaUnderline,
                settings.OverlayCustomLine4Underline,
                settings.OverlayCustomLine5Underline,
                settings.OverlayCustomLine6Underline
            };
            var strike = new[]
            {
                settings.OverlayCustomTitleStrikethrough,
                settings.OverlayCustomDetailStrikethrough,
                settings.OverlayCustomMetaStrikethrough,
                settings.OverlayCustomLine4Strikethrough,
                settings.OverlayCustomLine5Strikethrough,
                settings.OverlayCustomLine6Strikethrough
            };

            var builder = new StringBuilder("[");
            for (var i = 0; i < Math.Min(6, lines.Length); i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append("{");
                builder.Append("\"index\":").Append(i + 1).Append(",");
                builder.Append("\"html\":").Append(JsString(lines[i] ?? string.Empty)).Append(",");
                builder.Append("\"view\":").Append((int)views[i]).Append(",");
                builder.Append("\"order\":").Append(Math.Max(1, Math.Min(6, orders[i]))).Append(",");
                builder.Append("\"color\":").Append(JsString(CssColor(colors[i], "#ffffff"))).Append(",");
                builder.Append("\"outlineColor\":").Append(JsString(outlineEnabled[i] ? CssOptionalColor(outlineColors[i]) : string.Empty)).Append(",");
                builder.Append("\"shadowColor\":").Append(JsString(shadowEnabled[i] ? CssOptionalColor(shadowColors[i]) : string.Empty)).Append(",");
                var outlineSize = outlineSizes[i] > 0 ? outlineSizes[i] : settings.OverlayCustomOutlineSize;
                var shadowSize = shadowSizes[i] > 0 ? shadowSizes[i] : settings.OverlayCustomShadowSize;
                builder.Append("\"outlineSize\":").Append(Math.Max(0, Math.Min(8, outlineSize)).ToString("0.###", CultureInfo.InvariantCulture)).Append(",");
                builder.Append("\"shadowSize\":").Append(Math.Max(0, Math.Min(24, shadowSize)).ToString("0.###", CultureInfo.InvariantCulture)).Append(",");
                builder.Append("\"size\":").Append(Math.Max(8, Math.Min(34, sizes[i])).ToString("0.###", CultureInfo.InvariantCulture)).Append(",");
                var lineSpacing = spacing[i] > 0
                    ? spacing[i]
                    : (settings.OverlayCustomLineSpacing > 0 ? settings.OverlayCustomLineSpacing : 3);
                builder.Append("\"spacing\":").Append(Math.Max(0, Math.Min(48, lineSpacing)).ToString("0.###", CultureInfo.InvariantCulture)).Append(",");
                builder.Append("\"bold\":").Append(JsBool(bold[i])).Append(",");
                builder.Append("\"italic\":").Append(JsBool(italic[i])).Append(",");
                builder.Append("\"underline\":").Append(JsBool(underline[i])).Append(",");
                builder.Append("\"strike\":").Append(JsBool(strike[i]));
                builder.Append("}");
            }

            builder.Append("]");
            return builder.ToString();
        }

        private static bool UsesSanScorePrefix(string preset)
        {
            return preset == "xboxone" || preset == "xbox360" || preset == "gfwl";
        }

        private static string ResolveSanRuntimeTitleTemplate(string template, string preset)
        {
            if (!UsesSanScorePrefix(preset))
            {
                return template;
            }

            return string.Equals((template ?? string.Empty).Trim(), "<points> - Achievement unlocked", StringComparison.OrdinalIgnoreCase)
                ? "Achievement unlocked"
                : template;
        }

        private static string ResolveSanRuntimeGameTemplate(string template, string preset)
        {
            if (!UsesSanScorePrefix(preset))
            {
                return template;
            }

            var normalized = (template ?? string.Empty).Trim();
            if (string.Equals(normalized, "<gameName>", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "<achievementName>", StringComparison.OrdinalIgnoreCase))
            {
                return preset == "xbox360"
                    ? "G <points> - <gameName>"
                    : "G <points> - <achievementName>";
            }

            return template;
        }

        private static string ResolveSanLogoUri(LocalSettings settings, JObject customisation, string achievementTrophy, string achievementRarity, int? achievementPoints, string displayIconUri, string presetId = null)
        {
            if (!string.IsNullOrWhiteSpace(displayIconUri))
            {
                return displayIconUri;
            }

            var themeLogo = ResolveSanThemeIconUri(settings, customisation, "logo");
            if (!string.IsNullOrWhiteSpace(themeLogo))
            {
                return ToDataUri(themeLogo);
            }

            var preset = NormalizeSanPresetId(presetId ?? settings?.OverlayCustomSanPresetId);
            switch (preset)
            {
                case "xqjan":
                    return ResolveSanProviderIconUri(settings, "Local");
                case "default":
                    return ToDataUri(ResolveSanAssetUri(settings, "img", "sanlogotrophy.svg"));
                case "xbox360":
                case "gfwl":
                    return ToDataUri(ResolveSanAssetUri(settings, "img", "sanlogotrophy_small.svg"));
                case "ps5":
                case "ps4":
                    return ResolveSanTrophyIconUri(settings, achievementTrophy, achievementRarity, achievementPoints);
                default:
                    return ToDataUri(ResolveSanAssetUri(settings, "img", "sanlogotrophy.svg"));
            }
        }
        private static string ResolveSanDecorationUri(LocalSettings settings, JObject customisation, string achievementTrophy, string achievementRarity, int? achievementPoints)
        {
            var themeDecoration = ResolveSanThemeIconUri(settings, customisation, "decoration");
            if (!string.IsNullOrWhiteSpace(themeDecoration))
            {
                return ToDataUri(themeDecoration);
            }

            var preset = NormalizeSanPresetId(settings?.OverlayCustomSanPresetId);
            if (preset == "steamdeck")
            {
                return ToDataUri(ResolveSanAssetUri(settings, "img", "ribbonbw.svg"));
            }

            if (preset == "epicgames" || preset == "ps5" || preset == "ps4" || preset == "ps3")
            {
                return ResolveSanTrophyIconUri(settings, achievementTrophy, achievementRarity, achievementPoints);
            }

            return string.Empty;
        }

        private static string ResolveSanThemeIconUri(LocalSettings settings, JObject customisation, string key)
        {
            var preset = NormalizeSanPresetId(settings?.OverlayCustomSanPresetId);
            var customIcons = customisation?["customicons"]?[preset] as JObject;
            var value = customIcons?.Value<string>(key);
            return ResolveSanThemeFileUri(settings, value);
        }

        private static string ResolveSanIconBorder(LocalSettings settings, JObject customisation, string achievementRarity)
        {
            if (customisation?.Value<bool?>("showiconborder") != true)
            {
                return "none";
            }

            var key = "iconborderimg";
            if (customisation?.Value<bool?>("iconborderrarity") == true)
            {
                var percent = ResolveSanPercentValue(achievementRarity);
                var tier = ResolveSanPercentTier(
                    percent,
                    customisation?.Value<double?>("rarity") ?? 10,
                    customisation?.Value<double?>("semirarity") ?? 50);
                key = $"iconborderimg{tier}";
            }

            var uri = ResolveSanThemeFileUri(settings, customisation?.Value<string>(key));
            if (string.IsNullOrWhiteSpace(uri) && key != "iconborderimg")
            {
                uri = ResolveSanThemeFileUri(settings, customisation?.Value<string>("iconborderimg"));
            }

            return string.IsNullOrWhiteSpace(uri) ? "none" : $"url('{CssUrl(ToDataUri(uri))}')";
        }

        private static string ResolveSanThemeFileUri(LocalSettings settings, string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return string.Empty;
            }

            var path = rawPath.Trim();
            if (path.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Trim().TrimStart('u', 'r', 'l', '(').Trim('\'', '"', ')');
            }

            if (File.Exists(path))
            {
                return new Uri(path).AbsoluteUri;
            }

            var relativePath = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(settings?.OverlayCustomSanThemeDirectory) && !string.IsNullOrWhiteSpace(relativePath))
            {
                var themeRoot = settings.OverlayCustomSanThemeDirectory;
                var candidates = new[]
                {
                    Path.Combine(themeRoot, relativePath),
                    Path.Combine(themeRoot, "assets", relativePath),
                    Path.Combine(themeRoot, "customfiles", relativePath),
                    Path.Combine(themeRoot, "assets", Path.GetFileName(relativePath)),
                    Path.Combine(themeRoot, "customfiles", Path.GetFileName(relativePath))
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return new Uri(candidate).AbsoluteUri;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(settings?.OverlayCustomSanAssetRootPath) && !string.IsNullOrWhiteSpace(relativePath))
            {
                var assetRoot = settings.OverlayCustomSanAssetRootPath;
                var candidates = new[]
                {
                    Path.Combine(assetRoot, relativePath),
                    Path.Combine(assetRoot, "img", relativePath),
                    Path.Combine(assetRoot, "customfiles", relativePath)
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return new Uri(candidate).AbsoluteUri;
                    }
                }
            }

            var fileName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(settings?.OverlayCustomSanThemeDirectory) && !string.IsNullOrWhiteSpace(fileName))
            {
                var exportedAsset = Path.Combine(settings.OverlayCustomSanThemeDirectory, "assets", fileName);
                if (File.Exists(exportedAsset))
                {
                    return new Uri(exportedAsset).AbsoluteUri;
                }
            }

            if (!string.IsNullOrWhiteSpace(settings?.OverlayCustomSanAssetRootPath) && !string.IsNullOrWhiteSpace(fileName))
            {
                var sanAsset = Path.Combine(settings.OverlayCustomSanAssetRootPath, "img", fileName);
                if (File.Exists(sanAsset))
                {
                    return new Uri(sanAsset).AbsoluteUri;
                }
            }

            var installedAsset = ResolveInstalledSanAssetPath("img", fileName);
            if (!string.IsNullOrWhiteSpace(installedAsset))
            {
                return new Uri(installedAsset).AbsoluteUri;
            }

            return string.Empty;
        }

        private static string ResolveSanAssetUri(LocalSettings settings, string folder, string fileName)
        {
            var root = settings?.OverlayCustomSanAssetRootPath;
            if (!string.IsNullOrWhiteSpace(root))
            {
                var path = Path.Combine(root, folder, fileName);
                if (File.Exists(path))
                {
                    return new Uri(path).AbsoluteUri;
                }

                if (!string.Equals(folder, "img", StringComparison.OrdinalIgnoreCase))
                {
                    var imagePath = Path.Combine(root, "img", fileName);
                    if (File.Exists(imagePath))
                    {
                        return new Uri(imagePath).AbsoluteUri;
                    }
                }
            }

            var installedAsset = ResolveInstalledSanAssetPath(folder, fileName);
            if (string.IsNullOrWhiteSpace(installedAsset) && !string.Equals(folder, "img", StringComparison.OrdinalIgnoreCase))
            {
                installedAsset = ResolveInstalledSanAssetPath("img", fileName);
            }

            return string.IsNullOrWhiteSpace(installedAsset) ? string.Empty : new Uri(installedAsset).AbsoluteUri;
        }

        private static string ResolveInstalledSanAssetPath(string folder, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            var candidates = new[]
            {
                Path.Combine(GetPluginAssemblyDirectory(), "Resources", "Tools", "SAN-source", folder ?? string.Empty, fileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "steamachievementnotifierv1.9", "resources", folder ?? string.Empty, fileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "steamachievementnotifierv1.9", folder ?? string.Empty, fileName),
                Path.Combine(@"C:\Users\Danny\AppData\Local\Programs\steamachievementnotifierv1.9", "resources", folder ?? string.Empty, fileName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static string CssColor(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string CssOptionalColor(string value)
        {
            var color = value?.Trim();
            return string.IsNullOrWhiteSpace(color) ||
                   string.Equals(color, "transparent", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : color;
        }

        private static string CssUrl(string uri)
        {
            return (uri ?? string.Empty).Replace("\\", "/").Replace("'", "\\'");
        }

        private static string SanitizeInlineCss(string css)
        {
            return string.IsNullOrWhiteSpace(css)
                ? string.Empty
                : Regex.Replace(css, "</style", "<\\/style", RegexOptions.IgnoreCase);
        }

        private static string JsString(string value)
        {
            if (value == null)
            {
                return "''";
            }

            return "'" + value
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "'";
        }

        private static string JsStringArray(IEnumerable<string> values)
        {
            return "[" + string.Join(",", (values ?? Enumerable.Empty<string>()).Select(JsString)) + "]";
        }

        private static string JsInt(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string JsBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static IReadOnlyList<string> ReadSanStringArray(JObject customisation, string key, params string[] fallback)
        {
            var values = customisation?[key] as JArray;
            if (values == null)
            {
                return fallback ?? Array.Empty<string>();
            }

            return values
                .Select(token => token.Type == JTokenType.String ? token.Value<string>() : string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
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
                Background = backgroundBrush,
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

        private sealed class ManualElementAdjustment
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool HasAbsolutePosition { get; set; }
        }

        private static Dictionary<string, ManualElementAdjustment> ParseManualElementCssOffsets(string css)
        {
            var offsets = new Dictionary<string, ManualElementAdjustment>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(css))
            {
                return offsets;
            }

            foreach (Match match in Regex.Matches(css, @"(?is)([^{}]+)\{([^{}]*)\}"))
            {
                var selectors = match.Groups[1].Value;
                var key = ResolveManualElementKey(selectors);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var body = match.Groups[2].Value;
                var adjustment = new ManualElementAdjustment();
                var translate = Regex.Match(body, @"(?is)transform\s*:\s*translate\s*\(\s*([-+]?\d+(?:\.\d+)?)px\s*,\s*([-+]?\d+(?:\.\d+)?)px\s*\)");
                if (translate.Success)
                {
                    if (double.TryParse(translate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    {
                        adjustment.X = x;
                    }

                    if (double.TryParse(translate.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    {
                        adjustment.Y = y;
                    }
                }

                var width = Regex.Match(body, @"(?is)width\s*:\s*([-+]?\d+(?:\.\d+)?)px");
                if (width.Success && double.TryParse(width.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWidth))
                {
                    adjustment.Width = Math.Max(1, parsedWidth);
                }

                var height = Regex.Match(body, @"(?is)height\s*:\s*([-+]?\d+(?:\.\d+)?)px");
                if (height.Success && double.TryParse(height.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeight))
                {
                    adjustment.Height = Math.Max(1, parsedHeight);
                }

                var left = Regex.Match(body, @"(?is)\bleft\s*:\s*([-+]?\d+(?:\.\d+)?)px");
                var top = Regex.Match(body, @"(?is)\btop\s*:\s*([-+]?\d+(?:\.\d+)?)px");
                if (left.Success && top.Success &&
                    double.TryParse(left.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLeft) &&
                    double.TryParse(top.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTop))
                {
                    adjustment.Left = parsedLeft;
                    adjustment.Top = parsedTop;
                    adjustment.HasAbsolutePosition = true;
                }

                if (adjustment.X != 0 || adjustment.Y != 0 || adjustment.Width > 0 || adjustment.Height > 0 || adjustment.HasAbsolutePosition)
                {
                    offsets[key] = adjustment;
                }
            }

            return offsets;
        }

        private static string ResolveManualElementKey(string selectors)
        {
            var text = selectors ?? string.Empty;
            if (Regex.IsMatch(text, @"(?:#line1|nth-child\(1\)|#unlockmsg)", RegexOptions.IgnoreCase)) return "line1";
            if (Regex.IsMatch(text, @"(?:#line2|nth-child\(2\)|#title)", RegexOptions.IgnoreCase)) return "line2";
            if (Regex.IsMatch(text, @"(?:#line3|nth-child\(3\)|#desc)", RegexOptions.IgnoreCase)) return "line3";
            if (Regex.IsMatch(text, @"(?:#line4|nth-child\(4\))", RegexOptions.IgnoreCase)) return "line4";
            if (Regex.IsMatch(text, @"(?:#line5|nth-child\(5\))", RegexOptions.IgnoreCase)) return "line5";
            if (Regex.IsMatch(text, @"(?:#line6|nth-child\(6\))", RegexOptions.IgnoreCase)) return "line6";
            if (Regex.IsMatch(text, @"(?:#secondaryIconWrap|#logo|san-secondary-icon)", RegexOptions.IgnoreCase)) return "secondaryIcon";
            if (Regex.IsMatch(text, @"(?:#iconWrap|#achiconwrapper|#achicon|#iconbg|#sectors|#outercircle|#innercircle|#bgelems|#xpwrapper)", RegexOptions.IgnoreCase)) return "primaryIcon";
            if (Regex.IsMatch(text, @"(?:#coverLeft|san-game-cover\.left)", RegexOptions.IgnoreCase)) return "coverLeft";
            if (Regex.IsMatch(text, @"(?:#coverRight|san-game-cover\.right)", RegexOptions.IgnoreCase)) return "coverRight";
            if (Regex.IsMatch(text, @"(?:\.toast|#achcont)", RegexOptions.IgnoreCase)) return "notification";
            return string.Empty;
        }

        private static void ApplyManualElementOffset(FrameworkElement element, IDictionary<string, ManualElementAdjustment> offsets, params string[] keys)
        {
            if (element == null || offsets == null || keys == null)
            {
                return;
            }

            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key) && offsets.TryGetValue(key, out var offset))
                {
                    if (offset.HasAbsolutePosition)
                    {
                        return;
                    }

                    if (offset.X != 0 || offset.Y != 0)
                    {
                        element.RenderTransform = new TranslateTransform(offset.X, offset.Y);
                    }

                    if (offset.Width > 0)
                    {
                        element.Width = offset.Width;
                        ApplyManualChildWidth(element, offset.Width);
                    }

                    if (offset.Height > 0)
                    {
                        element.Height = offset.Height;
                        ApplyManualChildHeight(element, offset.Height);
                    }

                    return;
                }
            }
        }

        private static bool HasManualElementAdjustment(IDictionary<string, ManualElementAdjustment> offsets, string key)
        {
            return offsets != null && !string.IsNullOrWhiteSpace(key) && offsets.ContainsKey(key);
        }

        private static bool HasAbsoluteManualElementAdjustment(IDictionary<string, ManualElementAdjustment> offsets, string key)
        {
            return offsets != null && !string.IsNullOrWhiteSpace(key) && offsets.TryGetValue(key, out var offset) && offset.HasAbsolutePosition;
        }

        private static bool TryAddAbsoluteManualElement(Canvas layer, FrameworkElement element, IDictionary<string, ManualElementAdjustment> offsets, string key)
        {
            if (layer == null || element == null || offsets == null || string.IsNullOrWhiteSpace(key) ||
                !offsets.TryGetValue(key, out var offset) || !offset.HasAbsolutePosition)
            {
                return false;
            }

            element.Margin = new Thickness(0);
            element.RenderTransform = null;
            if (offset.Width > 0)
            {
                element.Width = offset.Width;
                ApplyManualChildWidth(element, offset.Width);
            }

            if (offset.Height > 0)
            {
                element.Height = offset.Height;
                ApplyManualChildHeight(element, offset.Height);
            }

            Canvas.SetLeft(element, offset.Left);
            Canvas.SetTop(element, offset.Top);
            layer.Children.Add(element);
            return true;
        }

        private static void ApplyManualChildWidth(FrameworkElement element, double width)
        {
            if (element is Border border && border.Child is FrameworkElement child)
            {
                child.Width = width;
            }
            else if (element is ContentControl contentControl && contentControl.Content is FrameworkElement childElement)
            {
                childElement.Width = width;
            }
        }

        private static void ApplyManualChildHeight(FrameworkElement element, double height)
        {
            if (element is Border border && border.Child is FrameworkElement child)
            {
                child.Height = height;
            }
            else if (element is ContentControl contentControl && contentControl.Content is FrameworkElement childElement)
            {
                childElement.Height = height;
            }
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
            var secondaryIconSize = Math.Max(24, (settings?.OverlayCustomSecondaryIconSize ?? settings?.OverlayCustomIconSize ?? 58) * overlayScale);
            var iconCornerRadius = Math.Max(0, Math.Min(iconSize / 2, (settings?.OverlayCustomIconCornerRadius ?? 10) * overlayScale));
            var secondaryIconCornerRadius = Math.Max(0, Math.Min(secondaryIconSize / 2, (settings?.OverlayCustomSecondaryIconCornerRadius ?? 10) * overlayScale));
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
            var manualOffsets = ParseManualElementCssOffsets(settings?.OverlayCustomManualElementCss);

            var useSanPresetHints = IsSanTransitionStyle(settings?.UnlockOverlayTransitionStyle ?? LocalUnlockOverlayTransitionStyle.Fade) ||
                !string.IsNullOrWhiteSpace(settings?.OverlayCustomSanElementPresetId);
            var sanPresetId = useSanPresetHints ? NormalizeSanPresetId(settings?.OverlayCustomSanPresetId) : string.Empty;

            if (settings?.OverlayCustomLayoutStyle == LocalCustomOverlayLayoutStyle.XboxModern || string.Equals(sanPresetId, "xboxone", StringComparison.Ordinal))
            {
                return BuildXboxModernCustomOverlayContent(
                    title,
                    gameName,
                    achievementName,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    providerKey,
                    settings,
                    overlayScale,
                    game,
                    sourceName,
                    timestamp);
            }

            if (settings?.OverlayCustomLayoutStyle == LocalCustomOverlayLayoutStyle.XboxClassic || string.Equals(sanPresetId, "xbox360", StringComparison.Ordinal))
            {
                return BuildXboxClassicCustomOverlayContent(
                    title,
                    gameName,
                    achievementName,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    providerKey,
                    settings,
                    overlayScale,
                    game,
                    sourceName,
                    timestamp);
            }

            if (settings?.OverlayCustomAnimationStyle == LocalCustomOverlayAnimationStyle.SanXqjan || string.Equals(sanPresetId, "xqjan", StringComparison.Ordinal))
            {
                return BuildSanXqjanCustomOverlayContent(
                    title,
                    gameName,
                    achievementName,
                    rawIconPath,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    providerKey,
                    settings,
                    game,
                    sourceName,
                    timestamp);
            }

            var customWidth = Math.Max(280, (settings?.OverlayCustomWidth ?? 460) * overlayScale);
            var customHeight = Math.Max(LocalSettings.MinCustomOverlayHeight, (settings?.OverlayCustomHeight ?? 128) * overlayScale);
            var isCompactSanCard = customHeight <= 65 && (
                settings?.OverlayCustomAnimationStyle == LocalCustomOverlayAnimationStyle.SanExpandCard ||
                settings?.OverlayCustomAnimationStyle == LocalCustomOverlayAnimationStyle.SanSlideCard ||
                !string.IsNullOrWhiteSpace(sanPresetId));
            var coverWidth = Math.Max(isCompactSanCard ? 32 : 48, (settings?.GameCoverWidth ?? 80) * overlayScale);
            var coverHeight = isCompactSanCard
                ? Math.Max(28, customHeight - 10)
                : Math.Max(iconSize + 18, 96 * overlayScale);
            var contentPadding = isCompactSanCard
                ? new Thickness(Math.Max(5, 6 * overlayScale), Math.Max(4, 5 * overlayScale), Math.Max(7, 8 * overlayScale), Math.Max(4, 5 * overlayScale))
                : new Thickness(16);

            var root = new Border
            {
                Width = customWidth,
                Background = backgroundBrush,
                BorderBrush = borderBrush,
                BorderThickness = (settings?.OverlayCustomShowBorder != false) ? new Thickness(1.5) : new Thickness(0),
                CornerRadius = new CornerRadius(cornerRadius),
                Padding = new Thickness(0),
                ClipToBounds = true
            };

            if (settings?.OverlayCustomAutoResizeToContent == true)
            {
                root.MinHeight = customHeight;
            }
            else
            {
                root.Height = customHeight;
            }

            var container = new Grid
            {
                ClipToBounds = true
            };
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

            var absoluteLayer = new Canvas
            {
                ClipToBounds = true,
                IsHitTestVisible = false
            };
            var primaryIconAbsolute = HasAbsoluteManualElementAdjustment(manualOffsets, "primaryIcon");
            var secondaryIconAbsolute = HasAbsoluteManualElementAdjustment(manualOffsets, "secondaryIcon");
            var coverLeftAbsolute = HasAbsoluteManualElementAdjustment(manualOffsets, "coverLeft");
            var coverRightAbsolute = HasAbsoluteManualElementAdjustment(manualOffsets, "coverRight");

            var grid = new Grid();
            grid.Margin = contentPadding;
            if (coverPosition == LocalOverlayCoverPosition.Left && !coverLeftAbsolute)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            if (!primaryIconAbsolute)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var showSecondaryIcon = settings?.OverlayCustomShowSecondaryIcon == true;
            var secondaryIconNextToPrimary = showSecondaryIcon && settings?.OverlayCustomSecondaryIconNextToPrimary == true;
            if (secondaryIconNextToPrimary && !secondaryIconAbsolute)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (showSecondaryIcon && !secondaryIconNextToPrimary && !secondaryIconAbsolute)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            if (coverPosition == LocalOverlayCoverPosition.Right && !coverRightAbsolute)
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
                    if (!TryAddAbsoluteManualElement(absoluteLayer, leftCover, manualOffsets, "coverLeft"))
                    {
                        ApplyManualElementOffset(leftCover, manualOffsets, "coverLeft");
                        Grid.SetColumn(leftCover, currentColumn);
                        grid.Children.Add(leftCover);
                        currentColumn++;
                    }
                }
            }

            var iconBackground = isCompactSanCard && settings?.OverlayCustomIconSource == LocalOverlayIconSource.TrophyIcon
                ? accentBrush
                : new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));

            var icon = new Border
            {
                Width = iconSize,
                Height = iconSize,
                Background = iconBackground,
                CornerRadius = new CornerRadius(iconCornerRadius),
                Margin = new Thickness(0, 0, isCompactSanCard ? Math.Max(6, 8 * overlayScale) : 14, 0)
            };

            var rarityKey = ResolveRarityKey(achievementRarity, achievementPoints);
            icon.Child = CreateCustomOverlayIconContent(settings, rawIconPath, providerKey, titleBrush, iconSize, titleSize, rarityKey, cornerRadius: iconCornerRadius);
            if (settings?.OverlayCustomShowIconRarityGlow == true)
            {
                var glow = CreateIconRarityGlowEffect(rarityKey);
                if (glow != null)
                {
                    icon.Effect = glow;
                }
            }

            if (!TryAddAbsoluteManualElement(absoluteLayer, icon, manualOffsets, "primaryIcon"))
            {
                ApplyManualElementOffset(icon, manualOffsets, "primaryIcon");
                Grid.SetColumn(icon, currentColumn);
                grid.Children.Add(icon);
                currentColumn++;
            }

            if (secondaryIconNextToPrimary)
            {
                var secondaryIcon = new Border
                {
                    Width = secondaryIconSize,
                    Height = secondaryIconSize,
                    Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                    CornerRadius = new CornerRadius(secondaryIconCornerRadius),
                    Margin = new Thickness(0, 0, isCompactSanCard ? Math.Max(6, 8 * overlayScale) : 14, 0),
                    Child = CreateCustomOverlayIconContent(settings, rawIconPath, providerKey, titleBrush, secondaryIconSize, titleSize, rarityKey, settings?.OverlayCustomSecondaryIconSource ?? LocalOverlayIconSource.AchievementIcon, secondaryIconCornerRadius)
                };
                if (settings?.OverlayCustomShowSecondaryIconRarityGlow == true)
                {
                    var glow = CreateIconRarityGlowEffect(rarityKey);
                    if (glow != null)
                    {
                        secondaryIcon.Effect = glow;
                    }
                }
                if (!TryAddAbsoluteManualElement(absoluteLayer, secondaryIcon, manualOffsets, "secondaryIcon"))
                {
                    ApplyManualElementOffset(secondaryIcon, manualOffsets, "secondaryIcon");
                    Grid.SetColumn(secondaryIcon, currentColumn);
                    grid.Children.Add(secondaryIcon);
                    currentColumn++;
                }
            }

            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (settings?.OverlayCustomShowLine1 != false)
            {
                var line = CreateCustomTemplateTextBlock(
                    settings?.OverlayCustomTitleTemplate,
                    "Achievement unlocked",
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
                    ApplyCustomLineTextEffect(line, settings, 1);
                    if (!TryAddAbsoluteManualElement(absoluteLayer, line, manualOffsets, "line1"))
                    {
                        ApplyManualElementOffset(line, manualOffsets, "line1");
                        textStack.Children.Add(line);
                    }
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
                    new Thickness(0, isCompactSanCard ? 0 : 4, 0, 0),
                    wrapAllText,
                    suppressWhenTemplateEmpty: true);
                if (line != null)
                {
                    ApplyCustomLineTextEffect(line, settings, 2);
                    if (!TryAddAbsoluteManualElement(absoluteLayer, line, manualOffsets, "line2"))
                    {
                        ApplyManualElementOffset(line, manualOffsets, "line2");
                        textStack.Children.Add(line);
                    }
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
                    ApplyCustomLineTextEffect(line, settings, 3);
                    if (!TryAddAbsoluteManualElement(absoluteLayer, line, manualOffsets, "line3"))
                    {
                        ApplyManualElementOffset(line, manualOffsets, "line3");
                        textStack.Children.Add(line);
                    }
                }
            }

            if (settings?.OverlayCustomShowLine4 == true)
            {
                var line = CreateCustomTemplateTextBlock(settings.OverlayCustomLine4Template, string.Empty, title, gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, providerKey, NotificationStyleCustom, game, sourceName, timestamp, ParseBrushOrDefault(settings.OverlayCustomLine4Color, Color.FromRgb(188, 208, 229)), settings.OverlayCustomLine4FontSize, settings.OverlayCustomLine4Bold, settings.OverlayCustomLine4Italic, settings.OverlayCustomLine4Underline, settings.OverlayCustomLine4Strikethrough, new Thickness(0, settings.OverlayCustomLine4Spacing, 0, 0), wrapAllText, suppressWhenTemplateEmpty: true);
                if (line != null)
                {
                    ApplyCustomLineTextEffect(line, settings, 4);
                    if (!TryAddAbsoluteManualElement(absoluteLayer, line, manualOffsets, "line4"))
                    {
                        ApplyManualElementOffset(line, manualOffsets, "line4");
                        textStack.Children.Add(line);
                    }
                }
            }

            if (settings?.OverlayCustomShowLine5 == true)
            {
                var line = CreateCustomTemplateTextBlock(settings.OverlayCustomLine5Template, string.Empty, title, gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, providerKey, NotificationStyleCustom, game, sourceName, timestamp, ParseBrushOrDefault(settings.OverlayCustomLine5Color, Color.FromRgb(188, 208, 229)), settings.OverlayCustomLine5FontSize, settings.OverlayCustomLine5Bold, settings.OverlayCustomLine5Italic, settings.OverlayCustomLine5Underline, settings.OverlayCustomLine5Strikethrough, new Thickness(0, settings.OverlayCustomLine5Spacing, 0, 0), wrapAllText, suppressWhenTemplateEmpty: true);
                if (line != null)
                {
                    ApplyCustomLineTextEffect(line, settings, 5);
                    if (!TryAddAbsoluteManualElement(absoluteLayer, line, manualOffsets, "line5"))
                    {
                        ApplyManualElementOffset(line, manualOffsets, "line5");
                        textStack.Children.Add(line);
                    }
                }
            }

            if (settings?.OverlayCustomShowLine6 == true)
            {
                var line = CreateCustomTemplateTextBlock(settings.OverlayCustomLine6Template, string.Empty, title, gameName, achievementName, achievementDescription, achievementPoints, achievementRarity, achievementTrophy, providerKey, NotificationStyleCustom, game, sourceName, timestamp, ParseBrushOrDefault(settings.OverlayCustomLine6Color, Color.FromRgb(188, 208, 229)), settings.OverlayCustomLine6FontSize, settings.OverlayCustomLine6Bold, settings.OverlayCustomLine6Italic, settings.OverlayCustomLine6Underline, settings.OverlayCustomLine6Strikethrough, new Thickness(0, settings.OverlayCustomLine6Spacing, 0, 0), wrapAllText, suppressWhenTemplateEmpty: true);
                if (line != null)
                {
                    ApplyCustomLineTextEffect(line, settings, 6);
                    if (!TryAddAbsoluteManualElement(absoluteLayer, line, manualOffsets, "line6"))
                    {
                        ApplyManualElementOffset(line, manualOffsets, "line6");
                        textStack.Children.Add(line);
                    }
                }
            }
            if (textStack.Children.Count > 0)
            {
                Grid.SetColumn(textStack, currentColumn);
                grid.Children.Add(textStack);
                currentColumn++;
            }

            if (showSecondaryIcon && !secondaryIconNextToPrimary)
            {
                var secondaryIcon = new Border
                {
                    Width = secondaryIconSize,
                    Height = secondaryIconSize,
                    Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                    CornerRadius = new CornerRadius(secondaryIconCornerRadius),
                    Margin = new Thickness(isCompactSanCard ? Math.Max(6, 8 * overlayScale) : 14, 0, 0, 0),
                    Child = CreateCustomOverlayIconContent(settings, rawIconPath, providerKey, titleBrush, secondaryIconSize, titleSize, rarityKey, settings?.OverlayCustomSecondaryIconSource ?? LocalOverlayIconSource.AchievementIcon, secondaryIconCornerRadius)
                };
                if (settings?.OverlayCustomShowSecondaryIconRarityGlow == true)
                {
                    var glow = CreateIconRarityGlowEffect(rarityKey);
                    if (glow != null)
                    {
                        secondaryIcon.Effect = glow;
                    }
                }
                if (!TryAddAbsoluteManualElement(absoluteLayer, secondaryIcon, manualOffsets, "secondaryIcon"))
                {
                    ApplyManualElementOffset(secondaryIcon, manualOffsets, "secondaryIcon");
                    Grid.SetColumn(secondaryIcon, currentColumn);
                    grid.Children.Add(secondaryIcon);
                    currentColumn++;
                }
            }

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
                    if (!TryAddAbsoluteManualElement(absoluteLayer, rightCover, manualOffsets, "coverRight"))
                    {
                        ApplyManualElementOffset(rightCover, manualOffsets, "coverRight");
                        Grid.SetColumn(rightCover, currentColumn);
                        grid.Children.Add(rightCover);
                    }
                }
            }

            container.Children.Add(grid);
            if (absoluteLayer.Children.Count > 0)
            {
                container.Children.Add(absoluteLayer);
            }
            root.Child = container;
            ApplySanTemplateAnimation(root, icon, textStack, settings);
            return root;
        }

        private FrameworkElement BuildSanXqjanCustomOverlayContent(
            string title,
            string gameName,
            string achievementName,
            string rawIconPath,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            string providerKey,
            LocalSettings settings,
            Game game,
            string sourceName,
            DateTime timestamp)
        {
            var width = Math.Max(320, settings?.OverlayCustomWidth ?? 500);
            var height = Math.Max(LocalSettings.MinCustomOverlayHeight, settings?.OverlayCustomHeight ?? 92);
            var iconSize = Math.Max(24, settings?.OverlayCustomIconSize ?? 62);
            var titleSize = settings?.OverlayCustomTitleFontSize ?? 17;
            var detailSize = settings?.OverlayCustomDetailFontSize ?? 13;
            var metaSize = settings?.OverlayCustomMetaFontSize ?? 11;
            var cornerRadius = Math.Max(0, settings?.OverlayCustomCornerRadius ?? 14);
            var backgroundBrush = ParseBrushOrDefault(settings?.OverlayCustomBackgroundColor, Color.FromRgb(16, 24, 32));
            var secondaryBrush = ParseBrushOrDefault(settings?.OverlayCustomBorderColor, Color.FromRgb(102, 192, 244));
            var tertiaryBrush = ParseBrushOrDefault(settings?.OverlayCustomAccentColor, Colors.White);
            var titleBrush = ParseBrushOrDefault(settings?.OverlayCustomTitleColor, Colors.White);
            var detailBrush = ParseBrushOrDefault(settings?.OverlayCustomDetailColor, Color.FromRgb(199, 213, 224));
            var metaBrush = ParseBrushOrDefault(settings?.OverlayCustomMetaColor, Color.FromRgb(143, 152, 160));

            var root = new Grid
            {
                Width = width,
                Height = height,
                ClipToBounds = false
            };

            var background = new Border
            {
                Width = height,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = backgroundBrush,
                CornerRadius = new CornerRadius(height / 2),
                ClipToBounds = true
            };
            root.Children.Add(background);

            var pulses = new[]
            {
                CreateSanXqjanPulse(height, secondaryBrush),
                CreateSanXqjanPulse(height, tertiaryBrush),
                CreateSanXqjanPulse(height, backgroundBrush)
            };
            foreach (var pulse in pulses)
            {
                root.Children.Add(pulse);
            }

            var logoWell = new Border
            {
                Width = height,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = CreateProviderIconContent(settings, providerKey, titleBrush, Math.Max(26, height * 0.58), titleSize)
                    ?? CreateXboxTrophyGlyph(titleBrush, Math.Max(26, height * 0.58))
            };
            root.Children.Add(logoWell);

            var contentGrid = new Grid
            {
                Opacity = 0,
                Width = width,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0),
                ClipToBounds = true
            };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(height) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconWell = new Border
            {
                Width = height,
                Height = height,
                Background = Brushes.Transparent,
                Child = CreateCustomOverlayIconContent(settings, rawIconPath, providerKey, titleBrush, iconSize, titleSize, ResolveRarityKey(achievementRarity, achievementPoints))
            };
            Grid.SetColumn(iconWell, 0);
            contentGrid.Children.Add(iconWell);

            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 14, 0)
            };

            AddSanTemplateLine(
                textStack,
                settings?.OverlayCustomTitleTemplate,
                "Achievement unlocked",
                title,
                gameName,
                achievementName,
                achievementDescription,
                achievementPoints,
                achievementRarity,
                achievementTrophy,
                providerKey,
                game,
                sourceName,
                timestamp,
                titleBrush,
                titleSize,
                settings?.OverlayCustomTitleBold == true,
                new Thickness(0));

            AddSanTemplateLine(
                textStack,
                settings?.OverlayCustomGameNameTemplate,
                "<achievementName>",
                title,
                gameName,
                achievementName,
                achievementDescription,
                achievementPoints,
                achievementRarity,
                achievementTrophy,
                providerKey,
                game,
                sourceName,
                timestamp,
                detailBrush,
                detailSize,
                settings?.OverlayCustomDetailBold == true,
                new Thickness(0, 2, 0, 0));

            if (settings?.OverlayCustomShowMeta == true)
            {
                AddSanTemplateLine(
                    textStack,
                    settings?.OverlayCustomAchievementTemplate,
                    "<achievementDescription>",
                    title,
                    gameName,
                    achievementName,
                    achievementDescription,
                    achievementPoints,
                    achievementRarity,
                    achievementTrophy,
                    providerKey,
                    game,
                    sourceName,
                    timestamp,
                    metaBrush,
                    metaSize,
                    settings?.OverlayCustomMetaBold == true,
                    new Thickness(0, 2, 0, 0));
            }

            Grid.SetColumn(textStack, 1);
            contentGrid.Children.Add(textStack);
            root.Children.Add(contentGrid);

            root.Loaded += (sender, args) =>
            {
                var transition = Math.Max(90, settings?.UnlockOverlayFadeInMilliseconds ?? 180);
                var display = Math.Max(1600, settings?.UnlockOverlayDurationMilliseconds ?? 3800);
                AnimateSanXqjan(root, background, pulses, logoWell, contentGrid, width, height, cornerRadius, transition, display);
            };

            return root;
        }

        private static Border CreateSanXqjanPulse(double size, Brush brush)
        {
            return new Border
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = brush,
                CornerRadius = new CornerRadius(size / 2),
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0, 0)
            };
        }

        private static void AddSanTemplateLine(
            Panel target,
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
            Game game,
            string sourceName,
            DateTime timestamp,
            Brush brush,
            double fontSize,
            bool bold,
            Thickness margin)
        {
            var line = CreateCustomTemplateTextBlock(
                template,
                fallback,
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
                brush,
                fontSize,
                bold,
                false,
                false,
                false,
                margin,
                false,
                suppressWhenTemplateEmpty: true);
            if (line != null)
            {
                line.TextTrimming = TextTrimming.CharacterEllipsis;
                target.Children.Add(line);
            }
        }

        private static void AnimateSanXqjan(
            FrameworkElement root,
            FrameworkElement background,
            IEnumerable<FrameworkElement> pulses,
            FrameworkElement logo,
            FrameworkElement content,
            double finalWidth,
            double height,
            double finalCornerRadius,
            int transitionMs,
            int displayMs)
        {
            var t = Math.Max(90, transitionMs);
            var popDuration = TimeSpan.FromMilliseconds(t * 2);
            var expandDelay = TimeSpan.FromMilliseconds(t * 10);
            var contentDelay = TimeSpan.FromMilliseconds(t * 12);
            var exitDelay = TimeSpan.FromMilliseconds(Math.Max(t * 14, displayMs - (t * 14)));

            root.Opacity = 1;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = new ScaleTransform(0, 0);
            if (root.RenderTransform is ScaleTransform rootScale)
            {
                rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames =
                    {
                        new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))),
                        new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(popDuration))
                    }
                });
                rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames =
                    {
                        new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))),
                        new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(popDuration))
                    }
                });
            }

            foreach (var entry in pulses.Select((pulse, index) => new { pulse, index }))
            {
                var begin = TimeSpan.FromMilliseconds(t * (entry.index * 2));
                entry.pulse.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
                {
                    BeginTime = begin,
                    KeyFrames =
                    {
                        new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t * 3))),
                        new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t * 6)))
                    }
                });

                if (entry.pulse.RenderTransform is ScaleTransform scale)
                {
                    var scaleAnim = new DoubleAnimationUsingKeyFrames
                    {
                        BeginTime = begin,
                        KeyFrames =
                        {
                            new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                            new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t * 3))),
                            new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t * 6)))
                        }
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim.Clone());
                }
            }

            logo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t * 8))),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t * 10)))
                }
            });

            background.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(height, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(height, KeyTime.FromTimeSpan(expandDelay)),
                    new EasingDoubleKeyFrame(finalWidth, KeyTime.FromTimeSpan(expandDelay + TimeSpan.FromMilliseconds(t * 2))),
                    new EasingDoubleKeyFrame(finalWidth, KeyTime.FromTimeSpan(exitDelay)),
                    new EasingDoubleKeyFrame(height, KeyTime.FromTimeSpan(exitDelay + TimeSpan.FromMilliseconds(t * 2)))
                }
            });

            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(contentDelay)),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(contentDelay + TimeSpan.FromMilliseconds(t * 2))),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(exitDelay)),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(exitDelay + TimeSpan.FromMilliseconds(t * 2)))
                }
            });

            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(t * 2)))
            {
                BeginTime = TimeSpan.FromMilliseconds(Math.Max(t * 10, displayMs - (t * 2)))
            });
        }

        private FrameworkElement BuildXboxModernCustomOverlayContent(
            string title,
            string gameName,
            string achievementName,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            string providerKey,
            LocalSettings settings,
            double overlayScale,
            Game game,
            string sourceName,
            DateTime timestamp)
        {
            var width = Math.Max(280, settings?.OverlayCustomWidth ?? 300);
            var height = Math.Max(LocalSettings.MinCustomOverlayHeight, settings?.OverlayCustomHeight ?? 50);
            var iconWellSize = height;
            var cornerRadius = height / 2;
            var titleBrush = ParseBrushOrDefault(settings?.OverlayCustomTitleColor, Colors.White);
            var detailBrush = ParseBrushOrDefault(settings?.OverlayCustomDetailColor, Colors.White);
            var backgroundBrush = ParseBrushOrDefault(settings?.OverlayCustomBackgroundColor, Color.FromRgb(79, 176, 16));
            var iconWellBrush = ParseBrushOrDefault(settings?.OverlayCustomAccentColor, Color.FromRgb(49, 128, 18));

            var root = new Border
            {
                Width = width,
                Height = height,
                Background = backgroundBrush,
                CornerRadius = new CornerRadius(cornerRadius),
                ClipToBounds = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.45,
                    Color = Colors.Black
                }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconWellSize) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconWell = new Border
            {
                Width = iconWellSize,
                Height = iconWellSize,
                Background = iconWellBrush,
                CornerRadius = new CornerRadius(cornerRadius),
                Child = CreateXboxTrophyGlyph(Brushes.White, iconWellSize * 0.58)
            };
            Grid.SetColumn(iconWell, 0);
            grid.Children.Add(iconWell);

            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(Math.Max(8, 10 * overlayScale), 0, Math.Max(10, 12 * overlayScale), 0)
            };

            var titleLine = CreateCustomTemplateTextBlock(
                settings?.OverlayCustomTitleTemplate,
                "<points> - Achievement unlocked",
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
                settings?.OverlayCustomTitleFontSize ?? 12,
                settings?.OverlayCustomTitleBold == true,
                settings?.OverlayCustomTitleItalic == true,
                settings?.OverlayCustomTitleUnderline == true,
                settings?.OverlayCustomTitleStrikethrough == true,
                new Thickness(0),
                false,
                suppressWhenTemplateEmpty: true);
            if (titleLine != null)
            {
                titleLine.TextTrimming = TextTrimming.CharacterEllipsis;
                textStack.Children.Add(titleLine);
            }

            var detailLine = CreateCustomTemplateTextBlock(
                settings?.OverlayCustomGameNameTemplate,
                "<achievementName>",
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
                settings?.OverlayCustomDetailFontSize ?? 11,
                settings?.OverlayCustomDetailBold == true,
                settings?.OverlayCustomDetailItalic == true,
                settings?.OverlayCustomDetailUnderline == true,
                settings?.OverlayCustomDetailStrikethrough == true,
                new Thickness(0, 2, 0, 0),
                false,
                suppressWhenTemplateEmpty: true);
            if (detailLine != null)
            {
                detailLine.TextTrimming = TextTrimming.CharacterEllipsis;
                textStack.Children.Add(detailLine);
            }

            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);
            root.Child = grid;
            ApplySanTemplateAnimation(root, iconWell, textStack, settings);
            return root;
        }

        private FrameworkElement BuildXboxClassicCustomOverlayContent(
            string title,
            string gameName,
            string achievementName,
            string achievementDescription,
            int? achievementPoints,
            string achievementRarity,
            string achievementTrophy,
            string providerKey,
            LocalSettings settings,
            double overlayScale,
            Game game,
            string sourceName,
            DateTime timestamp)
        {
            var height = Math.Max(LocalSettings.MinCustomOverlayHeight, settings?.OverlayCustomHeight ?? 92);
            var width = Math.Max(height * 5.8, settings?.OverlayCustomWidth ?? 300);
            var iconAreaWidth = height * 1.55;
            var cornerRadius = height / 2;
            var backgroundBrush = ParseBrushOrDefault(settings?.OverlayCustomBackgroundColor, Color.FromRgb(35, 35, 32));
            var borderBrush = ParseBrushOrDefault(settings?.OverlayCustomBorderColor, Color.FromRgb(101, 101, 94));
            var accentBrush = ParseBrushOrDefault(settings?.OverlayCustomAccentColor, Color.FromRgb(143, 195, 43));
            var titleBrush = ParseBrushOrDefault(settings?.OverlayCustomTitleColor, Color.FromRgb(222, 218, 211));

            var root = new Border
            {
                Width = width,
                Height = height,
                Background = backgroundBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(Math.Max(3, 5 * overlayScale)),
                CornerRadius = new CornerRadius(cornerRadius),
                ClipToBounds = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.35,
                    Color = Colors.Black
                }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconAreaWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = CreateXboxClassicBadge(accentBrush, borderBrush, backgroundBrush, height * 0.78);
            badge.HorizontalAlignment = HorizontalAlignment.Center;
            badge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var line = CreateCustomTemplateTextBlock(
                settings?.OverlayCustomTitleTemplate,
                "Achievement unlocked",
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
                settings?.OverlayCustomTitleFontSize ?? 19,
                settings?.OverlayCustomTitleBold == true,
                settings?.OverlayCustomTitleItalic == true,
                settings?.OverlayCustomTitleUnderline == true,
                settings?.OverlayCustomTitleStrikethrough == true,
                new Thickness(0, 0, Math.Max(28, 34 * overlayScale), 0),
                false,
                suppressWhenTemplateEmpty: false);
            if (line != null)
            {
                line.VerticalAlignment = VerticalAlignment.Center;
                line.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(line, 1);
                grid.Children.Add(line);
            }

            root.Child = grid;
            ApplySanTemplateAnimation(root, badge, line, settings);
            return root;
        }

        private static void ApplySanTemplateAnimation(FrameworkElement root, FrameworkElement icon, FrameworkElement content, LocalSettings settings)
        {
            if (root == null || settings == null)
            {
                return;
            }

            var style = settings.OverlayCustomAnimationStyle;
            var hasSanSelection = IsSanTransitionStyle(settings.UnlockOverlayTransitionStyle) ||
                !string.IsNullOrWhiteSpace(settings.OverlayCustomSanElementPresetId);
            var sanPresetId = hasSanSelection ? NormalizeSanPresetId(settings.OverlayCustomSanPresetId) : string.Empty;
            var transitionPresetId = ResolveSanAnimationPresetId(settings.UnlockOverlayTransitionStyle);
            if (!string.IsNullOrWhiteSpace(transitionPresetId))
            {
                sanPresetId = transitionPresetId;
                style = ResolveSanAnimationStyle(transitionPresetId);
            }

            if (style == LocalCustomOverlayAnimationStyle.Standard)
            {
                style = ResolveSanAnimationStyle(sanPresetId);
            }

            if (style == LocalCustomOverlayAnimationStyle.Standard)
            {
                return;
            }

            root.Loaded += (sender, args) =>
            {
                var transition = Math.Max(90, settings.UnlockOverlayFadeInMilliseconds);
                var display = Math.Max(1600, settings.UnlockOverlayDurationMilliseconds);
                if (settings.OverlayCustomLayoutStyle == LocalCustomOverlayLayoutStyle.XboxClassic || string.Equals(sanPresetId, "xbox360", StringComparison.Ordinal))
                {
                    AnimateSanXboxClassic(root, icon, content, transition, display);
                    return;
                }

                switch (style)
                {
                    case LocalCustomOverlayAnimationStyle.SanXqjan:
                        AnimateSanExpandCard(root, icon, content, transition, display, false);
                        break;
                    case LocalCustomOverlayAnimationStyle.SanExpandCard:
                    case LocalCustomOverlayAnimationStyle.SanDefault:
                        AnimateSanExpandCard(root, icon, content, transition, display, settings.OverlayCustomLayoutStyle == LocalCustomOverlayLayoutStyle.XboxModern || string.Equals(sanPresetId, "xboxone", StringComparison.Ordinal));
                        break;
                    case LocalCustomOverlayAnimationStyle.SanSlideCard:
                        AnimateSanSlideCard(root, transition);
                        break;
                    case LocalCustomOverlayAnimationStyle.SanEpic:
                        AnimateSanEpic(root, icon, content, transition, display);
                        break;
                    case LocalCustomOverlayAnimationStyle.SanAero:
                        AnimateSanAero(root, icon, content, transition, display);
                        break;
                }
            };
        }

        private static string NormalizeSanPresetId(string presetId)
        {
            return (presetId ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static LocalCustomOverlayAnimationStyle ResolveSanAnimationStyle(string sanPresetId)
        {
            switch (sanPresetId)
            {
                case "default":
                    return LocalCustomOverlayAnimationStyle.SanDefault;
                case "xboxone":
                    return LocalCustomOverlayAnimationStyle.SanExpandCard;
                case "xqjan":
                    return LocalCustomOverlayAnimationStyle.SanXqjan;
                case "epicgames":
                    return LocalCustomOverlayAnimationStyle.SanEpic;
                case "gfwl":
                    return LocalCustomOverlayAnimationStyle.SanAero;
                case "steamdeck":
                case "ps5":
                case "ps4":
                case "ps3":
                case "windows":
                case "xbox360":
                    return LocalCustomOverlayAnimationStyle.SanSlideCard;
                default:
                    return LocalCustomOverlayAnimationStyle.Standard;
            }
        }

        private static string ResolveSanAnimationPresetId(LocalSettings settings, string fallbackPresetId)
        {
            var fallback = NormalizeSanPresetId(fallbackPresetId);
            if (settings == null)
            {
                return fallback;
            }

            var transitionPreset = ResolveSanAnimationPresetId(settings.UnlockOverlayTransitionStyle);
            if (!string.IsNullOrWhiteSpace(transitionPreset))
            {
                return transitionPreset;
            }

            switch (settings.OverlayCustomAnimationStyle)
            {
                case LocalCustomOverlayAnimationStyle.SanDefault:
                    return "default";
                case LocalCustomOverlayAnimationStyle.SanXqjan:
                    return "xqjan";
                case LocalCustomOverlayAnimationStyle.SanExpandCard:
                    return "xboxone";
                case LocalCustomOverlayAnimationStyle.SanSlideCard:
                    return "steamdeck";
                case LocalCustomOverlayAnimationStyle.SanEpic:
                    return "epicgames";
                case LocalCustomOverlayAnimationStyle.SanAero:
                    return "gfwl";
                case LocalCustomOverlayAnimationStyle.Standard:
                default:
                    return fallback;
            }
        }

        private static string ResolveSanAnimationPresetId(LocalUnlockOverlayTransitionStyle transitionStyle)
        {
            switch (transitionStyle)
            {
                case LocalUnlockOverlayTransitionStyle.SanDefault:
                    return "default";
                case LocalUnlockOverlayTransitionStyle.SanXqjan:
                    return "xqjan";
                case LocalUnlockOverlayTransitionStyle.SanDeck:
                    return "steamdeck";
                case LocalUnlockOverlayTransitionStyle.SanEpic:
                    return "epicgames";
                case LocalUnlockOverlayTransitionStyle.SanXboxModern:
                    return "xboxone";
                case LocalUnlockOverlayTransitionStyle.SanXboxClassic:
                    return "xbox360";
                case LocalUnlockOverlayTransitionStyle.SanPsModern:
                    return "ps5";
                case LocalUnlockOverlayTransitionStyle.SanPsClassic:
                    return "ps4";
                case LocalUnlockOverlayTransitionStyle.SanPsRetro:
                    return "ps3";
                case LocalUnlockOverlayTransitionStyle.SanSquare:
                    return "windows";
                case LocalUnlockOverlayTransitionStyle.SanAero:
                    return "gfwl";
                default:
                    return string.Empty;
            }
        }

        private static bool IsSanTransitionStyle(LocalUnlockOverlayTransitionStyle transitionStyle)
        {
            return !string.IsNullOrWhiteSpace(ResolveSanAnimationPresetId(transitionStyle));
        }

        private static bool AreSanElementAndTransitionCompatible(string transitionPreset, string elementPreset)
        {
            var transition = NormalizeSanPresetId(transitionPreset);
            var element = NormalizeSanPresetId(elementPreset);
            if (string.IsNullOrWhiteSpace(transition) || string.IsNullOrWhiteSpace(element))
            {
                return true;
            }

            if (string.Equals(transition, element, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GetSanPresetFamily(transition) == GetSanPresetFamily(element);
        }

        private static string GetSanPresetFamily(string presetId)
        {
            switch (NormalizeSanPresetId(presetId))
            {
                case "ps5":
                case "ps4":
                case "ps3":
                    return "playstation";
                case "xboxone":
                case "xbox360":
                    return "xbox";
                default:
                    return NormalizeSanPresetId(presetId);
            }
        }

        private static void AnimateSanExpandCard(FrameworkElement root, FrameworkElement icon, FrameworkElement content, int transitionMs, int displayMs, bool useXboxModernTiming = false)
        {
            var finalWidth = root.Width > 0 ? root.Width : root.ActualWidth;
            var collapsedWidth = Math.Max(root.Height > 0 ? root.Height : root.ActualHeight, finalWidth * 0.166);
            var expandDelay = useXboxModernTiming ? transitionMs * 7 : transitionMs * 4;
            var contentDelay = useXboxModernTiming ? transitionMs * 8 : transitionMs * 6;
            var collapseDelay = Math.Max(transitionMs * 8, displayMs - transitionMs * (useXboxModernTiming ? 11 : 6));

            root.Width = collapsedWidth;
            root.Opacity = 1;
            root.ClipToBounds = true;

            if (content != null)
            {
                content.Opacity = 0;
            }

            if (icon != null)
            {
                icon.RenderTransformOrigin = new Point(0.5, 0.5);
                icon.RenderTransform = new ScaleTransform(0, 0);
                if (icon.RenderTransform is ScaleTransform iconScale)
                {
                    var iconIn = new DoubleAnimationUsingKeyFrames();
                    iconIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(transitionMs))));
                    iconIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(transitionMs * 2))));
                    iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconIn);
                    iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconIn.Clone());
                }
            }

            root.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(collapsedWidth, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(collapsedWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(expandDelay))),
                    new EasingDoubleKeyFrame(finalWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(expandDelay + transitionMs * 2))),
                    new EasingDoubleKeyFrame(finalWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(collapseDelay))),
                    new EasingDoubleKeyFrame(collapsedWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(collapseDelay + transitionMs * 2)))
                }
            });

            content?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(contentDelay))),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(contentDelay + transitionMs * 2))),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(collapseDelay))),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(collapseDelay + transitionMs * 2)))
                }
            });
        }

        private static void AnimateSanXboxClassic(FrameworkElement root, FrameworkElement icon, FrameworkElement content, int transitionMs, int displayMs)
        {
            var finalWidth = root.Width > 0 ? root.Width : root.ActualWidth;
            var collapsedWidth = Math.Max(root.Height > 0 ? root.Height : root.ActualHeight, finalWidth / 6.5);
            root.Width = collapsedWidth;
            root.Opacity = 1;
            root.ClipToBounds = true;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = new ScaleTransform(0, 0);

            if (content != null)
            {
                content.Opacity = 0;
            }

            if (root.RenderTransform is ScaleTransform scale)
            {
                var scaleIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(transitionMs)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn.Clone());
            }

            var collapseDelay = Math.Max(transitionMs * 4, displayMs - transitionMs);
            root.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(collapsedWidth, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(finalWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(transitionMs * 2))),
                    new EasingDoubleKeyFrame(finalWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(collapseDelay))),
                    new EasingDoubleKeyFrame(collapsedWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(collapseDelay + transitionMs)))
                }
            });

            content?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(transitionMs * 2))),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(transitionMs * 3))),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(transitionMs * 4, displayMs - transitionMs * 2)))),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(transitionMs * 5, displayMs - transitionMs))))
                }
            });

            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(transitionMs)))
            {
                BeginTime = TimeSpan.FromMilliseconds(collapseDelay)
            });
        }

        private static void AnimateSanSlideCard(FrameworkElement root, int transitionMs)
        {
            var transform = new TranslateTransform(70, 0);
            root.RenderTransform = transform;
            root.Opacity = 0;
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(transitionMs * 2)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(70, 0, new Duration(TimeSpan.FromMilliseconds(transitionMs * 2)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private static void AnimateSanEpic(FrameworkElement root, FrameworkElement icon, FrameworkElement content, int transitionMs, int displayMs)
        {
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = new ScaleTransform(0.65, 0.65);
            root.Opacity = 0;
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(transitionMs * 2))));
            if (root.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.65, 1, new Duration(TimeSpan.FromMilliseconds(transitionMs * 3)))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
                });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.65, 1, new Duration(TimeSpan.FromMilliseconds(transitionMs * 3)))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
                });
            }

            if (content != null)
            {
                content.Opacity = 0;
                content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(transitionMs * 2)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(transitionMs)
                });
                content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(transitionMs * 2)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(Math.Max(transitionMs * 8, displayMs - transitionMs * 3))
                });
            }
        }

        private static void AnimateSanAero(FrameworkElement root, FrameworkElement icon, FrameworkElement content, int transitionMs, int displayMs)
        {
            AnimateSanSlideCard(root, transitionMs);
            if (icon != null)
            {
                icon.RenderTransformOrigin = new Point(0.5, 0.5);
                icon.RenderTransform = new ScaleTransform(0, 0);
                if (icon.RenderTransform is ScaleTransform scale)
                {
                    var bounce = new DoubleAnimationUsingKeyFrames();
                    bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(transitionMs))));
                    bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(transitionMs * 2))));
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce.Clone());
                }
            }

            content?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(transitionMs * 2)))
            {
                BeginTime = TimeSpan.FromMilliseconds(Math.Max(transitionMs * 6, displayMs - transitionMs * 4))
            });
        }

        private static FrameworkElement CreateXboxClassicBadge(Brush accentBrush, Brush borderBrush, Brush backgroundBrush, double size)
        {
            var canvas = new Canvas
            {
                Width = size,
                Height = size
            };

            canvas.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Stroke = borderBrush,
                StrokeThickness = Math.Max(3, size * 0.055),
                Fill = backgroundBrush
            });

            canvas.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = size * 0.70,
                Height = size * 0.70,
                Stroke = borderBrush,
                StrokeThickness = Math.Max(3, size * 0.055),
                Fill = Brushes.Transparent
            });
            Canvas.SetLeft(canvas.Children[1], size * 0.15);
            Canvas.SetTop(canvas.Children[1], size * 0.15);

            var segmentThickness = Math.Max(8, size * 0.12);
            var topSegment = new Border
            {
                Width = size * 0.42,
                Height = segmentThickness,
                Background = accentBrush,
                CornerRadius = new CornerRadius(segmentThickness / 2),
                RenderTransform = new RotateTransform(-18, size * 0.21, segmentThickness / 2)
            };
            Canvas.SetLeft(topSegment, size * 0.10);
            Canvas.SetTop(topSegment, size * 0.16);
            canvas.Children.Add(topSegment);

            var sideSegment = new Border
            {
                Width = segmentThickness,
                Height = size * 0.35,
                Background = accentBrush,
                CornerRadius = new CornerRadius(segmentThickness / 2)
            };
            Canvas.SetLeft(sideSegment, size * 0.16);
            Canvas.SetTop(sideSegment, size * 0.30);
            canvas.Children.Add(sideSegment);

            var trophy = CreateXboxTrophyGlyph(accentBrush, size * 0.50);
            Canvas.SetLeft(trophy, size * 0.25);
            Canvas.SetTop(trophy, size * 0.22);
            canvas.Children.Add(trophy);

            return canvas;
        }

        private static FrameworkElement CreateXboxTrophyGlyph(Brush brush, double size)
        {
            var canvas = new Canvas
            {
                Width = size,
                Height = size
            };

            canvas.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = size * 0.42,
                Height = size * 0.46,
                RadiusX = size * 0.06,
                RadiusY = size * 0.06,
                Fill = brush
            });
            Canvas.SetLeft(canvas.Children[0], size * 0.29);
            Canvas.SetTop(canvas.Children[0], size * 0.12);

            var leftHandle = new System.Windows.Shapes.Path
            {
                Stroke = brush,
                StrokeThickness = Math.Max(3, size * 0.09),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse(string.Format(
                    CultureInfo.InvariantCulture,
                    "M {0:0.###},{1:0.###} L {2:0.###},{3:0.###} L {4:0.###},{5:0.###} C {6:0.###},{7:0.###} {8:0.###},{9:0.###} {10:0.###},{11:0.###}",
                    size * 0.30,
                    size * 0.20,
                    size * 0.14,
                    size * 0.20,
                    size * 0.14,
                    size * 0.38,
                    size * 0.14,
                    size * 0.50,
                    size * 0.22,
                    size * 0.58,
                    size * 0.32,
                    size * 0.60))
            };
            canvas.Children.Add(leftHandle);

            var rightHandle = new System.Windows.Shapes.Path
            {
                Stroke = brush,
                StrokeThickness = Math.Max(3, size * 0.09),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse(string.Format(
                    CultureInfo.InvariantCulture,
                    "M {0:0.###},{1:0.###} L {2:0.###},{3:0.###} L {4:0.###},{5:0.###} C {6:0.###},{7:0.###} {8:0.###},{9:0.###} {10:0.###},{11:0.###}",
                    size * 0.70,
                    size * 0.20,
                    size * 0.86,
                    size * 0.20,
                    size * 0.86,
                    size * 0.38,
                    size * 0.86,
                    size * 0.50,
                    size * 0.78,
                    size * 0.58,
                    size * 0.68,
                    size * 0.60))
            };
            canvas.Children.Add(rightHandle);

            canvas.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = size * 0.14,
                Height = size * 0.22,
                Fill = brush
            });
            Canvas.SetLeft(canvas.Children[3], size * 0.43);
            Canvas.SetTop(canvas.Children[3], size * 0.56);

            canvas.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = size * 0.48,
                Height = size * 0.10,
                RadiusX = size * 0.03,
                RadiusY = size * 0.03,
                Fill = brush
            });
            Canvas.SetLeft(canvas.Children[4], size * 0.26);
            Canvas.SetTop(canvas.Children[4], size * 0.80);

            canvas.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = size * 0.66,
                Height = size * 0.09,
                RadiusX = size * 0.03,
                RadiusY = size * 0.03,
                Fill = brush
            });
            Canvas.SetLeft(canvas.Children[5], size * 0.17);
            Canvas.SetTop(canvas.Children[5], size * 0.91);

            return canvas;
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

            var replaced = OverlayTemplateTokenPattern.Replace(
                effectiveTemplate,
                match => ResolveCustomTemplateToken(
                    match.Groups[1].Value,
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
                    match.Value));

            return replaced?.Trim() ?? string.Empty;
        }

        private static string ResolveSanTemplateLineHtml(
            LocalSettings settings,
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

            var builder = new StringBuilder();
            var hasVisibleContent = false;
            var lastIndex = 0;
            foreach (Match match in OverlayTemplateTokenPattern.Matches(effectiveTemplate))
            {
                AppendSanTemplateText(builder, effectiveTemplate.Substring(lastIndex, match.Index - lastIndex), ref hasVisibleContent);

                var tokenName = match.Groups[1].Value;
                if (string.Equals(tokenName, "trophyIcon", StringComparison.OrdinalIgnoreCase))
                {
                    AppendSanTemplateIcon(builder, ResolveSanTrophyIconUri(settings, achievementTrophy, achievementRarity, achievementPoints), ref hasVisibleContent);
                }
                else if (string.Equals(tokenName, "rarityIcon", StringComparison.OrdinalIgnoreCase))
                {
                    AppendSanTemplateIcon(builder, ResolveSanRarityIconUri(achievementRarity, achievementPoints), ref hasVisibleContent);
                }
                else
                {
                    AppendSanTemplateText(
                        builder,
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

            AppendSanTemplateText(builder, effectiveTemplate.Substring(lastIndex), ref hasVisibleContent);
            return hasVisibleContent ? builder.ToString() : string.Empty;
        }

        private static void AppendSanTemplateText(StringBuilder builder, string text, ref bool hasVisibleContent)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                hasVisibleContent = true;
            }

            builder.Append(SecurityElement.Escape(text) ?? string.Empty);
        }

        private static void AppendSanTemplateIcon(StringBuilder builder, string uri, ref bool hasVisibleContent)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return;
            }

            builder.Append("<span class=\"san-inline-token-icon\" style=\"background-image:url('");
            builder.Append(SecurityElement.Escape(CssUrl(uri)) ?? string.Empty);
            builder.Append("')\"></span>");
            hasVisibleContent = true;
        }

        private static string ResolveSanRarityIconUri(string achievementRarity, int? achievementPoints)
        {
            var resourceIcon = TryGetResourceImageDataUri(GetRarityBadgeResourceKey(ResolveRarityKey(achievementRarity, achievementPoints)));
            return string.IsNullOrWhiteSpace(resourceIcon)
                ? BuildSanRarityBadgeDataUri(ResolveRarityKey(achievementRarity, achievementPoints))
                : resourceIcon;
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
                else if (string.Equals(tokenName, "rarityIcon", StringComparison.OrdinalIgnoreCase))
                {
                    AddCustomTemplateRarityIcon(textBlock, achievementRarity, achievementPoints, fontSize, ref hasVisibleContent);
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


        private static void ApplyCustomLineTextEffect(TextBlock line, LocalSettings settings, int lineIndex)
        {
            if (line == null || settings == null)
            {
                return;
            }

            bool outlineEnabled;
            bool shadowEnabled;
            string outlineColor;
            string shadowColor;
            double outlineSize;
            double shadowSize;
            switch (lineIndex)
            {
                case 1:
                    outlineEnabled = settings.OverlayCustomLine1OutlineEnabled; shadowEnabled = settings.OverlayCustomLine1ShadowEnabled; outlineColor = settings.OverlayCustomLine1OutlineColor; shadowColor = settings.OverlayCustomLine1ShadowColor; outlineSize = settings.OverlayCustomLine1OutlineSize; shadowSize = settings.OverlayCustomLine1ShadowSize; break;
                case 2:
                    outlineEnabled = settings.OverlayCustomLine2OutlineEnabled; shadowEnabled = settings.OverlayCustomLine2ShadowEnabled; outlineColor = settings.OverlayCustomLine2OutlineColor; shadowColor = settings.OverlayCustomLine2ShadowColor; outlineSize = settings.OverlayCustomLine2OutlineSize; shadowSize = settings.OverlayCustomLine2ShadowSize; break;
                case 3:
                    outlineEnabled = settings.OverlayCustomLine3OutlineEnabled; shadowEnabled = settings.OverlayCustomLine3ShadowEnabled; outlineColor = settings.OverlayCustomLine3OutlineColor; shadowColor = settings.OverlayCustomLine3ShadowColor; outlineSize = settings.OverlayCustomLine3OutlineSize; shadowSize = settings.OverlayCustomLine3ShadowSize; break;
                case 4:
                    outlineEnabled = settings.OverlayCustomLine4OutlineEnabled; shadowEnabled = settings.OverlayCustomLine4ShadowEnabled; outlineColor = settings.OverlayCustomLine4OutlineColor; shadowColor = settings.OverlayCustomLine4ShadowColor; outlineSize = settings.OverlayCustomLine4OutlineSize; shadowSize = settings.OverlayCustomLine4ShadowSize; break;
                case 5:
                    outlineEnabled = settings.OverlayCustomLine5OutlineEnabled; shadowEnabled = settings.OverlayCustomLine5ShadowEnabled; outlineColor = settings.OverlayCustomLine5OutlineColor; shadowColor = settings.OverlayCustomLine5ShadowColor; outlineSize = settings.OverlayCustomLine5OutlineSize; shadowSize = settings.OverlayCustomLine5ShadowSize; break;
                case 6:
                    outlineEnabled = settings.OverlayCustomLine6OutlineEnabled; shadowEnabled = settings.OverlayCustomLine6ShadowEnabled; outlineColor = settings.OverlayCustomLine6OutlineColor; shadowColor = settings.OverlayCustomLine6ShadowColor; outlineSize = settings.OverlayCustomLine6OutlineSize; shadowSize = settings.OverlayCustomLine6ShadowSize; break;
                default:
                    return;
            }

            if (shadowEnabled)
            {
                line.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = Math.Max(0, shadowSize),
                    ShadowDepth = Math.Max(0, shadowSize) / 2,
                    Opacity = 1,
                    Color = ParseColorOrDefault(shadowColor, Colors.Black)
                };
            }
            else if (outlineEnabled)
            {
                line.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = Math.Max(0, outlineSize),
                    ShadowDepth = 0,
                    Opacity = 1,
                    Color = ParseColorOrDefault(outlineColor, Colors.Black)
                };
            }
            else
            {
                line.Effect = null;
            }
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

        private static void AddCustomTemplateRarityIcon(
            TextBlock textBlock,
            string achievementRarity,
            int? achievementPoints,
            double fontSize,
            ref bool hasVisibleContent)
        {
            var rarityBadge = TryGetResourceImageSource(GetRarityBadgeResourceKey(ResolveRarityKey(achievementRarity, achievementPoints)));
            if (rarityBadge == null)
            {
                return;
            }

            var iconSize = Math.Max(10, fontSize * 1.25);
            var image = new Image
            {
                Source = rarityBadge,
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
            var normalizedToken = (tokenName ?? string.Empty).ToLowerInvariant();
            if (IsNotificationScoreToken(normalizedToken))
            {
                return ResolveNotificationScoreToken(normalizedToken, game, gameName, achievementName, achievementRarity);
            }

            switch (normalizedToken)
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

        private sealed class NotificationScoreContext
        {
            public int AchievementCollectionScore { get; set; }
            public int AchievementPrestigeScore { get; set; }
            public int GameCollectionScore { get; set; }
            public int GameCollectionScoreTotal { get; set; }
            public int GamePrestigeScore { get; set; }
            public int GamePrestigeScoreTotal { get; set; }
            public int TotalCollectionScore { get; set; }
            public int TotalPrestigeScore { get; set; }
            public int CollectionLevel { get; set; }
            public string CollectionTier { get; set; } = string.Empty;
            public int CollectionExp { get; set; }
            public int CollectionExpTotal { get; set; }
            public int CollectionExpUntilNextLevel { get; set; }
            public int CollectionExpUntilNextTier { get; set; }
            public string CollectionNextTier { get; set; } = string.Empty;
            public int PrestigeLevel { get; set; }
            public string PrestigeTier { get; set; } = string.Empty;
            public int PrestigeExp { get; set; }
            public int PrestigeExpTotal { get; set; }
            public int PrestigeExpUntilNextLevel { get; set; }
            public int PrestigeExpUntilNextTier { get; set; }
            public string PrestigeNextTier { get; set; } = string.Empty;
            public string AchievementType { get; set; } = string.Empty;
            public string AchievementCategory { get; set; } = string.Empty;
            public int GamePoints { get; set; }
            public int GamePointsTotal { get; set; }
            public int TotalPoints { get; set; }
        }

        private static bool IsNotificationScoreToken(string token)
        {
            switch (token)
            {
                case "achievementcollectionscore":
                case "achievementprestigescore":
                case "gamecollectionscore":
                case "gamecollectionscoretotal":
                case "gameprestigescore":
                case "gameprestigescoretotal":
                case "totalcollectionscore":
                case "totalprestigescore":
                case "collectionlevel":
                case "collectiontier":
                case "collectionexp":
                case "collectionexptotal":
                case "collectionexpuntilnextlevel":
                case "collectionexpuntilnexttier":
                case "collectionnexttier":
                case "prestigelevel":
                case "prestigetier":
                case "prestigeexp":
                case "prestigeexptotal":
                case "prestigeexpuntilnextlevel":
                case "prestigeexpuntilnexttier":
                case "prestigenexttier":
                case "type":
                case "category":
                case "gamepoints":
                case "gamepointstotal":
                case "totalpoints":
                    return true;
                default:
                    return false;
            }
        }

        private static string ResolveNotificationScoreToken(string token, Game game, string gameName, string achievementName, string achievementRarity)
        {
            var scores = GetNotificationScoreContext(game, gameName, achievementName, achievementRarity);
            switch (token)
            {
                case "collectiontier":
                    return scores.CollectionTier;
                case "collectionnexttier":
                    return scores.CollectionNextTier;
                case "prestigetier":
                    return scores.PrestigeTier;
                case "prestigenexttier":
                    return scores.PrestigeNextTier;
                case "type":
                    return scores.AchievementType;
                case "category":
                    return scores.AchievementCategory;
            }

            int value;
            switch (token)
            {
                case "achievementcollectionscore":
                    value = scores.AchievementCollectionScore;
                    break;
                case "achievementprestigescore":
                    value = scores.AchievementPrestigeScore;
                    break;
                case "gamecollectionscore":
                    value = scores.GameCollectionScore;
                    break;
                case "gamecollectionscoretotal":
                    value = scores.GameCollectionScoreTotal;
                    break;
                case "gameprestigescore":
                    value = scores.GamePrestigeScore;
                    break;
                case "gameprestigescoretotal":
                    value = scores.GamePrestigeScoreTotal;
                    break;
                case "totalcollectionscore":
                    value = scores.TotalCollectionScore;
                    break;
                case "totalprestigescore":
                    value = scores.TotalPrestigeScore;
                    break;
                case "collectionlevel":
                    value = scores.CollectionLevel;
                    break;
                case "collectionexp":
                    value = scores.CollectionExp;
                    break;
                case "collectionexptotal":
                    value = scores.CollectionExpTotal;
                    break;
                case "collectionexpuntilnextlevel":
                    value = scores.CollectionExpUntilNextLevel;
                    break;
                case "collectionexpuntilnexttier":
                    value = scores.CollectionExpUntilNextTier;
                    break;
                case "prestigelevel":
                    value = scores.PrestigeLevel;
                    break;
                case "prestigeexp":
                    value = scores.PrestigeExp;
                    break;
                case "prestigeexptotal":
                    value = scores.PrestigeExpTotal;
                    break;
                case "prestigeexpuntilnextlevel":
                    value = scores.PrestigeExpUntilNextLevel;
                    break;
                case "prestigeexpuntilnexttier":
                    value = scores.PrestigeExpUntilNextTier;
                    break;
                case "gamepoints":
                    value = scores.GamePoints;
                    break;
                case "gamepointstotal":
                    value = scores.GamePointsTotal;
                    break;
                case "totalpoints":
                    value = scores.TotalPoints;
                    break;
                default:
                    value = 0;
                    break;
            }

            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static NotificationScoreContext GetNotificationScoreContext(Game game, string gameName, string achievementName, string achievementRarity)
        {
            var cacheKey = $"{game?.Id.ToString() ?? gameName ?? string.Empty}|{achievementName ?? string.Empty}|{achievementRarity ?? string.Empty}";
            lock (NotificationScoreCacheLock)
            {
                if (_notificationScoreCache != null &&
                    string.Equals(_notificationScoreCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow - _notificationScoreCacheUtc < TimeSpan.FromSeconds(2))
                {
                    return _notificationScoreCache;
                }
            }

            var context = new NotificationScoreContext();
            var plugin = PlayniteAchievementsPlugin.Instance;
            var runtimeSettings = plugin?.Settings;
            if (runtimeSettings != null)
            {
                context.TotalCollectionScore = Math.Max(0, runtimeSettings.CollectorScore);
                context.TotalPrestigeScore = Math.Max(0, runtimeSettings.PrestigeScore);
                context.CollectionLevel = Math.Max(0, runtimeSettings.CollectorLevel);
                context.CollectionTier = AchievementRankPresentation.FormatRank(runtimeSettings.CollectorRank);
                context.PrestigeLevel = Math.Max(0, runtimeSettings.PrestigeLevel);
                context.PrestigeTier = AchievementRankPresentation.FormatRank(runtimeSettings.PrestigeRank);
            }

            try
            {
                var dataService = plugin?.AchievementDataService;
                var gameData = game != null && game.Id != Guid.Empty
                    ? dataService?.GetVisibleGameAchievementData(game.Id)
                    : null;
                var allData = dataService?.GetAllVisibleGameAchievementDataForTheme() ?? new List<GameAchievementData>();
                gameData = gameData ?? allData.FirstOrDefault(data =>
                    !string.IsNullOrWhiteSpace(gameName) &&
                    string.Equals(data?.GameName, gameName, StringComparison.OrdinalIgnoreCase));
                var achievement = gameData?.Achievements?.FirstOrDefault(item =>
                    string.Equals(item?.DisplayName, achievementName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item?.ApiName, achievementName, StringComparison.OrdinalIgnoreCase));

                if (achievement != null)
                {
                    context.AchievementCollectionScore = achievement.CollectionScore;
                    context.AchievementPrestigeScore = achievement.PrestigeScore;
                }
                else
                {
                    var fallbackTier = ResolveNotificationRarityTier(achievementRarity);
                    context.AchievementCollectionScore = AchievementScoreCalculator.GetCollectionValue(fallbackTier);
                    context.AchievementPrestigeScore = AchievementScoreCalculator.GetPrestigeValue(
                        TryParseRarityPercent(achievementRarity),
                        fallbackTier);
                }
                context.AchievementType = achievement?.CategoryType ?? string.Empty;
                context.AchievementCategory = achievement?.Category ?? string.Empty;
                PopulateGameNotificationScores(context, gameData);

                var libraryScores = AchievementScoreCalculator.CalculateModernScores(allData);
                if (context.TotalCollectionScore <= 0 && libraryScores.CollectorScore > 0)
                {
                    context.TotalCollectionScore = libraryScores.CollectorScore;
                    context.CollectionLevel = libraryScores.CollectorLevel?.DisplayLevel ?? 0;
                    context.CollectionTier = AchievementRankPresentation.FormatRank(libraryScores.CollectorLevel?.RankValue ?? AchievementRank.Bronze5);
                }
                if (context.TotalPrestigeScore <= 0 && libraryScores.PrestigeScore > 0)
                {
                    context.TotalPrestigeScore = libraryScores.PrestigeScore;
                    context.PrestigeLevel = libraryScores.PrestigeLevel?.DisplayLevel ?? 0;
                    context.PrestigeTier = AchievementRankPresentation.FormatRank(libraryScores.PrestigeLevel?.RankValue ?? AchievementRank.Bronze5);
                }
                context.TotalPoints = SumNotificationPoints(
                    allData.SelectMany(data => data?.Achievements ?? Enumerable.Empty<AchievementDetail>())
                        .Where(item => item?.Unlocked == true));
            }
            catch
            {
                // Score wildcards should never prevent an unlock notification from rendering.
            }

            PopulateNotificationLevelProgress(context);

            lock (NotificationScoreCacheLock)
            {
                _notificationScoreCacheKey = cacheKey;
                _notificationScoreCacheUtc = DateTime.UtcNow;
                _notificationScoreCache = context;
            }

            return context;
        }

        private static void PopulateNotificationLevelProgress(NotificationScoreContext context)
        {
            var collection = AchievementLevelCalculator.CalculateModern(context.TotalCollectionScore);
            context.CollectionLevel = collection.DisplayLevel;
            context.CollectionTier = AchievementRankPresentation.FormatRank(collection.RankValue);
            context.CollectionExp = collection.CurrentLevelPoints;
            context.CollectionExpTotal = collection.CurrentLevelTotalPoints;
            context.CollectionExpUntilNextLevel = collection.PointsUntilNextLevel;
            context.CollectionExpUntilNextTier = collection.PointsUntilNextRank;
            context.CollectionNextTier = string.IsNullOrWhiteSpace(collection.NextRank)
                ? context.CollectionTier
                : AchievementRankPresentation.FormatRank(collection.NextRank);

            var prestige = AchievementLevelCalculator.CalculateModern(context.TotalPrestigeScore);
            context.PrestigeLevel = prestige.DisplayLevel;
            context.PrestigeTier = AchievementRankPresentation.FormatRank(prestige.RankValue);
            context.PrestigeExp = prestige.CurrentLevelPoints;
            context.PrestigeExpTotal = prestige.CurrentLevelTotalPoints;
            context.PrestigeExpUntilNextLevel = prestige.PointsUntilNextLevel;
            context.PrestigeExpUntilNextTier = prestige.PointsUntilNextRank;
            context.PrestigeNextTier = string.IsNullOrWhiteSpace(prestige.NextRank)
                ? context.PrestigeTier
                : AchievementRankPresentation.FormatRank(prestige.NextRank);
        }

        private static RarityTier ResolveNotificationRarityTier(string rarity)
        {
            var key = ResolveRarityKey(rarity, null);
            switch (key)
            {
                case "UltraRare":
                    return RarityTier.UltraRare;
                case "Rare":
                    return RarityTier.Rare;
                case "Uncommon":
                    return RarityTier.Uncommon;
                default:
                    return RarityTier.Common;
            }
        }

        private static double? TryParseRarityPercent(string rarity)
        {
            if (string.IsNullOrWhiteSpace(rarity))
            {
                return null;
            }

            var match = Regex.Match(rarity, @"[-+]?\d+(?:[.,]\d+)?");
            if (!match.Success)
            {
                return null;
            }

            var normalized = match.Value.Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? (double?)percent
                : null;
        }

        private static void PopulateGameNotificationScores(NotificationScoreContext context, GameAchievementData gameData)
        {
            var achievements = gameData?.Achievements ?? new List<AchievementDetail>();
            var unlocked = achievements.Where(item => item?.Unlocked == true).ToList();
            context.GameCollectionScore = SumNotificationValues(unlocked.Select(item => item.CollectionScore));
            context.GameCollectionScoreTotal = SumNotificationValues(achievements.Where(item => item != null).Select(item => item.CollectionScore));
            context.GamePrestigeScore = SumNotificationValues(unlocked.Select(item => item.PrestigeScore));
            context.GamePrestigeScoreTotal = SumNotificationValues(achievements.Where(item => item != null).Select(item => item.PrestigeScore));
            context.GamePoints = SumNotificationPoints(unlocked);
            context.GamePointsTotal = SumNotificationPoints(achievements);
        }

        private static int SumNotificationPoints(IEnumerable<AchievementDetail> achievements)
        {
            return SumNotificationValues((achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(item => item != null)
                .Select(item => Math.Max(0, item.Points ?? item.ScaledPoints ?? 0)));
        }

        private static int SumNotificationValues(IEnumerable<int> values)
        {
            long total = 0;
            foreach (var value in values ?? Enumerable.Empty<int>())
            {
                total += Math.Max(0, value);
                if (total >= int.MaxValue)
                {
                    return int.MaxValue;
                }
            }

            return (int)total;
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

        private FrameworkElement CreateCustomOverlayIconContent(LocalSettings settings, string rawIconPath, string providerKey, Brush titleBrush, double iconSize, double titleSize, string rarityKey, LocalOverlayIconSource? iconSourceOverride = null, double cornerRadius = 0)
        {
            var iconSourceMode = iconSourceOverride ?? settings?.OverlayCustomIconSource ?? LocalOverlayIconSource.AchievementIcon;
            var fallbackIconBrush = ParseBrushOrDefault(settings?.OverlayCustomAccentColor, Color.FromRgb(255, 255, 255));

            if (iconSourceMode == LocalOverlayIconSource.AchievementIcon)
            {
                var iconSource = TryCreateOverlayImageSource(rawIconPath);
                if (iconSource != null)
                {
                    var image = new Image
                    {
                        Source = iconSource,
                        Stretch = Stretch.UniformToFill,
                        Width = iconSize,
                        Height = iconSize
                    };
                    var radius = Math.Max(0, Math.Min(iconSize / 2, cornerRadius));
                    image.Clip = new RectangleGeometry(new Rect(0, 0, iconSize, iconSize), radius, radius);
                    return image;
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
                    Fill = fallbackIconBrush,
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

            if (iconSourceMode == LocalOverlayIconSource.CustomIcon)
            {
                var customPath = iconSourceOverride.HasValue
                    ? settings?.OverlayCustomSecondaryIconPath
                    : settings?.OverlayCustomIconPath;
                var customIcon = TryCreateOverlayImageSource(customPath);
                if (customIcon != null)
                {
                    return new Image
                    {
                        Source = customIcon,
                        Stretch = Stretch.Uniform,
                        Width = iconSize,
                        Height = iconSize,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
            }

            return new TextBlock
            {
                Text = "🏆",
                Foreground = fallbackIconBrush,
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

        private static string TryGetResourceImageDataUri(string key)
        {
            var source = TryGetResourceImageSource(key);
            if (source == null)
            {
                return string.Empty;
            }

            try
            {
                BitmapSource bitmap = source as BitmapSource;
                if (bitmap == null)
                {
                    var width = Math.Max(1, (int)Math.Ceiling(source.Width > 0 ? source.Width : 64));
                    var height = Math.Max(1, (int)Math.Ceiling(source.Height > 0 ? source.Height : 64));
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        context.DrawImage(source, new Rect(0, 0, width, height));
                    }

                    var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(visual);
                    bitmap = rendered;
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
                }
            }
            catch
            {
                return string.Empty;
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

        private static Color ParseColorOrDefault(string colorValue, Color fallback)
        {
            if (!string.IsNullOrWhiteSpace(colorValue))
            {
                try
                {
                    return (Color)ColorConverter.ConvertFromString(colorValue.Trim());
                }
                catch
                {
                }
            }

            return fallback;
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
                if (imageSource.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    var commaIndex = imageSource.IndexOf(',');
                    var header = commaIndex > 0 ? imageSource.Substring(0, commaIndex) : string.Empty;
                    if (commaIndex > 0 &&
                        header.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        header.IndexOf("svg", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        var bytes = Convert.FromBase64String(imageSource.Substring(commaIndex + 1));
                        using (var stream = new MemoryStream(bytes))
                        {
                            var decodedBitmap = new BitmapImage();
                            decodedBitmap.BeginInit();
                            decodedBitmap.CacheOption = BitmapCacheOption.OnLoad;
                            decodedBitmap.StreamSource = stream;
                            decodedBitmap.EndInit();
                            decodedBitmap.Freeze();
                            return decodedBitmap;
                        }
                    }

                    return null;
                }

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

        public static string ResolveSanDefaultSoundPath(LocalSettings settings)
        {
            var sound = ResolveInstalledSanAssetPath("sound", "notify.wav");
            if (!string.IsNullOrWhiteSpace(sound))
            {
                return sound;
            }

            var root = settings?.OverlayCustomSanAssetRootPath;
            if (!string.IsNullOrWhiteSpace(root))
            {
                sound = Path.Combine(root, "sound", "notify.wav");
                if (File.Exists(sound))
                {
                    return sound;
                }
            }

            return string.Empty;
        }
    }

    public sealed class AchievementUnlockNotificationItem
    {
        public AchievementUnlockNotificationItem(
            string name,
            string iconPath = null,
            string description = null,
            int? points = null,
            string rarity = null,
            string trophy = null)
        {
            Name = name ?? string.Empty;
            IconPath = iconPath;
            Description = description;
            Points = points;
            Rarity = rarity;
            Trophy = trophy;
        }

        public string Name { get; }

        public string IconPath { get; }

        public string Description { get; }

        public int? Points { get; }

        public string Rarity { get; }

        public string Trophy { get; }
    }
}
