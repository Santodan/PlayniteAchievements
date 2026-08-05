using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// Reads the dev_hdd0 location from RPCS3's vfs.yml so the plugin scans the
    /// same virtual file system the emulator writes to. RPCS3 lets users relocate
    /// dev_hdd0 (default "/dev_hdd0/: $(EmulatorDir)dev_hdd0/"); trophy data lives
    /// under that mapping, not necessarily under the executable directory.
    /// </summary>
    internal static class Rpcs3VfsYmlReader
    {
        private const string EmulatorDirKey = "$(EmulatorDir)";
        private const string DevHdd0Key = "/dev_hdd0/";

        internal static IEnumerable<string> EnumerateVfsYmlPaths(string configurationRoot)
        {
            if (string.IsNullOrWhiteSpace(configurationRoot))
            {
                yield break;
            }

            yield return Path.Combine(configurationRoot, "config", "vfs.yml");
            yield return Path.Combine(configurationRoot, "vfs.yml");
        }

        /// <summary>
        /// Resolves the dev_hdd0 root for the given RPCS3 configuration root: the
        /// vfs.yml mapping when present, otherwise the default {root}/dev_hdd0 layout —
        /// the same precedence RPCS3 applies.
        /// </summary>
        public static string ResolveDevHdd0Root(string configurationRoot, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(configurationRoot))
            {
                return null;
            }

            return ReadDevHdd0Root(configurationRoot, logger) ?? Path.Combine(configurationRoot, "dev_hdd0");
        }

        /// <summary>
        /// Reads the dev_hdd0 root from the first vfs.yml found under the given
        /// emulator root. Returns null when no vfs.yml exists or it carries no
        /// usable /dev_hdd0/ mapping.
        /// </summary>
        internal static string ReadDevHdd0Root(string configurationRoot, ILogger logger = null)
        {
            foreach (var vfsYmlPath in EnumerateVfsYmlPaths(configurationRoot))
            {
                if (!File.Exists(vfsYmlPath))
                {
                    continue;
                }

                var map = ReadTopLevelMap(vfsYmlPath, logger);
                if (!map.TryGetValue(DevHdd0Key, out var devHdd0Value) ||
                    string.IsNullOrWhiteSpace(devHdd0Value))
                {
                    return null;
                }

                // A non-empty $(EmulatorDir) entry overrides the executable
                // directory as the base for $(EmulatorDir)-relative mappings.
                var emulatorDir = configurationRoot;
                if (map.TryGetValue(EmulatorDirKey, out var emulatorDirValue) &&
                    !string.IsNullOrWhiteSpace(emulatorDirValue))
                {
                    emulatorDir = emulatorDirValue;
                }

                return ExpandMapping(devHdd0Value, emulatorDir);
            }

            return null;
        }

        /// <summary>
        /// Expands the $(EmulatorDir) token and normalizes the result to a full
        /// path without trailing separators.
        /// </summary>
        internal static string ExpandMapping(string value, string emulatorDir)
        {
            var expanded = value.Trim();
            if (expanded.StartsWith(EmulatorDirKey, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = expanded.Substring(EmulatorDirKey.Length)
                    .TrimStart('/', '\\');
                expanded = string.IsNullOrWhiteSpace(remainder)
                    ? emulatorDir
                    : Path.Combine(emulatorDir ?? string.Empty, remainder);
            }
            else if (!Path.IsPathRooted(expanded) && !string.IsNullOrWhiteSpace(emulatorDir))
            {
                expanded = Path.Combine(emulatorDir, expanded);
            }

            try
            {
                expanded = Path.GetFullPath(expanded);
            }
            catch
            {
                // Keep the unnormalized expansion; Directory.Exists gates usage downstream.
            }

            return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Reads the unindented top-level key/value lines from a vfs.yml. Indented
        /// lines (e.g. the Devices section) are skipped.
        /// </summary>
        private static IReadOnlyDictionary<string, string> ReadTopLevelMap(string vfsYmlPath, ILogger logger)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var rawLine in File.ReadLines(vfsYmlPath))
                {
                    if (rawLine.Length == 0 || char.IsWhiteSpace(rawLine[0]))
                    {
                        continue;
                    }

                    var line = Rpcs3GamesYmlReader.StripComment(rawLine).Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = Rpcs3GamesYmlReader.Unquote(line.Substring(0, separatorIndex).Trim());
                    var value = Rpcs3GamesYmlReader.Unquote(
                        Rpcs3GamesYmlReader.StripYamlStringTag(line.Substring(separatorIndex + 1).Trim()));

                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        map[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[RPCS3] Failed to parse vfs.yml at '{vfsYmlPath}'");
            }

            return map;
        }
    }
}
