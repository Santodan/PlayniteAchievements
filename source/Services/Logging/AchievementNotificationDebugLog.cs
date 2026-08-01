using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.Logging
{
    /// <summary>
    /// Owns the opt-in, focused diagnostic log for achievement notifications.
    /// </summary>
    public static class AchievementNotificationDebugLog
    {
        public const string FileName = "AchNotifDebug.log";

        private static readonly object SyncRoot = new object();
        private static FileLogger _logger;
        private static string _logDirectory;
        private static string _lastSettingsSnapshot;

        public static string LogFilePath
        {
            get
            {
                lock (SyncRoot)
                {
                    return string.IsNullOrWhiteSpace(_logDirectory)
                        ? null
                        : Path.Combine(_logDirectory, FileName);
                }
            }
        }

        public static bool IsEnabled
        {
            get
            {
                lock (SyncRoot)
                {
                    return _logger != null;
                }
            }
        }

        public static void Initialize(string extensionDataPath)
        {
            if (string.IsNullOrWhiteSpace(extensionDataPath))
            {
                return;
            }

            lock (SyncRoot)
            {
                _logDirectory = extensionDataPath;
            }
        }

        public static bool FileExists()
        {
            var path = LogFilePath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }

        public static void SetEnabled(bool enabled, bool recreate = false)
        {
            lock (SyncRoot)
            {
                if (!enabled)
                {
                    DisposeLogger();
                    return;
                }

                if (string.IsNullOrWhiteSpace(_logDirectory))
                {
                    throw new InvalidOperationException("The achievement notification debug log has not been initialized.");
                }

                if (_logger != null && !recreate)
                {
                    return;
                }

                DisposeLogger();
                Directory.CreateDirectory(_logDirectory);
                var path = Path.Combine(_logDirectory, FileName);
                if (recreate && File.Exists(path))
                {
                    File.Delete(path);
                }

                // Create/open synchronously so the file is present as soon as the setting is enabled.
                using (File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                }

                // This log follows the user's explicit recreate-or-append choice, so do not
                // rotate an existing file behind their back when append was selected.
                _logger = new FileLogger(_logDirectory, FileName, long.MaxValue);
                _lastSettingsSnapshot = null;
                _logger.Info(
                    $"Achievement Notification debug session started. " +
                    $"pluginVersion='{Assembly.GetExecutingAssembly().GetName().Version}', " +
                    $"OS='{Environment.OSVersion}', process64Bit='{Environment.Is64BitProcess}'.");
            }
        }

        public static void Info(string message)
        {
            lock (SyncRoot) { _logger?.Info(message); }
        }

        public static void Warn(string message)
        {
            lock (SyncRoot) { _logger?.Warn(message); }
        }

        public static void Warn(Exception exception, string message)
        {
            lock (SyncRoot) { _logger?.Warn(exception, message); }
        }

        public static void Error(Exception exception, string message)
        {
            lock (SyncRoot) { _logger?.Error(exception, message); }
        }

        public static void LogSettingsSnapshot(
            LocalSettings localSettings,
            PersistedSettings persistedSettings,
            string reason,
            bool force = false)
        {
            if (localSettings == null)
            {
                return;
            }

            try
            {
                var snapshot = BuildSettingsSnapshot(localSettings, persistedSettings);
                lock (SyncRoot)
                {
                    if (_logger == null || (!force && string.Equals(_lastSettingsSnapshot, snapshot, StringComparison.Ordinal)))
                    {
                        return;
                    }

                    _lastSettingsSnapshot = snapshot;
                    _logger.Info(
                        $"Achievement Notification settings snapshot reason='{reason ?? "unspecified"}':" +
                        Environment.NewLine + snapshot);
                }
            }
            catch (Exception ex)
            {
                Warn(ex, "Failed to collect the Achievement Notification settings snapshot.");
            }
        }

        public static void Shutdown()
        {
            lock (SyncRoot) { DisposeLogger(); }
        }

        private static void DisposeLogger()
        {
            _logger?.Dispose();
            _logger = null;
            _lastSettingsSnapshot = null;
        }

        private static string BuildSettingsSnapshot(LocalSettings localSettings, PersistedSettings persistedSettings)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[Local notification settings]");
            AppendProperties(builder, localSettings, IsLocalNotificationSetting);

            var slots = localSettings.CustomOverlayStyleSlots;
            builder.AppendLine($"CustomOverlayStyleSlotCount={slots?.Count ?? 0}");
            var selectedSlotIndex = Math.Max(0, (localSettings.SelectedCustomStyleSlot - 1));
            var selectedSlot = slots != null && selectedSlotIndex < slots.Count ? slots[selectedSlotIndex] : null;
            builder.AppendLine($"SelectedCustomStyleSlotName={FormatValue(selectedSlot?.Name)}");

            builder.AppendLine("[Global notification settings]");
            if (persistedSettings != null)
            {
                AppendNamedProperty(builder, persistedSettings, nameof(PersistedSettings.EnableNotifications));
                AppendNamedProperty(builder, persistedSettings, nameof(PersistedSettings.DefaultUnlockNotificationStyle));
                AppendNamedProperty(builder, persistedSettings, nameof(PersistedSettings.ProviderUnlockNotificationStyles));
                AppendNamedProperty(builder, persistedSettings, nameof(PersistedSettings.ProviderNotificationOverrides));
                builder.AppendLine(
                    $"DisabledRealtimeNotificationGameCount={persistedSettings.DisabledRealtimeNotificationGameIds?.Count ?? 0}");
                builder.AppendLine(
                    $"DisabledRealtimeNotificationGameIds={FormatValue(persistedSettings.DisabledRealtimeNotificationGameIds)}");
            }

            return builder.ToString().TrimEnd();
        }

        private static bool IsLocalNotificationSetting(PropertyInfo property)
        {
            var name = property?.Name ?? string.Empty;
            return name.StartsWith("Overlay", StringComparison.Ordinal) ||
                   name.StartsWith("Unlock", StringComparison.Ordinal) ||
                   name.StartsWith("Screenshot", StringComparison.Ordinal) ||
                   name.StartsWith("Recording", StringComparison.Ordinal) ||
                   name.StartsWith("EnableUnlock", StringComparison.Ordinal) ||
                   name.StartsWith("EnableGameCover", StringComparison.Ordinal) ||
                   name.StartsWith("EnableGameBanner", StringComparison.Ordinal) ||
                   name.StartsWith("GameCover", StringComparison.Ordinal) ||
                   name.StartsWith("GameBanner", StringComparison.Ordinal) ||
                   name.StartsWith("RefreshAchievements", StringComparison.Ordinal) ||
                   name.StartsWith("ActiveGameMonitoring", StringComparison.Ordinal) ||
                   name == nameof(LocalSettings.EnableActiveGameMonitoring) ||
                   name == nameof(LocalSettings.EnableOverlayDebugLogging) ||
                   name == nameof(LocalSettings.EnableWindowsToastNotifications) ||
                   name == nameof(LocalSettings.EnableInAppUnlockNotifications) ||
                   name == nameof(LocalSettings.ShowOverlayOnActiveGameMonitor) ||
                   name == nameof(LocalSettings.BundledUnlockSoundPath) ||
                   name == nameof(LocalSettings.CustomUnlockSoundPath) ||
                   name == nameof(LocalSettings.ExtraUnlockSoundPaths) ||
                   name == nameof(LocalSettings.EffectiveBundledUnlockSoundPath) ||
                   name == nameof(LocalSettings.EffectiveScreenshotSaveFolder) ||
                   name == nameof(LocalSettings.FfmpegPath) ||
                   name == nameof(LocalSettings.SelectedCustomStyleSlot) ||
                   name == nameof(LocalSettings.CollectionProgressNotifications) ||
                   name == nameof(LocalSettings.PrestigeProgressNotifications);
        }

        private static void AppendProperties(StringBuilder builder, object source, Func<PropertyInfo, bool> include)
        {
            foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 && include(property))
                .Where(property => property.Name != nameof(LocalSettings.CustomOverlayStyleSlots))
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                AppendProperty(builder, source, property);
            }
        }

        private static void AppendNamedProperty(StringBuilder builder, object source, string propertyName)
        {
            var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanRead)
            {
                AppendProperty(builder, source, property);
            }
        }

        private static void AppendProperty(StringBuilder builder, object source, PropertyInfo property)
        {
            try
            {
                builder.AppendLine($"{property.Name}={FormatValue(property.GetValue(source))}");
            }
            catch (Exception ex)
            {
                builder.AppendLine($"{property.Name}=<unavailable: {ex.GetType().Name}>");
            }
        }

        private static string FormatValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string text)
            {
                return text.Length <= 1000
                    ? JsonConvert.SerializeObject(text)
                    : $"<configured; {text.Length} characters omitted>";
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is decimal || value is Guid)
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value is ICollection collection && collection.Count == 0)
            {
                return "[]";
            }

            var json = JsonConvert.SerializeObject(value, Formatting.None);
            return json.Length <= 4000
                ? json
                : $"<configured collection/object; {json.Length} characters omitted>";
        }
    }
}
