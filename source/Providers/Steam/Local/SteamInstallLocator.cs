using Microsoft.Win32;
using Playnite.SDK;
using System;
using System.Globalization;
using System.IO;

namespace PlayniteAchievements.Providers.Steam.Local
{
    internal static class SteamInstallLocator
    {
        public static string ResolveSteamPath(string overridePath, ILogger logger = null)
        {
            var configured = Normalize(overridePath);
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            {
                return configured;
            }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    return Normalize(key?.GetValue("SteamPath") as string);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SteamLocal] Failed to resolve the Steam install path.");
                return null;
            }
        }

        public static string BuildUserGameStatsPath(string steamPath, uint accountId3, int appId)
        {
            return string.IsNullOrWhiteSpace(steamPath) || appId <= 0
                ? null
                : Path.Combine(
                    steamPath,
                    "appcache",
                    "stats",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "UserGameStats_{0}_{1}.bin",
                        accountId3,
                        appId));
        }

        public static string BuildSchemaPath(string steamPath, int appId)
        {
            return string.IsNullOrWhiteSpace(steamPath) || appId <= 0
                ? null
                : Path.Combine(
                    steamPath,
                    "appcache",
                    "stats",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "UserGameStatsSchema_{0}.bin",
                        appId));
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch
            {
                return path.Trim().Trim('"');
            }
        }
    }
}
