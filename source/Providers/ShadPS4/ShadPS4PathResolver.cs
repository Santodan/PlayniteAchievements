using System;
using System.IO;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.ShadPS4
{
    internal static class ShadPS4PathResolver
    {
        private static string TrimTrailingSeparators(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? path
                : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsExistingDirectory(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }

        private static bool HasLegacyGameDataMarker(string path)
        {
            return IsExistingDirectory(path) &&
                   Directory.Exists(Path.Combine(path, "game_data"));
        }

        private static bool HasAppDataMarkers(string path)
        {
            return IsExistingDirectory(path) &&
                   (Directory.Exists(Path.Combine(path, "home")) ||
                    Directory.Exists(Path.Combine(path, "trophy")));
        }

        private static string[] GetRootCandidates(string configuredPath)
        {
            var normalizedPath = NormalizePath(configuredPath);
            if (!IsExistingDirectory(normalizedPath))
            {
                return Array.Empty<string>();
            }

            var trimmedPath = TrimTrailingSeparators(normalizedPath);
            var parentPath = Path.GetDirectoryName(trimmedPath);
            var grandParentPath = string.IsNullOrWhiteSpace(parentPath)
                ? null
                : Path.GetDirectoryName(TrimTrailingSeparators(parentPath));

            return new[]
            {
                trimmedPath,
                Path.Combine(trimmedPath, "user"),
                parentPath,
                string.IsNullOrWhiteSpace(parentPath) ? null : Path.Combine(parentPath, "user"),
                grandParentPath,
                string.IsNullOrWhiteSpace(grandParentPath) ? null : Path.Combine(grandParentPath, "user")
            };
        }

        private static string ResolveRootPath(string configuredPath, Func<string, bool> match)
        {
            foreach (var candidatePath in GetRootCandidates(configuredPath))
            {
                var candidate = NormalizePath(candidatePath);
                if (!IsExistingDirectory(candidate))
                {
                    continue;
                }

                if (match(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        public static string GetDefaultSettingsPath()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return string.IsNullOrWhiteSpace(appData)
                    ? string.Empty
                    : Path.Combine(appData, "shadPS4");
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim();
            try
            {
                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return trimmed;
            }
        }

        public static bool LooksLikeLegacyGameDataPath(string path)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return string.Equals(
                Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "game_data",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool LooksLikeAppDataRootPath(string path)
        {
            return !string.IsNullOrWhiteSpace(ResolveConfiguredAppDataPath(path));
        }

        public static string ResolveConfiguredRootPath(string configuredPath)
        {
            return ResolveRootPath(
                configuredPath,
                candidate => HasAppDataMarkers(candidate) || HasLegacyGameDataMarker(candidate));
        }

        public static string ResolveConfiguredLegacyGameDataPath(string configuredPath)
        {
            var normalized = NormalizePath(configuredPath);
            if (LooksLikeLegacyGameDataPath(normalized) && IsExistingDirectory(normalized))
            {
                return normalized;
            }

            foreach (var candidatePath in GetRootCandidates(normalized))
            {
                var candidate = NormalizePath(candidatePath);
                if (!IsExistingDirectory(candidate))
                {
                    continue;
                }

                var gameDataPath = Path.Combine(candidate, "game_data");
                if (IsExistingDirectory(gameDataPath))
                {
                    return gameDataPath;
                }
            }

            return null;
        }

        public static string ResolveConfiguredAppDataPath(string configuredPath)
        {
            return ResolveRootPath(configuredPath, HasAppDataMarkers);
        }

        public static string DiscoverUserId(string appDataPath)
        {
            if (!string.IsNullOrWhiteSpace(appDataPath))
            {
                var homePath = Path.Combine(appDataPath, "home");
                if (Directory.Exists(homePath))
                {
                    try
                    {
                        var userDirectories = Directory.GetDirectories(homePath);
                        Array.Sort(userDirectories, StringComparer.OrdinalIgnoreCase);
                        if (userDirectories.Length > 0)
                        {
                            return Path.GetFileName(TrimTrailingSeparators(userDirectories[0]));
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return "1000";
        }

        public static string GetTrophyUserPath(string appDataPath, string userId = null)
        {
            if (string.IsNullOrWhiteSpace(appDataPath))
            {
                return null;
            }

            var resolvedUserId = string.IsNullOrWhiteSpace(userId)
                ? DiscoverUserId(appDataPath)
                : userId;

            return Path.Combine(appDataPath, "home", resolvedUserId, "trophy");
        }

        /// <summary>
        /// The AppData root that owns a per-user trophy file
        /// (&lt;root&gt;\home\&lt;userId&gt;\trophy\&lt;npcommid&gt;.xml), or null for any other layout.
        /// </summary>
        public static string GetAppDataRootFromUserTrophyXml(string userTrophyXmlPath)
        {
            try
            {
                var trophyDir = string.IsNullOrWhiteSpace(userTrophyXmlPath)
                    ? null
                    : new FileInfo(userTrophyXmlPath).Directory;
                var homeDir = trophyDir?.Parent?.Parent;
                if (trophyDir == null ||
                    homeDir == null ||
                    !string.Equals(trophyDir.Name, "trophy", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(homeDir.Name, "home", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return homeDir.Parent?.FullName;
            }
            catch
            {
                return null;
            }
        }

        public static string GetTrophyBasePath(string appDataPath)
        {
            return string.IsNullOrWhiteSpace(appDataPath)
                ? null
                : Path.Combine(appDataPath, "trophy");
        }

        /// <summary>
        /// Settings-row check: the path resolves to a legacy game_data folder or to an AppData
        /// root that already holds trophy data.
        /// </summary>
        public static ProviderPathValidation ValidateConfiguredPath(string configuredPath)
        {
            return !string.IsNullOrWhiteSpace(ResolveConfiguredLegacyGameDataPath(configuredPath)) ||
                   HasConfiguredAppDataTrophyData(configuredPath)
                ? ProviderPathValidation.Valid
                : ProviderPathValidation.Invalid("LOCPlayAch_InvalidPath");
        }

        public static bool HasConfiguredAppDataTrophyData(string configuredPath)
        {
            var appDataPath = ResolveConfiguredAppDataPath(configuredPath);
            if (string.IsNullOrWhiteSpace(appDataPath))
            {
                return false;
            }

            var userTrophyPath = GetTrophyUserPath(appDataPath);
            if (!string.IsNullOrWhiteSpace(userTrophyPath) && Directory.Exists(userTrophyPath))
            {
                try
                {
                    if (Directory.GetFiles(userTrophyPath, "*.xml").Length > 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            var trophyBasePath = GetTrophyBasePath(appDataPath);
            if (!string.IsNullOrWhiteSpace(trophyBasePath) && Directory.Exists(trophyBasePath))
            {
                try
                {
                    if (Directory.GetDirectories(trophyBasePath).Length > 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }
    }
}
