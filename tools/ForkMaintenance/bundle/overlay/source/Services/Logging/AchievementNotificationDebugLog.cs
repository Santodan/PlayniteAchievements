using System;
using System.IO;
using System.Reflection;

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

        public static void Shutdown()
        {
            lock (SyncRoot) { DisposeLogger(); }
        }

        private static void DisposeLogger()
        {
            _logger?.Dispose();
            _logger = null;
        }
    }
}
