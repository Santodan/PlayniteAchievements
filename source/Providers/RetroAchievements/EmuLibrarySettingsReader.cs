using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal static class EmuLibrarySettingsReader
    {
        private const string SettingsFileName = "config.json";

        public static bool TryResolveSourceRoot(
            string extensionsDataPath,
            string playniteApplicationPath,
            Guid mappingId,
            out string sourceRoot)
        {
            sourceRoot = null;

            if (mappingId == Guid.Empty || string.IsNullOrWhiteSpace(extensionsDataPath))
            {
                return false;
            }

            var settingsPath = Path.Combine(
                extensionsDataPath,
                EmuLibraryGameIdDecoder.EmuLibraryPluginId.ToString(),
                SettingsFileName);

            if (!File.Exists(settingsPath))
            {
                return false;
            }

            try
            {
                var settingsJson = File.ReadAllText(settingsPath, Encoding.UTF8);
                var settings = JsonConvert.DeserializeObject<EmuLibrarySettingsSnapshot>(settingsJson);
                var mapping = settings?.Mappings?.FirstOrDefault(m => m != null && m.MappingId == mappingId);

                var normalizedSource = NormalizeSourcePath(mapping?.SourcePath, playniteApplicationPath);
                if (string.IsNullOrWhiteSpace(normalizedSource))
                {
                    return false;
                }

                sourceRoot = normalizedSource;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeSourcePath(string sourcePath, string playniteApplicationPath)
        {
            var normalized = (sourcePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(playniteApplicationPath) &&
                normalized.IndexOf(ExpandableVariables.PlayniteDirectory, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                normalized = ReplaceInsensitive(normalized, ExpandableVariables.PlayniteDirectory, playniteApplicationPath);
            }

            return normalized;
        }

        private static string ReplaceInsensitive(string input, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
            {
                return input;
            }

            var idx = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return input;
            }

            var sb = new StringBuilder(input.Length);
            var start = 0;
            while (idx >= 0)
            {
                sb.Append(input.Substring(start, idx - start));
                sb.Append(newValue ?? string.Empty);
                start = idx + oldValue.Length;
                idx = input.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
            }

            sb.Append(input.Substring(start));
            return sb.ToString();
        }

        private sealed class EmuLibrarySettingsSnapshot
        {
            public EmuLibraryMappingSnapshot[] Mappings { get; set; }
        }

        private sealed class EmuLibraryMappingSnapshot
        {
            public Guid MappingId { get; set; }
            public string SourcePath { get; set; }
        }
    }
}