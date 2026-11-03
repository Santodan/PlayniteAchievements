using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    internal static class SantodanPollingConfiguration
    {
        // Notification templates, import snapshots and other games' overrides are not
        // source configuration. Serializing them on every check allocated hundreds of KB.
        public static string Build(LocalSettings settings, Guid gameId, string extraPaths,
            string excludedPaths, string installDirectory, string name, bool preserveUnlocks)
        {
            return JsonConvert.SerializeObject(new
            {
                settings?.IsEnabled,
                settings?.ExtraLocalPaths,
                settings?.ExcludedLocalPaths,
                settings?.SteamUserdataPath,
                settings?.SteamAppCacheUserId,
                settings?.SteamSchemaPreference,
                settings?.UseSteamHuntersForCategories,
                settings?.UseCustomPollingMetadataCache,
                SteamAppId = Read(settings?.SteamAppIdOverrides, gameId),
                LumaAppId = Read(settings?.LumaPlayAppIdOverrides, gameId),
                LumaIni = Read(settings?.LumaPlayIniPathOverrides, gameId),
                EpicSchema = Read(settings?.EpicSchemaPathOverrides, gameId),
                EpicSave = Read(settings?.EpicSavePathOverrides, gameId),
                EpicProduct = Read(settings?.EpicProductIdOverrides, gameId),
                Folder = Read(settings?.LocalFolderOverrides, gameId),
                CustomSchema = Read(settings?.CustomSchemaPathOverrides, gameId),
                CustomSchemaEnabled = Read(settings?.CustomSchemaEnabledOverrides, gameId),
                SteamUser = Read(settings?.SteamAppCacheUserOverrides, gameId),
                extraPaths, excludedPaths, installDirectory, name, preserveUnlocks
            });
        }

        private static KeyValuePair<bool, T> Read<T>(Dictionary<Guid, T> values, Guid gameId)
        {
            T value = default;
            var present = values != null && values.TryGetValue(gameId, out value);
            return new KeyValuePair<bool, T>(present, value);
        }
    }
}
