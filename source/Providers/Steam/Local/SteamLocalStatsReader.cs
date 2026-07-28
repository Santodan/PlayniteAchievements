using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Steam.Local
{
    internal sealed class SteamLocalUnlockReadResult
    {
        public bool Success { get; set; }

        public Dictionary<string, DateTime?> UnlockByApiName { get; set; } =
            new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class SteamLocalStatsReader
    {
        private sealed class SchemaCacheEntry
        {
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public Dictionary<string, string> ApiNamesByBit { get; set; }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, SchemaCacheEntry> _schemas =
            new Dictionary<string, SchemaCacheEntry>(StringComparer.OrdinalIgnoreCase);

        public SteamLocalUnlockReadResult TryRead(string statsPath, string schemaPath)
        {
            var failed = new SteamLocalUnlockReadResult();
            if (!TryGetSchemaMap(schemaPath, out var apiNamesByBit) ||
                !SteamBinaryKeyValuesReader.TryRead(statsPath, out var statsRoot))
            {
                return failed;
            }

            var cache = FindDescendant(statsRoot, "cache");
            if (cache == null)
            {
                return failed;
            }

            var result = new SteamLocalUnlockReadResult { Success = true };
            foreach (var group in cache.Children)
            {
                if (!int.TryParse(group?.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupIndex))
                {
                    continue;
                }

                var data = group.Child("data")?.IntegerValue;
                if (!data.HasValue)
                {
                    continue;
                }

                var bits = unchecked((uint)data.Value);
                var achievementTimes = group.Child("AchievementTimes");
                for (var bit = 0; bit < 32; bit++)
                {
                    if ((bits & (1U << bit)) == 0)
                    {
                        continue;
                    }

                    if (!apiNamesByBit.TryGetValue(BitKey(groupIndex, bit), out var apiName))
                    {
                        continue;
                    }

                    DateTime? unlockTime = null;
                    var timestamp = achievementTimes?.Child(bit.ToString(CultureInfo.InvariantCulture))?.IntegerValue;
                    if (timestamp.GetValueOrDefault() > 0)
                    {
                        try
                        {
                            unlockTime = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).UtcDateTime;
                        }
                        catch
                        {
                        }
                    }

                    result.UnlockByApiName[apiName] = unlockTime;
                }
            }

            return result;
        }

        private bool TryGetSchemaMap(string schemaPath, out Dictionary<string, string> map)
        {
            map = null;
            if (string.IsNullOrWhiteSpace(schemaPath) || !File.Exists(schemaPath))
            {
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(schemaPath);
            }
            catch
            {
                return false;
            }

            lock (_sync)
            {
                if (_schemas.TryGetValue(schemaPath, out var cached) &&
                    cached.Length == info.Length &&
                    cached.LastWriteUtc == info.LastWriteTimeUtc)
                {
                    map = cached.ApiNamesByBit;
                    return map != null && map.Count > 0;
                }
            }

            if (!SteamBinaryKeyValuesReader.TryRead(schemaPath, out var schemaRoot))
            {
                return false;
            }

            var stats = FindDescendant(schemaRoot, "stats");
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (stats != null)
            {
                foreach (var group in stats.Children)
                {
                    if (!int.TryParse(group?.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupIndex))
                    {
                        continue;
                    }

                    var bits = group.Child("bits");
                    if (bits == null)
                    {
                        continue;
                    }

                    foreach (var bitNode in bits.Children)
                    {
                        if (!int.TryParse(bitNode?.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitIndex))
                        {
                            continue;
                        }

                        var apiName = bitNode.Child("name")?.StringValue?.Trim();
                        if (!string.IsNullOrWhiteSpace(apiName))
                        {
                            parsed[BitKey(groupIndex, bitIndex)] = apiName;
                        }
                    }
                }
            }

            if (parsed.Count == 0)
            {
                return false;
            }

            lock (_sync)
            {
                _schemas[schemaPath] = new SchemaCacheEntry
                {
                    Length = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc,
                    ApiNamesByBit = parsed
                };
            }

            map = parsed;
            return true;
        }

        private static SteamKvNode FindDescendant(SteamKvNode node, string name)
        {
            if (node == null)
            {
                return null;
            }

            var stack = new Stack<SteamKvNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (string.Equals(current?.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                if (current?.Children == null)
                {
                    continue;
                }

                for (var i = current.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(current.Children[i]);
                }
            }

            return null;
        }

        private static string BitKey(int group, int bit)
        {
            return group.ToString(CultureInfo.InvariantCulture) + ":" +
                   bit.ToString(CultureInfo.InvariantCulture);
        }
    }
}
