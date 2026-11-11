using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Providers.Local
{
    // Definitions only: never persist player unlocks or custom per-game schema rows here.
    internal sealed class LocalOfflineSchemaCache
    {
        private static readonly object FileLock = new object();
        private static readonly JsonSerializerSettings Serialization = new JsonSerializerSettings
        {
            ContractResolver = new SchemaContractResolver()
        };
        private readonly string _directory;

        internal LocalOfflineSchemaCache(string userDataPath)
        {
            _directory = Path.Combine(userDataPath, "local_schema_cache");
        }

        internal SchemaAndPercentages Load(int appId, string language, LocalSteamSchemaPreference preference)
        {
            lock (FileLock)
            {
                var path = GetPath(appId, language, preference);
                if (!File.Exists(path)) return null;
                try
                {
                    var entry = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(path), Serialization);
                    return entry?.Version == 1 && entry.AppId == appId &&
                        string.Equals(entry.Language, language, StringComparison.OrdinalIgnoreCase) &&
                        entry.Preference == preference && IsUsable(entry.Schema) ? entry.Schema : null;
                }
                catch (JsonException) { return null; }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        internal int LoadResolvedAppId(Guid gameId, string name, int localAppId)
        {
            lock (FileLock)
            {
                var path = Path.Combine(_directory, "identity_" + gameId.ToString("N") + "_" + localAppId + ".json");
                if (!File.Exists(path)) return 0;
                var entry = JsonConvert.DeserializeObject<ResolvedIdentity>(File.ReadAllText(path));
                return entry?.GameId == gameId && entry.LocalAppId == localAppId &&
                    string.Equals(entry.Name, name, StringComparison.Ordinal) ? Math.Max(0, entry.SteamAppId) : 0;
            }
        }

        internal void SaveResolvedAppId(Guid gameId, string name, int localAppId, int steamAppId)
        {
            if (gameId == Guid.Empty || steamAppId <= 0) return;
            lock (FileLock)
            {
                Directory.CreateDirectory(_directory);
                WriteAtomically(Path.Combine(_directory, "identity_" + gameId.ToString("N") + "_" + localAppId + ".json"),
                    JsonConvert.SerializeObject(new ResolvedIdentity
                    {
                        GameId = gameId, Name = name, LocalAppId = localAppId, SteamAppId = steamAppId
                    }));
            }
        }

        internal void Save(int appId, string language, LocalSteamSchemaPreference preference, SchemaAndPercentages schema)
        {
            if (appId <= 0 || !IsUsable(schema)) return;
            var json = JsonConvert.SerializeObject(new Entry
            {
                Version = 1, AppId = appId, Language = language, Preference = preference, Schema = schema
            }, Serialization);
            lock (FileLock)
            {
                Directory.CreateDirectory(_directory);
                var path = GetPath(appId, language, preference);
                WriteAtomically(path, json);
            }
        }

        private static void WriteAtomically(string path, string json)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal)) return;
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private string GetPath(int appId, string language, LocalSteamSchemaPreference preference)
        {
            using (var hash = SHA256.Create())
            {
                var key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes((language ?? "").ToLowerInvariant())))
                    .Replace("-", "");
                return Path.Combine(_directory, appId + "_" + (int)preference + "_" + key + ".json");
            }
        }

        private static bool IsUsable(SchemaAndPercentages schema)
        {
            return schema?.Achievements?.Count > 0 &&
                schema.Achievements.All(row => row != null && !string.IsNullOrWhiteSpace(row.Name)) &&
                schema.Achievements.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == schema.Achievements.Count;
        }

        private sealed class Entry
        {
            public int Version { get; set; }
            public int AppId { get; set; }
            public string Language { get; set; }
            public LocalSteamSchemaPreference Preference { get; set; }
            public SchemaAndPercentages Schema { get; set; }
        }

        private sealed class ResolvedIdentity
        {
            public Guid GameId { get; set; }
            public string Name { get; set; }
            public int LocalAppId { get; set; }
            public int SteamAppId { get; set; }
        }

        // The API model's DataContract omits internal group/progress fields. A disk
        // snapshot must retain these too, rather than reproduce the API wire format.
        private sealed class SchemaContractResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
                => base.CreateProperties(type, MemberSerialization.OptOut);
        }
    }
}
