using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Steam.Local
{
    /// <summary>
    /// Current progress toward a still-locked achievement, read from local stats: the numerator
    /// against its target, matching the community page's "n / m" bar (min-offset applied).
    /// </summary>
    internal readonly struct SteamLocalProgress
    {
        public SteamLocalProgress(int num, int denom)
        {
            Num = num;
            Denom = denom;
        }

        public int Num { get; }

        public int Denom { get; }
    }

    internal sealed class SteamLocalUnlockReadResult
    {
        public bool Success { get; set; }

        public Dictionary<string, DateTime?> UnlockByApiName { get; set; } =
            new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-achievement progress for locked achievements the schema defines a progress bar for.
        /// Empty when the game has no progress achievements or no backing stat values were found.
        /// </summary>
        public Dictionary<string, SteamLocalProgress> ProgressByApiName { get; set; } =
            new Dictionary<string, SteamLocalProgress>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class SteamLocalStatsReader
    {
        /// <summary>
        /// One achievement's progress definition parsed from the schema: the stat that drives it
        /// and the bar's bounds. Steam encodes this as
        /// <c>progress { min_val, max_val, value { operation="statvalue", operand1="&lt;STAT&gt;" } }</c>
        /// on the achievement's bit node.
        /// </summary>
        private sealed class ProgressDefinition
        {
            public string ApiName { get; set; }
            public string StatName { get; set; }
            public int MinValue { get; set; }
            public int MaxValue { get; set; }
        }

        private sealed class SchemaCacheEntry
        {
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public Dictionary<string, string> ApiNamesByBit { get; set; }

            /// <summary>Integer-stat id (the cache group key) by stat name, for progress lookups.</summary>
            public Dictionary<string, int> IntStatIdByName { get; set; }

            /// <summary>Progress bar definitions, one per achievement that has one.</summary>
            public List<ProgressDefinition> ProgressDefinitions { get; set; }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, SchemaCacheEntry> _schemas =
            new Dictionary<string, SchemaCacheEntry>(StringComparer.OrdinalIgnoreCase);

        public SteamLocalUnlockReadResult TryRead(string statsPath, string schemaPath)
        {
            var failed = new SteamLocalUnlockReadResult();
            if (!TryGetSchema(schemaPath, out var schema) ||
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
            var intStatValueById = new Dictionary<int, long>();
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

                // Every cache group carries a "data" value: an achievement group's is a bitmask, an
                // integer stat's is the stat value. The two never collide because a bit key only
                // resolves for achievement groups; the value map is only read for stat ids below.
                intStatValueById[groupIndex] = data.Value;

                var bits = unchecked((uint)data.Value);
                var achievementTimes = group.Child("AchievementTimes");
                for (var bit = 0; bit < 32; bit++)
                {
                    if ((bits & (1U << bit)) == 0)
                    {
                        continue;
                    }

                    if (!schema.ApiNamesByBit.TryGetValue(BitKey(groupIndex, bit), out var apiName))
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

            ResolveProgress(schema, intStatValueById, result);
            return result;
        }

        /// <summary>
        /// Fills <see cref="SteamLocalUnlockReadResult.ProgressByApiName"/> for still-locked
        /// achievements whose backing stat has a value. The bar matches the community page: the
        /// numerator is the stat value offset by min and clamped into [0, max-min], the
        /// denominator is max-min. An already-unlocked achievement is skipped (it is done), as is
        /// a single-step bar the notification layer would drop anyway.
        /// </summary>
        private static void ResolveProgress(
            SchemaCacheEntry schema,
            Dictionary<int, long> intStatValueById,
            SteamLocalUnlockReadResult result)
        {
            if (schema.ProgressDefinitions == null || schema.ProgressDefinitions.Count == 0)
            {
                return;
            }

            foreach (var definition in schema.ProgressDefinitions)
            {
                if (result.UnlockByApiName.ContainsKey(definition.ApiName) ||
                    !schema.IntStatIdByName.TryGetValue(definition.StatName, out var statId) ||
                    !intStatValueById.TryGetValue(statId, out var value))
                {
                    continue;
                }

                var denom = definition.MaxValue - definition.MinValue;
                if (denom <= 1)
                {
                    continue;
                }

                var num = (int)Math.Max(0, Math.Min(denom, value - definition.MinValue));
                result.ProgressByApiName[definition.ApiName] = new SteamLocalProgress(num, denom);
            }
        }

        private bool TryGetSchema(string schemaPath, out SchemaCacheEntry schema)
        {
            schema = null;
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
                    schema = cached;
                    return cached.ApiNamesByBit != null && cached.ApiNamesByBit.Count > 0;
                }
            }

            if (!SteamBinaryKeyValuesReader.TryRead(schemaPath, out var schemaRoot))
            {
                return false;
            }

            var stats = FindDescendant(schemaRoot, "stats");
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var intStatIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var progressDefinitions = new List<ProgressDefinition>();
            if (stats != null)
            {
                foreach (var group in stats.Children)
                {
                    if (!int.TryParse(group?.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupIndex))
                    {
                        continue;
                    }

                    // Integer stats (type "1") back the progress bars: map name -> id so the stat
                    // value can be found in the stats file's cache, which is keyed by that id.
                    var statType = group.Child("type")?.StringValue?.Trim();
                    var statName = group.Child("name")?.StringValue?.Trim();
                    if (string.Equals(statType, "1", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(statName))
                    {
                        intStatIdByName[statName] = groupIndex;
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
                        if (string.IsNullOrWhiteSpace(apiName))
                        {
                            continue;
                        }

                        parsed[BitKey(groupIndex, bitIndex)] = apiName;

                        var definition = TryParseProgress(apiName, bitNode.Child("progress"));
                        if (definition != null)
                        {
                            progressDefinitions.Add(definition);
                        }
                    }
                }
            }

            if (parsed.Count == 0)
            {
                return false;
            }

            var entry = new SchemaCacheEntry
            {
                Length = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                ApiNamesByBit = parsed,
                IntStatIdByName = intStatIdByName,
                ProgressDefinitions = progressDefinitions
            };

            lock (_sync)
            {
                _schemas[schemaPath] = entry;
            }

            schema = entry;
            return true;
        }

        /// <summary>
        /// Parses an achievement bit node's <c>progress</c> child into a definition, or null when
        /// it is absent or not a plain stat-value bar (the only form Steam uses for the "n / m"
        /// community bar this reads).
        /// </summary>
        private static ProgressDefinition TryParseProgress(string apiName, SteamKvNode progress)
        {
            var value = progress?.Child("value");
            if (value == null ||
                !string.Equals(value.Child("operation")?.StringValue?.Trim(), "statvalue", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var statName = value.Child("operand1")?.StringValue?.Trim();
            if (string.IsNullOrWhiteSpace(statName) ||
                !TryParseIntString(progress.Child("min_val")?.StringValue, out var min) ||
                !TryParseIntString(progress.Child("max_val")?.StringValue, out var max) ||
                max <= min)
            {
                return null;
            }

            return new ProgressDefinition
            {
                ApiName = apiName,
                StatName = statName,
                MinValue = min,
                MaxValue = max
            };
        }

        private static bool TryParseIntString(string value, out int parsed)
        {
            // Steam stores min_val/max_val as strings, sometimes as floats ("5000.000000").
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                parsed = (int)Math.Round(number);
                return true;
            }

            parsed = 0;
            return false;
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
