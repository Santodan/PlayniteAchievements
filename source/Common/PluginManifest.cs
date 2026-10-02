using System;
using System.IO;
using System.Reflection;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Values read from the extension.yaml manifest shipped next to the plugin assembly.
    /// </summary>
    internal static class PluginManifest
    {
        private static readonly Lazy<string> CachedVersion = new Lazy<string>(ReadVersionSafe);

        /// <summary>
        /// The manifest <c>Version</c>, or null when the manifest is missing or unreadable. The
        /// assembly itself carries no meaningful version, so extension.yaml is the only source that
        /// matches the number users see in Playnite's add-on list.
        /// </summary>
        public static string Version => CachedVersion.Value;

        private static string ReadVersionSafe()
        {
            try
            {
                return ReadVersion();
            }
            catch
            {
                return null;
            }
        }

        private static string ReadVersion()
        {
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return null;
            }

            var manifestPath = Path.Combine(assemblyDirectory, "extension.yaml");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed.Substring("Version:".Length).Trim().Trim('"', '\'');
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }

            return null;
        }
    }
}
