using System;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSavesProvider
    {
        private readonly SemaphoreSlim _schemaConnectivityLock = new SemaphoreSlim(1, 1);
        private DateTime _schemaConnectivityCheckedUtc;
        private bool _schemaNetworkAvailable;

        private LocalOfflineSchemaCache GetOfflineSchemaCache()
        {
            var path = PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath();
            return string.IsNullOrWhiteSpace(path) ? null : new LocalOfflineSchemaCache(path);
        }

        private async Task<bool> CanUseLocalSchemaNetworkAsync()
        {
            if (ProviderRegistry.Settings<LocalSettings>()?.SkipUnavailableSchemaNetwork == false) return true;
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            await _schemaConnectivityLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (DateTime.UtcNow - _schemaConnectivityCheckedUtc < TimeSpan.FromSeconds(30))
                    return _schemaNetworkAvailable;
                try
                {
                    using (var client = new HttpClient())
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    using (var request = new HttpRequestMessage(HttpMethod.Head, "https://steamcommunity.com/"))
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        _schemaNetworkAvailable = response.IsSuccessStatusCode;
                    }
                }
                catch (Exception)
                {
                    _schemaNetworkAvailable = false;
                }
                _schemaConnectivityCheckedUtc = DateTime.UtcNow;
                return _schemaNetworkAvailable;
            }
            finally { _schemaConnectivityLock.Release(); }
        }

        private sealed class LocalSchemaResult
        {
            internal SchemaAndPercentages Schema;
            internal bool FromOfflineCache;
        }

        private async Task<int> ResolveLocalSchemaAppIdAsync(Game game, int localAppId, bool allowNetwork)
        {
            var useCache = ProviderRegistry.Settings<LocalSettings>()?.UseOfflineCachedSchemas != false;
            if (allowNetwork)
            {
                var resolved = await TryResolveSteamAppIdByGameNameAsync(game.Name).ConfigureAwait(false);
                if (resolved > 0)
                {
                    if (useCache)
                    {
                        try { GetOfflineSchemaCache()?.SaveResolvedAppId(game.Id, game.Name, localAppId, resolved); }
                        catch (Exception ex) { Log($"OFFLINE SCHEMA IDENTITY SAVE FAILED: {ex.Message}"); }
                    }
                    return resolved;
                }
            }
            if (useCache)
            {
                try { return GetOfflineSchemaCache()?.LoadResolvedAppId(game.Id, game.Name, localAppId) ?? 0; }
                catch (Exception ex) { Log($"OFFLINE SCHEMA IDENTITY READ FAILED: {ex.Message}"); }
            }
            return 0;
        }

        private async Task<LocalSchemaResult> GetLocalSchemaWithOfflineFallbackAsync(Game game, int appId, bool allowNetwork)
        {
            var useCache = ProviderRegistry.Settings<LocalSettings>()?.UseOfflineCachedSchemas != false;
            var preference = GetSteamSchemaPreference();
            var language = GetSteamLanguage();
            SchemaAndPercentages schema = null;
            if (allowNetwork)
            {
                try { schema = await TryGetSteamSchemaAsync(appId).ConfigureAwait(false); }
                catch (Exception ex) { Log($"SCHEMA ONLINE LOOKUP FAILED: appId={appId} {ex.Message}"); }
                if (schema?.Achievements?.Count > 0)
                {
                    if (useCache)
                    {
                        try { GetOfflineSchemaCache()?.Save(appId, language, preference, schema); }
                        catch (Exception ex) { Log($"OFFLINE SCHEMA SAVE FAILED: appId={appId} {ex.Message}"); }
                    }
                    return new LocalSchemaResult { Schema = schema };
                }
            }

            if (useCache)
            {
                try { schema = GetOfflineSchemaCache()?.Load(appId, language, preference); }
                catch (Exception ex) { Log($"OFFLINE SCHEMA READ FAILED: appId={appId} {ex.Message}"); }
                if (schema?.Achievements?.Count > 0)
                {
                    UseCachedLocalSchemaIcons(game, schema);
                    Log($"OFFLINE SCHEMA: game='{game?.Name}' appId={appId} language={language} count={schema.Achievements.Count}");
                    return new LocalSchemaResult { Schema = schema, FromOfflineCache = true };
                }
            }

            // Disk schemas remain usable even on a first run with no online cache.
            schema = TryGetInstallSchema(appId) ?? TryGetSteamAppCacheSchemaAndPercentages(appId);
            return new LocalSchemaResult { Schema = schema, FromOfflineCache = !allowNetwork };
        }

        private void UseCachedLocalSchemaIcons(Game game, SchemaAndPercentages schema)
        {
            var images = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (images == null || game == null) return;
            var stems = AchievementIconCachePathBuilder.BuildFileStems(schema.Achievements.Select(row => row.Name));
            foreach (var row in schema.Achievements)
            {
                if (!stems.TryGetValue(row.Name, out var stem)) continue;
                row.Icon = File.Exists(row.Icon) ? row.Icon : images.FindExistingAchievementIconCachePath(game.Id.ToString(), stem, AchievementIconVariant.Unlocked);
                row.IconGray = File.Exists(row.IconGray) ? row.IconGray : images.FindExistingAchievementIconCachePath(game.Id.ToString(), stem, AchievementIconVariant.Locked);
            }
        }

        private void UseCachedLocalResultIcons(GameAchievementData data)
        {
            if (data?.Achievements == null || !data.PlayniteGameId.HasValue) return;
            var images = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            var gameId = data.PlayniteGameId.Value.ToString("D");
            var stems = AchievementIconCachePathBuilder.BuildFileStems(data.Achievements.Select(row => row.ApiName));
            foreach (var row in data.Achievements)
            {
                if (string.IsNullOrWhiteSpace(row.ApiName) || !stems.TryGetValue(row.ApiName, out var stem)) continue;
                // Metadata recovery can restore remote URLs after schema correlation. Resolve
                // actual files once more so an offline refresh never schedules icon downloads.
                if (IsRemoteSchemaIcon(row.UnlockedIconPath))
                    row.UnlockedIconPath = images?.FindExistingAchievementIconCachePath(gameId, stem, AchievementIconVariant.Unlocked);
                if (IsRemoteSchemaIcon(row.LockedIconPath))
                    row.LockedIconPath = images?.FindExistingAchievementIconCachePath(gameId, stem, AchievementIconVariant.Locked);
            }
        }

        private static bool IsRemoteSchemaIcon(string path)
            => Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
