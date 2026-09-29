using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Xenia
{
    /// <summary>
    /// Finds the Xenia account folders that hold a game's .gpd files, and merges progress read
    /// from several of them. An account folder is
    /// &lt;Xenia root&gt;\content\&lt;XUID&gt;\FFFE07D1\00010000\&lt;XUID&gt;\ and contains an Account file.
    /// </summary>
    internal static class XeniaAccountResolver
    {
        private const string AccountFileName = "Account";

        /// <summary>
        /// Levels from an account folder up to the Xenia root that holds recent.toml.
        /// </summary>
        private const int AccountDepthBelowRoot = 5;

        internal static bool IsValidAccountDirectory(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   Directory.Exists(path) &&
                   File.Exists(Path.Combine(path, AccountFileName));
        }

        internal static ProviderPathValidation ValidateAccountPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return ProviderPathValidation.Invalid("LOCPlayAch_InvalidPath");
            }

            return File.Exists(Path.Combine(path, AccountFileName))
                ? ProviderPathValidation.Valid
                : ProviderPathValidation.Invalid("LOCPlayAch_XeniaValidation_NoAccount");
        }

        /// <summary>
        /// Configured account folders that exist and contain an Account file, in configured order.
        /// </summary>
        internal static List<string> GetConfiguredAccountDirectories(XeniaSettings settings)
        {
            return ProviderPathList.Normalize(settings?.AccountPaths)
                .Where(IsValidAccountDirectory)
                .ToList();
        }

        /// <summary>
        /// Account folders to read for <paramref name="game"/>: the configured folders first, then
        /// every account under the install directory of each Xenia emulator the game launches with.
        /// </summary>
        internal static List<string> ResolveAccountDirectories(Game game, XeniaSettings settings, IPlayniteAPI playniteApi)
        {
            var directories = GetConfiguredAccountDirectories(settings);
            foreach (var root in GetGameEmulatorRoots(game, playniteApi))
            {
                directories.AddRange(FindAccountDirectoriesUnderRoot(root));
            }

            return ProviderPathList.Normalize(directories);
        }

        /// <summary>
        /// Every account folder under a Xenia install root.
        /// </summary>
        internal static IEnumerable<string> FindAccountDirectoriesUnderRoot(string xeniaRoot)
        {
            var contentPath = string.IsNullOrWhiteSpace(xeniaRoot) ? null : Path.Combine(xeniaRoot, "content");
            if (contentPath == null || !Directory.Exists(contentPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory.EnumerateDirectories(contentPath)
                    .Select(profileDir => Path.Combine(profileDir, "FFFE07D1", "00010000"))
                    .Where(Directory.Exists)
                    .SelectMany(Directory.EnumerateDirectories)
                    .Where(IsValidAccountDirectory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// The Xenia root (where recent.toml lives) above an account folder.
        /// </summary>
        internal static string GetXeniaRoot(string accountDirectory)
        {
            var current = string.IsNullOrWhiteSpace(accountDirectory)
                ? null
                : new DirectoryInfo(accountDirectory.TrimEnd('\\', '/'));
            for (var level = 0; level < AccountDepthBelowRoot && current != null; level++)
            {
                current = current.Parent;
            }

            return current?.FullName;
        }

        internal static bool IsXeniaEmulator(Emulator emulator)
        {
            if (emulator == null)
            {
                return false;
            }

            var builtInId = emulator.BuiltInConfigId ?? string.Empty;
            var name = emulator.Name ?? string.Empty;
            var installDir = emulator.InstallDir ?? string.Empty;

            return builtInId.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   installDir.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<string> GetGameEmulatorRoots(Game game, IPlayniteAPI playniteApi)
        {
            if (game?.GameActions == null || playniteApi?.Database?.Emulators == null)
            {
                yield break;
            }

            foreach (var action in game.GameActions)
            {
                if (action?.Type != GameActionType.Emulator || action.EmulatorId == Guid.Empty)
                {
                    continue;
                }

                var emulator = playniteApi.Database.Emulators.Get(action.EmulatorId);
                if (IsXeniaEmulator(emulator) && !string.IsNullOrWhiteSpace(emulator.InstallDir))
                {
                    yield return emulator.InstallDir;
                }
            }
        }

        /// <summary>
        /// Merges one game's progress read from several account folders. An achievement is unlocked
        /// when any copy is unlocked, and its unlock time is the earliest non-zero time among the
        /// unlocked copies, so it stays put when another build picks the achievement up later.
        /// A locked achievement keeps the first copy's time. Ids keep first-seen order.
        /// </summary>
        internal static List<XeniaAchievementProgress> MergeProgress(IEnumerable<IEnumerable<XeniaAchievementProgress>> copies)
        {
            var merged = new List<XeniaAchievementProgress>();
            var byId = new Dictionary<uint, XeniaAchievementProgress>();
            foreach (var copy in copies ?? Enumerable.Empty<IEnumerable<XeniaAchievementProgress>>())
            {
                foreach (var item in copy ?? Enumerable.Empty<XeniaAchievementProgress>())
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (!byId.TryGetValue(item.Id, out var current))
                    {
                        current = new XeniaAchievementProgress { Id = item.Id };
                        byId[item.Id] = current;
                        merged.Add(current);
                    }

                    if (item.Unlocked)
                    {
                        if (!current.Unlocked)
                        {
                            // The first unlocked copy replaces any time carried from a locked one.
                            current.Unlocked = true;
                            current.UnlockTime = item.UnlockTime;
                        }
                        else if (item.UnlockTime != 0 && (current.UnlockTime == 0 || item.UnlockTime < current.UnlockTime))
                        {
                            current.UnlockTime = item.UnlockTime;
                        }
                    }
                    else if (!current.Unlocked && current.UnlockTime == 0)
                    {
                        // Xenia stamps a time on locked records too; keep it so a single copy
                        // passes through unchanged.
                        current.UnlockTime = item.UnlockTime;
                    }
                }
            }

            return merged;
        }
    }
}
