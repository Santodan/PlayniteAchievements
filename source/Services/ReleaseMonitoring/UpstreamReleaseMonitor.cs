using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services.ReleaseMonitoring
{
    internal sealed class UpstreamReleaseMonitor
    {
        private const string UpstreamInstallerManifestUrl = "https://raw.githubusercontent.com/justin-delano/PlayniteAchievements/main/InstallerManifest.yaml";
        private const string UpstreamReleasesUrl = "https://github.com/justin-delano/PlayniteAchievements/releases/latest";

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly NotificationPublisher _notifications;
        private readonly Action _persistSettings;
        private readonly ILogger _logger;

        public UpstreamReleaseMonitor(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            NotificationPublisher notifications,
            Action persistSettings,
            ILogger logger)
        {
            _api = api;
            _settings = settings;
            _notifications = notifications;
            _persistSettings = persistSettings;
            _logger = logger;
        }

        public async Task CheckForUpstreamReleaseAsync()
        {
            try
            {
                if (_settings?.Persisted?.EnableNotifications != true)
                {
                    return;
                }

                var currentVersion = GetInstalledVersion();
                if (string.IsNullOrWhiteSpace(currentVersion))
                {
                    return;
                }

                var upstreamVersion = await DownloadLatestUpstreamVersionAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(upstreamVersion) || !IsVersionNewer(upstreamVersion, currentVersion))
                {
                    return;
                }

                if (string.Equals(
                    _settings.Persisted.LastUpstreamReleaseNotificationVersion,
                    upstreamVersion,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _settings.Persisted.LastUpstreamReleaseNotificationVersion = upstreamVersion;
                _persistSettings?.Invoke();

                var dispatcher = _api?.MainView?.UIDispatcher;
                if (dispatcher != null)
                {
                    dispatcher.Invoke(() => _notifications?.ShowUpstreamReleaseAvailable(upstreamVersion, UpstreamReleasesUrl));
                }
                else
                {
                    _notifications?.ShowUpstreamReleaseAvailable(upstreamVersion, UpstreamReleasesUrl);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed checking for upstream PlayniteAchievements releases.");
            }
        }

        private static string GetInstalledVersion()
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var extensionYaml = Path.Combine(assemblyDir ?? string.Empty, "extension.yaml");
                if (!File.Exists(extensionYaml))
                {
                    return null;
                }

                return ParseVersionFromYaml(File.ReadAllText(extensionYaml), false);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> DownloadLatestUpstreamVersionAsync()
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(15);
                var yaml = await httpClient.GetStringAsync(UpstreamInstallerManifestUrl).ConfigureAwait(false);
                return ParseVersionFromYaml(yaml, true);
            }
        }

        private static string ParseVersionFromYaml(string yaml, bool allowListEntry)
        {
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return null;
            }

            foreach (var rawLine in yaml.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring("Version:".Length).Trim();
                }

                if (allowListEntry && line.StartsWith("- Version:", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring("- Version:".Length).Trim();
                }
            }

            return null;
        }

        private static bool IsVersionNewer(string candidateVersion, string currentVersion)
        {
            var candidate = ParseVersion(candidateVersion);
            var current = ParseVersion(currentVersion);
            if (candidate == null || current == null)
            {
                return !string.Equals(candidateVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
            }

            return candidate > current;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = value.Trim().TrimStart('v', 'V');
            var parts = cleaned.Split('.').Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
            while (parts.Count < 3)
            {
                parts.Add("0");
            }

            Version parsed;
            return Version.TryParse(string.Join(".", parts.Take(4)), out parsed) ? parsed : null;
        }
    }
}