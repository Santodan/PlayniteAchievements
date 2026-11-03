using System;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.InGameMonitoring;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSavesProvider
    {
        private sealed class SantodanStartupCapture
        {
            public string Fingerprint;
            public string AchievementFilePath;
            public string Configuration;
            public DateTime PreviousCacheUpdatedUtc;
            public readonly LocalPollingDirectoryCache Directories = new LocalPollingDirectoryCache();
        }

        private static bool StartupBaselineEnabled =>
            ProviderRegistry.Settings<LocalSettings>()?.UseCustomLocalPolling == true &&
            (ProviderRegistry.Settings<LocalSettings>()?.ReuseCustomPollingStartupBaseline == true ||
             ProviderRegistry.Settings<LocalSettings>()?.UseLightweightCustomStartup == true);

        private SantodanStartupCapture CaptureSantodanStartupBaseline(Game game)
        {
            if (!StartupBaselineEnabled || game == null || PlayniteAchievementsPlugin.Instance == null)
                return null;
            try
            {
                var capture = new SantodanStartupCapture
                {
                    Configuration = GetSantodanPollingConfiguration(game),
                    PreviousCacheUpdatedUtc = PlayniteAchievementsPlugin.Instance.CacheManager?
                        .LoadGameData(game.Id.ToString())?.LastUpdatedUtc ?? default
                };
                return TryGetSantodanPollingSourceFingerprint(game, out capture.Fingerprint, capture.Directories,
                    path => { if (capture.AchievementFilePath == null) capture.AchievementFilePath = path; })
                    ? capture : null;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SantodanPolling] Could not capture startup baseline source.");
                return null;
            }
        }

        internal bool HasSantodanKnownAchievementFile(Game game, GameAchievementData cached, DateTime sessionStartUtc)
        {
            if (ProviderRegistry.Settings<LocalSettings>()?.UseCustomLocalPolling != true ||
                ProviderRegistry.Settings<LocalSettings>()?.UseLightweightCustomStartup != true ||
                cached?.Achievements?.Count <= 0 || cached == null || cached.ExcludedByUser ||
                !string.Equals(cached.ProviderKey, "Local", StringComparison.OrdinalIgnoreCase)) return false;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var found = false;
            try
            {
                var plugin = PlayniteAchievementsPlugin.Instance;
                found = plugin != null && new SantodanStartupBaselineStore(plugin.GetPluginUserDataPath())
                    .HasExistingAchievementFile(game.Id, cached.AppId, cached.LastUpdatedUtc,
                        GetSantodanPollingConfiguration(game), DateTime.UtcNow, sessionStartUtc);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SantodanPolling] Known startup file unavailable; using discovery.");
            }
            PollingDebugLog.WriteExternal("game=" + game.Id + " startupKnownFile=" + found +
                " setupCheckMs=" + timer.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            return found;
        }

        private void SaveSantodanStartupBaseline(Game game, GameAchievementData data, SantodanStartupCapture capture)
        {
            if (!StartupBaselineEnabled || capture == null || data?.Achievements?.Count <= 0 ||
                data == null || !data.HasAchievements) return;
            try
            {
                var plugin = PlayniteAchievementsPlugin.Instance;
                var cached = plugin?.CacheManager?.LoadGameData(game.Id.ToString());
                if (cached?.Achievements?.Count > 0 && !cached.ExcludedByUser &&
                    cached.AppId == data.AppId && cached.LastUpdatedUtc != capture.PreviousCacheUpdatedUtc &&
                    string.Equals(cached.ProviderKey, "Local", StringComparison.OrdinalIgnoreCase) &&
                    TryGetSantodanPollingSourceFingerprint(game, out var after, capture.Directories) &&
                    string.Equals(after, capture.Fingerprint, StringComparison.Ordinal))
                {
                    new SantodanStartupBaselineStore(plugin.GetPluginUserDataPath()).Save(
                        game.Id, cached.AppId, cached.LastUpdatedUtc, capture.Fingerprint, DateTime.UtcNow,
                        capture.Configuration, capture.AchievementFilePath);
                    PollingDebugLog.WriteExternal("game=" + game.Id + " startupBaselineRecorded=True");
                }
            }
            catch (Exception ex)
            {
                // An optional validation receipt must never fail the actual refresh.
                _logger?.Debug(ex, "[SantodanPolling] Could not save startup baseline validation.");
            }
        }
    }
}
