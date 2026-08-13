using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// The concrete RPCS3 layout used by one game refresh. The context deliberately
    /// contains one user only: trophy progress is never combined across profiles.
    /// </summary>
    internal sealed class Rpcs3InstallationContext
    {
        public string EmulatorRoot { get; private set; }
        public string ConfigurationRoot { get; private set; }
        public string DevHdd0Root { get; private set; }
        public string UserId { get; private set; }
        public string TrophyFolder { get; private set; }
        public string UserIdSource { get; private set; }

        public string CacheKey => string.Join("|", new[]
        {
            NormalizeForKey(ConfigurationRoot),
            NormalizeForKey(DevHdd0Root),
            UserId ?? string.Empty
        });

        internal static Rpcs3InstallationContext Create(
            string emulatorRoot,
            string requestedUserId,
            string requestedUserSource,
            ILogger logger,
            string configurationRootOverride = null)
        {
            if (string.IsNullOrWhiteSpace(emulatorRoot) || !Directory.Exists(emulatorRoot))
            {
                return null;
            }

            var configurationRoot = ResolveConfigurationRoot(emulatorRoot, configurationRootOverride);
            if (string.IsNullOrWhiteSpace(configurationRoot))
            {
                return null;
            }

            var devHdd0Root = Rpcs3VfsYmlReader.ResolveDevHdd0Root(configurationRoot, logger);
            var userId = string.IsNullOrWhiteSpace(requestedUserId)
                ? ReadPersistedActiveUser(configurationRoot)
                : requestedUserId;
            var userSource = string.IsNullOrWhiteSpace(requestedUserId)
                ? "RPCS3 persisted active user"
                : requestedUserSource ?? "RPCS3 launch action";

            if (!TryNormalizeUserId(userId, out var normalizedUserId))
            {
                logger?.Warn($"[RPCS3] No valid active user could be resolved for configuration '{configurationRoot}'. Trophy progress was not scanned.");
                return null;
            }

            var trophyFolder = Path.Combine(devHdd0Root, "home", normalizedUserId, "trophy");
            if (!Directory.Exists(Path.Combine(devHdd0Root, "home", normalizedUserId)))
            {
                logger?.Warn($"[RPCS3] Active user '{normalizedUserId}' ({userSource}) does not exist under '{devHdd0Root}\\home'. Trophy progress was not scanned.");
                return null;
            }

            return new Rpcs3InstallationContext
            {
                EmulatorRoot = NormalizePath(emulatorRoot),
                ConfigurationRoot = NormalizePath(configurationRoot),
                DevHdd0Root = NormalizePath(devHdd0Root),
                UserId = normalizedUserId,
                TrophyFolder = NormalizePath(trophyFolder),
                UserIdSource = userSource
            };
        }

        internal static string ResolveConfigurationRoot(string emulatorRoot, string configurationRootOverride = null)
        {
            if (string.IsNullOrWhiteSpace(emulatorRoot))
            {
                return null;
            }

            var configuredRoot = configurationRootOverride;
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                configuredRoot = Environment.GetEnvironmentVariable("RPCS3_CONFIG_DIR");
            }

            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                return NormalizePath(configuredRoot);
            }

            var portableRoot = Path.Combine(emulatorRoot, "portable");
            return Directory.Exists(portableRoot)
                ? NormalizePath(portableRoot)
                : NormalizePath(emulatorRoot);
        }

        internal static string ReadPersistedActiveUser(string configurationRoot)
        {
            var settingsPath = Path.Combine(configurationRoot ?? string.Empty, "GuiConfigs", "persistent_settings.dat");
            if (!File.Exists(settingsPath))
            {
                // RPCS3 itself uses this default when the persistent setting is empty.
                return "00000001";
            }

            try
            {
                var section = string.Empty;
                foreach (var rawLine in File.ReadLines(settingsPath))
                {
                    var line = rawLine?.Trim() ?? string.Empty;
                    if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.Length >= 3 && line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (!string.Equals(section, "Users", StringComparison.OrdinalIgnoreCase) || separator <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim();
                    if (string.Equals(key, "active_user", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring(separator + 1).Trim();
                    }
                }
            }
            catch
            {
                return null;
            }

            return "00000001";
        }

        internal static bool TryNormalizeUserId(string rawUserId, out string userId)
        {
            userId = rawUserId?.Trim();
            if (string.IsNullOrWhiteSpace(userId) || userId.Length != 8 || userId == "00000000" || !userId.All(char.IsDigit))
            {
                userId = null;
                return false;
            }

            return true;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static string NormalizeForKey(string path)
        {
            return (path ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }
    }

    /// <summary>
    /// Resolves a game's RPCS3 installation in a strict order. It refuses to pick
    /// an arbitrary emulator or action when Playnite has multiple plausible choices.
    /// </summary>
    internal static class Rpcs3InstallationResolver
    {
        private static readonly Regex UserIdArgumentPattern = new Regex(
            @"(?:^|\s)--user-id(?:\s+|=)\s*[""']?(?<id>\d{8})[""']?(?=\s|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static Rpcs3InstallationContext Resolve(
            Game game,
            Rpcs3Settings settings,
            IPlayniteAPI playniteApi,
            ILogger logger)
        {
            var gameAction = ResolveGameAction(game, playniteApi, logger);
            if (gameAction != null)
            {
                if (TryReadUserIdArgument(gameAction.Action, out var userId, out var userIdSpecified))
                {
                    if (!userIdSpecified || Rpcs3InstallationContext.TryNormalizeUserId(userId, out _))
                    {
                        var actionContext = Rpcs3InstallationContext.Create(
                            gameAction.Root,
                            userId,
                            userIdSpecified ? "--user-id game action" : null,
                            logger);
                        if (actionContext != null)
                        {
                            return actionContext;
                        }

                        if (userIdSpecified)
                        {
                            // The action names a profile; another install's default
                            // user is not a substitute for it.
                            logger?.Warn(
                                $"[RPCS3] Game '{game?.Name}': the --user-id profile its launch action requests does " +
                                $"not exist under '{gameAction.Root}'. Trophy progress was not scanned.");
                            return null;
                        }

                        // The action's emulator holds no trophy profile (a second
                        // install, a fresh copy). The configured install is the
                        // user's explicit choice, so it gets the next attempt
                        // rather than the game losing trophy data outright.
                        logger?.Info(
                            $"[RPCS3] Game '{game?.Name}': emulator action root '{gameAction.Root}' has no trophy " +
                            "profile; falling back to the configured RPCS3 installation.");
                    }
                    else
                    {
                        logger?.Warn($"[RPCS3] Game '{game?.Name}' has an invalid --user-id launch argument. Trophy progress was not scanned.");
                        return null;
                    }
                }
            }

            var settingsRoot = GetSettingsRoot(settings);
            if (!string.IsNullOrWhiteSpace(settingsRoot))
            {
                var settingsContext = Rpcs3InstallationContext.Create(settingsRoot, null, null, logger);
                if (settingsContext != null)
                {
                    return settingsContext;
                }
            }

            var fallbackRoot = ResolveUniqueRegisteredRoot(playniteApi, logger);
            return string.IsNullOrWhiteSpace(fallbackRoot)
                ? null
                : Rpcs3InstallationContext.Create(fallbackRoot, null, null, logger);
        }

        internal static Rpcs3InstallationContext ResolveFromRoot(string emulatorRoot, ILogger logger)
        {
            return Rpcs3InstallationContext.Create(emulatorRoot, null, null, logger);
        }

        internal static string ResolveEmulatorRoot(Game game, Rpcs3Settings settings, IPlayniteAPI playniteApi, ILogger logger)
        {
            var context = Resolve(game, settings, playniteApi, logger);
            return context?.EmulatorRoot;
        }

        internal static bool IsRpcs3Emulator(Emulator emulator)
        {
            var builtInId = emulator?.BuiltInConfigId ?? string.Empty;
            var name = emulator?.Name ?? string.Empty;
            var installDir = emulator?.InstallDir ?? string.Empty;
            return builtInId.IndexOf("rpcs3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("rpcs3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   installDir.IndexOf("rpcs3", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ResolvedGameAction ResolveGameAction(Game game, IPlayniteAPI playniteApi, ILogger logger)
        {
            var candidates = (game?.GameActions ?? Enumerable.Empty<GameAction>())
                .Where(action => action?.Type == GameActionType.Emulator && action.EmulatorId != Guid.Empty)
                .Select(action => new ResolvedGameAction
                {
                    Action = action,
                    Root = playniteApi?.Database?.Emulators?.Get(action.EmulatorId)?.InstallDir
                })
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Root) &&
                    IsRpcs3Emulator(playniteApi?.Database?.Emulators?.Get(candidate.Action.EmulatorId)))
                .ToList();

            var playActions = candidates.Where(candidate => candidate.Action.IsPlayAction).ToList();
            if (playActions.Count == 1)
            {
                return playActions[0];
            }

            if (playActions.Count > 1)
            {
                logger?.Warn($"[RPCS3] Game '{game?.Name}' has multiple RPCS3 play actions; no trophy profile was selected.");
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            if (candidates.Count > 1)
            {
                logger?.Warn($"[RPCS3] Game '{game?.Name}' has multiple RPCS3 emulator actions and no selected play action; no trophy profile was selected.");
            }

            return null;
        }

        private static string GetSettingsRoot(Rpcs3Settings settings)
        {
            var executablePath = settings?.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            var root = Path.GetDirectoryName(executablePath);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
        }

        private static string ResolveUniqueRegisteredRoot(IPlayniteAPI playniteApi, ILogger logger)
        {
            var roots = (playniteApi?.Database?.Emulators ?? Enumerable.Empty<Emulator>())
                .Where(IsRpcs3Emulator)
                .Select(emulator => emulator.InstallDir)
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 1)
            {
                return roots[0];
            }

            if (roots.Count > 1)
            {
                logger?.Warn($"[RPCS3] Multiple registered RPCS3 installations were found: [{string.Join(", ", roots.OrderBy(root => root, StringComparer.OrdinalIgnoreCase))}]. Configure RPCS3 or select a game action to resolve trophy progress.");
            }

            return null;
        }

        private static bool TryReadUserIdArgument(GameAction action, out string userId, out bool userIdSpecified)
        {
            var arguments = string.Join(" ", new[] { action?.Arguments, action?.AdditionalArguments }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return TryGetRequestedUserId(arguments, out userId, out userIdSpecified);
        }

        internal static bool TryGetRequestedUserId(string arguments, out string userId, out bool userIdSpecified)
        {
            userId = null;
            userIdSpecified = !string.IsNullOrWhiteSpace(arguments) &&
                arguments.IndexOf("--user-id", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!userIdSpecified)
            {
                return true;
            }

            var match = UserIdArgumentPattern.Match(arguments);
            if (match.Success)
            {
                userId = match.Groups["id"].Value;
            }

            return true;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private sealed class ResolvedGameAction
        {
            public GameAction Action { get; set; }
            public string Root { get; set; }
        }
    }
}
