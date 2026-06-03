using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace PlayniteAchievements.Providers.Xbox
{
    internal static class XboxTitleIdResolver
    {
        private const string ConfigFileName = "MicrosoftGame.config";

        internal static bool TryResolveFromGameInstall(Game game, ILogger logger, out string titleId)
        {
            titleId = null;

            foreach (var configPath in GetCandidateConfigPaths(game))
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                if (TryResolveFromConfig(configPath, logger, out titleId))
                {
                    logger?.Debug($"[XboxAch] Resolved title ID from local MicrosoftGame.config: {configPath}");
                    return true;
                }
            }

            return false;
        }

        internal static bool TryNormalizeTitleId(string value, out string titleId)
        {
            titleId = null;

            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }

            if (trimmed.Length == 8 && trimmed.All(Uri.IsHexDigit) &&
                uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexTitleId))
            {
                titleId = hexTitleId.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (trimmed.All(char.IsDigit))
            {
                titleId = trimmed;
                return true;
            }

            return false;
        }

        private static IEnumerable<string> GetCandidateConfigPaths(Game game)
        {
            var installDirectory = game?.InstallDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                yield break;
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootConfigPath = Path.Combine(installDirectory, ConfigFileName);
            if (seenPaths.Add(rootConfigPath))
            {
                yield return rootConfigPath;
            }

            var contentConfigPath = Path.Combine(installDirectory, "Content", ConfigFileName);
            if (seenPaths.Add(contentConfigPath))
            {
                yield return contentConfigPath;
            }
        }

        private static bool TryResolveFromConfig(string configPath, ILogger logger, out string titleId)
        {
            titleId = null;

            try
            {
                var document = XDocument.Load(configPath);
                var titleIdElement = document
                    .Descendants()
                    .FirstOrDefault(element => string.Equals(element.Name.LocalName, "TitleId", StringComparison.OrdinalIgnoreCase));

                if (titleIdElement == null || !TryNormalizeTitleId(titleIdElement.Value, out titleId))
                {
                    logger?.Debug($"[XboxAch] MicrosoftGame.config present but invalid title ID: {configPath}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[XboxAch] MicrosoftGame.config present but could not be read: {configPath}");
                return false;
            }
        }
    }
}
