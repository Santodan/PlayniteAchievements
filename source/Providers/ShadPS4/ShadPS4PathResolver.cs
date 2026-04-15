using System;
using System.IO;

namespace PlayniteAchievements.Providers.ShadPS4
{
    internal static class ShadPS4PathResolver
    {
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
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized) || LooksLikeLegacyGameDataPath(normalized))
            {
                return false;
            }

            var leafName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(leafName, "shadPS4", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                return Directory.Exists(Path.Combine(normalized, "home")) ||
                       Directory.Exists(Path.Combine(normalized, "trophy"));
            }
            catch
            {
                return false;
            }
        }

        public static string ResolveConfiguredLegacyGameDataPath(string configuredPath)
        {
            var normalized = NormalizePath(configuredPath);
            if (!LooksLikeLegacyGameDataPath(normalized) || !Directory.Exists(normalized))
            {
                return null;
            }

            return normalized;
        }

        public static string ResolveConfiguredAppDataPath(string configuredPath)
        {
            var normalized = NormalizePath(configuredPath);
            if (!LooksLikeAppDataRootPath(normalized) || !Directory.Exists(normalized))
            {
                return null;
            }

            return normalized;
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
                        var dirs = Directory.GetDirectories(homePath);
                        if (dirs.Length > 0)
                        {
                            return Path.GetFileName(dirs[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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

        public static string GetTrophyBasePath(string appDataPath)
        {
            return string.IsNullOrWhiteSpace(appDataPath)
                ? null
                : Path.Combine(appDataPath, "trophy");
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
